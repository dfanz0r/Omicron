using System;
using System.Text;
using Omicron.Core.Text;
using Xunit;

// Note: [assembly: Utf8Formatter<...>] attributes in this file would NOT
// produce generated fast paths because the partial method defining declaration
// for TryFormatGenerated lives in Omicron.Core, which is already compiled.
// Partial method implementations must be in the SAME compilation as the
// defining declaration. Real generator integration tests are in Omicron.Core.Tests
// where the generator runs during the Omicron.Core compilation itself.
//
// Tests here validate Utf8ValueFormatter's runtime dispatch for IUtf8SpanFormattable
// types, which is the fallback path when no generated fast path exists.

namespace Omicron.Core.Generators.Tests;

/// <summary>A custom IUtf8SpanFormattable struct for testing runtime dispatch.</summary>
public readonly struct TestFormattable : IUtf8SpanFormattable
{
    private readonly int _value;

    public TestFormattable(int value) => _value = value;

    public bool TryFormat(Span<byte> destination, out int written, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        // Format as "VAL:<value>"
        var text = Encoding.UTF8.GetBytes($"VAL:{_value}");
        if (text.Length > destination.Length)
        {
            written = text.Length;
            return false;
        }
        text.CopyTo(destination);
        written = text.Length;
        return true;
    }

    public override string ToString() => $"WRONG:{_value}"; // deliberately different
}

public class Utf8FormatterGeneratorTests
{
    [Fact]
    public void IUtf8SpanFormattable_RuntimeDispatch_Works()
    {
        // When no generator fast path is available, Utf8ValueFormatter
        // falls back to runtime IUtf8SpanFormattable dispatch.
        var value = new TestFormattable(42);
        Span<byte> dest = stackalloc byte[64];
        var result = Utf8ValueFormatter.TryFormat(value, dest, out var written);

        Assert.Equal(Utf8FormatResult.Success, result);
        var output = Encoding.UTF8.GetString(dest[..written]);
        Assert.Equal("VAL:42", output);
    }

    [Fact]
    public void Large_IUtf8SpanFormattable_ReturnsInsufficientSpace()
    {
        // IUtf8SpanFormattable types with large output should correctly
        // report InsufficientSpace and propagate a size hint via written.
        var value = new LargeFormattable(3_000_000);
        var result = Utf8ValueFormatter.TryFormat(value, Span<byte>.Empty, out var written);

        Assert.Equal(Utf8FormatResult.InsufficientSpace, result);
        Assert.True(written > 0);
    }

    /// <summary>Large formattable for testing retry behavior.</summary>
    internal readonly struct LargeFormattable : IUtf8SpanFormattable
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
            destination[.._size].Fill((byte)'L');
            written = _size;
            return true;
        }

        public override string ToString() => new string('W', _size);
    }
}
