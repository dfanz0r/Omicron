using System.Buffers;

namespace Omicron.Core.Text;

/// <summary>
///     A single parsed segment of a prepared format template.
///     <c>FormatIndex == -1</c> indicates a literal segment;
///     <c>Offset</c> and <c>Count</c> refer into the owned literal buffer.
///     <c>FormatIndex >= 0</c> indicates a placeholder for the argument at that index.
/// </summary>
internal readonly struct Utf8FormatSegment
{
    public readonly int Offset;
    public readonly int Count;
    public readonly int FormatIndex; // -1 = literal
    public readonly int Alignment;
    public readonly int SpecStart; // -1 if no specifier
    public readonly int SpecLength;

    public Utf8FormatSegment(
        int offset,
        int count,
        int formatIndex,
        int alignment = 0,
        int specStart = -1,
        int specLength = 0)
    {
        Offset = offset;
        Count = count;
        FormatIndex = formatIndex;
        Alignment = alignment;
        SpecStart = specStart;
        SpecLength = specLength;
    }
}

// ──────────────────────────────────────────────
//  Prepared format — 1 argument
// ──────────────────────────────────────────────

/// <summary>
///     A pre-parsed UTF-8 composite format template for one argument.
///     Skips re-scanning the template on each use.
/// </summary>
public sealed class PreparedUtf8CompositeFormat<T1>
    where T1 : IUtf8SpanFormattable
{
    private readonly byte[] _formatBuffer;
    private readonly byte[] _literalBuffer;
    private readonly Utf8FormatSegment[] _segments;

    internal PreparedUtf8CompositeFormat(ReadOnlySpan<byte> format)
    {
        (_literalBuffer, _segments) = Utf8CompositeFormat.ParseFormat(format, 0);
        _formatBuffer = format.ToArray();
    }

    /// <summary>Format into a fixed buffer.</summary>
    public bool TryFormat(Span<byte> destination, out int written, T1 arg1)
    {
        written = 0;
        foreach (ref readonly Utf8FormatSegment seg in _segments.AsSpan())
        {
            if (seg.FormatIndex < 0)
            {
                Span<byte> src = _literalBuffer.AsSpan(seg.Offset, seg.Count);
                if (src.Length > destination.Length - written)
                {
                    return false;
                }

                src.CopyTo(destination.Slice(written));
                written += src.Length;
            }
            else
            {
                if (
                    !Utf8CompositeFormat.TryFormatAligned(arg1,
                        destination,
                        ref written,
                        seg.SpecStart >= 0
                            ? _formatBuffer.AsSpan(seg.SpecStart, seg.SpecLength)
                            : default,
                        seg.Alignment)
                )
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Format into an <see cref="IBufferWriter{byte}" />.</summary>
    public void Format<TBufferWriter>(ref TBufferWriter writer, T1 arg1)
        where TBufferWriter : IBufferWriter<byte>
    {
        int capacity = 256;
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0;
            Span<byte> span = writer.GetSpan(capacity);
            if (TryFormat(span, out written, arg1))
            {
                writer.Advance(written);
                return;
            }

            if (capacity >= maxCap)
            {
                throw new InvalidOperationException("Prepared format: output exceeds safety limit.");
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }
}

// ──────────────────────────────────────────────
//  Prepared format — 2 arguments
// ──────────────────────────────────────────────

/// <summary>Pre-parsed UTF-8 composite format for two arguments.</summary>
public sealed class PreparedUtf8CompositeFormat<T1, T2>
    where T1 : IUtf8SpanFormattable
    where T2 : IUtf8SpanFormattable
{
    private readonly byte[] _formatBuffer;
    private readonly byte[] _literalBuffer;
    private readonly Utf8FormatSegment[] _segments;

    internal PreparedUtf8CompositeFormat(ReadOnlySpan<byte> format)
    {
        (_literalBuffer, _segments) = Utf8CompositeFormat.ParseFormat(format, 1);
        _formatBuffer = format.ToArray();
    }

    /// <summary>Format into a fixed buffer.</summary>
    public bool TryFormat(Span<byte> destination, out int written, T1 arg1, T2 arg2)
    {
        written = 0;
        foreach (ref readonly Utf8FormatSegment seg in _segments.AsSpan())
        {
            if (seg.FormatIndex < 0)
            {
                Span<byte> src = _literalBuffer.AsSpan(seg.Offset, seg.Count);
                if (src.Length > destination.Length - written)
                {
                    return false;
                }

                src.CopyTo(destination.Slice(written));
                written += src.Length;
            }
            else
            {
                bool ok = seg.FormatIndex switch
                {
                    0 => Utf8CompositeFormat.TryFormatAligned(arg1,
                        destination,
                        ref written,
                        seg.SpecStart >= 0
                            ? _formatBuffer.AsSpan(seg.SpecStart, seg.SpecLength)
                            : default,
                        seg.Alignment),
                    1 => Utf8CompositeFormat.TryFormatAligned(arg2,
                        destination,
                        ref written,
                        seg.SpecStart >= 0
                            ? _formatBuffer.AsSpan(seg.SpecStart, seg.SpecLength)
                            : default,
                        seg.Alignment),
                    _ => throw new FormatException($"Invalid placeholder index {seg.FormatIndex}.")
                };
                if (!ok)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Format into an <see cref="IBufferWriter{byte}" />.</summary>
    public void Format<TBufferWriter>(ref TBufferWriter writer, T1 arg1, T2 arg2)
        where TBufferWriter : IBufferWriter<byte>
    {
        int capacity = 256;
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0;
            Span<byte> span = writer.GetSpan(capacity);
            if (TryFormat(span, out written, arg1, arg2))
            {
                writer.Advance(written);
                return;
            }

            if (capacity >= maxCap)
            {
                throw new InvalidOperationException("Prepared format: output exceeds safety limit.");
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }
}

// ──────────────────────────────────────────────
//  Prepared format — 3 arguments
// ──────────────────────────────────────────────

/// <summary>Pre-parsed UTF-8 composite format for three arguments.</summary>
public sealed class PreparedUtf8CompositeFormat<T1, T2, T3>
    where T1 : IUtf8SpanFormattable
    where T2 : IUtf8SpanFormattable
    where T3 : IUtf8SpanFormattable
{
    private readonly byte[] _formatBuffer;
    private readonly byte[] _literalBuffer;
    private readonly Utf8FormatSegment[] _segments;

    internal PreparedUtf8CompositeFormat(ReadOnlySpan<byte> format)
    {
        (_literalBuffer, _segments) = Utf8CompositeFormat.ParseFormat(format, 2);
        _formatBuffer = format.ToArray();
    }

    /// <summary>Format into a fixed buffer.</summary>
    public bool TryFormat(Span<byte> destination, out int written, T1 arg1, T2 arg2, T3 arg3)
    {
        written = 0;
        foreach (ref readonly Utf8FormatSegment seg in _segments.AsSpan())
        {
            if (seg.FormatIndex < 0)
            {
                Span<byte> src = _literalBuffer.AsSpan(seg.Offset, seg.Count);
                if (src.Length > destination.Length - written)
                {
                    return false;
                }

                src.CopyTo(destination.Slice(written));
                written += src.Length;
            }
            else
            {
                bool ok = seg.FormatIndex switch
                {
                    0 => Utf8CompositeFormat.TryFormatAligned(arg1,
                        destination,
                        ref written,
                        seg.SpecStart >= 0
                            ? _formatBuffer.AsSpan(seg.SpecStart, seg.SpecLength)
                            : default,
                        seg.Alignment),
                    1 => Utf8CompositeFormat.TryFormatAligned(arg2,
                        destination,
                        ref written,
                        seg.SpecStart >= 0
                            ? _formatBuffer.AsSpan(seg.SpecStart, seg.SpecLength)
                            : default,
                        seg.Alignment),
                    2 => Utf8CompositeFormat.TryFormatAligned(arg3,
                        destination,
                        ref written,
                        seg.SpecStart >= 0
                            ? _formatBuffer.AsSpan(seg.SpecStart, seg.SpecLength)
                            : default,
                        seg.Alignment),
                    _ => throw new FormatException($"Invalid placeholder index {seg.FormatIndex}.")
                };
                if (!ok)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Format into an <see cref="IBufferWriter{byte}" />.</summary>
    public void Format<TBufferWriter>(ref TBufferWriter writer, T1 arg1, T2 arg2, T3 arg3)
        where TBufferWriter : IBufferWriter<byte>
    {
        int capacity = 256;
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0;
            Span<byte> span = writer.GetSpan(capacity);
            if (TryFormat(span, out written, arg1, arg2, arg3))
            {
                writer.Advance(written);
                return;
            }

            if (capacity >= maxCap)
            {
                throw new InvalidOperationException("Prepared format: output exceeds safety limit.");
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }
}

// ──────────────────────────────────────────────
//  Prepared format — 4 arguments
// ──────────────────────────────────────────────

/// <summary>Pre-parsed UTF-8 composite format for four arguments.</summary>
public sealed class PreparedUtf8CompositeFormat<T1, T2, T3, T4>
    where T1 : IUtf8SpanFormattable
    where T2 : IUtf8SpanFormattable
    where T3 : IUtf8SpanFormattable
    where T4 : IUtf8SpanFormattable
{
    private readonly byte[] _formatBuffer;
    private readonly byte[] _literalBuffer;
    private readonly Utf8FormatSegment[] _segments;

    internal PreparedUtf8CompositeFormat(ReadOnlySpan<byte> format)
    {
        (_literalBuffer, _segments) = Utf8CompositeFormat.ParseFormat(format, 3);
        _formatBuffer = format.ToArray();
    }

    /// <summary>Format into a fixed buffer.</summary>
    public bool TryFormat(
        Span<byte> destination,
        out int written,
        T1 arg1,
        T2 arg2,
        T3 arg3,
        T4 arg4)
    {
        written = 0;
        foreach (ref readonly Utf8FormatSegment seg in _segments.AsSpan())
        {
            if (seg.FormatIndex < 0)
            {
                Span<byte> src = _literalBuffer.AsSpan(seg.Offset, seg.Count);
                if (src.Length > destination.Length - written)
                {
                    return false;
                }

                src.CopyTo(destination.Slice(written));
                written += src.Length;
            }
            else
            {
                bool ok = seg.FormatIndex switch
                {
                    0 => Utf8CompositeFormat.TryFormatAligned(arg1,
                        destination,
                        ref written,
                        seg.SpecStart >= 0
                            ? _formatBuffer.AsSpan(seg.SpecStart, seg.SpecLength)
                            : default,
                        seg.Alignment),
                    1 => Utf8CompositeFormat.TryFormatAligned(arg2,
                        destination,
                        ref written,
                        seg.SpecStart >= 0
                            ? _formatBuffer.AsSpan(seg.SpecStart, seg.SpecLength)
                            : default,
                        seg.Alignment),
                    2 => Utf8CompositeFormat.TryFormatAligned(arg3,
                        destination,
                        ref written,
                        seg.SpecStart >= 0
                            ? _formatBuffer.AsSpan(seg.SpecStart, seg.SpecLength)
                            : default,
                        seg.Alignment),
                    3 => Utf8CompositeFormat.TryFormatAligned(arg4,
                        destination,
                        ref written,
                        seg.SpecStart >= 0
                            ? _formatBuffer.AsSpan(seg.SpecStart, seg.SpecLength)
                            : default,
                        seg.Alignment),
                    _ => throw new FormatException($"Invalid placeholder index {seg.FormatIndex}.")
                };
                if (!ok)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Format into an <see cref="IBufferWriter{byte}" />.</summary>
    public void Format<TBufferWriter>(ref TBufferWriter writer, T1 arg1, T2 arg2, T3 arg3, T4 arg4)
        where TBufferWriter : IBufferWriter<byte>
    {
        int capacity = 256;
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0;
            Span<byte> span = writer.GetSpan(capacity);
            if (TryFormat(span, out written, arg1, arg2, arg3, arg4))
            {
                writer.Advance(written);
                return;
            }

            if (capacity >= maxCap)
            {
                throw new InvalidOperationException("Prepared format: output exceeds safety limit.");
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }
}

// ──────────────────────────────────────────────
//  Shared parser for all arities
// ──────────────────────────────────────────────

public static partial class Utf8CompositeFormat
{
    /// <summary>Parse a UTF-8 format template into literal buffer + segments.</summary>
    internal static (byte[] literalBuffer, Utf8FormatSegment[] segments) ParseFormat(
        ReadOnlySpan<byte> format,
        int maxIndex = -1)
    {
        // First pass: scan to count segments and compute literal buffer size
        int segmentCount = 0;
        int literalByteCount = 0;

        for (int i = 0; i < format.Length; i++)
        {
            byte b = format[i];
            if (b == (byte)'{')
            {
                if (i + 1 < format.Length && format[i + 1] == (byte)'{')
                {
                    // Escaped brace → contributes one byte to literals
                    literalByteCount++;
                    i++;
                    continue;
                }

                // Placeholder ends literal run; placeholder itself contributes no literal bytes
                segmentCount++;
                i = SkipPlaceholder(format, i);
                segmentCount++; // placeholder segment
            }
            else if (b == (byte)'}')
            {
                if (i + 1 < format.Length && format[i + 1] == (byte)'}')
                {
                    literalByteCount++;
                    i++;
                    continue;
                }

                throw new FormatException("Malformed UTF-8 format template: unexpected '}' outside placeholder.");
            }
            else
            {
                literalByteCount++;
            }
        }

        // Account for the final literal segment (if any)
        segmentCount++; // at least one segment (possibly empty)

        // Allocate
        byte[] buffer = new byte[literalByteCount];
        var segments = new Utf8FormatSegment[segmentCount];
        int bufPos = 0;
        int segIdx = 0;
        int litStart = -1; // -1 means not in a literal run

        void FlushLiteral()
        {
            if (litStart >= 0)
            {
                int len = bufPos - litStart;
                if (len > 0)
                {
                    segments[segIdx++] = new Utf8FormatSegment(litStart, len, -1);
                }

                litStart = -1;
            }
        }

        void StartLiteral()
        {
            if (litStart < 0)
            {
                litStart = bufPos;
            }
        }

        // Second pass: build segments
        for (int i = 0; i < format.Length; i++)
        {
            byte b = format[i];
            if (b == (byte)'{')
            {
                if (i + 1 < format.Length && format[i + 1] == (byte)'{')
                {
                    StartLiteral();
                    buffer[bufPos++] = (byte)'{';
                    i++;
                    continue;
                }

                FlushLiteral();
                // Parse index
                int argIndex = ParsePlaceholderIndex(format,
                    ref i,
                    out int align,
                    out int specStart,
                    out int specLen);
                if (maxIndex >= 0 && argIndex > maxIndex)
                {
                    throw new FormatException(
                        $"Placeholder {{{argIndex}}} exceeds available arguments (max index {maxIndex}).");
                }

                segments[segIdx++] = new Utf8FormatSegment(0,
                    0,
                    argIndex,
                    align,
                    specStart,
                    specLen);
            }
            else if (b == (byte)'}')
            {
                if (i + 1 < format.Length && format[i + 1] == (byte)'}')
                {
                    StartLiteral();
                    buffer[bufPos++] = (byte)'}';
                    i++;
                }
                // Should not reach here — validated in first pass
            }
            else
            {
                StartLiteral();
                buffer[bufPos++] = b;
            }
        }

        FlushLiteral();

        // Trim segment array if we allocated too many
        if (segIdx < segments.Length)
        {
            Array.Resize(ref segments, segIdx);
        }

        return (buffer, segments);
    }

    /// <summary>Skip past a placeholder {index} starting at the opening brace. Returns the index of the closing }.</summary>
    private static int SkipPlaceholder(ReadOnlySpan<byte> format, int i)
    {
        i++; // skip '{'
        // Skip index digits (at least one required)
        if (i >= format.Length || format[i] < (byte)'0' || format[i] > (byte)'9')
        {
            throw new FormatException("Empty placeholder index.");
        }

        while (i < format.Length && format[i] >= (byte)'0' && format[i] <= (byte)'9')
        {
            i++;
        }

        // Skip optional alignment
        if (i < format.Length && format[i] == (byte)',')
        {
            i++;
            while (i < format.Length && format[i] == (byte)' ')
            {
                i++;
            }

            if (i < format.Length && format[i] == (byte)'-')
            {
                i++;
            }

            // At least one digit required after alignment sign
            if (i >= format.Length || format[i] < (byte)'0' || format[i] > (byte)'9')
            {
                throw new FormatException("Expected alignment digits after ','.");
            }

            while (i < format.Length && format[i] >= (byte)'0' && format[i] <= (byte)'9')
            {
                i++;
            }
        }

        // Skip optional format specifier
        if (i < format.Length && format[i] == (byte)':')
        {
            i++;
            while (i < format.Length && format[i] != (byte)'}')
            {
                if (format[i] == (byte)'{')
                {
                    throw new FormatException("Nested '{' in format specifier.");
                }

                i++;
            }
        }

        if (i < format.Length && format[i] == (byte)'}')
        {
            return i;
        }

        throw new FormatException("Unclosed placeholder.");
    }

    /// <summary>Parse the integer index of a placeholder starting after the opening brace. Advances i past the closing brace.</summary>
    private static int ParsePlaceholderIndex(
        ReadOnlySpan<byte> format,
        scoped ref int i,
        out int alignment,
        out int specStart,
        out int specLength)
    {
        i++; // skip '{'
        int index = 0;
        bool hasDigit = false;
        while (i < format.Length && format[i] >= (byte)'0' && format[i] <= (byte)'9')
        {
            hasDigit = true;
            index = index * 10 + (format[i] - (byte)'0');
            i++;
        }

        if (!hasDigit)
        {
            throw new FormatException("Empty placeholder.");
        }

        alignment = 0;
        if (i < format.Length && format[i] == (byte)',')
        {
            i++;
            while (i < format.Length && format[i] == (byte)' ')
            {
                i++;
            }

            bool neg = false;
            if (i < format.Length && format[i] == (byte)'-')
            {
                neg = true;
                i++;
            }

            while (i < format.Length && format[i] >= (byte)'0' && format[i] <= (byte)'9')
            {
                alignment = alignment * 10 + (format[i] - (byte)'0');
                i++;
            }

            if (neg)
            {
                alignment = -alignment;
            }
        }

        specStart = -1;
        specLength = 0;
        if (i < format.Length && format[i] == (byte)':')
        {
            i++;
            specStart = i;
            while (i < format.Length && format[i] != (byte)'}')
            {
                // Reject non-ASCII specifier bytes at prepare time
                if (format[i] > 127)
                {
                    throw new FormatException("Non-ASCII format specifier is not supported.");
                }

                specLength++;
                i++;
            }
        }

        if (i >= format.Length || format[i] != (byte)'}')
        {
            throw new FormatException("Missing closing '}' in placeholder.");
        }

        // Note: for loop caller increments i past '}'
        return index;
    }
}
