namespace Omicron.Core.Rendering;

/// <summary>
///     A single cell in the <see cref="TerminalFrame" />.
///     Uses <see cref="GlyphRef" /> for memory-efficient storage.
///     Struct for dense array layout (no GC pressure).
/// </summary>
public struct RenderCell
{
    /// <summary>The glyph to display.</summary>
    public GlyphRef Glyph;

    /// <summary>Display width: 0 (continuation cell), 1 (narrow), or 2 (wide).</summary>
    public byte Width;

    /// <summary>Text style (colors, bold, italic, underline).</summary>
    public TextStyle Style;

    /// <summary>True if this cell should be skipped during rendering (empty or continuation).</summary>
    public readonly bool IsEmpty =>
        Width == 0 || (Width >= 1 && Glyph.IsAscii && Glyph.AsciiValue == (byte)' ');

    /// <summary>Create a default empty cell.</summary>
    public static RenderCell Empty =>
        new()
        {
            Glyph = GlyphRef.Ascii((byte)' '),
            Width = 1,
            Style = TextStyle.Default
        };
}
