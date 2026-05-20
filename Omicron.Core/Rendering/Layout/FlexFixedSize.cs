namespace Omicron.Core.Rendering.Layout;

/// <summary>
///     A fixed-size wrapper. The child receives exactly <see cref="Height" /> rows.
///     Useful for status bars, input lines, and other fixed-height panels.
/// </summary>
public sealed class FixedSizeWidget : ITuiWidget
{
    private Rect _bounds;

    /// <summary>The fixed height in rows.</summary>
    public int Height { get; init; }

    /// <summary>The child widget.</summary>
    public ITuiWidget Child { get; init; } = null!;

    public Size Measure(Size available)
    {
        Size childSize = Child.Measure(new Size(available.Width, Height));
        return new Size(childSize.Width, Height);
    }

    public void Arrange(Rect bounds)
    {
        _bounds = bounds;
        Child.Arrange(new Rect(bounds.X, bounds.Y, bounds.Width, Math.Min(Height, bounds.Height)));
    }

    public void Render(RenderContext context)
    {
        Child.Render(context);
    }
}

/// <summary>
///     A flex-size wrapper. The child receives remaining space after fixed-size
///     widgets have been allocated. Multiple flex children share remaining space equally.
/// </summary>
public sealed class FlexSizeWidget : ITuiWidget
{
    private Rect _bounds;

    /// <summary>Flex factor (not yet used; all flex children share equally for MVP).</summary>
    public int Flex { get; init; } = 1;

    /// <summary>The child widget.</summary>
    public ITuiWidget Child { get; init; } = null!;

    public Size Measure(Size available)
    {
        return Child.Measure(available);
    }

    public void Arrange(Rect bounds)
    {
        _bounds = bounds;
        Child.Arrange(bounds);
    }

    public void Render(RenderContext context)
    {
        Child.Render(context);
    }
}
