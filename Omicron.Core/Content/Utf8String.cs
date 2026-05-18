using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Omicron.Core.Content;

/// <summary>
/// Immutable UTF-8 text with explicit ownership. Supports three storage modes:
/// <see cref="Utf8StringStorageKind.Empty"/> (singleton), <see cref="Utf8StringStorageKind.ManagedArray"/>
/// (owned <c>byte[]</c>), and <see cref="Utf8StringStorageKind.StaticLiteral"/> (trusted compiler-emitted
/// <c>u8</c> literal data, zero-copy, internal construction only).
/// String materialization via <see cref="ToString()"/> is explicit and lazy-cached.
/// </summary>
[JsonConverter(typeof(Utf8StringJsonConverter))]
public sealed class Utf8String : IEquatable<Utf8String>
{
    /// <summary>Empty singleton instance.</summary>
    public static Utf8String Empty { get; } = new();

    private readonly Utf8StringStorageKind _kind;

    // ManagedArray storage
    private readonly byte[]? _managedBytes;

    // StaticLiteral storage (pointer to module static data)
    private readonly unsafe byte* _ptr;

    private readonly int _length;
    private string? _decoded;

    private Utf8String()
    {
        _kind = Utf8StringStorageKind.Empty;
        unsafe { _ptr = null; }
        _length = 0;
    }

    private Utf8String(byte[] bytes, int length)
    {
        _kind = Utf8StringStorageKind.ManagedArray;
        _managedBytes = bytes;
        _length = length;
        unsafe { _ptr = null; }
    }

    private unsafe Utf8String(byte* ptr, int length)
    {
        _kind = Utf8StringStorageKind.StaticLiteral;
        _ptr = ptr;
        _length = length;
    }

    /// <summary>Storage kind of this instance.</summary>
    public Utf8StringStorageKind Kind => _kind;

    /// <summary>Byte length of the UTF-8 content.</summary>
    public int Length => _length;

    /// <summary>Whether this instance contains no text.</summary>
    public bool IsEmpty => _length == 0;

    /// <summary>UTF-8 byte span of the text content. Zero-alloc.</summary>
    public ReadOnlySpan<byte> Utf8Span => _kind switch
    {
        Utf8StringStorageKind.ManagedArray => new ReadOnlySpan<byte>(_managedBytes, 0, _length),
        Utf8StringStorageKind.StaticLiteral => GetSpanFromLiteral(),
        _ => ReadOnlySpan<byte>.Empty,
    };

    private unsafe ReadOnlySpan<byte> GetSpanFromLiteral() => new ReadOnlySpan<byte>(_ptr, _length);

    // ── Public factories ──

