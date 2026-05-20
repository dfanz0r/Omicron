using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Omicron.Core.Rendering.Terminal.Unix;

namespace Omicron.Core.Rendering;

/// <summary>
///     Linux-specific terminal backend using <c>termios</c> + <c>poll()</c> + <c>read()</c>.
///     Avoids .NET <c>Console</c> input APIs entirely. Uses cbreak-style raw mode
///     with VMIN=1/VTIME=0 and poll() for cancellation-friendly readiness checks.
/// </summary>
public sealed class LinuxTerminalBackend : ITerminalBackend
{
    private readonly List<TerminalEvent> _eventBuffer = new();
    private readonly Stream _outputStream;
    private readonly ByteBufferWriter _outputWriter;
    private readonly TerminalInputParser _parser = new();

    // Scratch buffers
    private readonly byte[] _readScratch = new byte[2048];
    private readonly int _stdinFd = LinuxNative.STDIN_FILENO;
    private readonly int _stdoutFd = LinuxNative.STDOUT_FILENO;

    private bool _disposed;
    private TerminalSize _lastReportedSize;
    private DateTime _lastSizeCheck = DateTime.MinValue;
    private LinuxNative.Termios _originalTermios;
    private bool _rawMode;

    // Resize polling
    private TerminalSize _size;
    private bool _termiosSaved;

    public LinuxTerminalBackend()
    {
        _outputStream = Console.OpenStandardOutput();
        _outputWriter = new ByteBufferWriter(_outputStream);
    }

    public TerminalSize Size
    {
        get
        {
            RefreshSize();
            return _size;
        }
    }

    public IBufferWriter<byte> Output => _outputWriter;

    /// <summary>
    ///     Initialize terminal state: set cbreak raw mode, enable keyboard protocols.
    ///     Must be called before entering TUI mode. Idempotent.
    /// </summary>
    public void Initialize()
    {
        if (_rawMode)
        {
            return;
        }

        InitializeTerminalMode();
        RefreshSize();
        TerminalLifecycle.Initialize();
    }

    public void Flush()
    {
        _outputWriter.Flush();
        _outputStream.Flush();
    }

