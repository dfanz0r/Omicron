using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Omicron.Core.Collections;

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
    private Stream? _inputStream;

    // Input buffering for UTF-8 multi-byte sequences across reads
    private readonly byte[] _readBuffer = new byte[2048];
    private int _pendingCount = 0;

    // Bracketed paste accumulation
    private bool _accumulatingPaste;
    private readonly List<byte> _pasteBuffer = new();

    // Kitty keyboard protocol state
    private bool _kittyProtocolActive;
    private bool _suppressNextLegacyEnter;

    // Lock-free SPSC queue isolates the Windows background read thread
    // from the main parsing thread.  The producer reads raw console bytes
    // into a temp buffer and pushes them; the consumer pops into _readBuffer.
    private readonly QueueSPSC<byte> _inputQueue = new(8192);

    // Dedicated producer thread (Windows only) — never uses the thread pool.
    private CancellationTokenSource? _producerCts;
    private Thread? _producerThread;

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

        // xterm modifyOtherKeys mode 2 — encodes modifiers for keys that
        // already use escape sequences (arrows, function keys, etc.) as
        // CSI 27;modifier;key~.  This is independent of the kitty keyboard
        // protocol and acts as a fallback for terminals that support it but
        // not kitty.
        WriteRaw("\x1b[>4;2m");
        Flush();

        // Optimistically enable kitty keyboard protocol (flags = disambiguate | report_alternates | report_all_keys).
        EnableKittyProtocolOptimistically();
    }

    private TerminalSize _lastReportedSize;
    private DateTime _lastSizeCheck = DateTime.MinValue;

    public IAsyncEnumerable<TerminalEvent> ReadEvents(CancellationToken cancellationToken)
        => ReadEventsImpl(cancellationToken);

    private async IAsyncEnumerable<TerminalEvent> ReadEventsImpl(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _inputStream ??= Console.OpenStandardInput();
        _lastReportedSize = Size;

        // On Windows start a dedicated background thread that reads from the
        // console into the lock-free SPSC queue.  This isolates the blocking
        // Read() call from the parsing thread — no shared _readBuffer, no
        // race condition, no spliced escape sequences.  We use a real Thread
        // (not Task.Run) so the thread pool is never blocked.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _producerCts = new CancellationTokenSource();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _producerCts.Token);

            _producerThread = new Thread(() => WindowsProducerLoop(linkedCts.Token))
            {
                IsBackground = true,
                Name = "OmicronTerminalInput"
            };
            _producerThread.Start();
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            // Periodically check for terminal resize (every 500ms)
            if ((DateTime.UtcNow - _lastSizeCheck).TotalMilliseconds >= 500)
            {
                _lastSizeCheck = DateTime.UtcNow;
                RefreshSize();
                if (_size.Width != _lastReportedSize.Width || _size.Height != _lastReportedSize.Height)
                {
                    _lastReportedSize = _size;
                    yield return new ResizeEvent(_size.Width, _size.Height);
                }
            }

            int bytesRead;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Drain the lock-free queue into _readBuffer.  If the queue
                    // is empty we yield briefly so the async enumerator doesn't
                    // spin at 100% CPU.
                    bytesRead = DrainInputQueue();
                    if (bytesRead == 0)
                    {
                        await Task.Delay(5, cancellationToken);
                        continue;
                    }
                }
                else
                {
                    bytesRead = await _inputStream.ReadAsync(
                        _readBuffer.AsMemory(_pendingCount), cancellationToken);
                }
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
                int startOffset = offset;

                // Check for escape sequences and resize events
                if (TryParseResizeEvent(_readBuffer, ref offset, totalLength, out var resizeEvent))
                {
                    yield return resizeEvent!;
                    continue;
                }

                // Bracketed paste accumulation mode: scan for paste end marker
                if (_accumulatingPaste)
                {
                    // Scan for ESC[201~ (0x1B 0x5B 0x32 0x30 0x31 0x7E)
                    int endIdx = -1;
                    for (int i = offset; i <= totalLength - 6; i++)
                    {
                        if (_readBuffer[i] == 0x1B && _readBuffer[i + 1] == '[' &&
                            _readBuffer[i + 2] == '2' && _readBuffer[i + 3] == '0' &&
                            _readBuffer[i + 4] == '1' && _readBuffer[i + 5] == '~')
                        {
                            endIdx = i;
                            break;
                        }
                    }

                    if (endIdx >= 0)
                    {
                        // Accumulate raw bytes before the end marker
                        for (int i = offset; i < endIdx; i++)
                            _pasteBuffer.Add(_readBuffer[i]);

                        offset = endIdx + 6; // Skip ESC[201~

                        string pasteText = Encoding.UTF8.GetString(_pasteBuffer.ToArray());
                        _pasteBuffer.Clear();
                        _accumulatingPaste = false;
                        yield return new PasteEvent(pasteText);
                    }
                    else
                    {
                        // No end marker — accumulate all remaining bytes for next read
                        for (int i = offset; i < totalLength; i++)
                            _pasteBuffer.Add(_readBuffer[i]);
                        offset = totalLength;
                        _pendingCount = 0;
                    }
                    continue;
                }

                // 1. Kitty protocol sequences (CSI ... u with proper format)
                if (TryParseKittyKeySequence(_readBuffer, ref offset, totalLength, out var kittyEvent))
                {
                    // Partial kitty support: some terminals send both the kitty
                    // sequence AND the legacy character. Suppress the next bare
                    // \r so it doesn't trigger a duplicate Enter/Submit.
                    if (kittyEvent is KeyEvent ke && ke.Key == Key.Enter)
                        _suppressNextLegacyEnter = true;
                    yield return kittyEvent!;
                    continue;
                }

                // 2. Legacy escape sequences (CSI without kitty format, SS3, etc.)
                if (TryParseEscapeSequence(_readBuffer, ref offset, totalLength, out var terminalEvent))
                {
                    yield return terminalEvent!;
                    continue;
                }

                // If the escape parser couldn't make forward progress AND
                // we're sitting on an ESC byte, we have an incomplete escape
                // sequence at the end of the buffer. Preserve all remaining
                // bytes for the next read so they don't get mis-decoded as
                // typed characters.
                if (offset == startOffset && _readBuffer[offset] == 0x1B)
                {
                    int remaining = totalLength - offset;
                    _readBuffer.AsSpan(offset, remaining).CopyTo(_readBuffer);
                    _pendingCount = remaining;
                    break;
                }

                // Some Windows console configurations return UTF-16 data with
                // NUL bytes interleaved (0x1B 0x00 0x5B 0x00 ...). Skip NULs
                // so they don't break escape-sequence parsing.
                if (_readBuffer[offset] == 0x00)
                {
                    offset++;
                    continue;
                }

                // 3. Raw UTF-8 / control characters (legacy terminals only)
                var status = Rune.DecodeFromUtf8(
                    _readBuffer.AsSpan(offset, totalLength - offset),
                    out var rune, out int consumed);

                if (status == System.Buffers.OperationStatus.Done)
                {
                    offset += consumed;

                    // Detect \ + \r as Shift+Enter (some terminals encode it this way).
                    // Both bytes arrive in the same buffer — consume them together
                    // so the consumer never sees the intermediate \.
                    if (rune.Value == '\\' && offset < totalLength && _readBuffer[offset] == '\r')
                    {
                        offset++; // Consume \r
                        yield return new KeyEvent(Key.Enter, KeyModifiers.Shift, null);
                        continue;
                    }

                    // Windows fallback for non-kitty terminals:
                    // When the terminal does not support kitty keyboard protocol
                    // (or it ignored our request), Enter and Shift+Enter both send
                    // bare \r. Poll physical key state on Windows to distinguish them.
                    // This only runs when we're in the raw UTF-8 path, meaning
                    // neither the kitty parser nor the legacy escape parser matched.
                    bool handled = false;
#if !OMIT_WINDOWS_KEY_FALLBACK
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && rune.Value == '\r')
                    {
                        if (_suppressNextLegacyEnter)
                        {
                            // Terminal sent both kitty sequence and legacy \r;
                            // the kitty parser already handled this keypress.
                            _suppressNextLegacyEnter = false;
                            handled = true;
                        }
                        else
                        {
                            var modifiers = KeyModifiers.None;
                            if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) modifiers |= KeyModifiers.Shift;
                            if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) modifiers |= KeyModifiers.Control;
                            if ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) modifiers |= KeyModifiers.Alt;
                            yield return new KeyEvent(Key.Enter, modifiers, null);
                            handled = true;
                        }
                    }