    /// <summary>Create from a string. Encodes to UTF-8 into an owned managed array.</summary>
    public static Utf8String FromString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return Empty;
        var bytes = Encoding.UTF8.GetBytes(text);
        return new Utf8String(bytes, bytes.Length);
    }

    /// <summary>Create from a UTF-8 byte span. Copies into an owned managed array.</summary>
    public static Utf8String FromUtf8(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return Empty;
        var arr = new byte[utf8.Length];
        utf8.CopyTo(arr);
        return new Utf8String(arr, arr.Length);
    }

    /// <summary>
    /// Implicit conversion from a UTF-8 byte span for convenience-oriented call sites,
    /// especially tests and other places already constructing a fresh <see cref="Utf8String"/>.
    /// This conversion always copies the bytes into managed owned storage.
    /// It is not the zero-copy static-literal path.
    /// Prefer explicit factories in performance-sensitive or ownership-sensitive code.
    /// </summary>
    public static implicit operator Utf8String(ReadOnlySpan<byte> utf8) => FromUtf8(utf8);

    /// <summary>Create from a <see cref="Utf8ContentBuffer"/>. Copies content and disposes the buffer.</summary>
    public static Utf8String FromBuffer(Utf8ContentBuffer buffer)
    {
        var span = buffer.AsSpan();
        if (span.IsEmpty)
        {
            buffer.Dispose();
            return Empty;
        }
        var arr = new byte[span.Length];
        span.CopyTo(arr);
        buffer.Dispose();
        return new Utf8String(arr, arr.Length);
    }

    // ── Internal factories ──

    /// <summary>
    /// Create from a pre-allocated owned byte array. Takes ownership — the array must not
    /// be modified or shared after this call. <paramref name="length"/> may be less than
    /// <c>bytes.Length</c> to allow oversized buffers (e.g. from ArrayPool).
    /// </summary>
    internal static Utf8String FromOwnedArray(byte[] bytes, int length)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), "length must be non-negative");
        if (length == 0) return Empty;
        if (length > bytes.Length)
            throw new ArgumentException("length exceeds array length", nameof(length));
        return new Utf8String(bytes, length);
    }

    /// <summary>
    /// Create from a trusted compiler-emitted <c>u8</c> literal. Zero-copy — the pointer
    /// points to module static data that is valid for the process lifetime.
    /// This MUST only be called with spans backed by true compile-time <c>u8</c> literals,
    /// not spans backed by stack memory, movable managed arrays, or pooled buffers.
    /// </summary>
    internal static unsafe Utf8String FromTrustedUtf8Literal(ReadOnlySpan<byte> literal)
    {
        if (literal.IsEmpty) return Empty;
        ref byte first = ref MemoryMarshal.GetReference(literal);
        byte* ptr = (byte*)Unsafe.AsPointer(ref first);
        return new Utf8String(ptr, literal.Length);
    }

    // ── Accessors ──

    /// <summary>Materialize to a string. Allocates on first call; result is cached.</summary>
    public override string ToString()
    {
        if (_decoded is null)
        {
            _decoded = _kind switch
            {
                Utf8StringStorageKind.ManagedArray => Encoding.UTF8.GetString(_managedBytes!, 0, _length),
                Utf8StringStorageKind.StaticLiteral => Encoding.UTF8.GetString(Utf8Span),
                _ => string.Empty,
            };
        }
        return _decoded;
    }

    /// <summary>Copy the UTF-8 content into a destination span.</summary>
    public void CopyTo(Span<byte> destination)
    {
        Utf8Span.CopyTo(destination);
    }

    /// <summary>Append the UTF-8 content to a builder without allocating a string.</summary>
    public void AppendTo(ref Cysharp.Text.Utf8ValueStringBuilder builder)
    {
        builder.AppendLiteral(Utf8Span);
    }

    /// <summary>Write the text as a JSON string value.</summary>
    public void WriteJsonString(Utf8JsonWriter writer)
    {
        writer.WriteStringValue(Utf8Span);
    }

    // ── Equality ──

    public bool Equals(Utf8String? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        if (_length != other._length) return false;
        return Utf8Span.SequenceEqual(other.Utf8Span);
    }

    public override bool Equals(object? obj) => obj is Utf8String other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        var span = Utf8Span;
        hash.AddBytes(span);
        return hash.ToHashCode();
    }
}

/// <summary>Storage backing kind for <see cref="Utf8String"/>.</summary>
public enum Utf8StringStorageKind : byte
{
    /// <summary>Zero-length singleton.</summary>
    Empty,
    /// <summary>Owned managed byte array.</summary>
    ManagedArray,
    /// <summary>Trusted compiler-emitted <c>u8</c> literal data (module static).</summary>
    StaticLiteral,
}

/// <summary>
/// JSON converter for <see cref="Utf8String"/>. Persists as a JSON string (not base64).
/// </summary>
public sealed class Utf8StringJsonConverter : JsonConverter<Utf8String>
{
    public override Utf8String? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        // Estimate upper bound: raw span length (escaped sequences shrink on decode).
        int maxLen = reader.HasValueSequence
            ? (int)reader.ValueSequence.Length
            : reader.ValueSpan.Length;

        if (maxLen == 0)
            return Utf8String.Empty;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(maxLen);
        try
        {
            int written = reader.CopyString(buffer.AsSpan());
            if (written == 0)
                return Utf8String.Empty;
            var bytes = new byte[written];
            buffer.AsSpan(0, written).CopyTo(bytes);
            return Utf8String.FromOwnedArray(bytes, bytes.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public override void Write(Utf8JsonWriter writer, Utf8String value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        if (value.IsEmpty)
        {
            writer.WriteStringValue("");
            return;
        }
        writer.WriteStringValue(value.Utf8Span);
    }
}
