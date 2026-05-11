using System.Text;

namespace Omicron.Core.Diff;

/// <summary>
/// Facade for line-based diff operations. Selects the best strategy based on input size.
/// No fabricated partial edits — if all strategies decline, returns an explicit omitted status.
/// </summary>
internal static class TextDiffEngine
{
    // Strategy thresholds
    private const int TraceThreshold = 2000; // Trace Myers handles inputs up to this size
    private const int MaxAnyExact = 50_000;  // Absolute max for any exact strategy

    private static readonly TraceMyersDiffStrategy TraceStrategy = new();
    private static readonly DivideAndConquerMyersDiffStrategy DcStrategy = new();

    public static TextDiffResult DiffLines(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines,
        TextDiffOptions? options = null)
    {
        options ??= TextDiffOptions.Default;

        int N = oldLines.Count;
        int M = newLines.Count;

        // Apply caller's MaxLineCount if set (stricter than internal thresholds)
        int maxLines = options.MaxLineCount > 0 ? options.MaxLineCount : MaxAnyExact;

        if (N > maxLines || M > maxLines)
        {
            return new TextDiffResult(
                [], N, M,
                IsTruncated: true,
                TruncationReason: $"Input exceeds MaxLineCount ({maxLines}). Old={N}, New={M}");
        }

        // Normalize to integer codes (shared across strategies)
        var normalizer = new LineNormalizer(options);

        int[] oldCodes = new int[N];
        int[] newCodes = new int[M];
        for (int i = 0; i < N; i++) oldCodes[i] = normalizer.GetCode(oldLines[i]);
        for (int i = 0; i < M; i++) newCodes[i] = normalizer.GetCode(newLines[i]);

        // Fast paths
        if (N == 0 && M == 0)
            return new TextDiffResult([], 0, 0);
        if (N == 0)
            return new TextDiffResult([new TextDiffEdit(0, 0, 0, M)], 0, M);
        if (M == 0)
            return new TextDiffResult([new TextDiffEdit(0, N, 0, 0)], N, 0);

        // Try strategies in order
        List<TextDiffEdit>? edits = null;

        // Strategy 1: Trace Myers (good for small/medium inputs)
        if (N <= TraceThreshold && M <= TraceThreshold)
        {
            edits = TraceStrategy.Compute(oldCodes, N, newCodes, M);
        }

        // Strategy 2: Divide-and-conquer Myers (lower memory, handles larger inputs)
        edits ??= DcStrategy.Compute(oldCodes, N, newCodes, M);

        if (edits is null)
        {
            // All exact strategies declined — return omitted status
            return new TextDiffResult(
                [], N, M,
                IsTruncated: true,
                TruncationReason: $"All diff strategies declined. Old={N}, New={M}");
        }

        // Normalize edits (merge adjacent insert+delete into replacements)
        edits = TextDiffEditNormalizer.Normalize(edits);

        return new TextDiffResult(edits, N, M);
    }

    /// <summary>Normalizes lines to integer codes.</summary>
    internal sealed class LineNormalizer
    {
        private readonly TextDiffOptions _options;
        private readonly Dictionary<string, int> _codes = new(StringComparer.Ordinal);
        private int _nextCode;

        public LineNormalizer(TextDiffOptions options) => _options = options;

        public int GetCode(string line)
        {
            string key = _options.TrimWhitespace ? line.Trim() : line;

            if (_options.IgnoreWhitespaceRuns)
            {
                var sb = new StringBuilder(key.Length);
                bool inSpace = false;
                foreach (char c in key)
                {
                    if (char.IsWhiteSpace(c))
                    {
                        if (!inSpace)
                        {
                            sb.Append(' ');
                            inSpace = true;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                        inSpace = false;
                    }
                }
                key = sb.ToString().Trim();
            }

            if (_options.IgnoreCase) key = key.ToUpperInvariant();

            if (!_codes.TryGetValue(key, out var code))
            {
                code = _nextCode++;
                _codes[key] = code;
            }
            return code;
        }
    }
}
