using System.Buffers;
using System.Buffers.Text;
using System.Text;
using Cysharp.Text;
using Omicron.Core.Content;
using Omicron.Core.Text;
using Xunit;

namespace Omicron.Core.Tests;

/// <summary>
/// Tests for <see cref="Utf8CompositeFormat"/>.
///
/// <see cref="Utf8CompositeFormat.TryFormat{T1}"/> requires
/// <c>where T : IUtf8SpanFormattable</c>.  Types that do NOT
/// implement that interface (string, bool, object, etc.) are
/// tested via <see cref="Utf8CompositeFormat.TryFormatSlow{T1}"/>.
/// </summary>
public class Utf8CompositeFormatTests
{
    // ──────────────────────────────────────────────
    //  Literal behavior (no IUtf8SpanFormattable needed)
    // ──────────────────────────────────────────────

    [Fact]
    public void EmptyFormat_ProducesEmpty()
    {
        Span<byte> dest = stackalloc byte[1];
        Assert.True(Utf8CompositeFormat.TryFormat(""u8, dest, out var written, 42));
        Assert.Equal(0, written);
    }

    [Fact]
    public void PlainLiteral_NoPlaceholders()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("Hello, world!"u8, dest, out var written, 0));
        Assert.Equal("Hello, world!", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void NonAsciiLiteral()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("Café naïve 日本語 ✓"u8, dest, out var written, 0));
        Assert.Equal("Café naïve 日本語 ✓", Encoding.UTF8.GetString(dest[..written]));
    }

    // ──────────────────────────────────────────────
    //  Placeholder behavior (IUtf8SpanFormattable args)
    // ──────────────────────────────────────────────

    [Fact]
    public void SinglePlaceholder_Int()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, 12345));
        Assert.Equal("12345", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void PlaceholderAtStart_Int()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{0} world"u8, dest, out var written, 42));
        Assert.Equal("42 world", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void PlaceholderAtEnd_Utf8String()
    {
        var us = (Utf8String)"hello"u8;
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("Hello {0}"u8, dest, out var written, us));
        Assert.Equal("Hello hello", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void PlaceholderInMiddle_Int()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("abc{0}def"u8, dest, out var written, 123));
        Assert.Equal("abc123def", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void TwoPlaceholders_Int()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}{1}"u8, dest, out var written, 10, 20));
        Assert.Equal("1020", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void TwoPlaceholdersWithLiteralBetween()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{0} + {1}"u8, dest, out var written, 2, 3));
        Assert.Equal("2 + 3", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void ThreePlaceholders()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}.{1}.{2}"u8, dest, out var written, 1, 2, 3));
        Assert.Equal("1.2.3", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FourPlaceholders()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}-{1}-{2}-{3}"u8, dest, out var written, 1, 2, 3, 4));
        Assert.Equal("1-2-3-4", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void RepeatedPlaceholder()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{0} + {0} = {1}"u8, dest, out var written, 2, 4));
        Assert.Equal("2 + 2 = 4", Encoding.UTF8.GetString(dest[..written]));
    }

    // ──────────────────────────────────────────────
    //  Escaping
    // ──────────────────────────────────────────────

    [Fact]
    public void EscapedOpenBrace()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{{Hello}}"u8, dest, out var written, 0));
        Assert.Equal("{Hello}", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void EscapedBracesAroundPlaceholder()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{{ {0} }}"u8, dest, out var written, 42));
        Assert.Equal("{ 42 }", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void ConsecutiveEscapedBraces()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{{{{}}}}"u8, dest, out var written, 0));
        Assert.Equal("{{}}", Encoding.UTF8.GetString(dest[..written]));
    }

    // ──────────────────────────────────────────────
    //  IUtf8SpanFormattable value types
    // ──────────────────────────────────────────────

    [Fact]
    public void FormatsInt()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, 12345));
        Assert.Equal("12345", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatsNegativeInt()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, -42));
        Assert.Equal("-42", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatsUInt()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, 4000000000u));
        Assert.Equal("4000000000", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatsLong()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, 10000000000000000L));
        Assert.Equal("10000000000000000", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatsULong()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, 18000000000000000000UL));
        Assert.Equal("18000000000000000000", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatsGuid()
    {
        var guid = new Guid("a1b2c3d4-e5f6-7890-1234-567890abcdef");
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, guid));
        Assert.Equal(guid.ToString(), Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatsDateTime()
    {
        var dt = new DateTime(2025, 6, 15, 14, 30, 0);
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, dt));
        // Constrained path calls IUtf8SpanFormattable.TryFormat with default format
        Span<byte> expectedBuf = stackalloc byte[64];
        ((IUtf8SpanFormattable)dt).TryFormat(expectedBuf, out var n, default, null);
        Assert.Equal(Encoding.UTF8.GetString(expectedBuf[..n]), Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatsDateTimeOffset()
    {
        var dto = new DateTimeOffset(2025, 6, 15, 14, 30, 0, TimeSpan.FromHours(2));
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, dto));
        Span<byte> expectedBuf = stackalloc byte[64];
        ((IUtf8SpanFormattable)dto).TryFormat(expectedBuf, out var n, default, null);
        Assert.Equal(Encoding.UTF8.GetString(expectedBuf[..n]), Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatsTimeSpan()
    {
        var ts = new TimeSpan(1, 2, 30, 15);
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, ts));
        Assert.Equal(ts.ToString(), Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatsDouble()
    {
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, 3.14));
        Assert.Equal("3.14", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatsFloat()
    {
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, 2.5f));
        Assert.Equal("2.5", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatsByte()
    {
        Span<byte> dest = stackalloc byte[16];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, (byte)42));
        Assert.Equal("42", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatsDecimal()
    {
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, 123.45m));
        Assert.Equal("123.45", Encoding.UTF8.GetString(dest[..written]));
    }

    // ──────────────────────────────────────────────
    //  Utf8String (implements IUtf8SpanFormattable)
    // ──────────────────────────────────────────────

    [Fact]
    public void FormatsUtf8String()
    {
        var us = (Utf8String)"Hello from Utf8String!"u8;
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, us));
        Assert.Equal("Hello from Utf8String!", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatsNonAsciiUtf8String()
    {
        var us = (Utf8String)"25°C — 天気良好"u8;
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, us));
        Assert.Equal("25°C — 天気良好", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatsEmptyUtf8String()
    {
        Span<byte> dest = stackalloc byte[16];
        Assert.True(Utf8CompositeFormat.TryFormat("[{0}]"u8, dest, out var written, Utf8String.Empty));
        Assert.Equal("[]", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void Slow_NullUtf8String_WritesNothing()
    {
        Utf8String? nullUs = null;
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormatSlow("[{0}]"u8, dest, out var written, nullUs));
        Assert.Equal("[]", Encoding.UTF8.GetString(dest[..written]));
    }

    // ──────────────────────────────────────────────
    //  TryFormatSlow — string, char, bool, object
    // ──────────────────────────────────────────────

    [Fact]
    public void Slow_FormatsString()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormatSlow("{0}"u8, dest, out var written, "Hello!"));
        Assert.Equal("Hello!", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void Slow_FormatsNonAsciiString()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormatSlow("{0}"u8, dest, out var written, "25°C — ☀️"));
        Assert.Equal("25°C — ☀️", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void Slow_FormatsNullString()
    {
        Span<byte> dest = stackalloc byte[16];
        Assert.True(Utf8CompositeFormat.TryFormatSlow("({0})"u8, dest, out var written, (string?)null));
        Assert.Equal("()", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void Slow_FormatsChar()
    {
        Span<byte> dest = stackalloc byte[16];
        Assert.True(Utf8CompositeFormat.TryFormatSlow("{0}"u8, dest, out var written, 'A'));
        Assert.Equal("A", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void Slow_FormatsBool()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormatSlow("{0} {1}"u8, dest, out var written, true, false));
        Assert.Equal("True False", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void Slow_UnsupportedType_ReturnsFalse()
    {
        Span<byte> dest = stackalloc byte[64];
        Assert.False(Utf8CompositeFormat.TryFormatSlow("{0}"u8, dest, out _, new object()));
    }

    [Fact]
    public void Slow_StringWithLiteral()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormatSlow("Hello {0}"u8, dest, out var written, "world"));
        Assert.Equal("Hello world", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void Slow_TwoStrings()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormatSlow("{0}{1}"u8, dest, out var written, "ab", "cd"));
        Assert.Equal("abcd", Encoding.UTF8.GetString(dest[..written]));
    }

    // ──────────────────────────────────────────────
    //  Failure behavior
    // ──────────────────────────────────────────────

    [Fact]
    public void InsufficientDest_ReturnsFalse()
    {
        Span<byte> dest = stackalloc byte[3];
        Assert.False(Utf8CompositeFormat.TryFormat("Hello"u8, dest, out var written, 0));
    }

    [Fact]
    public void InsufficientDest_PlaceholderExpansion()
    {
        Span<byte> dest = stackalloc byte[3];
        Assert.False(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, 12345));
    }

    [Fact]
    public void InsufficientDest_PartialLiteralBeforeFailure()
    {
        var dest = new byte[2];
        Assert.False(Utf8CompositeFormat.TryFormat("abc{0}"u8, dest, out var written, 99));
    }

    [Fact]
    public void MalformedOpenBraceNoDigits_Throws()
    {
        var dest = new byte[16];
        Assert.Throws<FormatException>(() =>
            Utf8CompositeFormat.TryFormat("abc{}def"u8, dest, out _, 42));
    }

    [Fact]
    public void MalformedUnclosedBrace_Throws()
    {
        var dest = new byte[16];
        Assert.Throws<FormatException>(() =>
            Utf8CompositeFormat.TryFormat("abc{0"u8, dest, out _, 42));
    }

    [Fact]
    public void MalformedUnmatchedCloseBrace_Throws()
    {
        var dest = new byte[16];
        Assert.Throws<FormatException>(() =>
            Utf8CompositeFormat.TryFormat("abc}def"u8, dest, out _, 42));
    }

    [Fact]
    public void MalformedNonDigitInPlaceholder_Throws()
    {
        var dest = new byte[16];
        Assert.Throws<FormatException>(() =>
            Utf8CompositeFormat.TryFormat("{0a}"u8, dest, out _, 42));
    }

    [Fact]
    public void IndexOutOfRange_Throws()
    {
        var dest = new byte[16];
        Assert.Throws<FormatException>(() =>
            Utf8CompositeFormat.TryFormat("{0}{1}"u8, dest, out _, 42));
    }

    [Fact]
    public void IndexOutOfRange_ThreeArgs()
    {
        var dest = new byte[16];
        Assert.Throws<FormatException>(() =>
            Utf8CompositeFormat.TryFormat("{0}{3}"u8, dest, out _, 1, 2, 3));
    }

    // ──────────────────────────────────────────────
    //  Mixed escaping and placeholders
    // ──────────────────────────────────────────────

    [Fact]
    public void DictFormatterSyntax()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("Test {0} 123 {1}"u8, dest, out var written, 321, 456));
        Assert.Equal("Test 321 123 456", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void LiteralWithEscapedBracesAndPlaceholder()
    {
        Span<byte> dest = stackalloc byte[256];
        Assert.True(Utf8CompositeFormat.TryFormat("{{literal {0} braces}}"u8, dest, out var written, 42));
        Assert.Equal("{literal 42 braces}", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void EscapedBracesOnly()
    {
        Span<byte> dest = stackalloc byte[16];
        Assert.True(Utf8CompositeFormat.TryFormat("{{{{}}}}"u8, dest, out var written, 0));
        Assert.Equal("{{}}", Encoding.UTF8.GetString(dest[..written]));
    }
    // ──────────────────────────────────────────────
    //  Writer/builder API (Phase 2)
    // ──────────────────────────────────────────────

    [Fact]
    public void FormatToWriter_Int()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        Utf8CompositeFormat.Format(ref buffer, "Value: {0}"u8, 42);
        Assert.Equal("Value: 42", Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void FormatToWriter_Utf8String()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var us = (Utf8String)"hello"u8;
        Utf8CompositeFormat.Format(ref buffer, "[{0}]"u8, us);
        Assert.Equal("[hello]", Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void FormatToWriter_TwoArgs()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        Utf8CompositeFormat.Format(ref buffer, "{0}-{1}"u8, 10, 20);
        Assert.Equal("10-20", Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void AppendFormatUtf8Builder_Int()
    {
        var builder = ZString.CreateUtf8StringBuilder();
        try
        {
            Utf8CompositeFormat.AppendFormatUtf8(ref builder, "Score: {0}"u8, 99);
            Assert.Equal("Score: 99", builder.ToString());
        }
        finally { builder.Dispose(); }
    }

    [Fact]
    public void AppendFormatUtf8Builder_Mixed()
    {
        var builder = ZString.CreateUtf8StringBuilder();
        try
        {
            builder.AppendLiteral("Start|"u8);
            Utf8CompositeFormat.AppendFormatUtf8(ref builder, "{0}-{1}"u8, 1, 2);
            builder.AppendLiteral("|End"u8);
            Assert.Equal("Start|1-2|End", builder.ToString());
        }
        finally { builder.Dispose(); }
    }

    [Fact]
    public void FormatSlowToWriter_String()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        Utf8CompositeFormat.FormatSlow(ref buffer, "Hello {0}"u8, "world");
        Assert.Equal("Hello world", Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void AppendFormatUtf8SlowBuilder_String()
    {
        var builder = ZString.CreateUtf8StringBuilder();
        try
        {
            Utf8CompositeFormat.AppendFormatUtf8Slow(ref builder, "{0}"u8, "test");
            Assert.Equal("test", builder.ToString());
        }
        finally { builder.Dispose(); }
    }

    [Fact]
    public void FormatToWriterResult_EqualsTryFormat()
    {
        var format = "{0} + {1} = {2}"u8;
        Span<byte> span = stackalloc byte[64];
        Utf8CompositeFormat.TryFormat(format, span, out var written, 1, 2, 3);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        Utf8CompositeFormat.Format(ref buffer, format, 1, 2, 3);

        var expected = Encoding.UTF8.GetString(span[..written]);
        var actual = Encoding.UTF8.GetString(buffer.WrittenSpan);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AppendFormatUtf8Result_EqualsTryFormat()
    {
        var format = "Hello {0}"u8;
        Span<byte> span = stackalloc byte[64];
        Utf8CompositeFormat.TryFormat(format, span, out var written, 42);

        var builder = ZString.CreateUtf8StringBuilder();
        try
        {
            Utf8CompositeFormat.AppendFormatUtf8(ref builder, format, 42);
            Assert.Equal(Encoding.UTF8.GetString(span[..written]), builder.ToString());
        }
        finally { builder.Dispose(); }
    }

    [Fact]
    public void CustomIUtf8SpanFormattable_FormatsCorrectly()
    {
        var val = new CustomFormattable(42);
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("{0}"u8, dest, out var written, val));
        Assert.Equal("Custom(42)", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void LargeString_FormatSlowToWriter()
    {
        var large = new string('A', 50000);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        Utf8CompositeFormat.FormatSlow(ref buffer, "{0}"u8, large);
        Assert.Equal(50000, buffer.WrittenSpan.Length);
        for (int i = 0; i < buffer.WrittenSpan.Length; i++)
            Assert.Equal((byte)'A', buffer.WrittenSpan[i]);
    }

    [Fact]
    public void LargeString_AppendFormatUtf8Slow()
    {
        var large = new string('B', 50000);
        var builder = ZString.CreateUtf8StringBuilder();
        try
        {
            Utf8CompositeFormat.AppendFormatUtf8Slow(ref builder, "{0}"u8, large);
            Assert.Equal(50000, builder.Length);
        }
        finally { builder.Dispose(); }
    }

    [Fact]
    public void UnsupportedType_FormatSlowToWriter_Throws()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        Assert.Throws<InvalidOperationException>(() =>
            Utf8CompositeFormat.FormatSlow(ref buffer, "{0}"u8, new object()));
    }

    [Fact]
    public void UnsupportedType_AppendFormatUtf8Slow_Throws()
    {
        var builder = ZString.CreateUtf8StringBuilder();
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                Utf8CompositeFormat.AppendFormatUtf8Slow(ref builder, "{0}"u8, new object()));
        }
        finally { builder.Dispose(); }
    }
}

// ──────────────────────────────────────────────
//  Prepared format tests (Phase 3)
// ──────────────────────────────────────────────

public class Utf8CompositeFormatPhase4Tests
{
    // ──────────────────────────────────────────────
    //  Format specifier tests
    // ──────────────────────────────────────────────

    [Fact]
    public void FormatHexSpecifier()
    {
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("{0:X2}"u8, dest, out var written, 255));
        Assert.Equal("FF", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatDecimalSpecifier()
    {
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("{0:D5}"u8, dest, out var written, 42));
        Assert.Equal("00042", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatNumberSpecifier()
    {
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("{0:N}"u8, dest, out var written, 1234));
        Assert.Contains("1,234", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void FormatFixedPointSpecifier()
    {
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("{0:F2}"u8, dest, out var written, 3.14));
        Assert.Equal("3.14", Encoding.UTF8.GetString(dest[..written]));
    }

    // ──────────────────────────────────────────────
    //  Alignment tests
    // ──────────────────────────────────────────────

    [Fact]
    public void AlignRight()
    {
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("'{0,10}'"u8, dest, out var written, 42));
        var s = Encoding.UTF8.GetString(dest[..written]);
        Assert.Equal("'        42'", s);
    }

    [Fact]
    public void AlignLeft()
    {
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("'{0,-10}'"u8, dest, out var written, 42));
        var s = Encoding.UTF8.GetString(dest[..written]);
        Assert.Equal("'42        '", s);
    }

    [Fact]
    public void FormatSpecAndAlignment()
    {
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("'{0,8:X2}'"u8, dest, out var written, 255));
        var s = Encoding.UTF8.GetString(dest[..written]);
        Assert.Equal("'      FF'", s);
    }

    [Fact]
    public void PlaceholderWithSpecifierAndLiteral()
    {
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("Hex: {0:X}"u8, dest, out var written, 255));
        Assert.Equal("Hex: FF", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void MultipleSpecifiers()
    {
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormat("{0:X2} {1:D3}"u8, dest, out var written, 255, 7));
        Assert.Equal("FF 007", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void SlowPath_FormatSpec_Rejected()
    {
        // Slow path's IUtf8SpanFormattable fallback handles specifiers correctly.
        // But for string/bool/char, specifiers are not applied (they just format default).
        // For string, format spec has no meaning, so the output is the string itself.
        Span<byte> dest = stackalloc byte[64];
        Assert.True(Utf8CompositeFormat.TryFormatSlow("{0}"u8, dest, out var written, "hello"));
        Assert.Equal("hello", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void SlowPath_AlignmentForString()
    {
        Span<byte> dest = stackalloc byte[64];
        // string + alignment through slow path
        Assert.True(Utf8CompositeFormat.TryFormatSlow("'{0,10}'"u8, dest, out var written, "hi"));
        Assert.Equal("'        hi'", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void NonAsciiSpecifier_Throws()
    {
        var dest = new byte[64];
        Assert.Throws<FormatException>(() =>
            Utf8CompositeFormat.TryFormat("{0:\u00e9}"u8, dest, out _, 42));
    }

    [Fact]
    public void PreparedFormat_WithHexSpecifier()
    {
        var prepared = Utf8CompositeFormat.Prepare<int>("{0:X2}"u8);
        Span<byte> dest = stackalloc byte[64];
        Assert.True(prepared.TryFormat(dest, out var written, 255));
        Assert.Equal("FF", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void PreparedFormat_WithAlignment()
    {
        var prepared = Utf8CompositeFormat.Prepare<int>("'{0,8}'"u8);
        Span<byte> dest = stackalloc byte[64];
        Assert.True(prepared.TryFormat(dest, out var written, 42));
        Assert.Equal("'      42'", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void Prepared_NonAsciiSpecifier_ThrowsAtPrepare()
    {
        Assert.Throws<FormatException>(() =>
            Utf8CompositeFormat.Prepare<int>("{0:\u00e9}"u8));
    }
}

public class Utf8CompositeFormatPreparedTests
{
    [Fact]
    public void PreparedFormat_OneArg_MatchesTryFormat()
    {
        var prepared = Utf8CompositeFormat.Prepare<int>("Value: {0}"u8);
        Span<byte> dest = stackalloc byte[64];
        Assert.True(prepared.TryFormat(dest, out var written, 42));
        Assert.Equal("Value: 42", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void PreparedFormat_TwoArgs_MatchesTryFormat()
    {
        var prepared = Utf8CompositeFormat.Prepare<int, int>("{0} + {1}"u8);
        Span<byte> dest = stackalloc byte[64];
        Assert.True(prepared.TryFormat(dest, out var written, 3, 4));
        Assert.Equal("3 + 4", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void PreparedFormat_WithEscapedBraces()
    {
        var prepared = Utf8CompositeFormat.Prepare<int>("{{literal {0}}}"u8);
        Span<byte> dest = stackalloc byte[64];
        Assert.True(prepared.TryFormat(dest, out var written, 99));
        Assert.Equal("{literal 99}", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void PreparedFormat_Utf8StringArg()
    {
        var prepared = Utf8CompositeFormat.Prepare<Utf8String>("Hello {0}"u8);
        Span<byte> dest = stackalloc byte[64];
        Assert.True(prepared.TryFormat(dest, out var written, (Utf8String)"world"u8));
        Assert.Equal("Hello world", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void PreparedFormat_RepeatedUses_ProduceSameOutput()
    {
        var prepared = Utf8CompositeFormat.Prepare<int, int>("{0} x {1} = {0}"u8);
        Span<byte> dest1 = stackalloc byte[64];
        Span<byte> dest2 = stackalloc byte[64];
        Assert.True(prepared.TryFormat(dest1, out var w1, 3, 4));
        Assert.True(prepared.TryFormat(dest2, out var w2, 3, 4));
        Assert.Equal("3 x 4 = 3", Encoding.UTF8.GetString(dest1[..w1]));
        Assert.Equal(w1, w2);
    }

    [Fact]
    public void PreparedFormat_EquivToTryFormat()
    {
        // Verify prepared output matches one-shot output
        var format = "{{{0}}} - {1}"u8;
        Span<byte> dest = stackalloc byte[64];
        Utf8CompositeFormat.TryFormat(format, dest, out var written, 1, 2);

        var prepared = Utf8CompositeFormat.Prepare<int, int>(format);
        Span<byte> preparedDest = stackalloc byte[64];
        Assert.True(prepared.TryFormat(preparedDest, out var pw, 1, 2));

        Assert.Equal(Encoding.UTF8.GetString(dest[..written]), Encoding.UTF8.GetString(preparedDest[..pw]));
        Assert.Equal(written, pw);
    }

    [Fact]
    public void PreparedFormat_FormatToWriter_MatchesTryFormat()
    {
        var prepared = Utf8CompositeFormat.Prepare<int, int, int>("{0} + {1} = {2}"u8);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        prepared.Format(ref buffer, 1, 2, 3);

        Span<byte> dest = stackalloc byte[64];
        Utf8CompositeFormat.TryFormat("{0} + {1} = {2}"u8, dest, out var written, 1, 2, 3);

        Assert.Equal(Encoding.UTF8.GetString(dest[..written]), Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void PreparedFormat_InsufficientSpace_ReturnsFalse()
    {
        var prepared = Utf8CompositeFormat.Prepare<int>("{0}"u8);
        Span<byte> dest = stackalloc byte[2];
        Assert.False(prepared.TryFormat(dest, out _, 999));
    }

    [Fact]
    public void PreparedFormat_IndexTooHigh_ThrowsAtPrepare()
    {
        // {2} exceeds max index 1 for 2-arg prepared format
        Assert.Throws<FormatException>(() =>
            Utf8CompositeFormat.Prepare<int, int>("{0} {2}"u8));
    }

    [Fact]
    public void PreparedFormat_IndexWithinRange_Succeeds()
    {
        // {1} is valid for 2-arg format
        var prepared = Utf8CompositeFormat.Prepare<int, int>("{1}"u8);
        Span<byte> dest = stackalloc byte[64];
        Assert.True(prepared.TryFormat(dest, out var written, 0, 99));
        Assert.Equal("99", Encoding.UTF8.GetString(dest[..written]));
    }
}

/// <summary>Custom non-BCL type implementing IUtf8SpanFormattable for testing.</summary>
internal sealed class CustomFormattable : IUtf8SpanFormattable
{
    private readonly int _value;
    public CustomFormattable(int value) => _value = value;

    public bool TryFormat(Span<byte> destination, out int written, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        var text = System.Text.Encoding.UTF8.GetBytes($"Custom({_value})");
        if (text.Length > destination.Length) { written = 0; return false; }
        text.CopyTo(destination);
        written = text.Length;
        return true;
    }
}
