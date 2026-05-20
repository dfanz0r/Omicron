// Fallback implementation for the source generator hook.
// During Phase 6 (source-generated custom formatter fast paths), this file
// is removed or excluded from the build and a generated file provides the
// implementation instead, emitting explicit typeof(T) == typeof(...) branches
// for types registered via [Utf8FormatterAttribute{T}].

namespace Omicron.Core.Text
{
    internal static partial class Utf8ValueFormatter
    {
        /// <summary>
        /// Fallback: no source generator present. Returns <see cref="Utf8FormatResult.NoFormatter"/>.
        /// Phase 6 removes this file and a generated implementation takes its place.
        /// </summary>
        private static partial Utf8FormatResult TryFormatGenerated<T>(
            ref T value,
            Span<byte> destination,
            out int written,
            ReadOnlySpan<byte> formatSpec,
            IFormatProvider? provider)
        {
            written = 0;
            return Utf8FormatResult.NoFormatter;
        }
    }
}
