namespace Omicron.Core.Text;

/// <summary>
///     An append-only store for UTF-8 bytes backed by a linked chain of pooled
///     <see cref="TextChunk" /> instances (~8 KB each). Thread-safe for append.
/// </summary>
public sealed class Utf8TextStore : IDisposable
{
    /// <summary>Default chunk size: 8 KB.</summary>
    public const int DefaultChunkSize = 8192;

    private readonly object _appendLock = new();

    private readonly int _chunkSize;
    private readonly List<TextChunk> _chunks = [];
    private TextChunk? _current;
    private long _lengthBytes;

    public Utf8TextStore(int chunkSize = DefaultChunkSize)
    {
        if (chunkSize < 64)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize),
                "Chunk size must be at least 64 bytes.");
        }

        _chunkSize = chunkSize;
    }

    /// <summary>Total bytes appended to this store.</summary>
    public long LengthBytes => Volatile.Read(ref _lengthBytes);

    /// <summary>Number of chunks allocated.</summary>
    public int ChunkCount => _chunks.Count;

    /// <summary>Iterate all chunks (for zero-copy enumeration).</summary>
    internal IEnumerable<TextChunk> Chunks
    {
        get
        {
            // Return a snapshot under lock to avoid torn reads
            lock (_appendLock)
            {
                return _chunks.ToArray();
            }
        }
    }

    /// <summary>Release all pooled chunk buffers to the ArrayPool.</summary>
    public void Dispose()
    {
        lock (_appendLock)
        {
            foreach (TextChunk chunk in _chunks)
            {
                chunk.Dispose();
            }

            _chunks.Clear();
            _current = null;
        }
    }

    /// <summary>
    ///     Append UTF-8 bytes to the store. Thread-safe.
    ///     Returns the position of the first appended byte.
    /// </summary>
    public TextPosition Append(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            lock (_appendLock)
            {
                return new TextPosition(_lengthBytes, Math.Max(0, _chunks.Count - 1), 0);
            }
        }

        int totalAppended = 0;
        TextPosition firstPosition = default;
        bool first = true;

        while (totalAppended < utf8.Length)
        {
            lock (_appendLock)
            {
                EnsureCurrentChunk();

                int written = _current!.Append(utf8.Slice(totalAppended));
                if (written == 0)
                {
                    // Current chunk full; next loop iteration will create a new one
                    continue;
                }

                if (first)
                {
                    firstPosition = new TextPosition(_lengthBytes,
                        _current.Index,
                        _current.Length - written);
                    first = false;
                }

                totalAppended += written;
                _lengthBytes += written;
            }
        }

        return firstPosition;
    }

    /// <summary>
    ///     Read a contiguous slice of bytes. If the slice fits within a single chunk,
    ///     returns a zero-copy <see cref="ReadOnlyMemory{byte}" />. Cross-chunk slices
    ///     are copied into a new byte array.
    /// </summary>
    public ReadOnlyMemory<byte> Slice(long byteOffset, int byteLength)
    {
        if (byteOffset < 0 || byteLength < 0)
        {
            throw new ArgumentOutOfRangeException("Offset and length must be non-negative.");
        }

        if (byteOffset + byteLength > LengthBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength),
                "Requested slice extends past the end of the store.");
        }

        if (byteLength == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        // Find the starting chunk
        int startChunkIndex = FindChunkIndex(byteOffset, out int chunkOffset);

        // If it fits entirely within one chunk, return zero-copy
        TextChunk startChunk = _chunks[startChunkIndex];
        if (byteOffset + byteLength <= startChunk.GlobalByteStart + startChunk.Length)
        {
            int localStart = chunkOffset;
            return startChunk.AsMemory().Slice(localStart, byteLength);
        }

        // Cross-chunk: copy into a new byte array (GC collects it normally)
        byte[] copy = new byte[byteLength];
        int copied = 0;
        int ci = startChunkIndex;
        long remainingOffset = byteOffset;
        int remaining = byteLength;

        while (remaining > 0 && ci < _chunks.Count)
        {
            TextChunk chunk = _chunks[ci];
            long chunkEnd = chunk.GlobalByteStart + chunk.Length;
            int localStart = (int)(remainingOffset - chunk.GlobalByteStart);
            int available = (int)(chunkEnd - remainingOffset);
            int toCopy = Math.Min(remaining, available);

            if (toCopy > 0)
            {
                chunk.AsMemory().Slice(localStart, toCopy).Span.CopyTo(copy.AsSpan(copied));
                copied += toCopy;
                remainingOffset += toCopy;
                remaining -= toCopy;
            }

            ci++;
        }

        return copy.AsMemory(0, byteLength);
    }

    /// <summary>Get an individual chunk by index.</summary>
    internal TextChunk GetChunk(int chunkIndex)
    {
        lock (_appendLock)
        {
            return _chunks[chunkIndex];
        }
    }

    private void EnsureCurrentChunk()
    {
        if (_current == null || _current.FreeCapacity == 0)
        {
            long globalStart = _current == null ? 0 : _current.GlobalByteStart + _current.Length;
            int newIndex = _current == null ? 0 : _current.Index + 1;
            _current = new TextChunk(newIndex, globalStart, _chunkSize);
            _chunks.Add(_current);
        }
    }

    private int FindChunkIndex(long byteOffset, out int chunkOffset)
    {
        // Linear search from end — typical access is near the end
        for (int i = _chunks.Count - 1; i >= 0; i--)
        {
            TextChunk c = _chunks[i];
            if (byteOffset >= c.GlobalByteStart && byteOffset < c.GlobalByteStart + c.Length)
            {
                chunkOffset = (int)(byteOffset - c.GlobalByteStart);
                return i;
            }
        }

        // Fallback: forward search (offset at chunk boundary)
        for (int i = 0; i < _chunks.Count; i++)
        {
            TextChunk c = _chunks[i];
            if (
                byteOffset >= c.GlobalByteStart
                && (i + 1 >= _chunks.Count || byteOffset < _chunks[i + 1].GlobalByteStart)
            )
            {
                chunkOffset = (int)(byteOffset - c.GlobalByteStart);
                return i;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(byteOffset),
            "Byte offset not found in any chunk.");
    }

    /// <summary>
    ///     Copy all stored bytes into a single byte array.
    /// </summary>
    public byte[] ToArray()
    {
        byte[] result = new byte[LengthBytes];
        int offset = 0;

        lock (_appendLock)
        {
            foreach (TextChunk chunk in _chunks)
            {
                int len = chunk.Length;
                chunk.AsMemory().Slice(0, len).Span.CopyTo(result.AsSpan(offset));
                offset += len;
            }
        }

        return result;
    }
}
