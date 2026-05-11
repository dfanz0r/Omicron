namespace Omicron.Core.Rendering.Layout;

/// <summary>
/// Horizontal stack layout widget. Arranges children from left to right.
/// </summary>
public sealed class HStack : ITuiWidget
{
    private readonly List<ITuiWidget> _children = [];
    private Rect _bounds;

    /// <summary>Children widgets stacked horizontally.</summary>
    public IReadOnlyList<ITuiWidget> Children => _children;

    /// <summary>Add a child widget to the stack.</summary>
    public void Add(ITuiWidget widget) => _children.Add(widget);

    public Size Measure(Size available)
    {
        int totalWidth = 0;
        int maxHeight = 0;

        foreach (var child in _children)
        {
            var size = child.Measure(new Size(available.Width - totalWidth, available.Height));
            totalWidth += size.Width;
            maxHeight = Math.Max(maxHeight, size.Height);
        }

        return new Size(totalWidth, maxHeight);
    }

    public void Arrange(Rect bounds)
    {
        _bounds = bounds;
        int x = bounds.X;

        foreach (var child in _children)
        {
            var size = child.Measure(new Size(bounds.Width - (x - bounds.X), bounds.Height));
            child.Arrange(new Rect(x, bounds.Y, size.Width, bounds.Height));
            x += size.Width;
        }
    }

    public void Render(RenderContext context)
    {
        foreach (var child in _children)
            child.Render(context);
    }
}
