using System.Reflection;
using System.Text;
using Omicron.Core.Rendering;
using Xunit;

namespace Omicron.Core.Tests;

/// <summary>
///     Unit tests for kitty keyboard protocol parsing and text resolution.
///     Tests are written against the internal static methods of
///     <see cref="TerminalInputParser" /> to validate protocol conformance.
/// </summary>
public class KittyKeyboardProtocolTests
{
    // ================================================================
    // TryParseKittyKeySequence — basic sequences
    // ================================================================

    [Fact]
    public void Parse_Enter_NoModifiers()
    {
        // CSI 13 u → Enter with no modifiers
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[13u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        Assert.NotNull(evt);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.Enter, ke.Key);
        Assert.Equal(KeyModifiers.None, ke.Modifiers);
        Assert.Equal(13, ke.KeyCode);
        Assert.Equal(KeyEventType.Press, ke.EventType);
        Assert.Null(ke.ResolvedText);
        Assert.Equal(buffer.Length, offset);
    }

    [Fact]
    public void Parse_ShiftEnter()
    {
        // CSI 13;2 u → Shift+Enter
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[13;2u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.Enter, ke.Key);
        Assert.Equal(KeyModifiers.Shift, ke.Modifiers);
        Assert.Equal(13, ke.KeyCode);
    }

    [Fact]
    public void Parse_CtrlD()
    {
        // CSI 4;5 u → Ctrl+D
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[4;5u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.Character, ke.Key);
        Assert.Equal(KeyModifiers.Control, ke.Modifiers);
        Assert.Equal(4, ke.KeyCode);
        Assert.Equal("\x04", ke.ResolvedText);
    }

    [Fact]
    public void Parse_Shift1()
    {
        // CSI 49;2 u → Shift+1 (no shifted-key sub-parameter)
        // Without report_alternates, ResolvedText is the unshifted key code.
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[49;2u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.Character, ke.Key);
        Assert.Equal(KeyModifiers.Shift, ke.Modifiers);
        Assert.Equal(49, ke.KeyCode);
        Assert.Equal("1", ke.ResolvedText);
    }

    [Fact]
    public void Parse_Shift1_WithShiftedKey()
    {
        // CSI 49:33;2 u → Shift+1 with shifted-key sub-parameter (33 = '!')
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[49:33;2u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.Character, ke.Key);
        Assert.Equal(KeyModifiers.Shift, ke.Modifiers);
        Assert.Equal(49, ke.KeyCode);
        Assert.Equal("!", ke.ResolvedText);
    }

    [Fact]
    public void Parse_Backspace()
    {
        // CSI 127 u → Backspace (DEL)
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[127u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.Backspace, ke.Key);
        Assert.Equal(KeyModifiers.None, ke.Modifiers);
        Assert.Equal(127, ke.KeyCode);
    }

    [Fact]
    public void Parse_Tab()
    {
        // CSI 9 u → Tab
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[9u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.Tab, ke.Key);
        Assert.Equal(9, ke.KeyCode);
    }

    [Fact]
    public void Parse_Escape()
    {
        // CSI 27 u → Escape
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[27u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.Escape, ke.Key);
        Assert.Equal(27, ke.KeyCode);
    }

    // ================================================================
    // Event types (press/repeat/release)
    // ================================================================

    [Fact]
    public void Parse_RepeatEvent()
    {
        // CSI 13;1:2 u → Enter, no modifiers (xterm modifier 1 = no mods), repeat event
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[13;1:2u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.Enter, ke.Key);
        Assert.Equal(KeyModifiers.None, ke.Modifiers);
        Assert.Equal(KeyEventType.Repeat, ke.EventType);
    }

    [Fact]
    public void Parse_ReleaseEvent()
    {
        // CSI 13;1:3 u → Enter, no modifiers, release event
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[13;1:3u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.Enter, ke.Key);
        Assert.Equal(KeyEventType.Release, ke.EventType);
    }

    // ================================================================
    // Modifier combinations
    // ================================================================

    [Fact]
    public void Parse_AltX()
    {
        // CSI 120;3 u → Alt+X (x = 120)
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[120;3u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.Character, ke.Key);
        Assert.Equal(KeyModifiers.Alt, ke.Modifiers);
        Assert.Equal(120, ke.KeyCode);
        Assert.Equal("x", ke.ResolvedText);
    }

