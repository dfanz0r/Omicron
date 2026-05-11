using System.Text;

namespace Omicron.Core.Rendering;

/// <summary>
/// A reference to a displayable glyph without owning a string.
/// ASCII characters are stored inline (fast path). Non-ASCII glyphs
/// reference an entry in a <see cref="GlyphInternTable"/>.
/// </summary>
public readonly record struct GlyphRef
{
    private readonly uint _value;

    private GlyphRef(uint value) => _value = value;

    /// <summary>Create a glyph reference from an ASCII byte.</summary>
    public static GlyphRef Ascii(byte ascii) => new(ascii);

    /// <summary>Create a glyph reference from an intern table ID.</summary>
    public static GlyphRef Interned(int internId) => new((uint)internId | 0x80000000);

    /// <summary>Replacement character glyph (uses ASCII '?' as fallback).</summary>
    public static GlyphRef Replacement => Ascii((byte)'?');

    /// <summary>Whether this is an inline ASCII glyph.</summary>
    public bool IsAscii => (_value & 0x80000000) == 0;

    /// <summary>The ASCII byte value (valid only if <see cref="IsAscii"/> is true).</summary>
    public byte AsciiValue => (byte)_value;

    /// <summary>The intern table ID (valid only if <see cref="IsAscii"/> is false).</summary>
    public int InternId => (int)(_value & 0x7FFFFFFF);
}