    public async IAsyncEnumerable<TerminalEvent> ReadEvents(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _lastReportedSize = Size;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Poll for resize (every 500ms)
            if ((DateTime.UtcNow - _lastSizeCheck).TotalMilliseconds >= 500)
            {
                _lastSizeCheck = DateTime.UtcNow;
                RefreshSize();
                if (
                    _size.Width != _lastReportedSize.Width
                    || _size.Height != _lastReportedSize.Height
                )
                {
                    _lastReportedSize = _size;
                    yield return new ResizeEvent(_size.Width, _size.Height);
                }
            }

            // Poll stdin for readability with a short timeout.
            // Using poll() instead of fcntl(O_NONBLOCK) avoids issues with
            // non-blocking reads on ptys that can return EAGAIN spuriously.
            bool ready = PollStdin(cancellationToken, 10);

            if (ready)
            {
                int bytesRead = ReadStdin();
                if (bytesRead > 0)
                {
                    _eventBuffer.Clear();
                    _parser.Feed(_readScratch.AsSpan(0, bytesRead), _eventBuffer);

                    foreach (TerminalEvent evt in _eventBuffer)
                    {
                        yield return evt;
                    }
                }
                else if (bytesRead < 0)
                {
                    // Error or EOF — exit gracefully
                    yield break;
                }
            }

            // Check for lone ESC timeout
            _eventBuffer.Clear();
            if (_parser.TryFlushEscapeTimeout(_eventBuffer))
            {
                foreach (TerminalEvent evt in _eventBuffer)
                {
                    yield return evt;
                }
            }

            // If no event was yielded, give the async iterator a suspension
            // point. Without this, MoveNextAsync can spin inside this loop
            // until input arrives, which blocks TuiShell from servicing the
            // initial render signal and presents as a black screen on startup.
            await Task.Delay(1, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Restore terminal keyboard protocols
        _parser.WriteRestoreSequences(_outputWriter, Flush);

        // Restore original termios
        if (_termiosSaved)
        {
            LinuxNative.tcsetattr(_stdinFd, LinuxNative.TCSANOW, ref _originalTermios);
        }
    }

    private void InitializeTerminalMode()
    {
        if (LinuxNative.tcgetattr(_stdinFd, out _originalTermios) == 0)
        {
            _termiosSaved = true;
            LinuxNative.Termios raw = _originalTermios;

            // cbreak-style raw mode: disable canonical mode, echo, extended processing,
            // signal chars (but keep ISIG so Ctrl+C works), CR→NL translation,
            // NL→CR, and XON/XOFF flow control.
            raw.c_lflag &= ~(
                LinuxNative.ECHO
                | LinuxNative.ECHOE
                | LinuxNative.ECHOK
                | LinuxNative.ECHONL
                | LinuxNative.ICANON
                | LinuxNative.IEXTEN
            );
            // Keep ISIG for Ctrl+C
            raw.c_lflag |= LinuxNative.ISIG;

            raw.c_iflag &= ~(
                LinuxNative.ICRNL
                | LinuxNative.INLCR
                | LinuxNative.IXON
                | LinuxNative.IXOFF
                | LinuxNative.IXANY
                | LinuxNative.IGNBRK
                | LinuxNative.BRKINT
                | LinuxNative.PARMRK
                | LinuxNative.ISTRIP
                | LinuxNative.INPCK
                | LinuxNative.IGNPAR
            );
            // Enable UTF-8 input on Linux
            raw.c_iflag |= LinuxNative.IUTF8;

            // Disable output processing — the renderer emits raw ANSI sequences.
            raw.c_oflag &= ~(
                LinuxNative.OPOST
                | LinuxNative.ONLCR
                | LinuxNative.OCRNL
                | LinuxNative.ONOCR
                | LinuxNative.ONLRET
            );

            raw.c_cflag |= LinuxNative.CS8 | LinuxNative.CREAD;

            // VMIN=1, VTIME=0: each read() blocks until at least 1 byte is available.
            // Combined with poll() for readiness, this gives low-latency input.
            raw.c_cc[LinuxNative.VMIN] = 1;
            raw.c_cc[LinuxNative.VTIME] = 0;

            if (LinuxNative.tcsetattr(_stdinFd, LinuxNative.TCSANOW, ref raw) == 0)
            {
                _rawMode = true;
            }
        }

        // Only enable protocols if we successfully entered raw mode
        if (_rawMode)
        {
            _parser.EnableModifyOtherKeys(_outputWriter, Flush);
            _parser.EnableKittyProtocolOptimistically(_outputWriter, Flush);
        }
    }

    /// <summary>
    ///     Check if stdin has data available using poll().
    /// </summary>
    private bool PollStdin(CancellationToken ct, int timeoutMs)
    {
        var fd = new LinuxNative.PollFd
        {
            fd = _stdinFd,
            events = LinuxNative.POLLIN | LinuxNative.POLLPRI,
            revents = 0
        };

        int result;
        int remaining = timeoutMs;
        int pollTimeout = Math.Min(remaining, 10); // Max 10ms per poll iteration

        while (true)
        {
            fd.revents = 0;
            result = LinuxNative.poll(ref fd, 1, pollTimeout);

            if (result > 0)
            {
                if ((fd.revents & (LinuxNative.POLLIN | LinuxNative.POLLPRI)) != 0)
                {
                    return true;
                }

                if (
                    (
                        fd.revents
                        & (LinuxNative.POLLERR | LinuxNative.POLLHUP | LinuxNative.POLLNVAL)
                    ) != 0
                )
                {
                    return false;
                }
            }
            else if (result == 0)
            {
                // Timeout — check cancellation and loop
                remaining -= pollTimeout;
                if (remaining <= 0)
                {
                    return false;
                }
            }
            else // result < 0
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == LinuxNative.EINTR)
                {
                    // Interrupted by signal — check cancellation and retry
                    remaining -= pollTimeout;
                    if (remaining <= 0)
                    {
                        return false;
                    }

                    continue;
                }

                return false;
            }

            if (ct.IsCancellationRequested)
            {
                return false;
            }

            // Continue polling until timeout exhausted or data available
            if (remaining <= 0)
            {
                return false;
            }

            pollTimeout = Math.Min(remaining, 10);
        }
    }

    /// <summary>
    ///     Read available bytes from stdin. Returns number of bytes read, 0 for no data,
    ///     or -1 for EOF/error.
    /// </summary>
    private int ReadStdin()
    {
        nint result = LinuxNative.read(_stdinFd, _readScratch, (UIntPtr)_readScratch.Length);
        if (result > 0)
        {
            return (int)result;
        }

        if (result == 0)
        {
            return -1; // EOF
        }

        int errno = Marshal.GetLastPInvokeError();
        if (errno is LinuxNative.EAGAIN or LinuxNative.EINTR)
        {
            return 0; // No data available (shouldn't happen with VMIN=1 though)
        }

        return -1; // Error
    }

    private void RefreshSize()
    {
        try
        {
            var wsz = new LinuxNative.Winsize();
            if (
                LinuxNative.ioctl(_stdoutFd, LinuxNative.TIOCGWINSZ, ref wsz) == 0
                && wsz.ws_col > 0
            )
            {
                _size = new TerminalSize(wsz.ws_col, wsz.ws_row);
            }
            else
            {
                // Fallback: try stdin fd
                if (
                    LinuxNative.ioctl(_stdinFd, LinuxNative.TIOCGWINSZ, ref wsz) == 0
                    && wsz.ws_col > 0
                )
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

    /// <summary>
    ///     Safety-net: restore the console input mode to a state compatible
    ///     with <see cref="Console.ReadKey" /> and standard line reading.
    /// </summary>
    public static void EnsureSafeConsoleInputMode()
    {
        // Reset keyboard protocols before restoring termios — without these
        // escape sequences the terminal may still send multi-byte kitty/xterm
        // sequences that look like garbage to cooked-mode readers.
        int stdoutFd = LinuxNative.STDOUT_FILENO;
        byte[] kittyReset = Encoding.UTF8.GetBytes("\u001b[=0u");
        byte[] xtermReset = Encoding.UTF8.GetBytes("\u001b[>4;0m");
        LinuxNative.write(stdoutFd, kittyReset, (UIntPtr)kittyReset.Length);
        LinuxNative.write(stdoutFd, xtermReset, (UIntPtr)xtermReset.Length);

        int stdinFd = LinuxNative.STDIN_FILENO;
        if (LinuxNative.tcgetattr(stdinFd, out LinuxNative.Termios current) != 0)
        {
            return;
        }

        const uint needed = LinuxNative.ECHO | LinuxNative.ICANON;
        if ((current.c_lflag & needed) != needed)
        {
            LinuxNative.Termios safe = current;
            safe.c_lflag |= needed;
            safe.c_lflag &= ~LinuxNative.IEXTEN;
            safe.c_iflag |= LinuxNative.ICRNL;
            safe.c_oflag |= LinuxNative.OPOST | LinuxNative.ONLCR;
            safe.c_cc[LinuxNative.VMIN] = 1;
            safe.c_cc[LinuxNative.VTIME] = 0;
            LinuxNative.tcsetattr(stdinFd, LinuxNative.TCSANOW, ref safe);
        }
    }

    // ── Internal: Byte buffer writer ──

    private sealed class ByteBufferWriter : IBufferWriter<byte>
    {
        private readonly Stream _stream;
        private byte[] _buffer = new byte[4096];
        private int _written;

        public ByteBufferWriter(Stream stream)
        {
            _stream = stream;
        }

        public void Advance(int count)
        {
            _written += count;
        }

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
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _written + sizeHint));
            }
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
}
