// Test support types for the Utf8FormatterGenerator.
// Registered via assembly-level attributes so the generator emits
// TryFormatGenerated fast paths into Omicron.Core itself.

#if DEBUG
using System.Text;

[assembly: Omicron.Core.Text.Utf8Formatter<Omicron.Core.Text.GeneratorTests.TestFormattable>]
[assembly: Omicron.Core.Text.Utf8Formatter<Omicron.Core.Text.GeneratorTests.TestSpanFormattable>]
[assembly: Omicron.Core.Text.Utf8Formatter<Omicron.Core.Text.GeneratorTests.LargeFormattable>]
[assembly: Omicron.Core.Text.Utf8Formatter<Omicron.Core.Text.GeneratorTests.FormatSpecFormattable>]
[assembly: Omicron.Core.Text.Utf8Formatter<Omicron.Core.Text.GeneratorTests.RefFormattable>]
[assembly: Omicron.Core.Text.Utf8Formatter<Omicron.Core.Text.GeneratorTests.LargeRefFormattable>]

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

/// <summary>IUtf8SpanFormattable struct that uses the format specifier to control output.</summary>
/// <remarks>
/// Format specifiers:
///   (empty) → "DEF:{value}"
///   "X"     → "HEX:{value:X}"
///   "hex"   → "hex:{value:x}"
///   anything else → fallback
/// </remarks>
public readonly struct FormatSpecFormattable : IUtf8SpanFormattable
{
    private readonly int _value;
    public FormatSpecFormattable(int value) => _value = value;

    public bool TryFormat(Span<byte> destination, out int written, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (format.IsEmpty)
        {
            var prefix = "DEF:"u8;
            int totalLen = prefix.Length + 8;
            if (destination.Length < totalLen) { written = totalLen; return false; }
            prefix.CopyTo(destination);
            written = prefix.Length;
            return _value.TryFormat(destination.Slice(written), out var n, default, provider)
                ? (written += n, true).Item2
                : (written = totalLen, false).Item2;
        }

        if (format.Length == 1 && (format[0] == 'X' || format[0] == 'x'))
        {
            var prefix = format[0] == 'X' ? "HEX:"u8 : "hex:"u8;
            int totalLen = prefix.Length + 8;
            if (destination.Length < totalLen) { written = totalLen; return false; }
            prefix.CopyTo(destination);
            written = prefix.Length;
            return _value.TryFormat(destination.Slice(written), out var n, format, provider)
                ? (written += n, true).Item2
                : (written = totalLen, false).Item2;
        }

        written = 0;
        return false;
    }

    public override string ToString() => $"WRONG_FMT:{_value}";
}

/// <summary>
/// An IUtf8SpanFormattable reference type (class) for testing null-check codegen.
/// Generator emits a null guard before the Unsafe.As call.
/// </summary>
public sealed class RefFormattable : IUtf8SpanFormattable
{
    private readonly string _value;
    public RefFormattable(string value) => _value = value;

    public bool TryFormat(Span<byte> destination, out int written, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        int byteCount = Encoding.UTF8.GetByteCount(_value.AsSpan());
        if (byteCount > destination.Length)
        {
            written = byteCount;
            return false;
        }
        written = Encoding.UTF8.GetBytes(_value.AsSpan(), destination);
        return true;
    }

    public override string ToString() => $"WRONG_REF:{_value}";
}

/// <summary>Large IUtf8SpanFormattable reference type for retry testing.</summary>
public sealed class LargeRefFormattable : IUtf8SpanFormattable
{
    private readonly int _size;
    public LargeRefFormattable(int size) => _size = size;

    public bool TryFormat(Span<byte> destination, out int written, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (destination.Length < _size)
        {
            written = _size;
            return false;
        }
        destination[.._size].Fill((byte)'R');
        written = _size;
        return true;
    }

    public override string ToString() => new string('W', _size);
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
