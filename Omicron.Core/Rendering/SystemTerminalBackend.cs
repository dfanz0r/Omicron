using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Omicron.Core.Rendering;

/// <summary>
/// Concrete <see cref="ITerminalBackend"/> that uses platform-specific P/Invoke
/// to manage raw mode, alternate screen, cursor visibility, resize detection,
/// and unbuffered output.
/// </summary>
public sealed class SystemTerminalBackend : ITerminalBackend
{
    private readonly Stream _outputStream;
    private readonly IBufferWriter<byte> _outputWriter;
    private bool _disposed;
    private bool _rawMode;
    private bool _alternateScreenEnabled;
    private bool _cursorHidden;

    // Platform state
    private IntPtr _stdOutHandle;
    private IntPtr _stdInHandle;
    private uint _originalOutputMode;
    private uint _originalInputMode;

    // Unix termios
    private Termios _originalTermios;
    private bool _termiosSaved;
    private int _stdinFd;

    // Input buffering for UTF-8 multi-byte sequences across reads
    private readonly byte[] _readBuffer = new byte[2048];
    private int _pendingCount = 0;

    private TerminalSize _size;

    public TerminalSize Size
    {
        get
        {
            RefreshSize();
            return _size;
        }
    }

    public IBufferWriter<byte> Output => _outputWriter;

    public SystemTerminalBackend()
    {
        _outputStream = Console.OpenStandardOutput();
        _outputWriter = new ByteBufferWriter(_outputStream);
    }

    /// <summary>
    /// Initialize platform-specific terminal state (raw mode, VT processing, etc.).
    /// Must be called before entering TUI mode. Safe to call multiple times (idempotent).
    /// </summary>
    public void Initialize()
    {
        if (_rawMode) return; // Already initialized
        InitializePlatform();
        RefreshSize();
        TerminalLifecycle.Initialize();
    }

    private void InitializePlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _stdOutHandle = GetStdHandle(STD_OUTPUT_HANDLE);
            _stdInHandle = GetStdHandle(STD_INPUT_HANDLE);

            if (!GetConsoleMode(_stdOutHandle, out _originalOutputMode))
                _originalOutputMode = 0;
            if (!GetConsoleMode(_stdInHandle, out _originalInputMode))
                _originalInputMode = 0;

            // Enable virtual terminal processing on output
            uint newOutMode = _originalOutputMode | ENABLE_VIRTUAL_TERMINAL_PROCESSING;
            SetConsoleMode(_stdOutHandle, newOutMode);

