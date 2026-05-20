using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace Omicron.Core.Rendering;

/// <summary>
///     Shared byte parser for terminal escape sequences, keyboard protocol support,
///     and UTF-8 decoding. Backends feed raw bytes; this class yields
///     <see cref="TerminalEvent" /> values. Backends remain responsible for
///     terminal mode configuration, reading bytes from native APIs, and resize
///     polling.
/// </summary>
public sealed class TerminalInputParser
{
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    /// <summary>The ESC timeout used when a lone ESC byte is in the buffer.</summary>
    public static readonly TimeSpan EscapeKeyTimeout = TimeSpan.FromMilliseconds(50);

    // ================================================================
    // Kitty Keyboard Protocol Support
    // ================================================================

    private static readonly Dictionary<int, Key> KittyPuaKeyMap = new()
    {
        [0xF000] = Key.Up,
        [0xF001] = Key.Down,
        [0xF002] = Key.Left,
        [0xF003] = Key.Right,
        [0xF004] = Key.Home,
        [0xF005] = Key.End,
        [0xF006] = Key.Insert,
        [0xF007] = Key.Delete,
        [0xF008] = Key.PageUp,
        [0xF009] = Key.PageDown,
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
        [0xF030] = Key.None,
        [0xF031] = Key.None,
        [0xF032] = Key.None,
        [0xF033] = Key.None,
        [0xF034] = Key.None,
        [0xF035] = Key.None,
        [0xF036] = Key.None,
        [0xF037] = Key.None,
        [0xF038] = Key.None,
        [0xF039] = Key.None,
        [0xF03A] = Key.None,
        [0xF03B] = Key.None,
        [0xF03C] = Key.None,
        [0xF03D] = Key.None,
        [0xF03E] = Key.None,
        [0xF03F] = Key.None,
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
        [0xF04B] = Key.F24
    };

    private readonly List<byte> _pasteBuffer = new();

    // Bracketed paste accumulation
    private bool _accumulatingPaste;

    // Kitty keyboard protocol state

    // xterm modifyOtherKeys state
    private bool _modifyOtherKeys2Active;

    // Small suffix preserved across reads to detect ESC[201~ split across chunks
    private int _pasteSuffixLength;
    private int _pendingCount;

    private DateTime _pendingEscapeSince = DateTime.MinValue;

    // Input buffering for UTF-8 multi-byte sequences across read calls
    private byte[] _readBuffer = new byte[2048];

    // Duplicate Enter suppression: some terminals emit both kitty Enter + legacy \r
    private bool _suppressNextLegacyEnter;

    /// <summary>
    ///     When true, the parser will attempt to infer Shift/Ctrl/Alt modifiers for
    ///     bare \r (Enter) key presses using the Windows GetAsyncKeyState API.
    ///     Only meaningful on Windows; ignored on other platforms.
    /// </summary>
    public bool WindowsModifierFallbackEnabled { get; set; }

    /// <summary>
    ///     Whether the kitty keyboard protocol has been optimistically enabled.
    /// </summary>
    public bool KittyProtocolActive { get; private set; }

    /// <summary>
    ///     Write the kitty protocol enable sequence to the provided output.
    ///     Called by backends during initialization. The parser tracks the state
    ///     so it can skip query/push/pop responses.
    /// </summary>
    public void EnableKittyProtocolOptimistically(IBufferWriter<byte> output, Action flush)
    {
        // Query current state (the response, if any, is consumed silently later)
        WriteRaw(output, "\x1b[?u");
        // Enable disambiguate + report_alternates + report_all_keys + report_text.
        // report_text is needed for shifted printable input on terminals that
        // encode Shift+a as CSI 97;2;65u rather than a literal 'A'.
        WriteRaw(output, "\x1b[=29u");
        flush();
        KittyProtocolActive = true;
    }

    /// <summary>
    ///     Write the xterm modifyOtherKeys mode 2 enable sequence.
    /// </summary>
    public void EnableModifyOtherKeys(IBufferWriter<byte> output, Action flush)
    {
        WriteRaw(output, "\x1b[>4;2m");
        flush();
        _modifyOtherKeys2Active = true;
    }

