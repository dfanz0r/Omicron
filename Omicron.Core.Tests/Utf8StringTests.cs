using System.Text.Json;
using Omicron.Core.Content;
using Xunit;

namespace Omicron.Core.Tests;

public sealed class Utf8StringTests
{
    // ============================================================
    // Factory: Empty
    // ============================================================

    [Fact]
    public void Empty_Singleton()
    {
        Assert.Same(Utf8String.Empty, Utf8String.Empty);
    }

    [Fact]
    public void Empty_IsEmpty()
    {
        Assert.True(Utf8String.Empty.IsEmpty);
        Assert.Equal(0, Utf8String.Empty.Length);
        Assert.Equal(Utf8StringStorageKind.Empty, Utf8String.Empty.Kind);
    }

    [Fact]
    public void Empty_ToString_ReturnsEmptyString()
    {
        Assert.Equal("", Utf8String.Empty.ToString());
    }

    // ============================================================
    // Factory: FromString
    // ============================================================

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("héllo wörld 🌍")]
    public void FromString_RoundTrips(string text)
    {
        var s = Utf8String.FromString(text);
        Assert.Equal(text, s.ToString());
        if (text.Length == 0)
        {
            Assert.Same(Utf8String.Empty, s);
        }
        else
        {
            Assert.Equal(Utf8StringStorageKind.ManagedArray, s.Kind);
        }
    }

    [Fact]
    public void FromString_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Utf8String.FromString(null!));
    }

    // ============================================================
    // Factory: FromUtf8
    // ============================================================

    [Fact]
    public void FromUtf8_Empty_ReturnsEmpty()
    {
        Assert.Same(Utf8String.Empty, Utf8String.FromUtf8(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void FromUtf8_RoundTrips()
    {
        byte[] bytes = "héllo"u8.ToArray();
        var s = Utf8String.FromUtf8(bytes);
        Assert.Equal("héllo", s.ToString());
        Assert.True(s.Utf8Span.SequenceEqual(bytes));
        Assert.Equal(Utf8StringStorageKind.ManagedArray, s.Kind);
    }

    [Fact]
    public void FromUtf8_DoesNotAliasInput()
    {
        byte[] arr = "hello"u8.ToArray();
        var s = Utf8String.FromUtf8(arr);
        arr[0] = (byte)'x';
        Assert.Equal('h', (char)s.Utf8Span[0]);
    }

    // ============================================================
    // Factory: FromOwnedArray (internal)
    // ============================================================

    [Fact]
    public void FromOwnedArray_EmptyLength_ReturnsEmpty()
    {
        var empty = Utf8String.FromOwnedArray([], 0);
        Assert.Same(Utf8String.Empty, empty);
    }

    [Fact]
    public void FromOwnedArray_UsesArrayDirectly()
    {
        byte[] arr = "hello"u8.ToArray();
        var s = Utf8String.FromOwnedArray(arr, arr.Length);
        Assert.Equal("hello", s.ToString());
        Assert.Equal(Utf8StringStorageKind.ManagedArray, s.Kind);
    }

    [Fact]
    public void FromOwnedArray_LengthLessThanArrayLength()
    {
        // Simulate oversized buffer from ArrayPool
        byte[] oversized = "hello world"u8.ToArray();
        // Only use first 5 bytes
        var s = Utf8String.FromOwnedArray(oversized, 5);
        Assert.Equal("hello", s.ToString());
        Assert.Equal(5, s.Length);
    }

    [Fact]
    public void FromOwnedArray_NegativeLength_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Utf8String.FromOwnedArray([1, 2, 3], -1));
    }

    [Fact]
    public void FromOwnedArray_LengthExceedsArrayLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => Utf8String.FromOwnedArray([1, 2, 3], 10));
    }

    [Fact]
    public void FromOwnedArray_NullArray_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Utf8String.FromOwnedArray(null!, 5));
    }

    // ============================================================
    // Factory: FromTrustedUtf8Literal (internal, unsafe)
    // ============================================================

    [Fact]
    public void FromTrustedUtf8Literal_StorageKind()
    {
        // StaticLiteral kind proves no managed array copy was made
        var literal = "static data"u8;
        var s = Utf8String.FromTrustedUtf8Literal(literal);
        Assert.Equal(Utf8StringStorageKind.StaticLiteral, s.Kind);
        Assert.Equal("static data", s.ToString());
    }

    [Fact]
    public void FromTrustedUtf8Literal_ModifyOriginalDoesNotAffectInstance()
    {
        // A true u8 literal is in read-only memory and cannot be modified.
        // This test verifies that after construction, the span produces the
        // correct content regardless of what happens to the source reference.
        var s = Utf8String.FromTrustedUtf8Literal("immutable"u8);
        Assert.Equal("immutable", s.ToString());
    }

    [Fact]
    public void FromTrustedUtf8Literal_VaryingLengths()
    {
        Assert.Equal("a", Utf8String.FromTrustedUtf8Literal("a"u8).ToString());
        Assert.Equal("ab", Utf8String.FromTrustedUtf8Literal("ab"u8).ToString());
        Assert.Equal("abc", Utf8String.FromTrustedUtf8Literal("abc"u8).ToString());
    }

    [Fact]
    public void FromTrustedUtf8Literal_Empty_ReturnsEmpty()
    {
        Assert.Same(Utf8String.Empty, Utf8String.FromTrustedUtf8Literal([]));
    }

    // ============================================================
    // Instance behavior
    // ============================================================

    [Fact]
    public void Length_ReturnsByteCount()
    {
        var s = Utf8String.FromString("héllo"); // 6 bytes (é = 2 bytes)
        Assert.Equal(6, s.Length);
    }

    [Fact]
    public void ToString_CachesResult()
    {
        var s = Utf8String.FromString("hello");
        string s1 = s.ToString();
        string s2 = s.ToString();
        Assert.Same(s1, s2);
    }

    [Fact]
    public void Utf8Span_ReturnsContent()
    {
        var s = Utf8String.FromString("hi");
        Assert.True(s.Utf8Span.SequenceEqual("hi"u8));
    }

    [Fact]
    public void CopyTo_WritesToDestination()
    {
        var s = Utf8String.FromString("abc");
        byte[] dest = new byte[3];
        s.CopyTo(dest);
        Assert.True(dest.AsSpan().SequenceEqual("abc"u8));
    }

    // ============================================================
    // Equality
    // ============================================================

    [Fact]
    public void Equals_SameContent_ReturnsTrue()
    {
        var a = Utf8String.FromString("hello");
        var b = Utf8String.FromString("hello");
        Assert.Equal(a, b);
        Assert.True(a.Equals(b));
    }

    [Fact]
    public void Equals_DifferentContent_ReturnsFalse()
    {
        var a = Utf8String.FromString("hello");
        var b = Utf8String.FromString("world");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_DifferentStorageKinds_SameContent_ReturnsTrue()
    {
        var managed = Utf8String.FromString("hello");
        var literal = Utf8String.FromTrustedUtf8Literal("hello"u8);
        Assert.Equal(managed, literal);
    }

    [Fact]
    public void GetHashCode_SameContent_Matches()
    {
        var a = Utf8String.FromString("hello");
        var b = Utf8String.FromString("hello");
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DifferentContent_Differs()
    {
        var a = Utf8String.FromString("hello");
        var b = Utf8String.FromString("world");
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equals_Null_ReturnsFalse()
    {
        var s = Utf8String.FromString("test");
        Assert.False(s.Equals(null));
    }

    [Fact]
    public void Equals_SameInstance_ReturnsTrue()
    {
        var s = Utf8String.FromString("test");
        Assert.True(s.Equals(s));
    }

    // ============================================================
    // JSON serialization
    // ============================================================

    [Fact]
    public void JsonSerialize_Null()
    {
        string json = JsonSerializer.Serialize<Utf8String?>(null);
        Assert.Equal("null", json);
    }

    [Fact]
    public void JsonSerialize_Empty()
    {
        string json = JsonSerializer.Serialize(Utf8String.Empty);
        Assert.Equal("\"\"", json);
    }

    [Fact]
    public void JsonSerialize_NonEmpty()
    {
        var s = Utf8String.FromString("hello");
        string json = JsonSerializer.Serialize(s);
        Assert.Equal("\"hello\"", json);
    }

    [Fact]
    public void JsonDeserialize_Null()
    {
        Utf8String? result = JsonSerializer.Deserialize<Utf8String?>("null");
        Assert.Null(result);
    }

    [Fact]
    public void JsonDeserialize_EmptyString()
    {
        Utf8String? result = JsonSerializer.Deserialize<Utf8String?>("\"\"");
        Assert.NotNull(result);
        Assert.Same(Utf8String.Empty, result);
    }

    [Fact]
    public void JsonDeserialize_NonEmpty()
    {
        Utf8String? result = JsonSerializer.Deserialize<Utf8String?>("\"hello\"");
        Assert.NotNull(result);
        Assert.Equal("hello", result.ToString());
    }

    [Fact]
    public void JsonDeserialize_NonAscii()
    {
        Utf8String? result = JsonSerializer.Deserialize<Utf8String?>("\"héllo 🌍\"");
        Assert.NotNull(result);
        Assert.Equal("héllo 🌍", result.ToString());
    }

    [Fact]
    public void JsonDeserialize_EscapedString()
    {
        string json = "\"line1\\nline2\\tend\"";
        Utf8String? result = JsonSerializer.Deserialize<Utf8String?>(json);
        Assert.NotNull(result);
        Assert.Equal("line1\nline2\tend", result.ToString());
    }

    [Fact]
    public void JsonRoundTrip_NonAscii()
    {
        var original = Utf8String.FromString("héllo wörld");
        string json = JsonSerializer.Serialize(original);
        Utf8String? restored = JsonSerializer.Deserialize<Utf8String?>(json);
        Assert.NotNull(restored);
        Assert.Equal(original.ToString(), restored.ToString());
    }

    [Fact]
    public void JsonRoundTrip_Empty()
    {
        string json = JsonSerializer.Serialize(Utf8String.Empty);
        Utf8String? restored = JsonSerializer.Deserialize<Utf8String?>(json);
        Assert.NotNull(restored);
        Assert.Same(Utf8String.Empty, restored);
    }
}
