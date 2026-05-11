using Omicron.Core.Rendering;
using Omicron.Core.Rendering.Layout;
using Omicron.Core.Text;

namespace Omicron.CLI.Tui;

/// <summary>
/// Renders a single-row status bar with a Windows-2000-style gold gradient
/// background. The gradient transitions from dark amber on the left through
/// bright gold to safety-orange on the right. Text is pure black for maximum
/// contrast (WCAG 5.5:1) so it renders identically in VS Code, Windows Terminal,
/// and other emulators regardless of their minimum-contrast settings.
/// </summary>
public sealed class StatusBarWidget : ITuiWidget
{
    /// <summary>Model name to display on the left.</summary>
    public string ModelName { get; set; } = "";

    /// <summary>Provider name to display in the center.</summary>
    public string ProviderName { get; set; } = "";

    /// <summary>Status text to display on the right (e.g., token count).</summary>
    public string StatusText { get; set; } = "";

    private Rect _bounds;

    // ── Gradient colours (amber → gold → safety-orange) ──
    private static (byte R, byte G, byte B) GradientStart => TuiColors.StatusBarGradientStart;
    private static (byte R, byte G, byte B) GradientEnd => TuiColors.StatusBarGradientEnd;

    /// <summary>Text foreground colour. Pure black (0,0,0) — kept as a
    /// gradient property for API compatibility but start == end.</summary>
    private static (byte R, byte G, byte B) TextStart => TuiColors.StatusBarTextStart;
    private static (byte R, byte G, byte B) TextEnd => TuiColors.StatusBarTextEnd;

    public Size Measure(Size available) => new(available.Width, 1);

    public void Arrange(Rect bounds)
    {
        _bounds = bounds;
    }

    public void Render(RenderContext context)
    {
        int row = _bounds.Y;
        int width = _bounds.Width;
        if (width <= 0) return;

        // Fill the bar with BOTH background and foreground gradients.
        // Every cell gets the full-bar text gradient so empty spaces blend
        // seamlessly with text — no harsh white → dark jumps.
        FillWithTextGradient(context, _bounds);

        // Stamp glyphs on top, preserving the existing gradient style.
        // Left: model name
        if (!string.IsNullOrEmpty(ModelName))
            GradientHelper.DrawGlyphsOnly(context, _bounds.X, row, $" {ModelName} ");

        // Center: provider
        if (!string.IsNullOrEmpty(ProviderName))
        {
            int displayWidth = GetDisplayWidth(ProviderName);
            int centerCol = _bounds.X + (width - displayWidth) / 2;
            if (centerCol >= 0)
                GradientHelper.DrawGlyphsOnly(context, centerCol, row, ProviderName);
        }

        // Right: status text
        if (!string.IsNullOrEmpty(StatusText))
        {
            int displayWidth = GetDisplayWidth(StatusText);
            int rightCol = _bounds.Right - displayWidth - 2;
            if (rightCol >= _bounds.X)
                GradientHelper.DrawGlyphsOnly(context, rightCol, row, $" {StatusText} ");
        }
    }

    /// <summary>
    /// Fill the bar with the background gradient AND the text foreground
    /// gradient in one pass. This ensures empty cells share the same smooth
    /// foreground colour as text cells — no white gaps between text elements.
    /// </summary>
    private static void FillWithTextGradient(RenderContext context, Rect bounds)
    {
        var clipped = bounds.Intersect(context.Clip);
        if (clipped.Width <= 0 || clipped.Height <= 0) return;

        var cells = context.Frame.Cells;
        int frameWidth = context.Frame.Width;

        Span<ColorStop> bgStops = stackalloc ColorStop[2];
        bgStops[0] = new ColorStop(0.0, GradientStart.R, GradientStart.G, GradientStart.B);
        bgStops[1] = new ColorStop(1.0, GradientEnd.R, GradientEnd.G, GradientEnd.B);

        Span<ColorStop> fgStops = stackalloc ColorStop[2];
        fgStops[0] = new ColorStop(0.0, TextStart.R, TextStart.G, TextStart.B);
        fgStops[1] = new ColorStop(1.0, TextEnd.R, TextEnd.G, TextEnd.B);

        for (int row = clipped.Y; row < clipped.Bottom; row++)
        {
            int baseIdx = row * frameWidth + clipped.X;
            for (int col = clipped.X; col < clipped.Right; col++)
            {
                double t = bounds.Width > 1 ? (col - bounds.X) / (double)(bounds.Width - 1) : 0.0;
                var (bgR, bgG, bgB) = GradientHelper.Sample(bgStops, t);
                var (fgR, fgG, fgB) = GradientHelper.Sample(fgStops, t);
                cells[baseIdx++] = new RenderCell
                {
                    Glyph = GlyphRef.Ascii((byte)' '),
                    Width = 1,
                    Style = new TextStyle(fgR, fgG, fgB, bgR, bgG, bgB, false, false, false),
                };
            }
        }
    }

    private static int GetDisplayWidth(string text)
    {
        int width = 0;
        foreach (var rune in text.EnumerateRunes())
            width += CellWidthCalculator.GetWidth(rune);
        return width;
    }
}