    /// <summary>
    ///     Write terminal-restore sequences (kitty protocol reset, modifyOtherKeys reset).
    /// </summary>
    public void WriteRestoreSequences(IBufferWriter<byte> output, Action flush)
    {
        if (_modifyOtherKeys2Active)
        {
            WriteRaw(output, "\x1b[>4;0m");
            _modifyOtherKeys2Active = false;
        }

        if (KittyProtocolActive)
        {
            WriteRaw(output, "\x1b[=0u");
            KittyProtocolActive = false;
        }

        flush();
    }

    private static void WriteRaw(IBufferWriter<byte> output, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        output.Write(bytes);
    }

    /// <summary>
    ///     Feed raw bytes from the terminal into the parser. Returns parsed
    ///     <see cref="TerminalEvent" /> values. Call repeatedly as bytes arrive.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> bytes, ICollection<TerminalEvent> events)
    {
        // Copy bytes into internal buffer
        int totalLength = _pendingCount + bytes.Length;
        EnsureReadBufferCapacity(totalLength);
        bytes.CopyTo(_readBuffer.AsSpan(_pendingCount));

        int offset = 0;
        while (offset < totalLength)
        {
            int startOffset = offset;

            // 0. Bracketed paste accumulation mode: scan for paste end marker
            if (_accumulatingPaste)
            {
                // Scan for ESC[201~ (0x1B 0x5B 0x32 0x30 0x31 0x7E)
                int endIdx = -1;
                for (int i = offset; i <= totalLength - 6; i++)
                {
                    if (
                        _readBuffer[i] == 0x1B
                        && _readBuffer[i + 1] == '['
                        && _readBuffer[i + 2] == '2'
                        && _readBuffer[i + 3] == '0'
                        && _readBuffer[i + 4] == '1'
                        && _readBuffer[i + 5] == '~'
                    )
                    {
                        endIdx = i;
                        break;
                    }
                }

                if (endIdx >= 0)
                {
                    // Accumulate raw bytes before the end marker.
                    // Preserved suffix bytes from previous reads are already at the
                    // front of _readBuffer (not in _pasteBuffer), so they are correctly
                    // included here without any extra removal.
                    for (int i = offset; i < endIdx; i++)
                    {
                        _pasteBuffer.Add(_readBuffer[i]);
                    }

                    offset = endIdx + 6; // Skip ESC[201~

                    string pasteText = Encoding.UTF8.GetString(_pasteBuffer.ToArray());
                    _pasteBuffer.Clear();
                    _accumulatingPaste = false;
                    _pasteSuffixLength = 0;
                    events.Add(new PasteEvent(pasteText));
                    continue;
                }

                // No end marker yet. Accumulate most bytes, but preserve the last
                // 5 bytes as a suffix — they could be the start of ESC[201~
                // arriving in the next read.
                int preserveLen = Math.Min(5, totalLength - offset);
                int accrueLen = totalLength - offset - preserveLen;

                for (int i = offset; i < offset + accrueLen; i++)
                {
                    _pasteBuffer.Add(_readBuffer[i]);
                }

                // If preserveLen > 0, keep those bytes in the buffer for next Feed
                if (preserveLen > 0)
                {
                    _readBuffer
                        .AsSpan(totalLength - preserveLen, preserveLen)
                        .CopyTo(_readBuffer);
                    _pendingCount = preserveLen;
                }
                else
                {
                    _pendingCount = 0;
                }

                _pasteSuffixLength = preserveLen;
                // Don't advance offset — we already handled all bytes
                return;
            }

            // 1. Kitty protocol sequences (CSI ... u with proper format)
            if (TryParseKittyKeySequence(_readBuffer, ref offset, totalLength, out TerminalEvent? kittyEvent))
            {
                if (kittyEvent is not null)
                {
                    // Suppress duplicate legacy Enter if terminal sends both
                    if (kittyEvent is KeyEvent ke && ke.Key == Key.Enter)
                    {
                        _suppressNextLegacyEnter = true;
                    }

                    events.Add(kittyEvent);
                }

                continue;
            }

            // 2. Legacy escape sequences (CSI without kitty format, SS3, etc.)
            if (TryParseEscapeSequence(_readBuffer, ref offset, totalLength, out TerminalEvent? terminalEvent))
            {
                if (terminalEvent is not null)
                {
                    events.Add(terminalEvent);
                }

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
                bool pendingSingleEscape = remaining == 1 && _readBuffer[offset] == 0x1B;
                _readBuffer.AsSpan(offset, remaining).CopyTo(_readBuffer);
                _pendingCount = remaining;
                _pendingEscapeSince = pendingSingleEscape ? DateTime.UtcNow : DateTime.MinValue;
                return;
            }

            // Skip NUL bytes that some Windows console configurations inject
            if (_readBuffer[offset] == 0x00)
            {
                offset++;
                continue;
            }

            // 3. Raw UTF-8 / control characters (legacy terminals only)
            OperationStatus status = OperationStatus.Done;
            Rune rune = default;
            int consumed = 0;

            try
            {
                status = Rune.DecodeFromUtf8(_readBuffer.AsSpan(offset, totalLength - offset),
                    out rune,
                    out consumed);
            }
            catch
            {
                // Skip bad byte
                offset++;
                continue;
            }

            if (status == OperationStatus.Done)
            {
                offset += consumed;

                // Detect \ + \r as Shift+Enter (some terminals encode it this way)
                if (rune.Value == '\\' && offset < totalLength && _readBuffer[offset] == '\r')
                {
                    offset++; // Consume \r
                    events.Add(new KeyEvent(Key.Enter, KeyModifiers.Shift, null));
                    continue;
                }

                bool handled = false;

                if (rune.Value is '\r' or '\n')
                {
                    if (_suppressNextLegacyEnter)
                    {
                        // Terminal sent both kitty sequence and legacy Enter;
                        // the kitty parser already handled this keypress.
                        _suppressNextLegacyEnter = false;
                        handled = true;
                    }
                    else
                    {
                        KeyModifiers enterModifiers = KeyModifiers.None;
                        if (
                            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                            && WindowsModifierFallbackEnabled
                        )
                        {
                            enterModifiers = ReadWindowsKeyModifiers();
                        }

                        events.Add(new KeyEvent(Key.Enter, enterModifiers, null));
                        handled = true;
                    }
                }
                else if (rune.Value is 0x7F or '\b')
                {
                    events.Add(new KeyEvent(Key.Backspace, KeyModifiers.None, null));
                    handled = true;
                }
                else if (rune.Value == '\t')
                {
                    events.Add(new KeyEvent(Key.Tab, KeyModifiers.None, null));
                    handled = true;
                }

                if (!handled)
                {
                    events.Add(new KeyEvent(Key.Character, KeyModifiers.None, rune));
                }
            }
            else if (status == OperationStatus.NeedMoreData)
            {
                // Copy remaining bytes to the start of the buffer for next read
                int remaining = totalLength - offset;
                _readBuffer.AsSpan(offset, remaining).CopyTo(_readBuffer);
                _pendingCount = remaining;
                _pendingEscapeSince = DateTime.MinValue;
                return;
            }
            else // InvalidData
            {
                offset++; // Skip bad byte and continue
            }
        }

        if (offset >= totalLength)
        {
            _pendingCount = 0;
            _pendingEscapeSince = DateTime.MinValue;
        }
    }

