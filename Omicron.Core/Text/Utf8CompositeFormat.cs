using System.Buffers;
using System.Runtime.CompilerServices;

namespace Omicron.Core.Text;

/// <summary>
///     UTF-8-native composite format provider.
///     <see cref="TryFormat{T1}" /> and its arity overloads accept
///     <c>ReadOnlySpan&lt;byte&gt;</c> format templates and format
///     typed arguments that implement <see cref="IUtf8SpanFormattable" />
///     directly into a caller-provided <c>Span&lt;byte&gt;</c>.
///     <see cref="TryFormatSlow{T1}" /> and its arity overloads accept
///     any argument type via runtime dispatch, including <c>string</c>
///     and <c>char</c>, but may box value types.
///     Grammar:
///     - Literal bytes (copied verbatim from the template)
///     - <c>{{</c> and <c>}}</c> produce a single <c>{</c> or <c>}</c>
///     - <c>{index[,alignment][:format]}</c> substitutes a formatted argument
///     Alignment:
///     - <c>{0,10}</c> right-justifies in a 10-byte field
///     - <c>{0,-10}</c> left-justifies in a 10-byte field
///     Format specifiers:
///     - ASCII specifiers only (e.g. <c>{0:X2}</c>, <c>{0:D5}</c>)
///     - Transcoded to <see cref="ReadOnlySpan{char}" /> for <see cref="IUtf8SpanFormattable" />
///     Current limitations:
///     - Up to 4 arguments
///     - Slow path handles format specifiers for <see cref="IUtf8SpanFormattable" /> types
///     only; <c>string</c>/<c>char</c>/<c>bool</c> alignment works but specifiers have no effect
/// </summary>
/// <summary>Parsed placeholder info including alignment and format specifier.</summary>
internal readonly struct Utf8PlaceholderInfo
{
    public readonly int Index;
    public readonly int Alignment;

    /// <summary>Start index of the format specifier in the template, or -1 if none.</summary>
    public readonly int SpecStart;

    /// <summary>Length of the format specifier in the template.</summary>
    public readonly int SpecLength;

    public Utf8PlaceholderInfo(int index, int alignment, int specStart, int specLength)
    {
        Index = index;
        Alignment = alignment;
        SpecStart = specStart;
        SpecLength = specLength;
    }

    public bool HasAlignment => Alignment != 0;
    public bool HasFormat => SpecLength > 0;

    public ReadOnlySpan<byte> GetFormatSpec(ReadOnlySpan<byte> template)
    {
        return HasFormat ? template.Slice(SpecStart, SpecLength) : default;
    }
}

public static partial class Utf8CompositeFormat
{
    // ──────────────────────────────────────────────
    //  Public API — constrained (fast path)
    // ──────────────────────────────────────────────

