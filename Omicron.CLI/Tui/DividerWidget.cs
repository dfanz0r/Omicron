using System.Text;
using Omicron.Core.Rendering;
using Omicron.Core.Rendering.Layout;

namespace Omicron.CLI.Tui;

/// <summary>
///     A single-row horizontal divider drawn as repeating characters (e.g. '─')
///     across the terminal width, with an optional foreground gradient.
/// </summary>
public sealed class DividerWidget : ITuiWidget
{
    private Rect _bounds;

    /// <summary>The character to repeat across the divider (default '─').</summary>
    public char DividerChar { get; set; } = '─';

    /// <summary>
    ///     When true, each character gets a foreground colour interpolated across
    ///     <see cref="GradientStart" /> → <see cref="GradientEnd" />.
    ///     When false, the whole line uses <see cref="SolidStyle" />.
    /// </summary>
    public bool UseGradient { get; set; } = true;

    /// <summary>Gradient start colour (left side). Matches the status bar via <see cref="TuiColors" />.</summary>
    public (byte R, byte G, byte B) GradientStart { get; set; } = TuiColors.StatusBarGradientStart;

    /// <summary>Gradient end colour (right side). Matches the status bar via <see cref="TuiColors" />.</summary>
    public (byte R, byte G, byte B) GradientEnd { get; set; } = TuiColors.StatusBarGradientEnd;

    /// <summary>Fallback solid style when <see cref="UseGradient" /> is false.</summary>
    public TextStyle SolidStyle { get; set; } = TextStyle.ForegroundOnly(100, 100, 100);

    public Size Measure(Size available)
    {
        return new Size(available.Width, 1);
    }

    public void Arrange(Rect bounds)
    {
        _bounds = bounds;
    }

    public void Render(RenderContext context)
    {
        int row = _bounds.Y;
        int width = _bounds.Width;
        if (width <= 0)
        {
            return;
        }

        if (!UseGradient)
        {
            string line = new(DividerChar, width);
            context.DrawText(_bounds.X, row, Encoding.UTF8.GetBytes(line), SolidStyle);
            return;
        }

        // Draw each character with its own foreground colour from the gradient.
        // The divider char (e.g. U+2500 ─) is non-ASCII so it must be interned.
        RenderCell[] cells = context.Frame.Cells;
        int frameWidth = context.Frame.Width;
        int baseIdx = row * frameWidth + _bounds.X;

        byte[] dividerUtf8 = Encoding.UTF8.GetBytes(DividerChar.ToString());
        GlyphRef dividerGlyph =
            dividerUtf8.Length == 1 && dividerUtf8[0] < 0x80
                ? GlyphRef.Ascii(dividerUtf8[0])
                : GlyphRef.Interned(context.Frame.GlyphTable.Intern(dividerUtf8));

        // Pre-allocate gradient stops outside the loop to avoid repeated stackalloc
        Span<ColorStop> gradientStops = stackalloc ColorStop[2];
        gradientStops[0] = new ColorStop(0.0, GradientStart.R, GradientStart.G, GradientStart.B);
        gradientStops[1] = new ColorStop(1.0, GradientEnd.R, GradientEnd.G, GradientEnd.B);

        for (int col = 0; col < width; col++)
        {
            double t = width > 1 ? col / (double)(width - 1) : 0.0;
            (byte r, byte g, byte b) = GradientHelper.Sample(gradientStops, t);

            cells[baseIdx + col] = new RenderCell
            {
                Glyph = dividerGlyph,
                Width = 1,
                Style = new TextStyle(r, g, b, 0, 0, 0, false, false, false)
            };
        }
    }
}
