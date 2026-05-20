using System;
using System.Buffers;
using System.Text;
using Omicron.Core.Content;
using Omicron.Core.Text;
using Xunit;

namespace Omicron.Core.Tests;

public class Utf8BuilderTests
{
    // === Construction / Disposal ===

    [Fact]
    public void DefaultConstructor_CreatesUsableBuilder()
    {
        var builder = new Utf8Builder();
        Assert.Equal(0, builder.Length);
        Assert.False(builder.IsDisposed);
        builder.Dispose();
        Assert.True(builder.IsDisposed);
    }

    [Fact]
    public void CreateBuilder_FromFactory_IsUsable()
    {
        using var builder = Utf8Text.CreateBuilder();
        Assert.Equal(0, builder.Length);
        Assert.False(builder.IsDisposed);
    }

    [Fact]
    public void Dispose_MultipleTimes_IsSafe()
    {
        var builder = new Utf8Builder();
        builder.Dispose();
        builder.Dispose(); // should not throw
    }

    [Fact]
    public void DisposedBuilder_AppendLiteral_Throws()
    {
        var builder = new Utf8Builder();
        builder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => builder.AppendLiteral("x"u8));
    }

    [Fact]
    public void DisposedBuilder_AsSpan_Throws()
    {
        var builder = new Utf8Builder();
        builder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => builder.AsSpan());
    }

    // === AppendLiteral (raw UTF-8) ===

    [Fact]
    public void AppendLiteral_Empty_IsNoop()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.AppendLiteral(ReadOnlySpan<byte>.Empty);
        Assert.Equal(0, builder.Length);
    }

    [Fact]
    public void AppendLiteral_Ascii()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.AppendLiteral("Hello"u8);
        Assert.Equal("Hello", builder.ToString());
    }

    [Fact]
    public void AppendLiteral_MultiByteUtf8()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.AppendLiteral("héllo wörld"u8);
        Assert.Equal("héllo wörld", builder.ToString());
    }

    [Fact]
    public void AppendLiteral_MultipleCalls_Concat()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.AppendLiteral("Hello"u8);
        builder.AppendLiteral(" "u8);
        builder.AppendLiteral("World"u8);
        Assert.Equal("Hello World", builder.ToString());
    }

    // === Append (char) ===

    [Fact]
    public void AppendChar_Ascii()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append('c');
        Assert.Equal("c", builder.ToString());
    }

    [Fact]
    public void AppendChar_NonAscii()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append('\u00e9'); // é
        Assert.Equal("é", builder.ToString());
    }

    [Fact]
    public void AppendChar_HighCodePoint()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append('\u4e2d'); // 中 (CJK)
        Assert.Equal("中", builder.ToString());
    }

    // === Append (char, repeatCount) ===

    [Fact]
    public void AppendChar_Repeat_Ascii()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append('-', 5);
        Assert.Equal("-----", builder.ToString());
    }

    [Fact]
    public void AppendChar_Repeat_NonAscii()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append('\u00e9', 3); // ééé
        Assert.Equal("ééé", builder.ToString());
    }

    [Fact]
    public void AppendChar_NegativeRepeat_Throws()
    {
        using var builder = Utf8Text.CreateBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Append('a', -1));
    }

    // === Append (string) ===

    [Fact]
    public void AppendString_Null_IsNoop()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append((string?)null);
        Assert.Equal(0, builder.Length);
    }

    [Fact]
    public void AppendString_Empty_IsNoop()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append(string.Empty);
        Assert.Equal(0, builder.Length);
    }

    [Fact]
    public void AppendString_Ascii()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("Hello");
        Assert.Equal("Hello", builder.ToString());
    }

    [Fact]
    public void AppendString_NonAscii()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("héllo wörld");
        Assert.Equal("héllo wörld", builder.ToString());
    }

    // === Append (ReadOnlySpan<char>) ===

    [Fact]
    public void AppendSpanChar_Ascii()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("Hello".AsSpan());
        Assert.Equal("Hello", builder.ToString());
    }

    [Fact]
    public void AppendSpanChar_NonAscii()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("héllo".AsSpan());
        Assert.Equal("héllo", builder.ToString());
    }

    [Fact]
    public void AppendSpanChar_Empty_IsNoop()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append(ReadOnlySpan<char>.Empty);
        Assert.Equal(0, builder.Length);
    }

    // === Append (string, startIndex, count) ===

    [Fact]
    public void AppendString_Substring()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("Hello World", 0, 5);
        Assert.Equal("Hello", builder.ToString());
    }

    [Fact]
    public void AppendString_Substring_Middle()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("Hello World", 6, 5);
        Assert.Equal("World", builder.ToString());
    }

    // === AppendLine ===

    [Fact]
    public void AppendLine_AddsNewline()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.AppendLine();
        var expected = Environment.NewLine;
        Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void AppendLine_AfterText()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("Hello");
        builder.AppendLine();
        Assert.Equal("Hello" + Environment.NewLine, builder.ToString());
    }

    [Fact]
    public void AppendLine_String()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.AppendLine("Hello");
        Assert.Equal("Hello" + Environment.NewLine, builder.ToString());
    }

    [Fact]
    public void AppendLine_SpanChar()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.AppendLine("Hello".AsSpan());
        Assert.Equal("Hello" + Environment.NewLine, builder.ToString());
    }

    [Fact]
    public void AppendLine_Char()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.AppendLine('x');
        Assert.Equal("x" + Environment.NewLine, builder.ToString());
    }

    // === Append<T> (generic) ===

    [Fact]
    public void Append_Int()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append(42);
        Assert.Equal("42", builder.ToString());
    }

    [Fact]
    public void Append_NegativeInt()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append(-123);
        Assert.Equal("-123", builder.ToString());
    }

    [Fact]
    public void Append_Long()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append(long.MaxValue);
        Assert.Equal(long.MaxValue.ToString(), builder.ToString());
    }

    [Fact]
    public void Append_Double()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append(3.14159);
        // Utf8Formatter may produce culture-invariant "3.14159"
        Assert.Equal("3.14159", builder.ToString());
    }

    [Fact]
    public void Append_Bool_True()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append(true);
        Assert.Equal("True", builder.ToString());
    }

    [Fact]
    public void Append_Bool_False()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append(false);
        Assert.Equal("False", builder.ToString());
    }

    [Fact]
    public void Append_Null_IsNoop()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append((object?)null);
        Assert.Equal(0, builder.Length);
    }

    [Fact]
    public void Append_Utf8String()
    {
        using var builder = Utf8Text.CreateBuilder();
        var us = (Utf8String)"héllo"u8;
        builder.Append(us);
        Assert.Equal("héllo", builder.ToString());
    }

    [Fact]
    public void Append_NullUtf8String_IsNoop()
    {
        using var builder = Utf8Text.CreateBuilder();
        Utf8String? us = null;
        builder.Append(us);
        Assert.Equal(0, builder.Length);
    }

    [Fact]
    public void Append_String()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("héllo");
        Assert.Equal("héllo", builder.ToString());
    }

    [Fact]
    public void Append_CharType()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append('é');
        Assert.Equal("é", builder.ToString());
    }

    [Fact]
    public void Append_Guid()
    {
        using var builder = Utf8Text.CreateBuilder();
        var guid = Guid.NewGuid();
        builder.Append(guid);
        Assert.Equal(guid.ToString(), builder.ToString());
    }

    [Fact]
    public void Append_DateTime()
    {
        using var builder = Utf8Text.CreateBuilder();
        var dt = new DateTime(2026, 5, 17, 10, 30, 0);
        builder.Append(dt);
        Assert.NotEmpty(builder.ToString());
    }

    [Fact]
    public void Append_TimeSpan()
    {
        using var builder = Utf8Text.CreateBuilder();
        var ts = new TimeSpan(1, 2, 3, 4);
        builder.Append(ts);
        Assert.NotEmpty(builder.ToString());
    }

    [Fact]
    public void AppendLine_Int()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.AppendLine(42);
        Assert.Equal("42" + Environment.NewLine, builder.ToString());
    }

    // === Clear ===

    [Fact]
    public void Clear_ResetsLength()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("Hello");
        Assert.Equal(5, builder.Length);
        builder.Clear();
        Assert.Equal(0, builder.Length);
        builder.Append("World");
        Assert.Equal("World", builder.ToString());
    }

    // === AsSpan / AsMemory / AsArraySegment ===

    [Fact]
    public void AsSpan_ReturnsWrittenData()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("Hello");
        var span = builder.AsSpan();
        Assert.Equal("Hello", Encoding.UTF8.GetString(span));
    }

    [Fact]
    public void AsSpan_EmptyBuilder_ReturnsEmpty()
    {
        using var builder = Utf8Text.CreateBuilder();
        Assert.True(builder.AsSpan().IsEmpty);
    }

    [Fact]
    public void AsMemory_ReturnsWrittenData()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("Hello");
        var mem = builder.AsMemory();
        Assert.Equal("Hello", Encoding.UTF8.GetString(mem.Span));
    }

    [Fact]
    public void AsArraySegment_ReturnsWrittenData()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("Hello");
        var seg = builder.AsArraySegment();
        Assert.Equal("Hello", Encoding.UTF8.GetString(seg));
    }

    // === IBufferWriter<byte> ===

    [Fact]
    public void GetSpan_And_Advance()
    {
        using var builder = Utf8Text.CreateBuilder();
        var span = builder.GetSpan(10);
        "Hello"u8.CopyTo(span);
        builder.Advance(5);
        Assert.Equal("Hello", builder.ToString());
    }

    [Fact]
    public void IBufferWriter_ChainedWrites()
    {
        using var builder = Utf8Text.CreateBuilder();
        var span1 = builder.GetSpan(5);
        "Hello"u8.CopyTo(span1);
        builder.Advance(5);

        var span2 = builder.GetSpan(5);
        "World"u8.CopyTo(span2);
        builder.Advance(5);

        Assert.Equal("HelloWorld", builder.ToString());
    }

    [Fact]
    public void Advance_Negative_Throws()
    {
        using var builder = Utf8Text.CreateBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Advance(-1));
    }

    [Fact]
    public void Advance_TooLarge_Throws()
    {
        using var builder = Utf8Text.CreateBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Advance(999999));
    }

    // === CopyTo ===

    [Fact]
    public void TryCopyTo_SufficientSpace_Succeeds()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("Hello");
        Span<byte> dest = stackalloc byte[10];
        Assert.True(builder.TryCopyTo(dest, out var written));
        Assert.Equal(5, written);
        Assert.Equal("Hello", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void TryCopyTo_InsufficientSpace_Fails()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("Hello");
        Span<byte> dest = stackalloc byte[3];
        Assert.False(builder.TryCopyTo(dest, out var written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void CopyTo_Writer()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("Hello");
        var writer = new ArrayBufferWriter<byte>();
        builder.CopyTo(writer);
        Assert.Equal("Hello", Encoding.UTF8.GetString(writer.WrittenSpan));
    }

    // === ToString ===

    [Fact]
    public void ToString_Empty_ReturnsEmptyString()
    {
        using var builder = Utf8Text.CreateBuilder();
        Assert.Equal(string.Empty, builder.ToString());
    }

    [Fact]
    public void ToString_NonAscii()
    {
        using var builder = Utf8Text.CreateBuilder();
        builder.Append("héllo wörld");
        Assert.Equal("héllo wörld", builder.ToString());
    }

    // === Growth ===

    [Fact]
    public void AppendBeyondInitialCapacity_Grows()
    {
        using var builder = Utf8Text.CreateBuilder();
        // Default initial capacity is 4096. Write well beyond that.
        var data = new byte[5000];
        new Random(42).NextBytes(data);
        // Fill with ASCII data (bytes 32-126)
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(32 + (data[i] % 95));

        builder.AppendLiteral(data);
        Assert.Equal(data.Length, builder.Length);
        Assert.Equal(data, builder.AsSpan().ToArray());
    }

    [Fact]
    public void MultipleAppends_ContentPreserved()
    {
        using var builder = Utf8Text.CreateBuilder();
        for (int i = 0; i < 200; i++)
            builder.Append(i);
        // 0-9 (1 digit each) + 10-99 (2 digits) + 100-199 (3 digits)
        Assert.Equal(10 + 180 + 300, builder.Length);
        // Verify first and last values
        Assert.StartsWith("0123456789101112", builder.ToString());
        Assert.EndsWith("199", builder.ToString());
    }

    // === Regression: Large IUtf8SpanFormattable ===

    /// <summary>A custom IUtf8SpanFormattable that writes many bytes.</summary>
    private readonly struct LargeFormattable : IUtf8SpanFormattable
    {
        private readonly int _size;

        public LargeFormattable(int size) => _size = size;

        public bool TryFormat(Span<byte> destination, out int written, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            if (destination.Length < _size)
            {
                written = _size;
                return false;
            }
            destination[.._size].Fill((byte)'X');
            written = _size;
            return true;
        }

        public override string ToString() => new string('Y', _size); // deliberately different
    }

    [Fact]
    public void Append_LargeCustomIUtf8SpanFormattable_Success()
    {
        using var builder = Utf8Text.CreateBuilder();
        var big = new LargeFormattable(3_000_000); // 3 MB
        builder.Append<LargeFormattable>(big);
        Assert.Equal(3_000_000, builder.Length);
        // Should contain all X's (from TryFormat), NOT Y's (from ToString)
        var span = builder.AsSpan();
        for (int i = 0; i < span.Length; i++)
            Assert.Equal((byte)'X', span[i]);
    }

    // === Regression: Disposal ===

    [Fact]
    public void Disposed_Length_Throws()
    {
        var builder = new Utf8Builder();
        builder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = builder.Length);
    }

    // === Regression: Negative sizeHint ===

    [Fact]
    public void GetSpan_NegativeSizeHint_Throws()
    {
        using var builder = Utf8Text.CreateBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.GetSpan(-1));
    }

    [Fact]
    public void GetMemory_NegativeSizeHint_Throws()
    {
        using var builder = Utf8Text.CreateBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.GetMemory(-1));
    }
}
