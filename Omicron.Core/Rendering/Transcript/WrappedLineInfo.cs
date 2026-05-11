namespace Omicron.Core.Rendering.Transcript;

/// <summary>
/// Information about a single wrapped row in the transcript layout cache.
/// </summary>
public readonly record struct WrappedLineInfo(
    BlockId BlockId,
    long ByteStart,
    int ByteLength,
    int CellStart,
    int CellWidth,
    bool IsContinuation,
    int LogicalLineIndex);