            // Enable VT input, window, and mouse; disable line/echo/processed
            uint newInMode = _originalInputMode;
            newInMode |= ENABLE_VIRTUAL_TERMINAL_INPUT | ENABLE_WINDOW_INPUT | ENABLE_MOUSE_INPUT;
            newInMode &= ~(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT);
            SetConsoleMode(_stdInHandle, newInMode);
            _rawMode = true;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            _stdinFd = STDIN_FILENO; // stdin for tcgetattr/tcsetattr (needs TTY fd)
            if (tcgetattr(_stdinFd, out _originalTermios) == 0)
            {
                _termiosSaved = true;
                var raw = _originalTermios;
                // Disable ECHO, ICANON, ISIG
                raw.c_lflag &= ~(ECHO | ICANON); // Keep ISIG so Ctrl+C generates SIGINT
                // Disable ICRNL, INLCR
                raw.c_iflag &= ~(ICRNL | INLCR);
                // Non-blocking read
                raw.c_cc[VMIN] = 0;
                raw.c_cc[VTIME] = 0;
                tcsetattr(_stdinFd, TCSANOW, ref raw);
                _rawMode = true;
            }
        }
    }

    public IAsyncEnumerable<TerminalEvent> ReadEvents(CancellationToken cancellationToken)
        => ReadEventsImpl(cancellationToken);

    private async IAsyncEnumerable<TerminalEvent> ReadEventsImpl(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var inputStream = Console.OpenStandardInput();

        while (!cancellationToken.IsCancellationRequested)
        {
            int bytesRead;
            try
            {
                // Read after any pending bytes from previous incomplete UTF-8 sequence
                bytesRead = await inputStream.ReadAsync(
                    _readBuffer.AsMemory(_pendingCount), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (Exception)
            {
                yield break;
            }

            if (bytesRead == 0)
                yield break;

            int totalLength = _pendingCount + bytesRead;
            int offset = 0;

            while (offset < totalLength)
            {
                // Check for escape sequences and resize events
                if (TryParseResizeEvent(_readBuffer, ref offset, totalLength, out var resizeEvent))
                {
                    yield return resizeEvent!;
                    continue;
                }

                if (TryParseEscapeSequence(_readBuffer, ref offset, totalLength, out var terminalEvent))
                {
                    yield return terminalEvent!;
                    continue;
                }

                // Decode UTF-8 from current position
                var status = Rune.DecodeFromUtf8(
                    _readBuffer.AsSpan(offset, totalLength - offset),
                    out var rune, out int consumed);

                if (status == System.Buffers.OperationStatus.Done)
                {
                    offset += consumed;
                    yield return new KeyEvent(Key.Character, KeyModifiers.None, rune);
                }
                else if (status == System.Buffers.OperationStatus.NeedMoreData)
                {
                    // Copy remaining bytes to the start of the buffer for next read
                    int remaining = totalLength - offset;
                    _readBuffer.AsSpan(offset, remaining).CopyTo(_readBuffer);
                    _pendingCount = remaining;
                    break;
                }
                else // InvalidData
                {
                    offset++; // Skip bad byte and continue
                }
            }

            if (offset >= totalLength)
                _pendingCount = 0; // All consumed
        }
    }

    private static bool TryParseEscapeSequence(byte[] buffer, ref int offset, int length, out TerminalEvent? terminalEvent)
    {
        terminalEvent = null;

        if (offset >= length || buffer[offset] != 0x1B)
            return false;

        // Need at least ESC + one more byte
        if (offset + 2 > length)
            return false;

        if (buffer[offset + 1] == '[')
        {
            // CSI sequence: ESC[...~
            int seqStart = offset;
            offset += 2; // skip ESC[

            // Read parameters until final byte (0x40–0x7E)
            int paramStart = offset;
            while (offset < length && (buffer[offset] < 0x40 || buffer[offset] > 0x7E))
                offset++;

            if (offset >= length)
            {
                // Incomplete sequence — rewind and treat as literal
                offset = seqStart + 1;
                return false;
            }

            byte finalByte = buffer[offset++];
            string param = Encoding.ASCII.GetString(buffer, paramStart, offset - paramStart - 1);

            // SGR mouse: ESC[<button;col;rowM  or ESC[<button;col;rowm
            if (param.Length > 0 && param[0] == '<' && (finalByte == (byte)'M' || finalByte == (byte)'m'))
            {
                var mouseParts = param[1..].Split(';');
                if (mouseParts.Length == 3 &&
                    int.TryParse(mouseParts[0], out var btn) &&
                    int.TryParse(mouseParts[1], out var col) &&
                    int.TryParse(mouseParts[2], out var row))
                {
                    var (button, mouseModifiers) = MapSgrMouseButton(btn);
                    var kind = finalByte == (byte)'M' ? MouseEventKind.Pressed : MouseEventKind.Released;
                    terminalEvent = new MouseEvent(row - 1, col - 1, button, kind, mouseModifiers);
                    return true;
                }
            }

            // Parse modifiers (CSI <param>;<mod> <letter>)
            var parts = param.Split(';');
            int mainParam = parts.Length > 0 && int.TryParse(parts[0], out var p) ? p : 0;
            var modifiers = KeyModifiers.None;
            if (parts.Length > 1 && int.TryParse(parts[1], out var m))
            {
                if ((m & 1) != 0) modifiers |= KeyModifiers.Shift;
                if ((m & 2) != 0) modifiers |= KeyModifiers.Alt;
                if ((m & 4) != 0) modifiers |= KeyModifiers.Control;
            }

            Key key = MapCsiSequence(finalByte, mainParam);
            terminalEvent = new KeyEvent(key, modifiers, null);
            return true;
        }

        if (buffer[offset + 1] == 'O')
        {
            // SS3 sequence: ESC O ...
            offset += 2;
            if (offset < length)
            {
                byte cmd = buffer[offset++];
                Key key = cmd switch
                {
                    (byte)'P' => Key.F1,
                    (byte)'Q' => Key.F2,
                    (byte)'R' => Key.F3,
                    (byte)'S' => Key.F4,
                    _ => Key.None
                };
                terminalEvent = new KeyEvent(key, KeyModifiers.None, null);
                return true;
            }
        }

        // Plain ESC
        offset++;
        terminalEvent = new KeyEvent(Key.Escape, KeyModifiers.None, null);
        return true;
    }

    private static (MouseButton Button, KeyModifiers Modifiers) MapSgrMouseButton(int encoded)
    {
        int btn = encoded & 0b11;
        bool shift = (encoded & 0b100) != 0;
        bool alt = (encoded & 0b1000) != 0;
        bool ctrl = (encoded & 0b10000) != 0;
        bool scroll = (encoded & 0b1000000) != 0;

        var button = scroll
            ? (encoded & 0b1) == 0 ? MouseButton.ScrollUp : MouseButton.ScrollDown
            : btn switch
            {
                0 => MouseButton.Left,
                1 => MouseButton.Middle,
                2 => MouseButton.Right,
                _ => MouseButton.None,
            };

        var modifiers = KeyModifiers.None;
        if (shift) modifiers |= KeyModifiers.Shift;
        if (alt) modifiers |= KeyModifiers.Alt;
        if (ctrl) modifiers |= KeyModifiers.Control;

        return (button, modifiers);
    }

    private static Key MapCsiSequence(byte finalByte, int param)
    {
        return (finalByte, param) switch
        {
            (0x41, _) => Key.Up,
            (0x42, _) => Key.Down,
            (0x43, _) => Key.Right,
            (0x44, _) => Key.Left,
            (0x48, _) => Key.Home,
            (0x46, _) => Key.End,
            (0x5A, _) => Key.Tab, // Backtab
            (0x7E, 1) => Key.Home,
            (0x7E, 2) => Key.Insert,
            (0x7E, 3) => Key.Delete,
            (0x7E, 4) => Key.End,
            (0x7E, 5) => Key.PageUp,
            (0x7E, 6) => Key.PageDown,
            (0x7E, 11) => Key.F1,
            (0x7E, 12) => Key.F2,
            (0x7E, 13) => Key.F3,
            (0x7E, 14) => Key.F4,
            (0x7E, 15) => Key.F5,
            (0x7E, 17) => Key.F6,
            (0x7E, 18) => Key.F7,
            (0x7E, 19) => Key.F8,
            (0x7E, 20) => Key.F9,
            (0x7E, 21) => Key.F10,
            (0x7E, 23) => Key.F11,
            (0x7E, 24) => Key.F12,
            _ => Key.None
        };
    }

    private bool TryParseResizeEvent(byte[] buffer, ref int offset, int length, out ResizeEvent? resizeEvent)
    {
        resizeEvent = null;

        // On Windows, resize events come as console input records, not byte sequences.
        // We handle them via GetConsoleScreenBufferInfo polling in RefreshSize.
        // On Unix, SIGWINCH is handled by polling terminal size.

        // Check for a hypothetical resize sequence (not standard but we reserve the hooks)
        return false;
    }

    private void RefreshSize()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!GetConsoleScreenBufferInfo(_stdOutHandle, out var csbi))
                    return;

                _size = new TerminalSize(csbi.srWindow.Right - csbi.srWindow.Left + 1,
                                         csbi.srWindow.Bottom - csbi.srWindow.Top + 1);
            }
            else
            {
                // Unix: ioctl TIOCGWINSZ
                var wsz = new Winsize();
                if (ioctl(STDOUT_FILENO, TIOCGWINSZ, ref wsz) == 0 && wsz.ws_col > 0)
                {
                    _size = new TerminalSize(wsz.ws_col, wsz.ws_row);
                }
                else
                {
                    _size = new TerminalSize(Console.WindowWidth, Console.WindowHeight);
                }
            }
        }
        catch
        {
            _size = new TerminalSize(80, 24);
        }
    }

    public void Flush()
    {
        if (_outputWriter is ByteBufferWriter bbw)
            bbw.Flush();
        _outputStream.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Restore alternate screen
        if (_alternateScreenEnabled)
        {
            WriteRaw("\x1b[?1049l");
            Flush();
            _alternateScreenEnabled = false;
        }

        // Show cursor
        if (_cursorHidden)
        {
            WriteRaw("\x1b[?25h");
            Flush();
            _cursorHidden = false;
        }

        // Restore platform modes
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SetConsoleMode(_stdOutHandle, _originalOutputMode);
            SetConsoleMode(_stdInHandle, _originalInputMode);
        }
        else if (_termiosSaved)
        {
            tcsetattr(_stdinFd, TCSANOW, ref _originalTermios);
        }

        // Do NOT dispose _outputStream — it's Console.OpenStandardOutput(), owned by the process
    }

    private void WriteRaw(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        _outputWriter.Write(bytes);
    }

    // ── Internal: Byte buffer writer for IBufferWriter<byte> ──

    private sealed class ByteBufferWriter : IBufferWriter<byte>
    {
        private readonly Stream _stream;
        private byte[] _buffer = new byte[4096];
        private int _written;

        public ByteBufferWriter(Stream stream) => _stream = stream;

        public void Advance(int count) => _written += count;
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (_buffer.Length - _written < sizeHint)
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _written + sizeHint));
        }

        public void Flush()
        {
            if (_written > 0)
            {
                _stream.Write(_buffer, 0, _written);
                _written = 0;
            }
        }
    }

    // ── Windows P/Invoke ──

    private const uint STD_OUTPUT_HANDLE = unchecked((uint)-11);
    private const uint STD_INPUT_HANDLE = unchecked((uint)-10);
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;
    private const uint ENABLE_WINDOW_INPUT = 0x0008;
    private const uint ENABLE_MOUSE_INPUT = 0x0010;
    private const uint ENABLE_LINE_INPUT = 0x0002;
    private const uint ENABLE_ECHO_INPUT = 0x0004;
    private const uint ENABLE_PROCESSED_INPUT = 0x0001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(uint nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public short dwSizeX;
        public short dwSizeY;
        public short dwCursorPositionX;
        public short dwCursorPositionY;
        public short wAttributes;
        public SmallRect srWindow;
        public short dwMaximumWindowSizeX;
        public short dwMaximumWindowSizeY;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out ConsoleScreenBufferInfo lpConsoleScreenBufferInfo);

    // ── Unix P/Invoke ──

    private const int STDIN_FILENO = 0;
    private const int STDOUT_FILENO = 1;
    private const int TCSANOW = 0;
    private const int TIOCGWINSZ = 0x5413;
    private const uint ECHO = 0x00000008;
    private const uint ICANON = 0x00000002;
    private const uint ISIG = 0x00000001;
    private const uint ICRNL = 0x00000100;
    private const uint INLCR = 0x00000040;
    private const int VMIN = 6;
    private const int VTIME = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct Termios
    {
        public uint c_iflag;
        public uint c_oflag;
        public uint c_cflag;
        public uint c_lflag;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
        public byte[] c_cc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, out Termios termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optional_actions, ref Termios termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, int request, ref Winsize wsz);
}
