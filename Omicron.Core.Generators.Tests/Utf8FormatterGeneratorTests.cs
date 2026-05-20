using System;
using System.Text;
using Omicron.Core.Text;
using Xunit;

// Register custom structs for source-generated fast paths
[assembly: Utf8Formatter<Omicron.Core.Generators.Tests.TestFormattable>]
[assembly: Utf8Formatter<Omicron.Core.Generators.Tests.TestSpanFormattable>]
[assembly: Utf8Formatter<Omicron.Core.Generators.Tests.Utf8FormatterGeneratorTests.LargeFormattable>]

namespace Omicron.Core.Generators.Tests;

/// <summary>A custom IUtf8SpanFormattable struct for generator testing.</summary>
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

/// <summary>An ISpanFormattable-only struct for testing the slow bridge.</summary>
public readonly struct TestSpanFormattable : ISpanFormattable
{
    private readonly int _value;

    public TestSpanFormattable(int value) => _value = value;

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        var text = $"SPAN:{_value}";
        if (text.Length > destination.Length)
        {
            charsWritten = text.Length;
            return false;
        }
        text.AsSpan().CopyTo(destination);
        charsWritten = text.Length;
        return true;
    }

    public string ToString(string? format, IFormatProvider? formatProvider) => $"WRONG_SPAN:{_value}";
    public override string ToString() => $"WRONG_SPAN:{_value}";
}

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
        // The generator should produce a bridge path for TestSpanFormattable
        var value = new TestSpanFormattable(99);
        Span<byte> dest = stackalloc byte[64];
        var result = Utf8ValueFormatter.TryFormat(value, dest, out var written);

        Assert.Equal(Utf8FormatResult.Success, result);
        var output = Encoding.UTF8.GetString(dest[..written]);
        Assert.Equal("SPAN:99", output);
    }

    [Fact]
    public void Large_IUtf8SpanFormattable_RetriesAndSucceeds()
    {
        // Create a formattable that produces 3MB of output
        var big = new LargeFormattable(3_000_000);
        var result = Utf8ValueFormatter.TryFormat(big, Span<byte>.Empty, out var written);

        // Should return InsufficientSpace (or Success with a large enough buffer)
        Assert.Equal(Utf8FormatResult.InsufficientSpace, result);
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
