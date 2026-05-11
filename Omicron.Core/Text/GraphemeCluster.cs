namespace Omicron.Core.Text;

/// <summary>
/// A grapheme cluster found by <see cref="GraphemeSegmenter"/>.
/// Identified by its byte offset and length in the source UTF-8, and the number of
/// Unicode scalar values (runes) it contains.
/// </summary>
public readonly record struct GraphemeCluster(long ByteOffset, int ByteLength, int RuneCount);
