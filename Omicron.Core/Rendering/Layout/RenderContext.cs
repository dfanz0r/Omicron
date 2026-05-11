using System.Text;

namespace Omicron.Core.Rendering.Layout;

/// <summary>
/// Drawing context for <see cref="ITuiWidget.Render"/>.
/// Holds a reference to the target <see cref="TerminalFrame"/> and
/// provides clipped drawing helpers.
/// </summary>
public sealed class RenderContext
{
    /// <summary>The terminal frame being drawn into.</summary>
    public TerminalFrame Frame { get; }

    /// <summary>Clip rectangle — drawing outside this rect is suppressed.</summary>
    public Rect Clip { get; }

    /// <summary>Default text style for this context.</summary>
    public TextStyle DefaultStyle { get; }

    public RenderContext(TerminalFrame frame, Rect clip, TextStyle defaultStyle)
    {
        Frame = frame;
        Clip = clip;
        DefaultStyle = defaultStyle;
    }

    /// <summary>Draw UTF-8 text at the given position, clipped to the clip rect.</summary>
    public void DrawText(int x, int y, ReadOnlySpan<byte> utf8, TextStyle style)
    {
        if (y < Clip.Y || y >= Clip.Bottom)
            return;

        if (x >= Clip.Right)
            return;

        int maxWidth = Clip.Right - x;
        if (maxWidth <= 0)
            return;

        // Clip the text to the available width
        if (utf8.Length > maxWidth * 4) // rough upper bound
            utf8 = utf8[..(maxWidth * 4)];

        int actualRow = Clip.Y + (y - Clip.Y);
        Frame.SetText(actualRow, x, utf8, style);
    }

    /// <summary>Convenience: draw a string at the given position.</summary>
    public void DrawText(int x, int y, string text, TextStyle style)
        => DrawText(x, y, Encoding.UTF8.GetBytes(text), style);

    /// <summary>Fill a rect with a repeated cell.</summary>
    public void FillRect(Rect rect, RenderCell cell)
    {
        var clipped = rect.Intersect(Clip);
        Frame.FillRect(clipped.Y, clipped.X, clipped.Width, clipped.Height, cell);
    }
}