#endif

                    if (!handled)
                    {
                        yield return new KeyEvent(Key.Character, KeyModifiers.None, rune);
                    }
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

    private bool TryParseEscapeSequence(byte[] buffer, ref int offset, int length, out TerminalEvent? terminalEvent)
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
            int scan = offset + 2; // skip ESC[ locally

            // Read parameters until final byte (0x40–0x7E)
            int paramStart = scan;
            while (scan < length && (buffer[scan] < 0x40 || buffer[scan] > 0x7E))
                scan++;

            if (scan >= length)
            {
                // Incomplete sequence — don't advance offset so the caller
                // preserves all remaining bytes for the next read.
                return false;
            }

            byte finalByte = buffer[scan];
            string param = Encoding.ASCII.GetString(buffer, paramStart, scan - paramStart);

            // Windows console can inject NUL bytes inside escape sequences.
            // Strip them before parsing so int.TryParse doesn't fail.
            param = param.Replace("\0", "");

            offset = scan + 1;

            // Kitty protocol push/pop/query responses — consume but don't emit.
            if (finalByte == (byte)'u' && param.Length > 0 &&
                (param[0] == '?' || param[0] == '>' || param[0] == '<'))
            {
                terminalEvent = null;
                return true;
            }

            // SGR mouse: ESC[<button;col;rowM  or ESC[<button;col;rowm
            // In mode 1002 (button-event tracking) motion while a button is
            // held sets bit 5 (value 32) in the button code.
            if (param.Length > 0 && param[0] == '<' && (finalByte == (byte)'M' || finalByte == (byte)'m'))
            {
                var mouseParts = param[1..].Split(';');
                if (mouseParts.Length == 3 &&
                    int.TryParse(mouseParts[0], out var btn) &&
                    int.TryParse(mouseParts[1], out var col) &&
                    int.TryParse(mouseParts[2], out var row))
                {
                    bool isMotion = (btn & 32) != 0; // bit 5 = motion while pressed
                    var (button, mouseModifiers) = MapSgrMouseButton(btn);
                    var kind = finalByte == (byte)'m' ? MouseEventKind.Released
                        : isMotion ? MouseEventKind.Dragged
                        : MouseEventKind.Pressed;
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
                // CSI u protocol (final byte 'u' / 0x75) uses xterm-style modifier
                // encoding: 1=no mod, 2=Shift, 3=Alt, 4=Shift+Alt, 5=Ctrl,
                // 6=Shift+Ctrl, 7=Alt+Ctrl, 8=Shift+Alt+Ctrl.
                // xterm modifyOtherKeys mode 2 (CSI 27;modifier;key~) also uses
                // xterm-style modifier encoding.
                // Other CSI sequences (final byte '~' / 0x7E) use DEC bit-flag
                // encoding: 1=Shift, 2=Alt, 4=Ctrl.
                bool useXtermEncoding = finalByte == 0x75 || (finalByte == 0x7E && mainParam == 27);
                if (useXtermEncoding)
                {
                    // Convert xterm encoding to DEC bit-flag encoding
                    if (m == 1) m = 0;
                    else if (m > 1) m = m - 1;
                }
                if ((m & 1) != 0) modifiers |= KeyModifiers.Shift;
                if ((m & 2) != 0) modifiers |= KeyModifiers.Alt;
                if ((m & 4) != 0) modifiers |= KeyModifiers.Control;
            }

            // Bracketed paste start/end markers
            if (finalByte == 0x7E)
            {
                if (mainParam == 200)
                {
                    _accumulatingPaste = true;
                    _pasteBuffer.Clear();
                    terminalEvent = null;
                    return true;
                }
                if (mainParam == 201)
                {
                    string pasteText = Encoding.UTF8.GetString(_pasteBuffer.ToArray());
                    _pasteBuffer.Clear();
                    _accumulatingPaste = false;
                    terminalEvent = new PasteEvent(pasteText);
                    return true;
                }
            }

            // xterm modifyOtherKeys mode 2: CSI 27;modifier;key~
            // The key code is the third parameter (e.g. 13 = Enter).
            if (finalByte == 0x7E && mainParam == 27 && parts.Length >= 3 &&
                int.TryParse(parts[2], out var otherKeyCode))
            {
                Key modKey = MapKeyCode(otherKeyCode);
                terminalEvent = new KeyEvent(modKey, modifiers, null);
                return true;
            }

            Key key = MapCsiSequence(finalByte, mainParam);

            // Kitty keyboard protocol with allKeysAsEscapes sends ALL keys as
            // CSI codepoint;modifier u — not just printable characters.
            // Control characters (1-31) must be mapped back to Key.Character
            // so app-level handlers (e.g. Ctrl+D → Value==4) continue to work.
            // PUA range (57344-63743) is reserved for functional keys (Shift,
            // Ctrl, etc.) and must NOT be treated as text characters.
            if (finalByte == 0x75 && key == Key.None &&
                mainParam is > 0 and < 57344)
            {
                try
                {
                    var rune = new System.Text.Rune(mainParam);
                    terminalEvent = new KeyEvent(Key.Character, modifiers, rune);
                    return true;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Invalid codepoint — fall through to default handling
                }
            }

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
            // Note: CSI u (0x75) sequences are NOT handled here anymore.
            // They are parsed by the dedicated TryParseKittyKeySequence method
            // which runs BEFORE TryParseEscapeSequence in the event loop.
            _ => Key.None
        };
    }

    /// <summary>
    /// Map a numeric key code (from xterm modifyOtherKeys mode 2) to a <see cref="Key" />.
    /// Key codes are ASCII / Unicode values: 13 = Enter, 9 = Tab, 32 = Space, etc.
    /// </summary>
    private static Key MapKeyCode(int code)
    {
        return code switch
        {
            13 => Key.Enter,
            9 => Key.Tab,
            27 => Key.Escape,
            127 => Key.Backspace,
            8 => Key.Backspace,
            >= 32 and < 0x110000 => Key.Character,
            _ => Key.None,
        };
    }

    // ================================================================
    // Kitty Keyboard Protocol Support
    // ================================================================

    /// <summary>
    /// PUA (Private Use Area) key codes 57344-63743 map to functional keys
    /// as defined by the kitty keyboard protocol.
    /// </summary>
    private static readonly Dictionary<int, Key> KittyPuaKeyMap = new()
    {
        // 0xF000-0xF00F: Cursor keys
        [0xF000] = Key.Up,
        [0xF001] = Key.Down,
        [0xF002] = Key.Left,
        [0xF003] = Key.Right,
        [0xF004] = Key.Home,
        [0xF005] = Key.End,
        // 0xF006 = Insert (not in our Key enum yet? it is)
        [0xF006] = Key.Insert,
        [0xF007] = Key.Delete,
        [0xF008] = Key.PageUp,
        [0xF009] = Key.PageDown,
        // 0xF00A-0xF00F: reserved

        // 0xF010-0xF01F: Function keys F1-F19
        [0xF010] = Key.F1,
        [0xF011] = Key.F2,
        [0xF012] = Key.F3,
        [0xF013] = Key.F4,
        [0xF014] = Key.F5,
        [0xF015] = Key.F6,
        [0xF016] = Key.F7,
        [0xF017] = Key.F8,
        [0xF018] = Key.F9,
        [0xF019] = Key.F10,
        [0xF01A] = Key.F11,
        [0xF01B] = Key.F12,

        // 0xF020: Keypad keys (mapped to equivalent non-keypad keys for now)
        [0xF020] = Key.Up,
        [0xF021] = Key.Down,
        [0xF022] = Key.Left,
        [0xF023] = Key.Right,
        [0xF024] = Key.Home,
        [0xF025] = Key.End,
        [0xF026] = Key.PageUp,
        [0xF027] = Key.PageDown,
        [0xF028] = Key.Insert,
        [0xF029] = Key.Delete,
        [0xF02A] = Key.Tab,
        [0xF02B] = Key.Enter,
        [0xF02C] = Key.Escape,
        [0xF02D] = Key.Backspace,

        // 0xF030-0xF03F: Modifier keys (pressed/released)
        // These are handled as modifier-only events, not as Key.Character.
        [0xF030] = Key.None, // Left Shift
        [0xF031] = Key.None, // Right Shift
        [0xF032] = Key.None, // Left Control
        [0xF033] = Key.None, // Right Control
        [0xF034] = Key.None, // Left Alt
        [0xF035] = Key.None, // Right Alt
        [0xF036] = Key.None, // Left Meta
        [0xF037] = Key.None, // Right Meta
        [0xF038] = Key.None, // Left Super
        [0xF039] = Key.None, // Right Super
        [0xF03A] = Key.None, // Left Hyper
        [0xF03B] = Key.None, // Right Hyper
        [0xF03C] = Key.None, // Caps Lock
        [0xF03D] = Key.None, // Num Lock
        [0xF03E] = Key.None, // Scroll Lock
        [0xF03F] = Key.None, // Print Screen

        // 0xF040-0xF04F: more F keys (20-37)
        [0xF040] = Key.F13,
        [0xF041] = Key.F14,
        [0xF042] = Key.F15,
        [0xF043] = Key.F16,
        [0xF044] = Key.F17,
        [0xF045] = Key.F18,
        [0xF046] = Key.F19,
        [0xF047] = Key.F20,
        [0xF048] = Key.F21,
        [0xF049] = Key.F22,
        [0xF04A] = Key.F23,
        [0xF04B] = Key.F24,
    };

    /// <summary>
    /// Try to parse a kitty keyboard protocol sequence.
    /// Format: CSI unicode-key-code [:shifted-key][:base-layout-key] ; modifiers [:event-type] [; text-as-codepoints] u
    /// </summary>
    internal static bool TryParseKittyKeySequence(byte[] buffer, ref int offset, int length, out TerminalEvent? terminalEvent)
    {
        terminalEvent = null;

        if (offset >= length || buffer[offset] != 0x1B)
            return false;

        if (offset + 2 > length || buffer[offset + 1] != '[')
            return false;

        // Scan forward to find the final byte (0x40-0x7E)
        int scan = offset + 2;
        int paramStart = scan;
        while (scan < length && (buffer[scan] < 0x40 || buffer[scan] > 0x7E))
            scan++;

        if (scan >= length)
            return false; // Incomplete sequence

        byte finalByte = buffer[scan];

        // Only interested in 'u' sequences (0x75)
        if (finalByte != 0x75)
            return false;

        // Extract parameter string, stripping NULs
        string rawParam = Encoding.ASCII.GetString(buffer, paramStart, scan - paramStart);
        rawParam = rawParam.Replace("\0", "");

        if (rawParam.Length == 0)
            return false;

        // Kitty query/response (prefix with ? or > or <) — consume but don't emit
        if (rawParam[0] == '?' || rawParam[0] == '>' || rawParam[0] == '<')
        {
            offset = scan + 1;
            terminalEvent = null;
            return true;
        }

        // Split parameters by ';'
        var parts = rawParam.Split(';');

        // ---- First parameter: key-code:shifted-key:base-layout-key ----
        var firstParts = parts[0].Split(':');
        if (!int.TryParse(firstParts[0], out int keyCode))
            return false;

        int? shiftedKey = null;
        if (firstParts.Length > 1 && firstParts[1].Length > 0)
        {
            if (int.TryParse(firstParts[1], out int sk))
                shiftedKey = sk;
        }

        // baseLayoutKey (firstParts[2]) is not used in MVP — skip

        // ---- Second parameter (optional): modifier-value[:event-type] ----
        var modifiers = KeyModifiers.None;
        var eventType = KeyEventType.Press;

        if (parts.Length >= 2)
        {
            var secondParts = parts[1].Split(':');
            if (int.TryParse(secondParts[0], out int modifierValue))
            {
                // Decode xterm-style modifiers: value = 1 + actual_modifiers
                // 1=none, 2=shift, 3=alt, 4=shift+alt, 5=ctrl, 6=shift+ctrl, 7=alt+ctrl, 8=shift+alt+ctrl
                int decMod = modifierValue - 1;

                if ((decMod & 1) != 0) modifiers |= KeyModifiers.Shift;
                if ((decMod & 2) != 0) modifiers |= KeyModifiers.Alt;
                if ((decMod & 4) != 0) modifiers |= KeyModifiers.Control;
            }

            // Event type (Press/Repeat/Release) — default Press
            if (secondParts.Length > 1 && int.TryParse(secondParts[1], out int eventTypeValue))
            {
                eventType = eventTypeValue switch
                {
                    2 => KeyEventType.Repeat,
                    3 => KeyEventType.Release,
                    _ => KeyEventType.Press,
                };
            }
        }

        // ---- Optional third parameter: text-as-codepoints ----
        string? textAsCodepoints = null;
        if (parts.Length > 2)
        {
            textAsCodepoints = parts[2];
        }

        offset = scan + 1;

        // ---- Map key code to Key enum ----
        Key key = MapKittyKeyCode(keyCode);

        // ---- Resolve text ----
        string? resolvedText = ResolveText(keyCode, modifiers, shiftedKey, textAsCodepoints);

        Rune? rune = null;
        if (key == Key.Character && keyCode is >= 32 and < 0x110000)
        {
            try { rune = new Rune(keyCode); }
            catch (ArgumentOutOfRangeException) { }
        }

        terminalEvent = new KeyEvent(key, modifiers, rune)
        {
            KeyCode = keyCode,
            ResolvedText = resolvedText,
            EventType = eventType,
        };
        return true;
    }

    /// <summary>
    /// Map a kitty key code to a <see cref="Key"/> enum value.
    /// </summary>
    private static Key MapKittyKeyCode(int keyCode)
    {
        // PUA range: functional keys (modifiers, cursor, etc.)
        if (keyCode >= 0xE000 && keyCode <= 0xF8FF)
        {
            if (KittyPuaKeyMap.TryGetValue(keyCode, out var mapped))
                return mapped;
            return Key.None;
        }

        return keyCode switch
        {
            9 => Key.Tab,
            13 => Key.Enter,
            27 => Key.Escape,
            127 => Key.Backspace,
            8 => Key.Backspace,
            // Control characters 1-26 are Key.Character (for Ctrl+A etc.)
            >= 1 and <= 26 => Key.Character,
            // Printable ASCII and Unicode
            >= 32 and < 0x110000 => Key.Character,
            _ => Key.None,
        };
    }

    /// <summary>
    /// Resolve the text that would be produced by a key combination.
    /// </summary>
    internal static string? ResolveText(int keyCode, KeyModifiers modifiers, int? shiftedKey, string? textAsCodepoints)
    {
        // 1. If the terminal sent explicit text (report_text flag), use it
        if (textAsCodepoints is not null && textAsCodepoints.Length > 0)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var part in textAsCodepoints.Split(':'))
                {
                    if (int.TryParse(part, out int cp))
                        sb.Append(char.ConvertFromUtf32(cp));
                }
                if (sb.Length > 0)
                    return sb.ToString();
            }
            catch { /* fall through to other resolution */ }
        }

        // 2. If shifted key is available and shift is active, use it
        if (shiftedKey.HasValue && modifiers.HasFlag(KeyModifiers.Shift))
        {
            try { return char.ConvertFromUtf32(shiftedKey.Value); }
            catch (ArgumentOutOfRangeException) { }
        }

        // 3. Control characters: Ctrl+A → "\x01"
        if (keyCode is >= 1 and <= 26 && modifiers.HasFlag(KeyModifiers.Control))
        {
            return ((char)keyCode).ToString();
        }

        // 4. Printable character from key code (excluding DEL at 127)
        if ((keyCode >= 32 && keyCode < 127) || (keyCode > 127 && keyCode < 0x110000))
        {
            try { return char.ConvertFromUtf32(keyCode); }
            catch (ArgumentOutOfRangeException) { }
        }

        return null;
    }

    /// <summary>
    /// Optimistically enable the kitty keyboard protocol.
    /// Sends the enable sequence with flags = 13 (disambiguate + report_alternates + report_all_keys).
    /// Also sends a query (ESC[?u) as a probe; the response, if any, is consumed silently
    /// by the parser later.  Terminals that ignore the enable sequence continue in legacy mode.
    /// </summary>
    /// <remarks>
    /// Flags breakdown:
    ///   1 = disambiguate (CSI 27;mod;key~ for functional keys)
    ///   4 = report_alternates (shifted-key sub-parameter, e.g. 49:33 for Shift+1)
    ///   8 = report_all_keys (all keys as CSI codepoint;modifier u)
    /// </remarks>
    private void EnableKittyProtocolOptimistically()
    {
        // Query current state (the response, if any, is consumed silently later)
        WriteRaw("\x1b[?u");
        // Enable disambiguate + report_alternates + report_all_keys
        WriteRaw("\x1b[=13u");
        Flush();

        _kittyProtocolActive = true;
    }

    // ----------------------------------------------------------------
    // Windows SPSC producer / consumer helpers
    // ----------------------------------------------------------------

    /// <summary>
    /// Background producer loop (Windows only). Reads raw bytes from the
    /// console input stream into a temporary buffer, then pushes each byte
    /// into the lock-free SPSC queue.  Never touches _readBuffer or
    /// _pendingCount — those belong exclusively to the consumer thread.
    /// </summary>
    private void WindowsProducerLoop(CancellationToken cancellationToken)
    {
        var temp = new byte[512];
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
                break;

            for (int i = 0; i < read; i++)
            {
                // Blocking push — spin until space is available.  The queue
                // is large (8 KiB) so this should be extremely rare.
                while (!_inputQueue.TryPush(temp[i]) && !cancellationToken.IsCancellationRequested)
                {
                    Thread.SpinWait(1);
                }
                if (cancellationToken.IsCancellationRequested)
                    break;
            }
        }
    }

    /// <summary>
    /// Consumer-side drain: pop as many bytes as possible from the SPSC
    /// queue into _readBuffer starting at _pendingCount.  Returns the
    /// number of bytes transferred (may be zero).
    /// </summary>
    private int DrainInputQueue()
    {
        int space = _readBuffer.Length - _pendingCount;
        if (space <= 0) return 0;

        int count = 0;
        while (count < space && _inputQueue.TryPop(out var b))
        {
            _readBuffer[_pendingCount + count] = b;
            count++;
        }
        return count;
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
                if (GetConsoleScreenBufferInfo(_stdOutHandle, out var csbi))
                {
                    _size = new TerminalSize(csbi.srWindow.Right - csbi.srWindow.Left + 1,
                                             csbi.srWindow.Bottom - csbi.srWindow.Top + 1);
                }
                else
                {
                    _size = new TerminalSize(Console.WindowWidth, Console.WindowHeight);
                }
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

        // Signal the dedicated producer thread to exit
        _producerCts?.Cancel();

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

        // Reset xterm modifyOtherKeys
        WriteRaw("\x1b[>4;0m");

        // Reset kitty keyboard protocol if we enabled it
        if (_kittyProtocolActive)
        {
            WriteRaw("\x1b[=0u");
            _kittyProtocolActive = false;
        }

        Flush();

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

        // Wait for the producer thread to finish (with a generous timeout)
        if (_producerThread is not null && _producerThread.IsAlive)
        {
            try { _producerThread.Join(TimeSpan.FromSeconds(2)); }
            catch { /* ignore */ }
        }

        _producerCts?.Dispose();

        // Do NOT dispose _outputStream — it's Console.OpenStandardOutput(), owned by the process
    }

    private void WriteRaw(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        _outputWriter.Write(bytes);
    }

    /// <summary>
    /// Static safety-net: restore the console input mode to a state compatible
    /// with <see cref="Console.ReadKey"/> and standard line reading.
    /// Call this before entering standard CLI mode if the TUI may have left
    /// the terminal in raw / VT-input mode.
    /// </summary>
    public static void EnsureSafeConsoleInputMode()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var stdInHandle = GetStdHandle(STD_INPUT_HANDLE);
            if (stdInHandle == IntPtr.Zero)
                return;

            if (!GetConsoleMode(stdInHandle, out uint currentMode))
                return;

            // If VT input is enabled, the console is in TUI/raw mode.
            // Disable it and re-enable the traditional input processing flags
            // so Console.ReadKey works correctly.
            if ((currentMode & ENABLE_VIRTUAL_TERMINAL_INPUT) != 0)
            {
                uint safeMode = currentMode;
                safeMode &= ~ENABLE_VIRTUAL_TERMINAL_INPUT;
                safeMode &= ~ENABLE_MOUSE_INPUT;
                safeMode &= ~ENABLE_WINDOW_INPUT;
                safeMode |= ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT;
                SetConsoleMode(stdInHandle, safeMode);
            }

            // Reset kitty keyboard protocol (clear all flags) and xterm modifyOtherKeys
            var resetBytes = Encoding.UTF8.GetBytes("\x1b[=0u\x1b[>4;0m");
            Console.OpenStandardOutput().Write(resetBytes, 0, resetBytes.Length);
            Console.OpenStandardOutput().Flush();
        }
        else
        {
            int stdinFd = STDIN_FILENO;
            if (tcgetattr(stdinFd, out Termios current) != 0)
                return;

            // If canonical mode or echo is disabled, restore them.
            const uint needed = ECHO | ICANON;
            if ((current.c_lflag & needed) != needed)
            {
                var safe = current;
                safe.c_lflag |= needed;
                safe.c_iflag |= ICRNL;
                safe.c_cc[VMIN] = 1;
                safe.c_cc[VTIME] = 0;
                tcsetattr(stdinFd, TCSANOW, ref safe);
            }
        }
    }

    // ── Internal: Byte buffer writer for IBufferWriter<byte> ──

    private sealed class ByteBufferWriter : IBufferWriter<byte>
    {
        private readonly Stream _stream;
        private byte[] _buffer = new byte[4096];
        private int _written;

        public ByteBufferWriter(Stream stream) => _stream = stream;

        public int WrittenCount => _written;

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

#if !OMIT_WINDOWS_KEY_FALLBACK
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
#endif



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
