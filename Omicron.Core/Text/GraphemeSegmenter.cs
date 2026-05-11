using System.Buffers;
using System.Text;

namespace Omicron.Core.Text;

/// <summary>
/// Segments UTF-8 byte spans into grapheme clusters using <see cref="Rune"/> decoding
/// and a minimal hardcoded table for:
///   - Zero-width joiner (U+200D) sequences (emoji ZWJ sequences)
///   - Combining marks (General Categories Mn, Mc, Me)
///   - Regional indicator pairs (flags)
///
/// Limitations (MVP):
///   - Does not implement full Unicode Grapheme Cluster Boundary Rules (UAX #29).
///   - Thai/Lao/Devanagari reordering is not segmented — each rune is its own cluster.
///   - Indic conjuncts are not merged.
///   - Use <see cref="System.Globalization.StringInfo"/> for a managed baseline reference.
/// </summary>
public static class GraphemeSegmenter
{
    /// <summary>
    /// Segment UTF-8 bytes into grapheme clusters.
    /// Returns an empty list for empty input.
    /// </summary>
    public static List<GraphemeCluster> SegmentUtf8(ReadOnlySpan<byte> utf8)
    {
        var result = new List<GraphemeCluster>();
        if (utf8.IsEmpty)
            return result;

        int offset = 0;
        int clusterStartOffset = 0;
        int runeCount = 0;
        bool hasClusterStart = false;
        Rune? previousRune = null;

        while (offset < utf8.Length)
        {
            var status = Rune.DecodeFromUtf8(utf8[offset..], out Rune rune, out int consumed);
            if (status != OperationStatus.Done)
            {
                // Invalid UTF-8 sequence: emit remaining as one cluster
                if (!hasClusterStart)
                {
                    result.Add(new GraphemeCluster(offset, utf8.Length - offset, 1));
                }
                else
                {
                    result.Add(new GraphemeCluster(clusterStartOffset, offset - clusterStartOffset, runeCount));
                }
                return result;
            }

            int runeValue = rune.Value;

            // Check for boundary: does this rune continue the current cluster?
            bool continues = false;

            if (previousRune.HasValue)
            {
                // ZWJ sequence: previous is ZWJ
                if (previousRune.Value.Value == 0x200D)
                {
                    continues = true;
                }
                // Combining mark attaches to base
                else if (IsCombiningMark(runeValue))
                {
                    continues = true;
                }
                // Regional indicator pair: both are regional indicators
                else if (IsRegionalIndicator(previousRune.Value.Value) && IsRegionalIndicator(runeValue) && runeCount < 2)
                {
                    continues = true;
                }
                // Zero-width rune following a base
                else if (IsZeroWidthModifier(runeValue))
                {
                    continues = true;
                }
            }

            if (!continues && hasClusterStart)
            {
                // Emit the previous cluster
                result.Add(new GraphemeCluster(
                    clusterStartOffset,
                    offset - clusterStartOffset,
                    runeCount));
                clusterStartOffset = offset;
                runeCount = 0;
            }
            else if (!continues && !hasClusterStart)
            {
                hasClusterStart = true;
                clusterStartOffset = offset;
            }

            runeCount++;
            previousRune = rune;
            offset += consumed;
        }

        // Emit the last cluster
        if (hasClusterStart || runeCount > 0)
        {
            result.Add(new GraphemeCluster(
                clusterStartOffset,
                offset - clusterStartOffset,
                runeCount));
        }

        return result;
    }

    /// <summary>
    /// Returns true if the rune is a combining mark (General Categories Mn, Mc, Me).
    /// </summary>
    private static bool IsCombiningMark(int code)
    {
        // Exclude format characters handled by other branches (ZWJ, variation selectors, etc.)
        if (code == 0x200B || code == 0x200C || code == 0x200D ||
            (code >= 0xFE00 && code <= 0xFE0F) ||
            (code >= 0xE0100 && code <= 0xE01EF))
            return false;

        // Delegate to CellWidthCalculator's combining ranges
        return CellWidthCalculator.GetWidth(code) == 0
            && code > 0x0300;
    }

    /// <summary>
    /// Returns true if this rune is a regional indicator symbol (U+1F1E6–U+1F1FF).
    /// </summary>
    private static bool IsRegionalIndicator(int code)
        => code >= 0x1F1E6 && code <= 0x1F1FF;

    /// <summary>
    /// Returns true if this rune should attach to the preceding base character
    /// as a zero-width modifier (variation selectors, zero-width joiners, etc.).
    /// </summary>
    private static bool IsZeroWidthModifier(int code)
    {
        return code == 0x200D  // ZWJ
            || (code >= 0xFE00 && code <= 0xFE0F)  // Variation selectors
            || (code >= 0xE0100 && code <= 0xE01EF) // Variation selectors supplement
            || code == 0x200C  // ZWNJ
            || code == 0x200B; // ZWSP
    }
}
