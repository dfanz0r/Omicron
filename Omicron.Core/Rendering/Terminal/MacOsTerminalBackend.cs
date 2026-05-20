using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Omicron.Core.Rendering.Terminal.Unix;

namespace Omicron.Core.Rendering;

/// <summary>
///     macOS-specific terminal backend using Darwin <c>termios</c> + <c>poll()</c> + <c>read()</c>.
///     Uses separate structs and constants from Linux — the termios layout differs
///     (ulong flags, NCCS=20, different c_cc indices, different ioctl constant).
/// </summary>
public sealed class MacOsTerminalBackend : ITerminalBackend
{
    private readonly List<TerminalEvent> _eventBuffer = new();
    private readonly Stream _outputStream;
    private readonly ByteBufferWriter _outputWriter;
    private readonly TerminalInputParser _parser = new();

    private readonly byte[] _readScratch = new byte[2048];
    private readonly int _stdinFd = MacOsNative.STDIN_FILENO;
    private readonly int _stdoutFd = MacOsNative.STDOUT_FILENO;

    private bool _disposed;
    private TerminalSize _lastReportedSize;
    private DateTime _lastSizeCheck = DateTime.MinValue;
    private MacOsNative.Termios _originalTermios;
    private bool _rawMode;

    private TerminalSize _size;
    private bool _termiosSaved;

