#if DEBUG
using System;
using System.Text;
using Omicron.Core.Text;
using Omicron.Core.Text.GeneratorTests;
using Xunit;

namespace Omicron.Core.Tests;

public class Utf8FormatterGeneratorTests
{
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
    public void Generated_ISpanFormattable_BridgePath_Used()
    {
        var value = new TestSpanFormattable(99);
        Span<byte> dest = stackalloc byte[64];
        var result = Utf8ValueFormatter.TryFormat(value, dest, out var written);

        Assert.Equal(Utf8FormatResult.Success, result);
        var output = Encoding.UTF8.GetString(dest[..written]);
        Assert.Equal("SPAN:99", output);
    }

    [Fact]
    public void Large_IUtf8SpanFormattable_ReturnsInsufficientSpace()
    {
        var big = new LargeFormattable(3_000_000);
        var result = Utf8ValueFormatter.TryFormat(big, Span<byte>.Empty, out var written);

        // Should return InsufficientSpace (3MB doesn't fit in empty span)
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
}
#endif