    /// <summary>
    ///     Check if a lone ESC byte has been pending long enough to emit as Escape key.
    ///     Returns true if an Escape event was generated.
    /// </summary>
    public bool TryFlushEscapeTimeout(ICollection<TerminalEvent> events)
    {
        if (
            _pendingCount == 1
            && _readBuffer[0] == 0x1B
            && _pendingEscapeSince != DateTime.MinValue
            && DateTime.UtcNow - _pendingEscapeSince >= EscapeKeyTimeout
        )
        {
            _pendingCount = 0;
            _pendingEscapeSince = DateTime.MinValue;
            events.Add(new KeyEvent(Key.Escape, KeyModifiers.None, null));
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Reset all internal parser state (called on backend re-initialization).
    /// </summary>
    public void Reset()
    {
        _pendingCount = 0;
        _pendingEscapeSince = DateTime.MinValue;
        _accumulatingPaste = false;
        _pasteBuffer.Clear();
        _pasteSuffixLength = 0;
        _suppressNextLegacyEnter = false;
        KittyProtocolActive = false;
        _modifyOtherKeys2Active = false;
    }

    private void EnsureReadBufferCapacity(int required)
    {
        if (required > _readBuffer.Length)
        {
            byte[] newBuffer = new byte[Math.Max(_readBuffer.Length * 2, required)];
            Array.Copy(_readBuffer, newBuffer, _pendingCount);
            // _readBuffer is reassigned — Feed reads from it after copy-in
            _readBuffer = newBuffer;
        }
    }

    // ================================================================
    // Escape sequence parsing
    // ================================================================

    private bool TryParseEscapeSequence(
        byte[] buffer,
        ref int offset,
        int length,
        out TerminalEvent? terminalEvent)
    {
        terminalEvent = null;

        if (offset >= length || buffer[offset] != 0x1B)
        {
            return false;
        }

        // Need at least ESC + one more byte
        if (offset + 2 > length)
        {
            return false;
        }

        if (buffer[offset + 1] == '[')
        {
            // CSI sequence: ESC[...~
            int scan = offset + 2;

            // Read parameters until final byte (0x40–0x7E)
            int paramStart = scan;
            while (scan < length && (buffer[scan] < 0x40 || buffer[scan] > 0x7E))
            {
                scan++;
            }

            if (scan >= length)
            {
                // Incomplete sequence — don't advance offset so the caller
                // preserves all remaining bytes for the next read.
                return false;
            }

            byte finalByte = buffer[scan];
            string param = Encoding.ASCII.GetString(buffer, paramStart, scan - paramStart);

            // Windows console can inject NUL bytes inside escape sequences.
            param = param.Replace("\0", "");

            offset = scan + 1;

            // Kitty protocol push/pop/query responses — consume but don't emit.
            if (
                finalByte == (byte)'u'
                && param.Length > 0
                && (param[0] == '?' || param[0] == '>' || param[0] == '<')
            )
            {
                terminalEvent = null;
                return true;
            }

            // SGR mouse: ESC[<button;col;rowM  or ESC[<button;col;rowm
            if (
                param.Length > 0
                && param[0] == '<'
                && (finalByte == (byte)'M' || finalByte == (byte)'m')
            )
            {
                string[] mouseParts = param[1..].Split(';');
                if (
                    mouseParts.Length == 3
                    && int.TryParse(mouseParts[0], out int btn)
                    && int.TryParse(mouseParts[1], out int col)
                    && int.TryParse(mouseParts[2], out int row)
                )
                {
                    bool isMotion = (btn & 32) != 0;
                    (MouseButton button, KeyModifiers mouseModifiers) = MapSgrMouseButton(btn);
                    MouseEventKind kind =
                        finalByte == (byte)'m' ? MouseEventKind.Released
                        : isMotion ? MouseEventKind.Dragged
                        : MouseEventKind.Pressed;
                    terminalEvent = new MouseEvent(row - 1, col - 1, button, kind, mouseModifiers);
                    return true;
                }
            }

            // Parse modifiers (CSI <param>;<mod> <letter>)
            string[] parts = param.Split(';');
            int mainParam = parts.Length > 0 && int.TryParse(parts[0], out int p) ? p : 0;
            KeyModifiers modifiers = KeyModifiers.None;
            if (parts.Length > 1 && int.TryParse(parts[1], out int m))
            {
                bool useXtermEncoding = finalByte == 0x75 || (finalByte == 0x7E && mainParam == 27);
                if (useXtermEncoding)
                {
                    if (m == 1)
                    {
                        m = 0;
                    }
                    else if (m > 1)
                    {
                        m = m - 1;
                    }
                }

                if ((m & 1) != 0)
                {
                    modifiers |= KeyModifiers.Shift;
                }

                if ((m & 2) != 0)
                {
                    modifiers |= KeyModifiers.Alt;
                }

                if ((m & 4) != 0)
                {
                    modifiers |= KeyModifiers.Control;
                }
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
            if (
                finalByte == 0x7E
                && mainParam == 27
                && parts.Length >= 3
                && int.TryParse(parts[2], out int otherKeyCode)
            )
            {
                Key modKey = MapKeyCode(otherKeyCode);
                Rune? rune = null;
                if (modKey == Key.Character)
                {
                    try
                    {
                        rune = new Rune(otherKeyCode);
                    }
                    catch (ArgumentOutOfRangeException) { }
                }

                terminalEvent = new KeyEvent(modKey, modifiers, rune);
                return true;
            }

            Key key = MapCsiSequence(finalByte, mainParam);

            // CSI u: codepoint;modifier u — kitty protocol with allKeysAsEscapes
            // Sends ALL keys as CSI. Control characters (1-31) map to Key.Character.
            // PUA range (57344-63743) is for functional keys.
            if (finalByte == 0x75 && key == Key.None && mainParam is > 0 and < 57344)
            {
                try
                {
                    var rune = new Rune(mainParam);
                    terminalEvent = new KeyEvent(Key.Character, modifiers, rune);
                    return true;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Invalid codepoint — fall through
                }
            }

            terminalEvent = new KeyEvent(key, modifiers, null);
            return true;
        }

        if (buffer[offset + 1] == 'O')
        {
            // SS3 sequence: ESC O ...
            // Need at least ESC + O + command byte
            if (offset + 3 > length)
            {
                // Incomplete — don't advance. The pending bytes will be
                // copied to the front of the buffer for the next read.
                terminalEvent = null;
                return false;
            }

            offset += 2;
            byte cmd = buffer[offset++];
            Key key = cmd switch
            {
                (byte)'A' => Key.Up,
                (byte)'B' => Key.Down,
                (byte)'C' => Key.Right,
                (byte)'D' => Key.Left,
                (byte)'H' => Key.Home,
                (byte)'F' => Key.End,
                (byte)'P' => Key.F1,
                (byte)'Q' => Key.F2,
                (byte)'R' => Key.F3,
                (byte)'S' => Key.F4,
                _ => Key.None
            };
            terminalEvent = new KeyEvent(key, KeyModifiers.None, null);
            return true;
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

        MouseButton button = scroll
            ? (encoded & 0b1) == 0
                ? MouseButton.ScrollUp
                : MouseButton.ScrollDown
            : btn switch
            {
                0 => MouseButton.Left,
                1 => MouseButton.Middle,
                2 => MouseButton.Right,
                _ => MouseButton.None
            };

        KeyModifiers modifiers = KeyModifiers.None;
        if (shift)
        {
            modifiers |= KeyModifiers.Shift;
        }

        if (alt)
        {
            modifiers |= KeyModifiers.Alt;
        }

        if (ctrl)
        {
            modifiers |= KeyModifiers.Control;
        }

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
            _ => Key.None
        };
    }

    internal static bool TryParseKittyKeySequence(
        byte[] buffer,
        ref int offset,
        int length,
        out TerminalEvent? terminalEvent)
    {
        terminalEvent = null;

        if (offset >= length || buffer[offset] != 0x1B)
        {
            return false;
        }

        if (offset + 2 > length || buffer[offset + 1] != '[')
        {
            return false;
        }

        int scan = offset + 2;
        int paramStart = scan;
        while (scan < length && (buffer[scan] < 0x40 || buffer[scan] > 0x7E))
        {
            scan++;
        }

        if (scan >= length)
        {
            return false;
        }

        byte finalByte = buffer[scan];

        if (finalByte != 0x75)
        {
            return false;
        }

        string rawParam = Encoding.ASCII.GetString(buffer, paramStart, scan - paramStart);
        rawParam = rawParam.Replace("\0", "");

        if (rawParam.Length == 0)
        {
            return false;
        }

        // Kitty query/response — consume but don't emit
        if (rawParam[0] == '?' || rawParam[0] == '>' || rawParam[0] == '<')
        {
            offset = scan + 1;
            terminalEvent = null;
            return true;
        }

        string[] parts = rawParam.Split(';');

        // First parameter: key-code:shifted-key:base-layout-key
        string[] firstParts = parts[0].Split(':');
        if (!int.TryParse(firstParts[0], out int keyCode))
        {
            return false;
        }

        int? shiftedKey = null;
        if (firstParts.Length > 1 && firstParts[1].Length > 0)
        {
            if (int.TryParse(firstParts[1], out int sk))
            {
                shiftedKey = sk;
            }
        }

        KeyModifiers modifiers = KeyModifiers.None;
        KeyEventType eventType = KeyEventType.Press;

        if (parts.Length >= 2)
        {
            string[] secondParts = parts[1].Split(':');
            if (int.TryParse(secondParts[0], out int modifierValue))
            {
                int decMod = modifierValue - 1;
                if ((decMod & 1) != 0)
                {
                    modifiers |= KeyModifiers.Shift;
                }

                if ((decMod & 2) != 0)
                {
                    modifiers |= KeyModifiers.Alt;
                }

                if ((decMod & 4) != 0)
                {
                    modifiers |= KeyModifiers.Control;
                }
            }

            if (secondParts.Length > 1 && int.TryParse(secondParts[1], out int eventTypeValue))
            {
                eventType = eventTypeValue switch
                {
                    2 => KeyEventType.Repeat,
                    3 => KeyEventType.Release,
                    _ => KeyEventType.Press
                };
            }
        }

        string? textAsCodepoints = null;
        if (parts.Length > 2)
        {
            textAsCodepoints = parts[2];
        }

        offset = scan + 1;

        Key key = MapKittyKeyCode(keyCode);
        string? resolvedText = ResolveText(keyCode, modifiers, shiftedKey, textAsCodepoints);

        // Kitty report_text can encode pure text events with key-code 0
        // (for example text produced by layout/IME handling). Treat printable
        // resolved text as character input even when the physical key code
        // itself is Key.None, otherwise shifted/text-producing keys can be
        // silently ignored by widgets that only consume Key.Character.
        if (key == Key.None && IsPrintableText(resolvedText))
        {
            key = Key.Character;
        }

        Rune? rune = null;
        if (key == Key.Character)
        {
            if (keyCode is >= 32 and < 0x110000)
            {
                try
                {
                    rune = new Rune(keyCode);
                }
                catch (ArgumentOutOfRangeException) { }
            }
            else if (resolvedText is not null)
            {
                StringRuneEnumerator enumerator = resolvedText.EnumerateRunes().GetEnumerator();
                if (enumerator.MoveNext())
                {
                    rune = enumerator.Current;
                }
            }
        }

        terminalEvent = new KeyEvent(key, modifiers, rune)
        {
            KeyCode = keyCode,
            ResolvedText = resolvedText,
            EventType = eventType
        };
        return true;
    }

    private static bool IsPrintableText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (Rune rune in text.EnumerateRunes())
        {
            if (rune.Value < 32 || rune.Value == 127)
            {
                return false;
            }
        }

        return true;
    }

    private static Key MapKittyKeyCode(int keyCode)
    {
        if (keyCode >= 0xE000 && keyCode <= 0xF8FF)
        {
            if (KittyPuaKeyMap.TryGetValue(keyCode, out Key mapped))
            {
                return mapped;
            }

            return Key.None;
        }

        return keyCode switch
        {
            9 => Key.Tab,
            13 => Key.Enter,
            27 => Key.Escape,
            127 => Key.Backspace,
            8 => Key.Backspace,
            >= 1 and <= 26 => Key.Character,
            >= 32 and < 0x110000 => Key.Character,
            _ => Key.None
        };
    }

    internal static string? ResolveText(
        int keyCode,
        KeyModifiers modifiers,
        int? shiftedKey,
        string? textAsCodepoints)
    {
        // 1. Explicit text from terminal (report_text flag)
        if (textAsCodepoints is not null && textAsCodepoints.Length > 0)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (string part in textAsCodepoints.Split(':'))
                {
                    if (int.TryParse(part, out int cp))
                    {
                        sb.Append(char.ConvertFromUtf32(cp));
                    }
                }

                if (sb.Length > 0)
                {
                    return sb.ToString();
                }
            }
            catch { }
        }

        // 2. Shifted key available and shift active
        if (shiftedKey.HasValue && modifiers.HasFlag(KeyModifiers.Shift))
        {
            try
            {
                return char.ConvertFromUtf32(shiftedKey.Value);
            }
            catch (ArgumentOutOfRangeException) { }
        }

        // 3. Control characters: Ctrl+A → "\x01"
        if (keyCode is >= 1 and <= 26 && modifiers.HasFlag(KeyModifiers.Control))
        {
            return ((char)keyCode).ToString();
        }

        // 4. Printable character from key code
        if ((keyCode >= 32 && keyCode < 127) || (keyCode > 127 && keyCode < 0x110000))
        {
            try
            {
                return char.ConvertFromUtf32(keyCode);
            }
            catch (ArgumentOutOfRangeException) { }
        }

        return null;
    }

    // ── Windows modifier key state polling ──

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static KeyModifiers ReadWindowsKeyModifiers()
    {
        KeyModifiers modifiers = KeyModifiers.None;
        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0)
        {
            modifiers |= KeyModifiers.Shift;
        }

        if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0)
        {
            modifiers |= KeyModifiers.Control;
        }

        if ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
        {
            modifiers |= KeyModifiers.Alt;
        }

        return modifiers;
    }
}