    public MacOsTerminalBackend()
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
                    yield break;
                }
            }

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

        _parser.WriteRestoreSequences(_outputWriter, Flush);

        if (_termiosSaved)
        {
            MacOsNative.tcsetattr(_stdinFd, MacOsNative.TCSANOW, ref _originalTermios);
        }
    }

    private void InitializeTerminalMode()
    {
        if (MacOsNative.tcgetattr(_stdinFd, out _originalTermios) == 0)
        {
            _termiosSaved = true;
            MacOsNative.Termios raw = _originalTermios;

            // cbreak-style raw mode
            raw.c_lflag &= ~(
                MacOsNative.ECHO
                | MacOsNative.ECHOE
                | MacOsNative.ECHOK
                | MacOsNative.ECHONL
                | MacOsNative.ICANON
                | MacOsNative.IEXTEN
            );
            // Keep ISIG for Ctrl+C
            raw.c_lflag |= MacOsNative.ISIG;

            raw.c_iflag &= ~(
                MacOsNative.ICRNL
                | MacOsNative.INLCR
                | MacOsNative.IXON
                | MacOsNative.IXOFF
                | MacOsNative.IXANY
                | MacOsNative.IGNBRK
                | MacOsNative.BRKINT
                | MacOsNative.PARMRK
                | MacOsNative.ISTRIP
                | MacOsNative.INPCK
                | MacOsNative.IGNPAR
            );
            raw.c_iflag |= MacOsNative.IUTF8;

            raw.c_oflag &= ~(
                MacOsNative.OPOST
                | MacOsNative.ONLCR
                | MacOsNative.OCRNL
                | MacOsNative.ONOCR
                | MacOsNative.ONLRET
            );

            raw.c_cflag |= MacOsNative.CS8 | MacOsNative.CREAD;

            raw.c_cc[MacOsNative.VMIN] = 1;
            raw.c_cc[MacOsNative.VTIME] = 0;

            if (MacOsNative.tcsetattr(_stdinFd, MacOsNative.TCSANOW, ref raw) == 0)
            {
                _rawMode = true;
            }
        }

        if (_rawMode)
        {
            _parser.EnableModifyOtherKeys(_outputWriter, Flush);
            _parser.EnableKittyProtocolOptimistically(_outputWriter, Flush);
        }
    }

    private bool PollStdin(CancellationToken ct, int timeoutMs)
    {
        var fd = new MacOsNative.PollFd
        {
            fd = _stdinFd,
            events = MacOsNative.POLLIN | MacOsNative.POLLPRI,
            revents = 0
        };

        int remaining = timeoutMs;
        int pollTimeout = Math.Min(remaining, 10);

        while (true)
        {
            fd.revents = 0;
            int result = MacOsNative.poll(ref fd, 1, pollTimeout);

            if (result > 0)
            {
                if ((fd.revents & (MacOsNative.POLLIN | MacOsNative.POLLPRI)) != 0)
                {
                    return true;
                }

                if (
                    (
                        fd.revents
                        & (MacOsNative.POLLERR | MacOsNative.POLLHUP | MacOsNative.POLLNVAL)
                    ) != 0
                )
                {
                    return false;
                }
            }
            else if (result == 0)
            {
                remaining -= pollTimeout;
                if (remaining <= 0)
                {
                    return false;
                }
            }
            else
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == MacOsNative.EINTR)
                {
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

            if (remaining <= 0)
            {
                return false;
            }

            pollTimeout = Math.Min(remaining, 10);
        }
    }

    private int ReadStdin()
    {
        nint result = MacOsNative.read(_stdinFd, _readScratch, (UIntPtr)_readScratch.Length);
        if (result > 0)
        {
            return (int)result;
        }

        if (result == 0)
        {
            return -1; // EOF
        }

        int errno = Marshal.GetLastPInvokeError();
        if (errno is MacOsNative.EAGAIN or MacOsNative.EINTR)
        {
            return 0;
        }

        return -1;
    }

    private void RefreshSize()
    {
        try
        {
            var wsz = new MacOsNative.Winsize();
            int result;

            // macOS ARM64 ioctl is variadic and requires register padding.
            // Use the padded overload on ARM64; use the standard one on x64.
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                result = MacOsNative.ioctl_arm64(_stdoutFd,
                    MacOsNative.TIOCGWINSZ,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    ref wsz);
            }
            else
            {
                result = MacOsNative.ioctl(_stdoutFd, MacOsNative.TIOCGWINSZ, ref wsz);
            }

            if (result == 0 && wsz.ws_col > 0)
            {
                _size = new TerminalSize(wsz.ws_col, wsz.ws_row);
            }
            else
            {
                // Fallback: try stdin fd (ARM64-aware)
                if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                {
                    result = MacOsNative.ioctl_arm64(_stdinFd,
                        MacOsNative.TIOCGWINSZ,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        ref wsz);
                }
                else
                {
                    result = MacOsNative.ioctl(_stdinFd, MacOsNative.TIOCGWINSZ, ref wsz);
                }

                if (result == 0 && wsz.ws_col > 0)
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
    ///     Safety-net: restore console input mode for standard CLI usage.
    /// </summary>
    public static void EnsureSafeConsoleInputMode()
    {
        // Reset keyboard protocols before restoring termios — without these
        // escape sequences the terminal may still send multi-byte kitty/xterm
        // sequences that look like garbage to cooked-mode readers.
        int stdoutFd = MacOsNative.STDOUT_FILENO;
        byte[] kittyReset = Encoding.UTF8.GetBytes("\u001b[=0u");
        byte[] xtermReset = Encoding.UTF8.GetBytes("\u001b[>4;0m");
        MacOsNative.write(stdoutFd, kittyReset, (UIntPtr)kittyReset.Length);
        MacOsNative.write(stdoutFd, xtermReset, (UIntPtr)xtermReset.Length);

        int stdinFd = MacOsNative.STDIN_FILENO;
        if (MacOsNative.tcgetattr(stdinFd, out MacOsNative.Termios current) != 0)
        {
            return;
        }

        const ulong needed = MacOsNative.ECHO | MacOsNative.ICANON;
        if ((current.c_lflag & needed) != needed)
        {
            MacOsNative.Termios safe = current;
            safe.c_lflag |= needed;
            safe.c_lflag &= ~MacOsNative.IEXTEN;
            safe.c_iflag |= MacOsNative.ICRNL;
            safe.c_oflag |= MacOsNative.OPOST | MacOsNative.ONLCR;
            safe.c_cc[MacOsNative.VMIN] = 1;
            safe.c_cc[MacOsNative.VTIME] = 0;
            MacOsNative.tcsetattr(stdinFd, MacOsNative.TCSANOW, ref safe);
        }
    }

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
