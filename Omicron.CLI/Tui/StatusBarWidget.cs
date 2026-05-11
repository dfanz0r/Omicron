using System.Text;
using Omicron.Core.Rendering;
using Omicron.Core.Rendering.Layout;
using Omicron.Core.Text;

namespace Omicron.CLI.Tui;

/// <summary>
/// Renders a single-row status bar with model name (left),
/// provider (center), and status text (right).
/// Uses <see cref="CellWidthCalculator"/> for correct alignment of CJK text.
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

    public Size Measure(Size available) => new(available.Width, 1);

    public void Arrange(Rect bounds)
    {
        _bounds = bounds;
    }

    public void Render(RenderContext context)
    {
        int row = _bounds.Y;
        var style = TextStyle.Inverted;

        // Clear the row with inverted spaces
        var emptyCell = new RenderCell
        {
            Glyph = GlyphRef.Ascii((byte)' '),
            Width = 1,
            Style = style,
        };
        context.FillRect(_bounds, emptyCell);

        // Left: model name
        if (!string.IsNullOrEmpty(ModelName))
        {
            var leftBytes = Encoding.UTF8.GetBytes($" {ModelName} ");
            context.DrawText(_bounds.X, row, leftBytes, style);
        }

        // Center: provider (use display width for CJK alignment)
        if (!string.IsNullOrEmpty(ProviderName))
        {
            int displayWidth = GetDisplayWidth(ProviderName);
            int centerCol = _bounds.X + (_bounds.Width - displayWidth) / 2;
            if (centerCol >= 0)
            {
                var centerBytes = Encoding.UTF8.GetBytes(ProviderName);
                context.DrawText(centerCol, row, centerBytes, style);
            }
        }

        // Right: status text (use display width for CJK alignment)
        if (!string.IsNullOrEmpty(StatusText))
        {
            int displayWidth = GetDisplayWidth(StatusText);
            int rightCol = _bounds.Right - displayWidth - 1;
            if (rightCol >= _bounds.X)
            {
                var rightBytes = Encoding.UTF8.GetBytes($" {StatusText} ");
                context.DrawText(rightCol, row, rightBytes, style);
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
