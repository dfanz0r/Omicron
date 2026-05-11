namespace Omicron.Core.Text;

/// <summary>
/// A grapheme cluster annotated with its terminal display width (in columns).
/// Produced by combining <see cref="GraphemeSegmenter"/> and <see cref="CellWidthCalculator"/>.
/// </summary>
public readonly record struct TerminalCluster(
    long ByteOffset,
    int ByteLength,
    int RuneCount,
    int CellWidth);
