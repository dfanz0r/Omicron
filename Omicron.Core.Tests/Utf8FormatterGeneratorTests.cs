#if DEBUG
using System;
using System.Text;
using Omicron.Core.Text;
using Omicron.Core.Text.GeneratorTests;
using Xunit;

namespace Omicron.Core.Tests;

public class Utf8FormatterGeneratorTests
{
    // ================================================================
    //  IUtf8SpanFormattable fast path (value type)
    // ================================================================

    [Fact]
    public void Generated_IUtf8SpanFormattable_FastPath_Used()
    {
        // The generator should produce a fast path for TestFormattable
        // that uses TryFormat (VAL:...) not ToString() (WRONG:...)
        var value = new TestFormattable(42);
        Span<byte> dest = stackalloc byte[64];
        var result = Utf8ValueFormatter.TryFormat(value, dest, out var written);

        Assert.Equal(Utf8FormatResult.Success, result);
        var output = Encoding.UTF8.GetString(dest[..written]);
        Assert.Equal("VAL:42", output);
    }

    [Fact]
    public void IUtf8SpanFormattable_FormatSpecifier_Transcoded()
    {
        // FormatSpecFormattable handles specifiers: empty -> DEF:, 'X' -> HEX:, 'x' -> hex:
        var value = new FormatSpecFormattable(255);
        var format = "X"u8;
        Span<byte> dest = stackalloc byte[64];
        var result = Utf8ValueFormatter.TryFormat(value, dest, out var written, format);

        Assert.Equal(Utf8FormatResult.Success, result);
        var output = Encoding.UTF8.GetString(dest[..written]);
        Assert.Equal("HEX:FF", output);
    }

    [Fact]
    public void IUtf8SpanFormattable_LowercaseFormatSpecifier_Transcoded()
    {
        var value = new FormatSpecFormattable(255);
        var format = "x"u8;
        Span<byte> dest = stackalloc byte[64];
        var result = Utf8ValueFormatter.TryFormat(value, dest, out var written, format);

        Assert.Equal(Utf8FormatResult.Success, result);
        var output = Encoding.UTF8.GetString(dest[..written]);
        Assert.Equal("hex:ff", output);
    }

    [Fact]
    public void IUtf8SpanFormattable_NonAsciiFormatSpecifier_ReturnsUnsupported()
    {
        // Non-ASCII bytes in format specifier should produce UnsupportedType.
        // The generated code checks formatSpec[i] > 127 and returns UnsupportedType.
        var value = new FormatSpecFormattable(42);
        var format = new byte[] { 0xC3, 0xA9 }; // UTF-8 for é
        Span<byte> dest = stackalloc byte[64];
        var result = Utf8ValueFormatter.TryFormat(value, dest, out var written, format);

        Assert.Equal(Utf8FormatResult.UnsupportedType, result);
    }

    [Fact]
    public void IUtf8SpanFormattable_LongFormatSpecifier_ReturnsUnsupported()
    {
        // Format specifier > 32 bytes should produce UnsupportedType.
        var value = new FormatSpecFormattable(42);
        var format = Encoding.UTF8.GetBytes(new string('X', 33)); // 33-byte specifier
        Span<byte> dest = stackalloc byte[64];
        var result = Utf8ValueFormatter.TryFormat(value, dest, out var written, format);

        Assert.Equal(Utf8FormatResult.UnsupportedType, result);
    }

    // ================================================================
    //  IUtf8SpanFormattable fast path (reference type with null check)
    // ================================================================

    [Fact]
    public void IUtf8SpanFormattable_ReferenceType_FormatsCorrectly()
    {
        // RefFormattable is a class (reference type). The generator emits
        // a null guard before the Unsafe.As call.
        var value = new RefFormattable("hello");
        Span<byte> dest = stackalloc byte[64];
        var result = Utf8ValueFormatter.TryFormat(value, dest, out var written);

        Assert.Equal(Utf8FormatResult.Success, result);
        var output = Encoding.UTF8.GetString(dest[..written]);
        Assert.Equal("hello", output);
    }

    [Fact]
    public void IUtf8SpanFormattable_NullReferenceType_ReturnsSuccessWithZeroWritten()
    {
        // Null reference type should hit the generated null guard and
        // return Success with written=0 (via TryFormatGenerated).
        RefFormattable? value = null;
        Span<byte> dest = stackalloc byte[64];
        var result = Utf8ValueFormatter.TryFormat<RefFormattable?>(value, dest, out var written);

        Assert.Equal(Utf8FormatResult.Success, result);
        Assert.Equal(0, written);
    }

