namespace Omicron.Core.Rendering;

/// <summary>
///     Terminal text style with 24-bit RGB colors and basic attributes.
///     MVP supports only TrueColor. Future: fall back to 256-color or 16-color.
/// </summary>
public readonly record struct TextStyle(
    byte FgR,
    byte FgG,
    byte FgB,
    byte BgR,
    byte BgG,
    byte BgB,
    bool Bold,
    bool Italic,
    bool Underline)
{
    /// <summary>Default style: white on black, no attributes.</summary>
    public static readonly TextStyle Default = new(255,
        255,
        255, // white fg
        0,
        0,
        0, // black bg
        false,
        false,
        false);

    /// <summary>Inverted style (black on white).</summary>
    public static readonly TextStyle Inverted = new(0,
        0,
        0, // black fg
        255,
        255,
        255, // white bg
        false,
        false,
        false);

    /// <summary>Style with only foreground set, default background.</summary>
    public static TextStyle ForegroundOnly(byte r, byte g, byte b, bool bold = false)
    {
        return new TextStyle(r, g, b, 0, 0, 0, bold, false, false);
    }

    /// <summary>Style with only background set, default foreground.</summary>
    public static TextStyle BackgroundOnly(byte r, byte g, byte b)
    {
        return new TextStyle(255, 255, 255, r, g, b, false, false, false);
    }
}
