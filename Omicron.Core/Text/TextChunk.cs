using System.Buffers;

namespace Omicron.Core.Text;

/// <summary>
/// A single append-only UTF-8 chunk backing <see cref="Utf8TextStore"/>.
/// Chunks are ~8 KB and use <see cref="ArrayPool{T}.Shared"/> for buffer rental.
/// </summary>
internal sealed class TextChunk
{
    private readonly byte[] _buffer;
    private bool _disposed;

    /// <summary>Stable chunk index in the store (0-based).</summary>
    public int Index { get; }

    /// <summary>Cumulative byte offset of this chunk's first byte in the store.</summary>
    public long GlobalByteStart { get; }

    /// <summary>Number of valid bytes in this chunk.</summary>
    public int Length { get; private set; }

    public TextChunk(int index, long globalByteStart, int chunkSize)
    {
        Index = index;
        GlobalByteStart = globalByteStart;
        _buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
    }

    /// <summary>Return a span over the valid bytes.</summary>
    public ReadOnlySpan<byte> AsSpan() => _buffer.AsSpan(0, Length);

    /// <summary>Return a memory over the valid bytes.</summary>
    public ReadOnlyMemory<byte> AsMemory() => _buffer.AsMemory(0, Length);

    /// <summary>Return how many bytes can still be appended.</summary>
    public int FreeCapacity => _buffer.Length - Length;

    /// <summary>
    /// Append UTF-8 bytes to this chunk. Returns the number of bytes
    /// actually written (may be less than <paramref name="source"/> length
    /// if the chunk is nearly full).
    /// </summary>
    public int Append(ReadOnlySpan<byte> source)
    {
        int toCopy = Math.Min(source.Length, FreeCapacity);
        if (toCopy > 0)
        {
            source.Slice(0, toCopy).CopyTo(_buffer.AsSpan(Length));
            Length += toCopy;
        }
        return toCopy;
    }

    /// <summary>Return the rented buffer to the shared pool.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }
}
