namespace Omicron.Core.Diff;

internal static class TextLineSplitter
{
    /// <summary>
    ///     Build an indexed UTF-8 line view over an existing buffer without decoding
    ///     or allocating per-line strings. The returned index preserves
    ///     <c>Split('\n')</c>-style trailing-empty-line behavior for buffers ending
    ///     in LF because diff output needs to observe final-newline changes.
    /// </summary>
    public static Utf8LineIndex CreateUtf8LineIndex(ReadOnlyMemory<byte> utf8Bytes)
    {
        return Utf8LineIndex.Create(utf8Bytes);
    }

    /// <summary>
    ///     Slice the next LF-terminated line from <paramref name="text" /> without
    ///     allocating. A trailing CR before LF is excluded from the returned line.
    ///     The caller owns <paramref name="index" /> and can resume enumeration by
    ///     passing it back to the next call.
    /// </summary>
    public static bool TryReadNextLine(
        ReadOnlySpan<char> text,
        ref int index,
        out ReadOnlySpan<char> line)
    {
        if ((uint)index > (uint)text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (index == text.Length)
        {
            line = default;
            return false;
        }

        int start = index;
        ReadOnlySpan<char> remaining = text[start..];
        int newlineOffset = remaining.IndexOf('\n');

        if (newlineOffset < 0)
        {
            line = remaining;
            index = text.Length;
            return true;
        }

        int end = start + newlineOffset;
        index = end + 1;

        if (end > start && text[end - 1] == '\r')
        {
            end--;
        }

        line = text.Slice(start, end - start);
        return true;
    }

    /// <summary>
    ///     Slice the next LF-terminated UTF-8 line from <paramref name="utf8Bytes" />
    ///     without allocating or decoding. A trailing CR before LF is excluded from
    ///     the returned line. The caller owns <paramref name="index" /> and can resume
    ///     enumeration by passing it back to the next call.
    /// </summary>
    public static bool TryReadNextLine(
        ReadOnlySpan<byte> utf8Bytes,
        ref int index,
        out ReadOnlySpan<byte> line)
    {
        if ((uint)index > (uint)utf8Bytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (index == utf8Bytes.Length)
        {
            line = default;
            return false;
        }

        int start = index;
        ReadOnlySpan<byte> remaining = utf8Bytes[start..];
        int newlineOffset = remaining.IndexOf((byte)'\n');

        if (newlineOffset < 0)
        {
            line = remaining;
            index = utf8Bytes.Length;
            return true;
        }

        int end = start + newlineOffset;
        index = end + 1;

        if (end > start && utf8Bytes[end - 1] == (byte)'\r')
        {
            end--;
        }

        line = utf8Bytes.Slice(start, end - start);
        return true;
    }
}

/// <summary>
///     Indexed, non-owning line view over a UTF-8 buffer. Stores only line-start
///     offsets so diff algorithms can retain random access without materializing
///     line strings or a list of line objects.
/// </summary>
internal readonly struct Utf8LineIndex
{
    private readonly ReadOnlyMemory<byte> _source;
    private readonly int[] _lineStarts;

    private Utf8LineIndex(ReadOnlyMemory<byte> source, int[] lineStarts)
    {
        _source = source;
        _lineStarts = lineStarts;
    }

    public int Count => _lineStarts.Length;

    public static Utf8LineIndex Empty { get; } =
        new(ReadOnlyMemory<byte>.Empty, Array.Empty<int>());

    public static Utf8LineIndex Create(ReadOnlyMemory<byte> source)
    {
        if (source.IsEmpty)
        {
            return Empty;
        }

        ReadOnlySpan<byte> span = source.Span;
        int lineCount = 1;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == (byte)'\n')
            {
                lineCount++;
            }
        }

        int[] starts = new int[lineCount];
        starts[0] = 0;
        int line = 1;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == (byte)'\n')
            {
                starts[line++] = i + 1;
            }
        }

        return new Utf8LineIndex(source, starts);
    }

    public ReadOnlySpan<byte> this[int index] => GetLine(index);

    public ReadOnlySpan<byte> GetLine(int index)
    {
        if ((uint)index >= (uint)_lineStarts.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        ReadOnlySpan<byte> span = _source.Span;
        int start = _lineStarts[index];
        int end = index + 1 < _lineStarts.Length ? _lineStarts[index + 1] : span.Length;

        if (end > start && span[end - 1] == (byte)'\n')
        {
            end--;
        }

        if (end > start && span[end - 1] == (byte)'\r')
        {
            end--;
        }

        return span.Slice(start, end - start);
    }

    internal Utf8LineKey GetKey(int index)
    {
        if ((uint)index >= (uint)_lineStarts.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        ReadOnlySpan<byte> span = _source.Span;
        int start = _lineStarts[index];
        int end = index + 1 < _lineStarts.Length ? _lineStarts[index + 1] : span.Length;

        if (end > start && span[end - 1] == (byte)'\n')
        {
            end--;
        }

        if (end > start && span[end - 1] == (byte)'\r')
        {
            end--;
        }

        return new Utf8LineKey(_source, start, end - start);
    }
}

internal readonly struct Utf8LineKey : IEquatable<Utf8LineKey>
{
    private readonly ReadOnlyMemory<byte> _source;
    private readonly int _start;
    private readonly int _length;
    private readonly int _hash;

    public Utf8LineKey(ReadOnlyMemory<byte> source, int start, int length)
    {
        _source = source;
        _start = start;
        _length = length;
        _hash = ComputeHash(source.Span.Slice(start, length));
    }

    private ReadOnlySpan<byte> Span => _source.Span.Slice(_start, _length);

    public bool Equals(Utf8LineKey other)
    {
        return _hash == other._hash && Span.SequenceEqual(other.Span);
    }

    public override bool Equals(object? obj)
    {
        return obj is Utf8LineKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return _hash;
    }

    private static int ComputeHash(ReadOnlySpan<byte> span)
    {
        var hash = new HashCode();
        foreach (byte b in span)
        {
            hash.Add(b);
        }

        return hash.ToHashCode();
    }
}
