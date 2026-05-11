namespace Omicron.Core.Text;

/// <summary>
/// Absolute position inside the <see cref="Utf8TextStore"/>.
/// </summary>
public readonly record struct TextPosition(long GlobalByteOffset, int ChunkIndex, int ChunkByteOffset);
