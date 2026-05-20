// Test support types for the Utf8FormatterGenerator.
// Registered via assembly-level attributes so the generator emits
// TryFormatGenerated fast paths into Omicron.Core itself.

#if DEBUG
using System.Text;

[assembly: Omicron.Core.Text.Utf8Formatter<Omicron.Core.Text.GeneratorTests.TestFormattable>]
[assembly: Omicron.Core.Text.Utf8Formatter<Omicron.Core.Text.GeneratorTests.TestSpanFormattable>]
[assembly: Omicron.Core.Text.Utf8Formatter<Omicron.Core.Text.GeneratorTests.LargeFormattable>]

namespace Omicron.Core.Text.GeneratorTests;

/// <summary>A custom IUtf8SpanFormattable struct for generator testing.</summary>
public readonly struct TestFormattable : IUtf8SpanFormattable
{
    private readonly int _value;
    public TestFormattable(int value) => _value = value;

    public bool TryFormat(Span<byte> destination, out int written, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        var text = "VAL:"u8;
        int totalLen = text.Length + 8; // enough for any int
        if (destination.Length < totalLen)
        {
            written = totalLen;
            return false;
        }
        text.CopyTo(destination);
        written = text.Length;
        if (_value.TryFormat(destination.Slice(written), out var n, default, null))
        {
            written += n;
            return true;
        }
        written = totalLen;
        return false;
    }

    public override string ToString() => $"WRONG:{_value}";
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

/// <summary>Large formattable for testing retry behavior.</summary>
public readonly struct LargeFormattable : IUtf8SpanFormattable
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
#endif