    [Fact]
    public void IUtf8SpanFormattable_LargeReferenceType_ThroughBuilder_RetriesAndSucceeds()
    {
        // LargeRefFormattable produces 3MB output. The builder should
        // retry and eventually succeed.
        using var builder = Utf8Text.CreateBuilder();
        var big = new LargeRefFormattable(3_000_000);
        builder.Append<LargeRefFormattable>(big);
        Assert.Equal(3_000_000, builder.Length);
        // Should contain all R's (from TryFormat), not W's (from ToString)
        var span = builder.AsSpan();
        for (int i = 0; i < span.Length; i++)
            Assert.Equal((byte)'R', span[i]);
    }

    // ================================================================
    //  ISpanFormattable bridge path
    // ================================================================

    [Fact]
    public void Generated_ISpanFormattable_BridgePath_Used()
    {
        var value = new TestSpanFormattable(99);
        Span<byte> dest = stackalloc byte[64];
        var result = Utf8ValueFormatter.TryFormat(value, dest, out var written);

        Assert.Equal(Utf8FormatResult.Success, result);
        var output = Encoding.UTF8.GetString(dest[..written]);
        Assert.Equal("SPAN:99", output);
    }

    // ================================================================
    //  Large output retry (value type IUtf8SpanFormattable)
    // ================================================================

    [Fact]
    public void Large_IUtf8SpanFormattable_ReturnsInsufficientSpace()
    {
        var big = new LargeFormattable(3_000_000);
        var result = Utf8ValueFormatter.TryFormat(big, Span<byte>.Empty, out var written);

        // Should return InsufficientSpace (3MB doesn't fit in empty span)
        // and propagate a size hint via written.
        Assert.Equal(Utf8FormatResult.InsufficientSpace, result);
        Assert.True(written > 0);
    }

    [Fact]
    public void Large_IUtf8SpanFormattable_SucceedsWithEnoughSpace()
    {
        var big = new LargeFormattable(3_000_000);
        var dest = new byte[3_000_000];
        var result = Utf8ValueFormatter.TryFormat(big, dest, out var written);

        Assert.Equal(Utf8FormatResult.Success, result);
        Assert.Equal(3_000_000, written);
        // Should contain all L's (from TryFormat), NOT W's (from ToString)
        for (int i = 0; i < written; i++)
            Assert.Equal((byte)'L', dest[i]);
    }

    [Fact]
    public void Large_IUtf8SpanFormattable_ThroughBuilder_RetriesAndSucceeds()
    {
        using var builder = Utf8Text.CreateBuilder();
        var big = new LargeFormattable(3_000_000);
        builder.Append<LargeFormattable>(big);
        Assert.Equal(3_000_000, builder.Length);
        // Should contain all L's (from TryFormat), not W's (from ToString)
        var span = builder.AsSpan();
        for (int i = 0; i < span.Length; i++)
            Assert.Equal((byte)'L', span[i]);
    }

    // ================================================================
    //  Format specifier via composite format (end-to-end)
    // ================================================================

    [Fact]
    public void CompositeFormat_With_GeneratedType_AndSpecifier()
    {
        // FormatSpecFormattable with "X" specifier, composed via Utf8CompositeFormat
        Span<byte> dest = stackalloc byte[64];
        var result = Utf8CompositeFormat.TryFormat("Value: {0:X}"u8, dest, out var written,
            new FormatSpecFormattable(255));

        Assert.True(result, $"Format failed, written={written}");
        var output = Encoding.UTF8.GetString(dest[..written]);
        Assert.Equal("Value: HEX:FF", output);
    }

    [Fact]
    public void CompositeFormatSlow_With_GeneratedType()
    {
        // TryFormatSlow should also route through the generator
        // and produce correct output.
        Span<byte> dest = stackalloc byte[64];
        var result = Utf8CompositeFormat.TryFormatSlow("Test: {0}"u8, dest, out var written,
            new TestFormattable(77));

        Assert.True(result);
        var output = Encoding.UTF8.GetString(dest[..written]);
        Assert.Equal("Test: VAL:77", output);
    }

    // ================================================================
    //  Prepared format with generated types
    // ================================================================

    [Fact]
    public void PreparedFormat_With_GeneratedType()
    {
        var prepared = Utf8CompositeFormat.Prepare<FormatSpecFormattable>("Hex: {0:X}"u8);
        Span<byte> dest = stackalloc byte[64];
        var result = prepared.TryFormat(dest, out var written, new FormatSpecFormattable(171));

        Assert.True(result);
        var output = Encoding.UTF8.GetString(dest[..written]);
        Assert.Equal("Hex: HEX:AB", output);
    }
}
#endif
