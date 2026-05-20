using System.Text;
using Omicron.Core.Rendering;
using Omicron.Core.Text;

namespace Omicron.CLI.Tui;

/// <summary>
///     Simple status bar that renders model name (left), provider (center),
///     and token count (right) into a <see cref="TerminalFrame" />.
/// </summary>
public static class SimpleStatusBar
{
    /// <summary>Render the status bar into the last row of the frame.</summary>
    public static void Render(
        TerminalFrame frame,
        string modelName,
        string provider,
        string rightText)
    {
        int row = frame.Height - 1;
        if (row < 0)
        {
            return;
        }

        TextStyle style = TextStyle.Inverted;

        // Clear the row
        for (int col = 0; col < frame.Width; col++)
        {
            frame[row, col] = new RenderCell
            {
                Glyph = GlyphRef.Ascii((byte)' '),
                Width = 1,
                Style = style
            };
        }

        // Left: model name
        if (!string.IsNullOrEmpty(modelName))
        {
            byte[] leftBytes = Encoding.UTF8.GetBytes($" {modelName} ");
            frame.SetText(row, 0, leftBytes, style);
        }

        // Center: provider (using display width, not string length)
        if (!string.IsNullOrEmpty(provider))
        {
            int providerWidth = GetDisplayWidth(provider);
            int centerCol = (frame.Width - providerWidth) / 2;
            if (centerCol < 0)
            {
                centerCol = 0;
            }

            byte[] centerBytes = Encoding.UTF8.GetBytes(provider);
            frame.SetText(row, centerCol, centerBytes, style);
        }

        // Right: token count or status (using display width)
        if (!string.IsNullOrEmpty(rightText))
        {
            int rightWidth = GetDisplayWidth(rightText);
            int rightCol = frame.Width - rightWidth - 1;
            if (rightCol < 0)
            {
                rightCol = 0;
            }

            byte[] rightBytes = Encoding.UTF8.GetBytes($" {rightText} ");
            frame.SetText(row, rightCol, rightBytes, style);
        }
    }

    /// <summary>Compute the terminal display width of a string (CJK-aware).</summary>
    private static int GetDisplayWidth(string text)
    {
        int width = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            width += CellWidthCalculator.GetWidth(rune);
        }

        return width;
    }
}
