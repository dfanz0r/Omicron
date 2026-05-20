namespace Omicron.Core.Rendering.Layout;

/// <summary>
///     A terminal UI widget that can measure, arrange, and render itself.
///     Follows the measure/arrange layout protocol (similar to WPF/Avalonia).
/// </summary>
public interface ITuiWidget
{
    /// <summary>
    ///     Measure the widget's desired size given available space.
    ///     Called during layout before <see cref="Arrange" />.
    /// </summary>
    Size Measure(Size available);

    /// <summary>
    ///     Assign the widget its final bounds within the layout.
    ///     Called after Measure and before <see cref="Render" />.
    /// </summary>
    void Arrange(Rect bounds);

    /// <summary>
    ///     Render the widget into the provided context.
    /// </summary>
    void Render(RenderContext context);
}
