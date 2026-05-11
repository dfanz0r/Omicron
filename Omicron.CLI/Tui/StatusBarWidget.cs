using Omicron.Core.Rendering;
using Omicron.Core.Rendering.Layout;
using Omicron.Core.Text;

namespace Omicron.CLI.Tui;

/// <summary>
/// Renders a single-row status bar with a Windows-2000-style gold gradient
/// background. The gradient transitions from dark amber on the left through
/// bright gold to safety-orange on the right. Text is drawn in very dark
/// brown for readability across the full gradient.
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
    // Values come from TuiColors so they stay in sync with DividerWidget.
    private static (byte R, byte G, byte B) GradientStart => TuiColors.StatusBarGradientStart;
    private static (byte R, byte G, byte B) GradientEnd => TuiColors.StatusBarGradientEnd;

    /// <summary>Text foreground gradient: dark brown → medium brown.
    /// Each character gets its own interpolated shade for smooth readability.</summary>
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

        // Fill the bar with a per-column gold gradient.
        GradientHelper.Fill(context, _bounds, GradientDirection.Horizontal,
            GradientStart, GradientEnd);

        // Left: model name (per-character foreground gradient)
        if (!string.IsNullOrEmpty(ModelName))
            GradientHelper.DrawText(context, _bounds.X, row, $" {ModelName} ",
                GradientDirection.Horizontal, TextStart, TextEnd);

        // Center: provider
        if (!string.IsNullOrEmpty(ProviderName))
        {
            int displayWidth = GetDisplayWidth(ProviderName);
            int centerCol = _bounds.X + (width - displayWidth) / 2;
            if (centerCol >= 0)
                GradientHelper.DrawText(context, centerCol, row, ProviderName,
                    GradientDirection.Horizontal, TextStart, TextEnd);
        }

        // Right: status text
        if (!string.IsNullOrEmpty(StatusText))
        {
            int displayWidth = GetDisplayWidth(StatusText);
            int rightCol = _bounds.Right - displayWidth - 2;
            if (rightCol >= _bounds.X)
                GradientHelper.DrawText(context, rightCol, row, $" {StatusText} ",
                    GradientDirection.Horizontal, TextStart, TextEnd);
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
