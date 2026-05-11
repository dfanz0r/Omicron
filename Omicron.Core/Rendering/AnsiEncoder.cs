using System.Buffers;
using System.Text;

namespace Omicron.Core.Rendering;

/// <summary>
/// Static methods that emit raw UTF-8 / ANSI escape sequences into an
/// <see cref="IBufferWriter{T}"/>. Uses UTF-8 literal spans for static
/// sequences. Emits 24-bit RGB everywhere; the caller should detect
/// terminal color depth via <see cref="TerminalCapabilities"/>.
/// </summary>
public static class AnsiEncoder
{
    // ── Screen ──

    /// <summary>Clear the entire screen and home the cursor.</summary>
    public static void ClearScreen(IBufferWriter<byte> output)
    {
        output.Write("\x1b[2J"u8);
        output.Write("\x1b[H"u8);
    }

    /// <summary>Move cursor to (row, col), 1-based.</summary>
    public static void SetCursorPosition(int row, int col, IBufferWriter<byte> output)
    {
        Span<byte> buf = stackalloc byte[32];
        int pos = 0;
        buf[pos++] = 0x1B;
        buf[pos++] = (byte)'[';
        pos += Utf8Formatter.WriteInt(row + 1, buf.Slice(pos));
        buf[pos++] = (byte)';';
        pos += Utf8Formatter.WriteInt(col + 1, buf.Slice(pos));
        buf[pos++] = (byte)'H';
        output.Write(buf.Slice(0, pos));
    }

    // ── Style ──

    /// <summary>
    /// Emit ANSI style codes transitioning from <paramref name="previous"/>
    /// to <paramref name="style"/>. If <paramref name="previous"/> is null,
    /// emits a full reset + complete style.
    /// If styles are identical, emits nothing.
    /// </summary>
    public static void SetStyle(TextStyle style, TextStyle? previous, IBufferWriter<byte> output)
    {
        if (previous == style)
            return;

        // If previous is null or the style is completely different, reset first
        if (previous is null)
        {
            ResetStyle(output);
            EmitFullStyle(style, output);
            return;
        }

        var prev = previous.Value;

        // Bold
        if (style.Bold != prev.Bold)
            output.Write(style.Bold ? "\x1b[1m"u8 : "\x1b[22m"u8);

        // Italic
        if (style.Italic != prev.Italic)
            output.Write(style.Italic ? "\x1b[3m"u8 : "\x1b[23m"u8);

        // Underline
        if (style.Underline != prev.Underline)
            output.Write(style.Underline ? "\x1b[4m"u8 : "\x1b[24m"u8);

        // Foreground color
        if (style.FgR != prev.FgR || style.FgG != prev.FgG || style.FgB != prev.FgB)
            WriteRgbSequence(38, style.FgR, style.FgG, style.FgB, output);

        // Background color
        if (style.BgR != prev.BgR || style.BgG != prev.BgG || style.BgB != prev.BgB)
            WriteRgbSequence(48, style.BgR, style.BgG, style.BgB, output);
    }

    private static void EmitFullStyle(TextStyle style, IBufferWriter<byte> output)
    {
        // Bold
        if (style.Bold)
            output.Write("\x1b[1m"u8);

        // Italic
        if (style.Italic)
            output.Write("\x1b[3m"u8);

        // Underline
        if (style.Underline)
            output.Write("\x1b[4m"u8);

        // Foreground
        WriteRgbSequence(38, style.FgR, style.FgG, style.FgB, output);

        // Background
        WriteRgbSequence(48, style.BgR, style.BgG, style.BgB, output);
    }

    private static void WriteRgbSequence(byte code, byte r, byte g, byte b, IBufferWriter<byte> output)
    {
        Span<byte> buf = stackalloc byte[32];
        int pos = 0;
        buf[pos++] = 0x1B;
        buf[pos++] = (byte)'[';
        pos += Utf8Formatter.WriteInt(code, buf.Slice(pos));
        buf[pos++] = (byte)';';
        buf[pos++] = (byte)'2';
        buf[pos++] = (byte)';';
        pos += Utf8Formatter.WriteInt(r, buf.Slice(pos));
        buf[pos++] = (byte)';';
        pos += Utf8Formatter.WriteInt(g, buf.Slice(pos));
        buf[pos++] = (byte)';';
        pos += Utf8Formatter.WriteInt(b, buf.Slice(pos));
        buf[pos++] = (byte)'m';
        output.Write(buf.Slice(0, pos));
    }

    /// <summary>Reset all style attributes to default.</summary>
    public static void ResetStyle(IBufferWriter<byte> output)
    {
        output.Write("\x1b[0m"u8);
    }

    // ── Glyph ──

    /// <summary>Write a glyph's UTF-8 bytes to the output.</summary>
    public static void WriteGlyph(GlyphRef glyph, IBufferWriter<byte> output, GlyphInternTable? internTable)
    {
        if (glyph.IsAscii)
        {
            // Single ASCII byte
            Span<byte> buf = stackalloc byte[1];
            buf[0] = glyph.AsciiValue;
            output.Write(buf);
        }
        else if (internTable is not null)
        {
            var utf8 = internTable.Resolve(glyph.InternId);
            if (!utf8.IsEmpty)
                output.Write(utf8);
        }
        else
        {
            // Fallback: replacement character
            output.Write("\uFFFD"u8);
        }
    }

    // ── Cursor ──

    /// <summary>Show the cursor.</summary>
    public static void ShowCursor(IBufferWriter<byte> output)
        => output.Write("\x1b[?25h"u8);

    /// <summary>Hide the cursor.</summary>
    public static void HideCursor(IBufferWriter<byte> output)
        => output.Write("\x1b[?25l"u8);

    // ── Synchronized Output (DEC 2026) ──

    /// <summary>Begin synchronized output (atomic frame update).</summary>
    public static void BeginSynchronizedOutput(IBufferWriter<byte> output)
        => output.Write("\x1b[?2026h"u8);

    /// <summary>End synchronized output.</summary>
    public static void EndSynchronizedOutput(IBufferWriter<byte> output)
        => output.Write("\x1b[?2026l"u8);
}

/// <summary>Zero-allocation integer-to-ASCII formatting into a UTF-8 span.</summary>
internal static class Utf8Formatter
{
    /// <summary>Write a non-negative integer as ASCII digits into the span. Returns bytes written.</summary>
    public static int WriteInt(int value, Span<byte> destination)
    {
        if (value == 0)
        {
            destination[0] = (byte)'0';
            return 1;
        }

        int pos = 0;
        int v = value;
        while (v > 0)
        {
            destination[pos++] = (byte)('0' + (v % 10));
            v /= 10;
        }

        // Reverse the digits in-place
        int left = 0, right = pos - 1;
        while (left < right)
        {
            (destination[left], destination[right]) = (destination[right], destination[left]);
            left++;
            right--;
        }
        return pos;
    }
}
