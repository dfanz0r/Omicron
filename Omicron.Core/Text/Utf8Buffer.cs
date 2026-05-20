using System.Buffers;
using System.Text;
using Omicron.Core.Content;

namespace Omicron.Core.Text;

/// <summary>
/// Transient pooled UTF-8 byte buffer. Rents from <see cref="ArrayPool{T}.Shared"/>
/// and returns the buffer on <see cref="Dispose"/>.
///
/// Use for short-lived formatting, one-shot <see cref="Utf8String"/> construction,
/// or temporary storage where a full <see cref="Utf8Builder"/> is overkill.
///
/// Not for long-lived content — use <see cref="Utf8ContentBuffer"/> for that.
/// </summary>
public sealed class Utf8Buffer : IDisposable
{
    private const int DefaultCapacity = 4096;

    private byte[] _buffer;
    private int _written;
    private bool _disposed;

    /// <summary>Number of bytes written. Throws after dispose.</summary>
    public int WrittenCount
    {
        get
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Utf8Buffer));
            return _written;
        }
    }

    /// <summary>Written content as a span.</summary>
    public ReadOnlySpan<byte> WrittenSpan
    {
        get
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Utf8Buffer));
            return _buffer.AsSpan(0, _written);
        }
    }

    // WrittenMemory is intentionally omitted.
    // ReadOnlyMemory<byte> over a rented array can outlive the buffer and observe
    // stale/corrupt data after Grow() or Dispose(). Use WrittenSpan instead.

    /// <summary>Create a buffer with default capacity.</summary>
    public Utf8Buffer()
    {
        _buffer = ArrayPool<byte>.Shared.Rent(DefaultCapacity);
        _written = 0;
    }

    /// <summary>Create a buffer with the given initial capacity.
    /// The actual rented array may be larger at the pool's discretion.</summary>
    public Utf8Buffer(int initialCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 1));
        _written = 0;
    }

    /// <summary>Returns the rented buffer to the pool.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = Array.Empty<byte>();
        _written = 0;
    }

    /// <summary>Copy <paramref name="data"/> into the buffer.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Utf8Buffer));
        EnsureCapacity(data.Length);
        data.CopyTo(_buffer.AsSpan(_written));
        _written += data.Length;
    }

    /// <summary>Append a single byte.</summary>
    public void Append(byte b)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Utf8Buffer));
        if (_written >= _buffer.Length)
            Grow(1);
        _buffer[_written++] = b;
    }

    /// <summary>Reserve capacity for at least <paramref name="additionalBytes"/> more bytes.</summary>
    private void EnsureCapacity(int additionalBytes)
    {
        int needed = _written + additionalBytes;
        if (needed > _buffer.Length)
            Grow(additionalBytes);
    }

    private void Grow(int sizeHint)
    {
        int nextSize = Math.Max(_buffer.Length * 2, _written + sizeHint);
        var newBuffer = ArrayPool<byte>.Shared.Rent(nextSize);
        _buffer.AsSpan(0, _written).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }

    /// <summary>Copy written content to a span.</summary>
    public bool TryCopyTo(Span<byte> destination, out int bytesWritten)
    {
        var span = WrittenSpan;
        if (destination.Length < span.Length)
        {
            bytesWritten = 0;
            return false;
        }
        span.CopyTo(destination);
        bytesWritten = span.Length;
        return true;
    }

    /// <summary>Return written content as a <c>byte[]</c>.</summary>
    public byte[] ToArray()
    {
        var span = WrittenSpan;
        if (span.Length == 0) return Array.Empty<byte>();
        return span.ToArray();
    }

    /// <summary>Create an <see cref="Utf8String"/> from the written content.</summary>
    public Utf8String ToUtf8String()
    {
        var span = WrittenSpan;
        if (span.Length == 0) return Utf8String.Empty;
        return Utf8String.FromUtf8(span);
    }

    /// <summary>Copy written content to an <see cref="IBufferWriter{T}"/>.</summary>
    public void CopyTo<TBufferWriter>(ref TBufferWriter writer)
        where TBufferWriter : IBufferWriter<byte>
    {
        var span = WrittenSpan;
        if (span.Length == 0) return;
        var dest = writer.GetSpan(span.Length);
        span.CopyTo(dest);
        writer.Advance(span.Length);
    }

    /// <summary>Decode written content to <see cref="string"/>.</summary>
    public override string ToString()
    {
        var span = WrittenSpan;
        if (span.Length == 0) return string.Empty;
        return Encoding.UTF8.GetString(span);
    }
}
