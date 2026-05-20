using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Omicron.Core.Collections;

namespace Omicron.Core.Rendering;

/// <summary>
///     Windows-specific terminal backend using Console API handles, VT processing,
///     and a dedicated background producer thread.
/// </summary>
public sealed class WindowsTerminalBackend : ITerminalBackend
{
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
    private readonly List<TerminalEvent> _eventBuffer = new();

    // Lock-free SPSC queue for Windows producer/consumer
    private readonly QueueSPSC<byte> _inputQueue = new(8192);
    private readonly Stream _outputStream;
    private readonly ByteBufferWriter _outputWriter;
    private readonly TerminalInputParser _parser = new();
    private readonly byte[] _readScratch = new byte[2048];

    private bool _disposed;

    // Input
    private Stream? _inputStream;
    private TerminalSize _lastReportedSize;
    private DateTime _lastSizeCheck = DateTime.MinValue;
    private uint _originalInputMode;
    private uint _originalOutputMode;
    private CancellationTokenSource? _producerCts;
    private Thread? _producerThread;
    private bool _rawMode;

    // Resize polling
    private TerminalSize _size;
    private IntPtr _stdInHandle;

    // Windows Console handles and mode state
    private IntPtr _stdOutHandle;

    public WindowsTerminalBackend()
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
    ///     Initialize Windows terminal state: enable VT processing, configure
    ///     input mode, and probe keyboard protocols.
    /// </summary>
    public void Initialize()
    {
        if (_rawMode)
        {
            return;
        }

        InitializeConsole();
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
        _inputStream ??= Console.OpenStandardInput();
        _lastReportedSize = Size;

        // Start dedicated background producer thread
        _producerCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            _producerCts.Token);

        _producerThread = new Thread(() => WindowsProducerLoop(linkedCts.Token))
        {
            IsBackground = true,
            Name = "OmicronTerminalInput"
        };
        _producerThread.Start();

        while (!cancellationToken.IsCancellationRequested)
        {
            // Periodic resize check
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

            // Drain the SPSC queue into the scratch buffer, then feed parser
            int bytesRead = DrainInputQueue();
            if (bytesRead > 0)
            {
                _eventBuffer.Clear();
                _parser.Feed(_readScratch.AsSpan(0, bytesRead), _eventBuffer);

                foreach (TerminalEvent evt in _eventBuffer)
                {
                    yield return evt;
                }
            }
            else
            {
                // Flush lone ESC timeout on Windows too (same as Linux/macOS)
                _eventBuffer.Clear();
                if (_parser.TryFlushEscapeTimeout(_eventBuffer))
                {
                    foreach (TerminalEvent evt in _eventBuffer)
                    {
                        yield return evt;
                    }
                }
                else
                {
                    await Task.Delay(5, cancellationToken);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Signal producer thread to exit
        _producerCts?.Cancel();

        // Restore keyboard protocols
        _parser.WriteRestoreSequences(_outputWriter, Flush);

        // Restore Windows console modes
        SetConsoleMode(_stdOutHandle, _originalOutputMode);
        SetConsoleMode(_stdInHandle, _originalInputMode);

        // Wait for producer thread
        if (_producerThread is not null && _producerThread.IsAlive)
        {
            try
            {
                _producerThread.Join(TimeSpan.FromSeconds(2));
            }
            catch { }
        }

        _producerCts?.Dispose();
    }

    private void InitializeConsole()
    {
        _stdOutHandle = GetStdHandle(STD_OUTPUT_HANDLE);
        _stdInHandle = GetStdHandle(STD_INPUT_HANDLE);

        if (!GetConsoleMode(_stdOutHandle, out _originalOutputMode))
        {
            _originalOutputMode = 0;
        }

        if (!GetConsoleMode(_stdInHandle, out _originalInputMode))
        {
            _originalInputMode = 0;
        }

        // Enable VT processing on output
        uint newOutMode = _originalOutputMode | ENABLE_VIRTUAL_TERMINAL_PROCESSING;
        SetConsoleMode(_stdOutHandle, newOutMode);

        // Enable VT input, window, and mouse; disable line/echo/processed
        uint newInMode = _originalInputMode;
        newInMode |= ENABLE_VIRTUAL_TERMINAL_INPUT | ENABLE_WINDOW_INPUT | ENABLE_MOUSE_INPUT;
        newInMode &= ~(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT);
        SetConsoleMode(_stdInHandle, newInMode);
        _rawMode = true;

        // Enable keyboard protocols
        _parser.EnableModifyOtherKeys(_outputWriter, Flush);
        _parser.EnableKittyProtocolOptimistically(_outputWriter, Flush);
        _parser.WindowsModifierFallbackEnabled = true;
    }

    /// <summary>
    ///     Background producer loop — reads raw bytes from the console input stream
    ///     and pushes them into the lock-free SPSC queue.
    /// </summary>
    private void WindowsProducerLoop(CancellationToken cancellationToken)
    {
        byte[] temp = new byte[512];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = _inputStream!.Read(temp, 0, temp.Length);
            }
            catch
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                while (!_inputQueue.TryPush(temp[i]) && !cancellationToken.IsCancellationRequested)
                {
                    Thread.SpinWait(1);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    ///     Drain the SPSC queue into _readScratch. Returns bytes transferred.
    /// </summary>
    private int DrainInputQueue()
    {
        int count = 0;
        while (count < _readScratch.Length && _inputQueue.TryPop(out byte b))
        {
            _readScratch[count] = b;
            count++;
        }

        return count;
    }

    private void RefreshSize()
    {
        try
        {
            if (GetConsoleScreenBufferInfo(_stdOutHandle, out ConsoleScreenBufferInfo csbi))
            {
                _size = new TerminalSize(csbi.srWindow.Right - csbi.srWindow.Left + 1,
                    csbi.srWindow.Bottom - csbi.srWindow.Top + 1);
            }
            else
            {
                _size = new TerminalSize(Console.WindowWidth, Console.WindowHeight);
            }
        }
        catch
        {
            _size = new TerminalSize(80, 24);
        }
    }

    // ── Safety net ──

    /// <summary>
    ///     Static safety-net: restore console input mode for standard CLI usage.
    /// </summary>
    public static void EnsureSafeConsoleInputMode()
    {
        IntPtr stdInHandle = GetStdHandle(STD_INPUT_HANDLE);
        if (stdInHandle == IntPtr.Zero)
        {
            return;
        }

        if (!GetConsoleMode(stdInHandle, out uint currentMode))
        {
            return;
        }

        if ((currentMode & ENABLE_VIRTUAL_TERMINAL_INPUT) != 0)
        {
            uint safeMode = currentMode;
            safeMode &= ~ENABLE_VIRTUAL_TERMINAL_INPUT;
            safeMode &= ~ENABLE_MOUSE_INPUT;
            safeMode &= ~ENABLE_WINDOW_INPUT;
            safeMode |= ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT;
            SetConsoleMode(stdInHandle, safeMode);
        }

        // Reset keyboard protocols
        byte[] resetBytes = Encoding.UTF8.GetBytes("\x1b[=0u\x1b[>4;0m");
        Console.OpenStandardOutput().Write(resetBytes, 0, resetBytes.Length);
        Console.OpenStandardOutput().Flush();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(uint nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(
        IntPtr hConsoleOutput,
        out ConsoleScreenBufferInfo lpConsoleScreenBufferInfo);

    // ── Byte buffer writer ──

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
}
