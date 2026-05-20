using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text;
using Omicron.Core.Content;

namespace Omicron.Core.Text;

/// <summary>
///     Shared internal UTF-8 value formatter. Centralizes primitive formatting,
///     <see cref="IUtf8SpanFormattable" /> dispatch, alignment, and format-specifier
///     transcoding so that <see cref="Utf8Builder" />, <see cref="Utf8CompositeFormat" />,
///     and transient buffer helpers all share one implementation.
/// </summary>
/// <remarks>
///     This type is <c>partial</c> to allow a future Roslyn source generator
///     (Phase 6) to provide custom formatter fast paths via <c>TryFormatGenerated</c>.
/// </remarks>
internal static partial class Utf8ValueFormatter
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    // ──────────────────────────────────────────────
    //  Public entry points
    // ──────────────────────────────────────────────

    /// <summary>
    ///     Format a single value into <paramref name="destination" /> starting at offset 0.
    ///     Returns <see cref="Utf8FormatResult.Success" /> with <paramref name="written" />
    ///     set to bytes written.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Utf8FormatResult TryFormat<T>(
        T value,
        Span<byte> destination,
        out int written,
        ReadOnlySpan<byte> formatSpec = default,
        IFormatProvider? provider = null)
    {
        written = 0;
        return TryFormatValue(value,
            destination,
            ref written,
            formatSpec,
            0,
            provider);
    }

    /// <summary>
    ///     Format a value with optional alignment. Written position starts at
    ///     <paramref name="written" /> and is advanced past the formatted content.
    ///     Used by <see cref="Utf8CompositeFormat" /> for composite template rendering.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Utf8FormatResult TryFormatAligned<T>(
        T value,
        Span<byte> destination,
        ref int written,
        ReadOnlySpan<byte> formatSpec,
        int alignment,
        IFormatProvider? provider = null)
    {
        return TryFormatValue(value, destination, ref written, formatSpec, alignment, provider);
    }

    // ──────────────────────────────────────────────
    //  Source generator hook (Phase 6)
    // ──────────────────────────────────────────────

    /// <summary>
    ///     Source generator extension point (declaration only).
    ///     The implementation is in <c>Utf8ValueFormatter.Fallback.cs</c>.
    ///     Phase 6 replaces it with generated custom-type fast paths.
    /// </summary>
    private static partial Utf8FormatResult TryFormatGenerated<T>(
        ref T value,
        Span<byte> destination,
        out int written,
        ReadOnlySpan<byte> formatSpec,
        IFormatProvider? provider);

    // ──────────────────────────────────────────────
    //  Core value formatting dispatch
    // ──────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Utf8FormatResult TryFormatValue<T>(
        T value,
        Span<byte> destination,
        ref int written,
        ReadOnlySpan<byte> formatSpec,
        int alignment,
        IFormatProvider? provider)
    {
        // Fast path for Utf8String
        if (typeof(T) == typeof(Utf8String))
        {
            Utf8String? us = Unsafe.As<T, Utf8String>(ref value);
            if (us is null)
            {
                return Utf8FormatResult.Success;
            }

            int before = written;
            ReadOnlySpan<byte> span = us.Utf8Span;
            if (span.Length > destination.Length - written)
            {
                return Utf8FormatResult.InsufficientSpace;
            }

            span.CopyTo(destination.Slice(written));
            written += span.Length;
            return alignment != 0
                ? ApplyAlignment(destination, ref written, before, alignment)
                : Utf8FormatResult.Success;
        }

        // Fast path for string
        if (typeof(T) == typeof(string))
        {
            string? s = Unsafe.As<T, string>(ref value);
            if (s is null)
            {
                return Utf8FormatResult.Success;
            }

            int before = written;
            int byteCount = Encoding.UTF8.GetByteCount(s);
            if (byteCount > destination.Length - written)
            {
                return Utf8FormatResult.InsufficientSpace;
            }

            written += Encoding.UTF8.GetBytes(s, destination.Slice(written));
            return alignment != 0
                ? ApplyAlignment(destination, ref written, before, alignment)
                : Utf8FormatResult.Success;
        }

        // Fast path for char
        if (typeof(T) == typeof(char))
        {
            ushort raw = Unsafe.As<T, ushort>(ref value);
            char c = (char)raw;
            int before = written;
            int byteCount = Encoding.UTF8.GetByteCount(stackalloc char[1]
            {
                c
            });
            if (byteCount > destination.Length - written)
            {
                return Utf8FormatResult.InsufficientSpace;
            }

            written += Encoding.UTF8.GetBytes(stackalloc char[1]
                {
                    c
                },
                destination.Slice(written));
            return alignment != 0
                ? ApplyAlignment(destination, ref written, before, alignment)
                : Utf8FormatResult.Success;
        }

        // Fast path for bool
        if (typeof(T) == typeof(bool))
        {
            bool b = Unsafe.As<T, bool>(ref value);
            int before = written;
            ReadOnlySpan<byte> text = b ? "True"u8 : "False"u8;
            if (text.Length > destination.Length - written)
            {
                return Utf8FormatResult.InsufficientSpace;
            }

            text.CopyTo(destination.Slice(written));
            written += text.Length;
            return alignment != 0
                ? ApplyAlignment(destination, ref written, before, alignment)
                : Utf8FormatResult.Success;
        }

        // Try source-generated custom formatter fast paths
        {
            Utf8FormatResult genResult = TryFormatGenerated(ref value,
                destination.Slice(written),
                out int genWritten,
                formatSpec,
                provider);
            if (genResult != Utf8FormatResult.NoFormatter)
            {
                if (genResult == Utf8FormatResult.Success)
                {
                    if (alignment != 0)
                    {
                        int before = written;
                        written += genWritten;
                        return ApplyAlignment(destination, ref written, before, alignment);
                    }

                    written += genWritten;
                }

                return genResult;
            }
        }

        // Fallback to formatter cache (for Utf8Formatter-backed BCL types)
        // When a format specifier is present, skip the cache and route through
        // IUtf8SpanFormattable which can handle specifiers like :X2, :D5.
        CachedFormatter<T>? formatter = formatSpec.IsEmpty ? FormatterCache<T>.Writer : null;
        if (formatter is not null)
        {
            int before = written;
            if (formatter(value, destination.Slice(written), out int n))
            {
                written += n;
                return alignment != 0
                    ? ApplyAlignment(destination, ref written, before, alignment)
                    : Utf8FormatResult.Success;
            }

            return Utf8FormatResult.InsufficientSpace;
        }

        // Check IUtf8SpanFormattable at runtime (may box value types)
        if (value is IUtf8SpanFormattable formattable)
        {
            int before = written;
            if (formatSpec.IsEmpty)
            {
                if (
                    !formattable.TryFormat(destination.Slice(written),
                        out int n,
                        default,
                        provider)
                )
                {
                    return Utf8FormatResult.InsufficientSpace;
                }

                written += n;
            }
            else
            {
                Span<char> charBuf = stackalloc char[formatSpec.Length];
                try
                {
                    TranscodeFormatSpec(formatSpec, charBuf);
                }
                catch (FormatException)
                {
                    return Utf8FormatResult.UnsupportedType;
                }

                if (
                    !formattable.TryFormat(destination.Slice(written),
                        out int n,
                        charBuf,
                        provider)
                )
                {
                    return Utf8FormatResult.InsufficientSpace;
                }

                written += n;
            }

            if (alignment != 0)
            {
                return ApplyAlignment(destination, ref written, before, alignment);
            }

            return Utf8FormatResult.Success;
        }

        // No supported formatter found
        return Utf8FormatResult.NoFormatter;
    }

    // ──────────────────────────────────────────────
    //  Alignment
    // ──────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Utf8FormatResult ApplyAlignment(
        Span<byte> destination,
        ref int written,
        int before,
        int alignment)
    {
        int valueLen = written - before;
        int absAlign = alignment < 0 ? -alignment : alignment;
        if (valueLen >= absAlign)
        {
            return Utf8FormatResult.Success;
        }

        int padding = absAlign - valueLen;
        if (written + padding > destination.Length)
        {
            return Utf8FormatResult.InsufficientSpace;
        }

        if (alignment > 0) // Right-justify
        {
            destination.Slice(before, valueLen).CopyTo(destination.Slice(before + padding));
            destination.Slice(before, padding).Fill((byte)' ');
        }
        else // Left-justify
        {
            destination.Slice(written, padding).Fill((byte)' ');
        }

        written += padding;
        return Utf8FormatResult.Success;
    }

    // ──────────────────────────────────────────────
    //  Format-specifier transcoding
    // ──────────────────────────────────────────────

    /// <summary>
    ///     Validate and transcode ASCII format specifier bytes to chars.
    ///     Throws <see cref="FormatException" /> if any byte is non-ASCII.
    /// </summary>
    internal static void TranscodeFormatSpec(ReadOnlySpan<byte> formatSpec, Span<char> charBuf)
    {
        for (int i = 0; i < formatSpec.Length; i++)
        {
            byte sb = formatSpec[i];
            if (sb > 127)
            {
                throw new FormatException("Non-ASCII format specifier is not supported.");
            }

            charBuf[i] = (char)sb;
        }
    }

    // ──────────────────────────────────────────────
    //  Per-type formatter cache
    // ──────────────────────────────────────────────

    /// <summary>Delegate type for cached formatters.</summary>
    internal delegate bool CachedFormatter<T>(T value, Span<byte> destination, out int written);

    /// <summary>
    ///     Per-type formatter cache. Initialized once per unique T.
    ///     Covers all BCL types known to implement <see cref="IUtf8SpanFormattable" />,
    ///     plus <c>string</c> and <c>Utf8String</c>.
    /// </summary>
    internal static class FormatterCache<T>
    {
        public static readonly CachedFormatter<T>? Writer;

        static FormatterCache()
        {
            Type t = typeof(T);

            if (t == typeof(Utf8String))
            {
                var inner = new CachedFormatter<Utf8String>(FormatUtf8String);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(string))
            {
                var inner = new CachedFormatter<string>(FormatPlainString);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(int))
            {
                CachedFormatter<int> inner = static (v, d, out w) =>
                    Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(uint))
            {
                CachedFormatter<uint> inner = static (v, d, out w) =>
                    Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(long))
            {
                CachedFormatter<long> inner = static (v, d, out w) =>
                    Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(ulong))
            {
                CachedFormatter<ulong> inner = static (v, d, out w) =>
                    Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(byte))
            {
                CachedFormatter<byte> inner = static (v, d, out w) =>
                    Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(sbyte))
            {
                CachedFormatter<sbyte> inner = static (v, d, out w) =>
                    Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(short))
            {
                CachedFormatter<short> inner = static (v, d, out w) =>
                    Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(ushort))
            {
                CachedFormatter<ushort> inner = static (v, d, out w) =>
                    Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(float))
            {
                CachedFormatter<float> inner = static (v, d, out w) =>
                    Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(double))
            {
                CachedFormatter<double> inner = static (v, d, out w) =>
                    Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(decimal))
            {
                CachedFormatter<decimal> inner = static (v, d, out w) =>
                    Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(Guid))
            {
                CachedFormatter<Guid> inner = static (v, d, out w) =>
                    Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(DateTime))
            {
                CachedFormatter<DateTime> inner = static (
                    v, d,
                    out w) => Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(DateTimeOffset))
            {
                CachedFormatter<DateTimeOffset> inner = static (
                    v, d,
                    out w) => Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(TimeSpan))
            {
                CachedFormatter<TimeSpan> inner = static (
                    v, d,
                    out w) => Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            else if (t == typeof(char))
            {
                CachedFormatter<char> inner = static (v, d, out w) =>
                    Utf8Formatter.TryFormat(v, d, out w);
                Writer = (CachedFormatter<T>)(object)inner;
            }
            // else: Writer stays null — TryFormatValue handles IUtf8SpanFormattable
            // fallback and the source generator hook for custom types.
        }

        private static bool FormatUtf8String(Utf8String val, Span<byte> dest, out int w)
        {
            if (val is null)
            {
                w = 0;
                return true;
            }

            ReadOnlySpan<byte> s = val.Utf8Span;
            if (s.Length > dest.Length)
            {
                w = 0;
                return false;
            }

            s.CopyTo(dest);
            w = s.Length;
            return true;
        }

        private static bool FormatPlainString(string val, Span<byte> dest, out int w)
        {
            if (val is null)
            {
                w = 0;
                return true;
            }

            int byteCount = Encoding.UTF8.GetByteCount(val);
            if (byteCount > dest.Length)
            {
                w = 0;
                return false;
            }

            w = Encoding.UTF8.GetBytes(val.AsSpan(), dest);
            return true;
        }
    }
}