    [Fact]
    public void Parse_ShiftAltCtrlA()
    {
        // CSI 97;8 u → Shift+Alt+Ctrl+A (value 8 = 1+7, decMod = 7 → shift+alt+ctrl)
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[97;8u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        // 8 in xterm encoding = 1 + 7, so actual modifiers = 7 = shift(1)+alt(2)+ctrl(4)
        Assert.Equal(KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Control, ke.Modifiers);
        Assert.Equal(Key.Character, ke.Key);
    }

    // ================================================================
    // Text-as-codepoints (report_text flag)
    // ================================================================

    [Fact]
    public void Parse_TextAsCodepoints()
    {
        // CSI 49;2;33 u → Shift+1 with explicit text-as-codepoints "33" = '!'
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[49;2;33u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.Character, ke.Key);
        Assert.Equal(KeyModifiers.Shift, ke.Modifiers);
        Assert.Equal("!", ke.ResolvedText); // From text-as-codepoints
    }

    [Fact]
    public void Parse_MultiCodepointText()
    {
        // CSI 97;2;65:66 u → Shift+A with text "AB" (65='A', 66='B')
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[97;2;65:66u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal("AB", ke.ResolvedText);
    }

    // ================================================================
    // PUA functional key codes
    // ================================================================

    [Fact]
    public void Parse_PuaDownArrow()
    {
        // CSI 61441;2 u → Shift+Down (PUA 0xF001 = Down, with shift modifier)
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[61441;2u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.Down, ke.Key);
        Assert.Equal(KeyModifiers.Shift, ke.Modifiers);
    }

    [Fact]
    public void Parse_PuaF1()
    {
        // CSI 61456 u → F1 (PUA 0xF010)
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[61456u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.F1, ke.Key);
    }

    [Fact]
    public void Parse_PuaF12()
    {
        // CSI 61467 u → F12 (PUA 0xF01B)
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[61467u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.F12, ke.Key);
    }

    // ================================================================
    // Malformed / non-kitty sequences
    // ================================================================

    [Fact]
    public void Parse_NonKittyCsi_ReturnsFalse()
    {
        // CSI A (cursor up) — not a kitty sequence
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[A");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.False(result);
        Assert.Null(evt);
        Assert.Equal(0, offset); // offset unchanged
    }

    [Fact]
    public void Parse_LegacySs3ApplicationCursorDown()
    {
        byte[] buffer = Encoding.ASCII.GetBytes("\x1bOB");
        (bool result, int offset, TerminalEvent? evt) = InvokeLegacyEscapeParser(buffer);

        Assert.True(result);
        Assert.Equal(buffer.Length, offset);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.Down, ke.Key);
    }

    [Fact]
    public void Parse_KittyQueryResponse_ConsumedButNoEvent()
    {
        // CSI ?1 u → kitty query response
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[?1u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        Assert.Null(evt); // Consumed but no event emitted
        Assert.Equal(buffer.Length, offset);
    }

    [Fact]
    public void Parse_PushResponse_ConsumedButNoEvent()
    {
        // CSI >1 u → kitty push response
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[>1u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        Assert.Null(evt);
    }

    [Fact]
    public void Parse_PopResponse_ConsumedButNoEvent()
    {
        // CSI <1 u → kitty pop response
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[<1u");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(result);
        Assert.Null(evt);
    }

    // ================================================================
    // Incomplete sequences
    // ================================================================

    [Fact]
    public void Parse_IncompleteSequence_ReturnsFalse()
    {
        // Just ESC[ without final byte
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.False(result);
        Assert.Null(evt);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void Parse_IncompleteParam_ReturnsFalse()
    {
        // ESC[13 without 'u'
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[13");
        int offset = 0;
        bool result = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.False(result);
        Assert.Equal(0, offset);
    }

    // ================================================================
    // ResolveText tests
    // ================================================================

    [Fact]
    public void ResolveText_CtrlA()
    {
        // Ctrl+A → "\x01"
        string? result = TerminalInputParser.ResolveText(1, KeyModifiers.Control, null, null);
        Assert.Equal("\x01", result);
    }

    [Fact]
    public void ResolveText_CtrlD()
    {
        // Ctrl+D → "\x04"
        string? result = TerminalInputParser.ResolveText(4, KeyModifiers.Control, null, null);
        Assert.Equal("\x04", result);
    }

    [Fact]
    public void ResolveText_CtrlShiftA()
    {
        // Ctrl+Shift+A → "\x01" (same as Ctrl+A, shift doesn't change control chars)
        string? result = TerminalInputParser.ResolveText(1,
            KeyModifiers.Control | KeyModifiers.Shift,
            null,
            null);
        Assert.Equal("\x01", result);
    }

    [Fact]
    public void ResolveText_PrintableChar()
    {
        // Just 'a' → "a"
        string? result = TerminalInputParser.ResolveText(97, KeyModifiers.None, null, null);
        Assert.Equal("a", result);
    }

    [Fact]
    public void ResolveText_WithShiftedKey()
    {
        // keyCode=49 ('1'), shiftedKey=33 ('!'), shift active → "!"
        string? result = TerminalInputParser.ResolveText(49, KeyModifiers.Shift, 33, null);
        Assert.Equal("!", result);
    }

    [Fact]
    public void ResolveText_ShiftedKeyWithoutShift()
    {
        // keyCode=49 ('1'), shiftedKey=33 ('!'), NO shift → should return the unshifted key
        // shiftedKey is only used when shift is active
        string? result = TerminalInputParser.ResolveText(49, KeyModifiers.None, 33, null);
        Assert.Equal("1", result);
    }

    [Fact]
    public void ResolveText_TextAsCodepointsPreferred()
    {
        // textAsCodepoints takes precedence over other resolution
        string? result = TerminalInputParser.ResolveText(49, KeyModifiers.Shift, 33, "65");
        Assert.Equal("A", result); // From text-as-codepoints, not shiftedKey
    }

    [Fact]
    public void ResolveText_NonPrintable()
    {
        // keyCode=13 (Enter) → null (not a printable character)
        string? result = TerminalInputParser.ResolveText(13, KeyModifiers.None, null, null);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveText_Backspace()
    {
        // keyCode=127 (DEL) → not printable (excluded from printable range), returns null
        string? result = TerminalInputParser.ResolveText(127, KeyModifiers.None, null, null);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveText_Shift1_WithoutShiftedKey_ReturnsKeyCode()
    {
        // Shift+1 with no shifted-key sub-parameter → returns unshifted key code "1"
        string? result = TerminalInputParser.ResolveText(49, KeyModifiers.Shift, null, null);
        Assert.Equal("1", result);
    }

    [Fact]
    public void ResolveText_ShiftA_WithoutShiftedKey_ReturnsKeyCode()
    {
        // Shift+A with no shifted-key sub-parameter → returns unshifted key code "a"
        string? result = TerminalInputParser.ResolveText(97, KeyModifiers.Shift, null, null);
        Assert.Equal("a", result);
    }

    private static (bool Result, int Offset, TerminalEvent? Event) InvokeLegacyEscapeParser(byte[] buffer)
    {
        var parser = new TerminalInputParser();
        MethodInfo? method = typeof(TerminalInputParser).GetMethod("TryParseEscapeSequence",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        object?[] args = [buffer, 0, buffer.Length, null];
        bool result = (bool)method.Invoke(parser, args)!;
        return (result, (int)args[1]!, (TerminalEvent?)args[3]);
    }

    // ================================================================
    // Parser integration: the kitty parser runs before the legacy parser
    // and legitimate kitty sequences should NOT fall through to legacy
    // ================================================================

    [Fact]
    public void KittyParser_Prevents_MapCsiSequence_Conflict()
    {
        // CSI 13 u should be parsed by kitty parser as Enter, NOT by
        // the legacy MapCsiSequence (which no longer has 0x75 entries).
        byte[] buffer = Encoding.ASCII.GetBytes("\x1b[13u");
        int offset = 0;
        bool kittyResult = TerminalInputParser.TryParseKittyKeySequence(buffer,
            ref offset,
            buffer.Length,
            out TerminalEvent? evt);

        Assert.True(kittyResult);
        KeyEvent ke = Assert.IsType<KeyEvent>(evt);
        Assert.Equal(Key.Enter, ke.Key);
        Assert.Equal(13, ke.KeyCode);
    }
}
