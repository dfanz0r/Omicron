namespace Omicron.Core.Rendering.Layout;

/// <summary>
/// Vertical stack layout widget. Arranges children from top to bottom.
/// Supports flex and fixed sizing via <see cref="FlexSizeWidget"/> and <see cref="FixedSizeWidget"/>.
/// Each child renders in its own clipped <see cref="RenderContext"/>.
/// </summary>
public sealed class VStack : ITuiWidget
{
    private readonly List<ITuiWidget> _children = [];
    private readonly List<Rect> _childBounds = [];
    private Rect _bounds;

    /// <summary>Vertical gap in rows between children (default 0).</summary>
    public int Gap { get; set; } = 0;

    /// <summary>Children widgets stacked vertically.</summary>
    public IReadOnlyList<ITuiWidget> Children => _children;

    /// <summary>Add a child widget to the stack.</summary>
    public void Add(ITuiWidget widget) => _children.Add(widget);

    public Size Measure(Size available)
    {
        int totalHeight = 0;
        int maxWidth = 0;
        int gapTotal = Math.Max(0, _children.Count - 1) * Gap;

        foreach (var child in _children)
        {
            var size = child.Measure(new Size(available.Width, available.Height - totalHeight - gapTotal));
            totalHeight += size.Height;
            maxWidth = Math.Max(maxWidth, size.Width);
        }

        return new Size(maxWidth, totalHeight + gapTotal);
    }

    public void Arrange(Rect bounds)
    {
        _bounds = bounds;
        _childBounds.Clear();

        // First pass: measure all children to compute consumed and remaining space
        int flexCount = 0;
        int fixedHeight = 0;
        var measuredHeights = new int[_children.Count];

        for (int i = 0; i < _children.Count; i++)
        {
            var child = _children[i];
            if (child is FixedSizeWidget fixedChild)
            {
                measuredHeights[i] = Math.Min(fixedChild.Height, bounds.Height);
                fixedHeight += measuredHeights[i];
            }
            else if (child is FlexSizeWidget)
            {
                flexCount++;
                measuredHeights[i] = 0; // determined in second pass
            }
            else
            {
                var size = child.Measure(new Size(bounds.Width, bounds.Height - fixedHeight));
                measuredHeights[i] = size.Height;
                fixedHeight += size.Height;
            }
        }

        // Second pass: arrange everyone in order with correct Y positions
        int totalGap = Math.Max(0, _children.Count - 1) * Gap;
        int remainingHeight = Math.Max(0, bounds.Height - fixedHeight - totalGap);
        int flexHeight = flexCount > 0 ? remainingHeight / flexCount : 0;
        int y = bounds.Y;

        for (int i = 0; i < _children.Count; i++)
        {
            var child = _children[i];
            int h;

            if (child is FlexSizeWidget)
            {
                h = Math.Min(flexHeight, remainingHeight);
                remainingHeight -= h;
            }
            else
            {
                h = measuredHeights[i];
            }

            var childRect = new Rect(bounds.X, y, bounds.Width, h);
            child.Arrange(childRect);
            _childBounds.Add(childRect);
            y += h + Gap;
        }
    }

    public void Render(RenderContext context)
    {
        var emptyCell = new RenderCell
        {
            Glyph = GlyphRef.Ascii((byte)' '),
            Width = 1,
            Style = TextStyle.Default,
        };

        for (int i = 0; i < _children.Count; i++)
        {
            Rect childRect = i < _childBounds.Count ? _childBounds[i] : _bounds;
            var childContext = new RenderContext(
                context.Frame,
                context.Clip.Intersect(childRect),
                context.DefaultStyle);
            _children[i].Render(childContext);

            // Clear gap rows between children so stale content doesn't bleed
            if (Gap > 0 && i < _children.Count - 1)
            {
                int gapY = childRect.Bottom;
                if (gapY < context.Clip.Bottom)
                {
                    var gapRect = new Rect(childRect.X, gapY, childRect.Width, Math.Min(Gap, context.Clip.Bottom - gapY));
                    context.FillRect(gapRect, emptyCell);
                }
            }
        }
    }
}