    /// <summary>
    ///     Format with one argument. Argument type must implement <see cref="IUtf8SpanFormattable" />.
    /// </summary>
    public static bool TryFormat<T1>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        out int written,
        T1 arg1)
        where T1 : IUtf8SpanFormattable
    {
        written = 0;
        int offset = 0;
        return TryFormatCore(format, destination, ref written, ref offset, arg1)
               == Utf8FormatResult.Success;
    }

    /// <summary>Format with two arguments.</summary>
    public static bool TryFormat<T1, T2>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        out int written,
        T1 arg1,
        T2 arg2)
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable
    {
        written = 0;
        int offset = 0;
        return TryFormatCore(format, destination, ref written, ref offset, arg1, arg2)
               == Utf8FormatResult.Success;
    }

    /// <summary>Format with three arguments.</summary>
    public static bool TryFormat<T1, T2, T3>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        out int written,
        T1 arg1,
        T2 arg2,
        T3 arg3)
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable
        where T3 : IUtf8SpanFormattable
    {
        written = 0;
        int offset = 0;
        return TryFormatCore(format, destination, ref written, ref offset, arg1, arg2, arg3)
               == Utf8FormatResult.Success;
    }

    /// <summary>Format with four arguments.</summary>
    public static bool TryFormat<T1, T2, T3, T4>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        out int written,
        T1 arg1,
        T2 arg2,
        T3 arg3,
        T4 arg4)
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable
        where T3 : IUtf8SpanFormattable
        where T4 : IUtf8SpanFormattable
    {
        written = 0;
        int offset = 0;
        return TryFormatCore(format, destination, ref written, ref offset, arg1, arg2, arg3, arg4)
               == Utf8FormatResult.Success;
    }

    // ──────────────────────────────────────────────
    //  Public API — slow path (runtime dispatch)
    // ──────────────────────────────────────────────

    /// <summary>
    ///     Format with one argument using runtime type dispatch.
    ///     Supports <c>string</c>, <c>char</c>, and any type
    ///     implementing <see cref="IUtf8SpanFormattable" />.
    ///     Value types implementing the interface may box via the runtime check.
    /// </summary>
    public static bool TryFormatSlow<T1>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        out int written,
        T1 arg1)
    {
        written = 0;
        int offset = 0;
        return TryFormatSlowCore(format, destination, ref written, ref offset, arg1)
               == Utf8FormatResult.Success;
    }

    /// <summary>Format with two arguments via runtime dispatch.</summary>
    public static bool TryFormatSlow<T1, T2>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        out int written,
        T1 arg1,
        T2 arg2)
    {
        written = 0;
        int offset = 0;
        return TryFormatSlowCore(format, destination, ref written, ref offset, arg1, arg2)
               == Utf8FormatResult.Success;
    }

    /// <summary>Format with three arguments via runtime dispatch.</summary>
    public static bool TryFormatSlow<T1, T2, T3>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        out int written,
        T1 arg1,
        T2 arg2,
        T3 arg3)
    {
        written = 0;
        int offset = 0;
        return TryFormatSlowCore(format, destination, ref written, ref offset, arg1, arg2, arg3)
               == Utf8FormatResult.Success;
    }

    /// <summary>Format with four arguments via runtime dispatch.</summary>
    public static bool TryFormatSlow<T1, T2, T3, T4>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        out int written,
        T1 arg1,
        T2 arg2,
        T3 arg3,
        T4 arg4)
    {
        written = 0;
        int offset = 0;
        return TryFormatSlowCore(format,
            destination,
            ref written,
            ref offset,
            arg1,
            arg2,
            arg3,
            arg4) == Utf8FormatResult.Success;
    }

    // ──────────────────────────────────────────────
    //  Prepared format factories
    // ──────────────────────────────────────────────

    /// <summary>Prepare a format template for repeated use with one argument.</summary>
    public static PreparedUtf8CompositeFormat<T1> Prepare<T1>(ReadOnlySpan<byte> format)
        where T1 : IUtf8SpanFormattable
    {
        return new PreparedUtf8CompositeFormat<T1>(format);
    }

    /// <summary>Prepare a format template for repeated use with two arguments.</summary>
    public static PreparedUtf8CompositeFormat<T1, T2> Prepare<T1, T2>(ReadOnlySpan<byte> format)
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable
    {
        return new PreparedUtf8CompositeFormat<T1, T2>(format);
    }

    /// <summary>Prepare a format template for repeated use with three arguments.</summary>
    public static PreparedUtf8CompositeFormat<T1, T2, T3> Prepare<T1, T2, T3>(ReadOnlySpan<byte> format)
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable
        where T3 : IUtf8SpanFormattable
    {
        return new PreparedUtf8CompositeFormat<T1, T2, T3>(format);
    }

    /// <summary>Prepare a format template for repeated use with four arguments.</summary>
    public static PreparedUtf8CompositeFormat<T1, T2, T3, T4> Prepare<T1, T2, T3, T4>(ReadOnlySpan<byte> format)
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable
        where T3 : IUtf8SpanFormattable
        where T4 : IUtf8SpanFormattable
    {
        return new PreparedUtf8CompositeFormat<T1, T2, T3, T4>(format);
    }

    // ──────────────────────────────────────────────
    //  Constrained core scan loops
    //  Uses IUtf8SpanFormattable.TryFormat directly
    // ──────────────────────────────────────────────

    private static Utf8FormatResult TryFormatCore<T1>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        ref int written,
        scoped ref int offset,
        T1 arg1)
        where T1 : IUtf8SpanFormattable
    {
        while (offset < format.Length)
        {
            byte b = format[offset];
            if (b == (byte)'{')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'{')
                {
                    if (!TryCopyByte((byte)'{', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                Utf8PlaceholderInfo info = ParsePlaceholder(format, ref offset);
                switch (info.Index)
                {
                    case 0:
                        if (
                            !TryFormatAligned(arg1,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment)
                        )
                        {
                            return Utf8FormatResult.InsufficientSpace;
                        }

                        break;
                    default:
                        ThrowIndexOutOfRange(info.Index);
                        break;
                }
            }
            else if (b == (byte)'}')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'}')
                {
                    if (!TryCopyByte((byte)'}', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                ThrowUnmatchedClose();
            }
            else
            {
                if (!TryCopyByte(b, destination, ref written))
                {
                    return Utf8FormatResult.InsufficientSpace;
                }

                offset++;
            }
        }

        return Utf8FormatResult.Success;
    }

    private static Utf8FormatResult TryFormatCore<T1, T2>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        ref int written,
        scoped ref int offset,
        T1 arg1,
        T2 arg2)
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable
    {
        while (offset < format.Length)
        {
            byte b = format[offset];
            if (b == (byte)'{')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'{')
                {
                    if (!TryCopyByte((byte)'{', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                Utf8PlaceholderInfo info = ParsePlaceholder(format, ref offset);
                switch (info.Index)
                {
                    case 0:
                        if (
                            !TryFormatAligned(arg1,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment)
                        )
                        {
                            return Utf8FormatResult.InsufficientSpace;
                        }

                        break;
                    case 1:
                        if (
                            !TryFormatAligned(arg2,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment)
                        )
                        {
                            return Utf8FormatResult.InsufficientSpace;
                        }

                        break;
                    default:
                        ThrowIndexOutOfRange(info.Index);
                        break;
                }
            }
            else if (b == (byte)'}')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'}')
                {
                    if (!TryCopyByte((byte)'}', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                ThrowUnmatchedClose();
            }
            else
            {
                if (!TryCopyByte(b, destination, ref written))
                {
                    return Utf8FormatResult.InsufficientSpace;
                }

                offset++;
            }
        }

        return Utf8FormatResult.Success;
    }

    private static Utf8FormatResult TryFormatCore<T1, T2, T3>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        ref int written,
        scoped ref int offset,
        T1 arg1,
        T2 arg2,
        T3 arg3)
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable
        where T3 : IUtf8SpanFormattable
    {
        while (offset < format.Length)
        {
            byte b = format[offset];
            if (b == (byte)'{')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'{')
                {
                    if (!TryCopyByte((byte)'{', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                Utf8PlaceholderInfo info = ParsePlaceholder(format, ref offset);
                switch (info.Index)
                {
                    case 0:
                        if (
                            !TryFormatAligned(arg1,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment)
                        )
                        {
                            return Utf8FormatResult.InsufficientSpace;
                        }

                        break;
                    case 1:
                        if (
                            !TryFormatAligned(arg2,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment)
                        )
                        {
                            return Utf8FormatResult.InsufficientSpace;
                        }

                        break;
                    case 2:
                        if (
                            !TryFormatAligned(arg3,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment)
                        )
                        {
                            return Utf8FormatResult.InsufficientSpace;
                        }

                        break;
                    default:
                        ThrowIndexOutOfRange(info.Index);
                        break;
                }
            }
            else if (b == (byte)'}')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'}')
                {
                    if (!TryCopyByte((byte)'}', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                ThrowUnmatchedClose();
            }
            else
            {
                if (!TryCopyByte(b, destination, ref written))
                {
                    return Utf8FormatResult.InsufficientSpace;
                }

                offset++;
            }
        }

        return Utf8FormatResult.Success;
    }

    private static Utf8FormatResult TryFormatCore<T1, T2, T3, T4>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        ref int written,
        scoped ref int offset,
        T1 arg1,
        T2 arg2,
        T3 arg3,
        T4 arg4)
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable
        where T3 : IUtf8SpanFormattable
        where T4 : IUtf8SpanFormattable
    {
        while (offset < format.Length)
        {
            byte b = format[offset];
            if (b == (byte)'{')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'{')
                {
                    if (!TryCopyByte((byte)'{', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                Utf8PlaceholderInfo info = ParsePlaceholder(format, ref offset);
                switch (info.Index)
                {
                    case 0:
                        if (
                            !TryFormatAligned(arg1,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment)
                        )
                        {
                            return Utf8FormatResult.InsufficientSpace;
                        }

                        break;
                    case 1:
                        if (
                            !TryFormatAligned(arg2,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment)
                        )
                        {
                            return Utf8FormatResult.InsufficientSpace;
                        }

                        break;
                    case 2:
                        if (
                            !TryFormatAligned(arg3,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment)
                        )
                        {
                            return Utf8FormatResult.InsufficientSpace;
                        }

                        break;
                    case 3:
                        if (
                            !TryFormatAligned(arg4,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment)
                        )
                        {
                            return Utf8FormatResult.InsufficientSpace;
                        }

                        break;
                    default:
                        ThrowIndexOutOfRange(info.Index);
                        break;
                }
            }
            else if (b == (byte)'}')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'}')
                {
                    if (!TryCopyByte((byte)'}', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                ThrowUnmatchedClose();
            }
            else
            {
                if (!TryCopyByte(b, destination, ref written))
                {
                    return Utf8FormatResult.InsufficientSpace;
                }

                offset++;
            }
        }

        return Utf8FormatResult.Success;
    }

    // ──────────────────────────────────────────────
    //  Slow core scan loops (runtime dispatch)
    // ──────────────────────────────────────────────

    private static Utf8FormatResult TryFormatSlowCore<T1>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        ref int written,
        scoped ref int offset,
        T1 arg1)
    {
        while (offset < format.Length)
        {
            byte b = format[offset];
            if (b == (byte)'{')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'{')
                {
                    if (!TryCopyByte((byte)'{', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                Utf8PlaceholderInfo info = ParsePlaceholder(format, ref offset);
                switch (info.Index)
                {
                    case 0:
                        {
                            Utf8FormatResult fmtResult = Utf8ValueFormatter.TryFormatAligned(arg1,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment);
                            if (fmtResult != Utf8FormatResult.Success)
                            {
                                return fmtResult;
                            }

                            break;
                        }
                    default:
                        ThrowIndexOutOfRange(info.Index);
                        break;
                }
            }
            else if (b == (byte)'}')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'}')
                {
                    if (!TryCopyByte((byte)'}', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                ThrowUnmatchedClose();
            }
            else
            {
                if (!TryCopyByte(b, destination, ref written))
                {
                    return Utf8FormatResult.InsufficientSpace;
                }

                offset++;
            }
        }

        return Utf8FormatResult.Success;
    }

    private static Utf8FormatResult TryFormatSlowCore<T1, T2>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        ref int written,
        scoped ref int offset,
        T1 arg1,
        T2 arg2)
    {
        while (offset < format.Length)
        {
            byte b = format[offset];
            if (b == (byte)'{')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'{')
                {
                    if (!TryCopyByte((byte)'{', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                Utf8PlaceholderInfo info = ParsePlaceholder(format, ref offset);
                switch (info.Index)
                {
                    case 0:
                        {
                            Utf8FormatResult fmtResult = Utf8ValueFormatter.TryFormatAligned(arg1,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment);
                            if (fmtResult != Utf8FormatResult.Success)
                            {
                                return fmtResult;
                            }

                            break;
                        }
                    case 1:
                        {
                            Utf8FormatResult fmtResult = Utf8ValueFormatter.TryFormatAligned(arg2,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment);
                            if (fmtResult != Utf8FormatResult.Success)
                            {
                                return fmtResult;
                            }

                            break;
                        }
                    default:
                        ThrowIndexOutOfRange(info.Index);
                        break;
                }
            }
            else if (b == (byte)'}')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'}')
                {
                    if (!TryCopyByte((byte)'}', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                ThrowUnmatchedClose();
            }
            else
            {
                if (!TryCopyByte(b, destination, ref written))
                {
                    return Utf8FormatResult.InsufficientSpace;
                }

                offset++;
            }
        }

        return Utf8FormatResult.Success;
    }

    private static Utf8FormatResult TryFormatSlowCore<T1, T2, T3>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        ref int written,
        scoped ref int offset,
        T1 arg1,
        T2 arg2,
        T3 arg3)
    {
        while (offset < format.Length)
        {
            byte b = format[offset];
            if (b == (byte)'{')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'{')
                {
                    if (!TryCopyByte((byte)'{', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                Utf8PlaceholderInfo info = ParsePlaceholder(format, ref offset);
                switch (info.Index)
                {
                    case 0:
                        {
                            Utf8FormatResult fmtResult = Utf8ValueFormatter.TryFormatAligned(arg1,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment);
                            if (fmtResult != Utf8FormatResult.Success)
                            {
                                return fmtResult;
                            }

                            break;
                        }
                    case 1:
                        {
                            Utf8FormatResult fmtResult = Utf8ValueFormatter.TryFormatAligned(arg2,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment);
                            if (fmtResult != Utf8FormatResult.Success)
                            {
                                return fmtResult;
                            }

                            break;
                        }
                    case 2:
                        {
                            Utf8FormatResult fmtResult = Utf8ValueFormatter.TryFormatAligned(arg3,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment);
                            if (fmtResult != Utf8FormatResult.Success)
                            {
                                return fmtResult;
                            }

                            break;
                        }
                    default:
                        ThrowIndexOutOfRange(info.Index);
                        break;
                }
            }
            else if (b == (byte)'}')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'}')
                {
                    if (!TryCopyByte((byte)'}', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                ThrowUnmatchedClose();
            }
            else
            {
                if (!TryCopyByte(b, destination, ref written))
                {
                    return Utf8FormatResult.InsufficientSpace;
                }

                offset++;
            }
        }

        return Utf8FormatResult.Success;
    }

    private static Utf8FormatResult TryFormatSlowCore<T1, T2, T3, T4>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        ref int written,
        scoped ref int offset,
        T1 arg1,
        T2 arg2,
        T3 arg3,
        T4 arg4)
    {
        while (offset < format.Length)
        {
            byte b = format[offset];
            if (b == (byte)'{')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'{')
                {
                    if (!TryCopyByte((byte)'{', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                Utf8PlaceholderInfo info = ParsePlaceholder(format, ref offset);
                switch (info.Index)
                {
                    case 0:
                        {
                            Utf8FormatResult fmtResult = Utf8ValueFormatter.TryFormatAligned(arg1,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment);
                            if (fmtResult != Utf8FormatResult.Success)
                            {
                                return fmtResult;
                            }

                            break;
                        }
                    case 1:
                        {
                            Utf8FormatResult fmtResult = Utf8ValueFormatter.TryFormatAligned(arg2,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment);
                            if (fmtResult != Utf8FormatResult.Success)
                            {
                                return fmtResult;
                            }

                            break;
                        }
                    case 2:
                        {
                            Utf8FormatResult fmtResult = Utf8ValueFormatter.TryFormatAligned(arg3,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment);
                            if (fmtResult != Utf8FormatResult.Success)
                            {
                                return fmtResult;
                            }

                            break;
                        }
                    case 3:
                        {
                            Utf8FormatResult fmtResult = Utf8ValueFormatter.TryFormatAligned(arg4,
                                destination,
                                ref written,
                                info.GetFormatSpec(format),
                                info.Alignment);
                            if (fmtResult != Utf8FormatResult.Success)
                            {
                                return fmtResult;
                            }

                            break;
                        }
                    default:
                        ThrowIndexOutOfRange(info.Index);
                        break;
                }
            }
            else if (b == (byte)'}')
            {
                if (offset + 1 < format.Length && format[offset + 1] == (byte)'}')
                {
                    if (!TryCopyByte((byte)'}', destination, ref written))
                    {
                        return Utf8FormatResult.InsufficientSpace;
                    }

                    offset += 2;
                    continue;
                }

                ThrowUnmatchedClose();
            }
            else
            {
                if (!TryCopyByte(b, destination, ref written))
                {
                    return Utf8FormatResult.InsufficientSpace;
                }

                offset++;
            }
        }

        return Utf8FormatResult.Success;
    }

    // ──────────────────────────────────────────────
    //  Index parser (byte-native)
    // ──────────────────────────────────────────────

    /// <summary>
    ///     Parse {index} starting after the opening {.
    ///     Advances offset past the closing }.
    /// </summary>
    private static Utf8PlaceholderInfo ParsePlaceholder(
        ReadOnlySpan<byte> format,
        scoped ref int offset)
    {
        offset++; // skip {
        int index = 0;
        bool hasDigit = false;

        // Parse index
        while (offset < format.Length)
        {
            byte b = format[offset];
            if (b >= (byte)'0' && b <= (byte)'9')
            {
                hasDigit = true;
                index = index * 10 + (b - (byte)'0');
                offset++;
            }
            else
            {
                break;
            }
        }

        if (!hasDigit)
        {
            ThrowMalformed("Empty placeholder index.");
        }

        int alignment = 0;
        bool leftJustify = false;

        // Parse optional alignment: ,[ws][-][digits]
        if (offset < format.Length && format[offset] == (byte)',')
        {
            offset++; // skip ,

            while (offset < format.Length && format[offset] == (byte)' ')
            {
                offset++;
            }

            if (offset < format.Length && format[offset] == (byte)'-')
            {
                leftJustify = true;
                offset++;
            }

            bool hasAlign = false;
            while (offset < format.Length)
            {
                byte b = format[offset];
                if (b >= (byte)'0' && b <= (byte)'9')
                {
                    hasAlign = true;
                    alignment = alignment * 10 + (b - (byte)'0');
                    offset++;
                }
                else
                {
                    break;
                }
            }

            if (!hasAlign)
            {
                ThrowMalformed("Expected alignment digits after ','.");
            }

            if (leftJustify)
            {
                alignment = -alignment;
            }
        }

        // Parse optional format specifier: :[bytes]
        int specStart = -1;
        int specLength = 0;
        if (offset < format.Length && format[offset] == (byte)':')
        {
            offset++; // skip :
            specStart = offset;
            while (offset < format.Length)
            {
                byte b = format[offset];
                if (b == (byte)'}')
                {
                    break;
                }

                if (b == (byte)'{')
                {
                    ThrowMalformed("Nested '{' in format specifier.");
                }

                offset++;
            }

            specLength = offset - specStart;
        }

        // Expect closing }
        if (offset >= format.Length || format[offset] != (byte)'}')
        {
            ThrowMalformed("Missing closing '}'.");
        }

        offset++; // skip }

        return new Utf8PlaceholderInfo(index, alignment, specStart, specLength);
    }

    // ──────────────────────────────────────────────
    //  Constrained formatting helper
    // ──────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryFormatUtf8<T>(
        T value,
        Span<byte> destination,
        ref int written,
        ReadOnlySpan<byte> formatSpec = default)
        where T : IUtf8SpanFormattable
    {
        if (formatSpec.IsEmpty)
        {
            if (!value.TryFormat(destination.Slice(written), out int n, default, null))
            {
                return false;
            }

            written += n;
            return true;
        }

        // Use shared TranscodeFormatSpec for specifier validation
        Span<char> charBuf = stackalloc char[formatSpec.Length];
        Utf8ValueFormatter.TranscodeFormatSpec(formatSpec, charBuf);

        if (!value.TryFormat(destination.Slice(written), out int n2, charBuf, null))
        {
            return false;
        }

        written += n2;
        return true;
    }

    /// <summary>Format a value with optional specifier and alignment. Handles padding.</summary>
    internal static bool TryFormatAligned<T>(
        T value,
        Span<byte> destination,
        ref int written,
        ReadOnlySpan<byte> formatSpec,
        int alignment)
        where T : IUtf8SpanFormattable
    {
        int before = written;
        if (!TryFormatUtf8(value, destination, ref written, formatSpec))
        {
            return false;
        }

        if (alignment == 0)
        {
            return true;
        }

        // Delegate to shared ApplyAlignment
        return Utf8ValueFormatter.ApplyAlignment(destination, ref written, before, alignment)
               == Utf8FormatResult.Success;
    }

    // ──────────────────────────────────────────────
    //  Runtime-dispatch value formatting
    //  Used by TryFormatSlow for string/char and
    //  as fallback for non-IUtf8SpanFormattable types
    // ──────────────────────────────────────────────

    // TryFormatValue and ApplyAlignment have moved to Utf8ValueFormatter.

    // ──────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryCopyByte(byte b, Span<byte> destination, ref int written)
    {
        if (written >= destination.Length)
        {
            return false;
        }

        destination[written++] = b;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryAdvance(ref int written, int n)
    {
        written += n;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMalformed(string message)
    {
        throw new FormatException($"Malformed UTF-8 format template: {message}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUnmatchedClose()
    {
        throw new FormatException("Malformed UTF-8 format template: unexpected '}' outside placeholder.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIndexOutOfRange(int index)
    {
        throw new FormatException($"Argument index {index} is not available. Check arity and placeholder indices.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInsufficientSpace()
    {
        throw new InvalidOperationException("Formatting failed: unsupported type or output too large.");
    }

    // ──────────────────────────────────────────────
    //  Writer/builder API
    //  Growth-loop strategy: start with a reasonable
    //  estimate, double on each failure, stop at
    //  100 MB safety limit.
    // ──────────────────────────────────────────────

    /// <summary>Format one arg into an IBufferWriter<byte>.</summary>
    public static void Format<TBufferWriter, T1>(
        ref TBufferWriter writer,
        ReadOnlySpan<byte> format,
        T1 arg1)
        where TBufferWriter : IBufferWriter<byte>
        where T1 : IUtf8SpanFormattable
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = writer.GetSpan(capacity);
            Utf8FormatResult result = TryFormatCore(format, span, ref written, ref offset, arg1);
            if (result == Utf8FormatResult.Success)
            {
                writer.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }

    /// <summary>Format two args into an IBufferWriter<byte>.</summary>
    public static void Format<TBufferWriter, T1, T2>(
        ref TBufferWriter writer,
        ReadOnlySpan<byte> format,
        T1 arg1,
        T2 arg2)
        where TBufferWriter : IBufferWriter<byte>
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = writer.GetSpan(capacity);
            Utf8FormatResult result = TryFormatCore(format, span, ref written, ref offset, arg1, arg2);
            if (result == Utf8FormatResult.Success)
            {
                writer.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }

    /// <summary>Format three args into an IBufferWriter<byte>.</summary>
    public static void Format<TBufferWriter, T1, T2, T3>(
        ref TBufferWriter writer,
        ReadOnlySpan<byte> format,
        T1 arg1,
        T2 arg2,
        T3 arg3)
        where TBufferWriter : IBufferWriter<byte>
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable
        where T3 : IUtf8SpanFormattable
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = writer.GetSpan(capacity);
            Utf8FormatResult result = TryFormatCore(format, span, ref written, ref offset, arg1, arg2, arg3);
            if (result == Utf8FormatResult.Success)
            {
                writer.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }

    /// <summary>Format four args into an IBufferWriter<byte>.</summary>
    public static void Format<TBufferWriter, T1, T2, T3, T4>(
        ref TBufferWriter writer,
        ReadOnlySpan<byte> format,
        T1 arg1,
        T2 arg2,
        T3 arg3,
        T4 arg4)
        where TBufferWriter : IBufferWriter<byte>
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable
        where T3 : IUtf8SpanFormattable
        where T4 : IUtf8SpanFormattable
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = writer.GetSpan(capacity);
            Utf8FormatResult result = TryFormatCore(format,
                span,
                ref written,
                ref offset,
                arg1,
                arg2,
                arg3,
                arg4);
            if (result == Utf8FormatResult.Success)
            {
                writer.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }

    /// <summary>Format one arg via runtime dispatch into an IBufferWriter<byte>.</summary>
    public static void FormatSlow<TBufferWriter, T1>(
        ref TBufferWriter writer,
        ReadOnlySpan<byte> format,
        T1 arg1)
        where TBufferWriter : IBufferWriter<byte>
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = writer.GetSpan(capacity);
            Utf8FormatResult result = TryFormatSlowCore(format, span, ref written, ref offset, arg1);
            if (result == Utf8FormatResult.Success)
            {
                writer.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }

    /// <summary>Format two args via runtime dispatch into an IBufferWriter<byte>.</summary>
    public static void FormatSlow<TBufferWriter, T1, T2>(
        ref TBufferWriter writer,
        ReadOnlySpan<byte> format,
        T1 arg1,
        T2 arg2)
        where TBufferWriter : IBufferWriter<byte>
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = writer.GetSpan(capacity);
            Utf8FormatResult result = TryFormatSlowCore(format, span, ref written, ref offset, arg1, arg2);
            if (result == Utf8FormatResult.Success)
            {
                writer.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }

    /// <summary>Format three args via runtime dispatch into an IBufferWriter<byte>.</summary>
    public static void FormatSlow<TBufferWriter, T1, T2, T3>(
        ref TBufferWriter writer,
        ReadOnlySpan<byte> format,
        T1 arg1,
        T2 arg2,
        T3 arg3)
        where TBufferWriter : IBufferWriter<byte>
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = writer.GetSpan(capacity);
            Utf8FormatResult result = TryFormatSlowCore(format, span, ref written, ref offset, arg1, arg2, arg3);
            if (result == Utf8FormatResult.Success)
            {
                writer.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }

    /// <summary>Format four args via runtime dispatch into an IBufferWriter<byte>.</summary>
    public static void FormatSlow<TBufferWriter, T1, T2, T3, T4>(
        ref TBufferWriter writer,
        ReadOnlySpan<byte> format,
        T1 arg1,
        T2 arg2,
        T3 arg3,
        T4 arg4)
        where TBufferWriter : IBufferWriter<byte>
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = writer.GetSpan(capacity);
            Utf8FormatResult result = TryFormatSlowCore(format,
                span,
                ref written,
                ref offset,
                arg1,
                arg2,
                arg3,
                arg4);
            if (result == Utf8FormatResult.Success)
            {
                writer.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }

    /// <summary>Append formatted output to a Utf8Builder.</summary>
    public static void AppendFormatUtf8<T1>(
        ref Utf8Builder builder,
        ReadOnlySpan<byte> format,
        T1 arg1)
        where T1 : IUtf8SpanFormattable
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = builder.GetSpan(capacity);
            Utf8FormatResult result = TryFormatCore(format, span, ref written, ref offset, arg1);
            if (result == Utf8FormatResult.Success)
            {
                builder.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }

    /// <summary>Append formatted output with two args.</summary>
    public static void AppendFormatUtf8<T1, T2>(
        ref Utf8Builder builder,
        ReadOnlySpan<byte> format,
        T1 arg1,
        T2 arg2)
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = builder.GetSpan(capacity);
            Utf8FormatResult result = TryFormatCore(format, span, ref written, ref offset, arg1, arg2);
            if (result == Utf8FormatResult.Success)
            {
                builder.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }

    /// <summary>Append formatted output with three args.</summary>
    public static void AppendFormatUtf8<T1, T2, T3>(
        ref Utf8Builder builder,
        ReadOnlySpan<byte> format,
        T1 arg1,
        T2 arg2,
        T3 arg3)
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable
        where T3 : IUtf8SpanFormattable
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = builder.GetSpan(capacity);
            Utf8FormatResult result = TryFormatCore(format, span, ref written, ref offset, arg1, arg2, arg3);
            if (result == Utf8FormatResult.Success)
            {
                builder.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }

    /// <summary>Append formatted output with four args.</summary>
    public static void AppendFormatUtf8<T1, T2, T3, T4>(
        ref Utf8Builder builder,
        ReadOnlySpan<byte> format,
        T1 arg1,
        T2 arg2,
        T3 arg3,
        T4 arg4)
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable
        where T3 : IUtf8SpanFormattable
        where T4 : IUtf8SpanFormattable
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = builder.GetSpan(capacity);
            Utf8FormatResult result = TryFormatCore(format,
                span,
                ref written,
                ref offset,
                arg1,
                arg2,
                arg3,
                arg4);
            if (result == Utf8FormatResult.Success)
            {
                builder.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }

    /// <summary>Append formatted output via runtime dispatch.</summary>
    public static void AppendFormatUtf8Slow<T1>(
        ref Utf8Builder builder,
        ReadOnlySpan<byte> format,
        T1 arg1)
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = builder.GetSpan(capacity);
            Utf8FormatResult result = TryFormatSlowCore(format, span, ref written, ref offset, arg1);
            if (result == Utf8FormatResult.Success)
            {
                builder.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }

    /// <summary>Append formatted output with two args via runtime dispatch.</summary>
    public static void AppendFormatUtf8Slow<T1, T2>(
        ref Utf8Builder builder,
        ReadOnlySpan<byte> format,
        T1 arg1,
        T2 arg2)
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = builder.GetSpan(capacity);
            Utf8FormatResult result = TryFormatSlowCore(format, span, ref written, ref offset, arg1, arg2);
            if (result == Utf8FormatResult.Success)
            {
                builder.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }

    /// <summary>Append formatted output with three args via runtime dispatch.</summary>
    public static void AppendFormatUtf8Slow<T1, T2, T3>(
        ref Utf8Builder builder,
        ReadOnlySpan<byte> format,
        T1 arg1,
        T2 arg2,
        T3 arg3)
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = builder.GetSpan(capacity);
            Utf8FormatResult result = TryFormatSlowCore(format, span, ref written, ref offset, arg1, arg2, arg3);
            if (result == Utf8FormatResult.Success)
            {
                builder.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }

    /// <summary>Append formatted output with four args via runtime dispatch.</summary>
    public static void AppendFormatUtf8Slow<T1, T2, T3, T4>(
        ref Utf8Builder builder,
        ReadOnlySpan<byte> format,
        T1 arg1,
        T2 arg2,
        T3 arg3,
        T4 arg4)
    {
        int capacity = Math.Max(format.Length + 256, 256);
        const int maxCap = 1024 * 1024 * 100;
        while (true)
        {
            int written = 0,
                offset = 0;
            Span<byte> span = builder.GetSpan(capacity);
            Utf8FormatResult result = TryFormatSlowCore(format,
                span,
                ref written,
                ref offset,
                arg1,
                arg2,
                arg3,
                arg4);
            if (result == Utf8FormatResult.Success)
            {
                builder.Advance(written);
                return;
            }

            if (result != Utf8FormatResult.InsufficientSpace)
            {
                ThrowInsufficientSpace();
            }

            if (capacity >= maxCap)
            {
                ThrowInsufficientSpace();
            }

            capacity = Math.Min(capacity * 2, maxCap);
        }
    }
    // CachedFormatter<T> and FormatterCache<T> have moved to Utf8ValueFormatter.
}
