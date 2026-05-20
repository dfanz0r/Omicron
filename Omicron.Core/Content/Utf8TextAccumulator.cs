namespace Omicron.Core.Content;

/// <summary>
///     Accumulates UTF-8 text without forcing intermediate string allocations.
///     Provides backward-compatible <see cref="ToString()" /> for callers that
///     still need strings, but can also transfer ownership of the underlying
///     <see cref="Utf8ContentBuffer" /> for UTF-8-native consumption.
/// </summary>
internal sealed class Utf8TextAccumulator : IDisposable
{
    private Utf8ContentBuffer _buffer;

    public Utf8TextAccumulator()
    {
        _buffer = new Utf8ContentBuffer();
    }

    /// <summary>Current byte length of accumulated text.</summary>
    public int Length => _buffer.Length;

    public void Dispose()
    {
        _buffer.Dispose();
    }

    /// <summary>Append a string (or null) to the accumulator.</summary>
    public void Append(string? text)
    {
        if (text is null)
        {
            return;
        }

        _buffer.Mutate(text, static (ref b, t) => b.Append(t));
    }

    /// <summary>Append raw UTF-8 bytes to the accumulator.</summary>
    public void AppendUtf8(ReadOnlySpan<byte> utf8)
    {
        _buffer.ThrowIfDisposed();
        _buffer.Builder.AppendLiteral(utf8);
    }

    /// <summary>Append a character to the accumulator.</summary>
    public void Append(char c)
    {
        _buffer.ThrowIfDisposed();
        _buffer.Builder.Append(c);
    }

    /// <summary>Clear the accumulator (dispose old buffer, create new).</summary>
    public void Clear()
    {
        _buffer.Dispose();
        _buffer = new Utf8ContentBuffer();
    }

    /// <summary>Materialize accumulated text as a string. The accumulator keeps its content.</summary>
    public override string ToString()
    {
        return _buffer.ToString();
    }

    /// <summary>
    ///     Transfer ownership of the underlying <see cref="Utf8ContentBuffer" />.
    ///     After this call, the accumulator is empty and must not be used until reinitialized.
    ///     Caller is responsible for disposing the returned buffer.
    /// </summary>
    public Utf8ContentBuffer ToOwnedBuffer()
    {
        Utf8ContentBuffer result = _buffer;
        _buffer = new Utf8ContentBuffer();
        return result;
    }
}
