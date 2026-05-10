using System.Text;

namespace Omicron.Core.Diff;

/// <summary>
/// Options for unified diff rendering.
/// </summary>
internal sealed record UnifiedDiffRenderOptions
{
    public int ContextLines { get; init; } = 3;
    public int MaxOutputLines { get; init; } = 2000;
    public int MaxOutputBytes { get; init; } = 50 * 1024;
}

/// <summary>
/// Renders a unified diff from old/new lines and a TextDiffResult.
/// Includes a truncation marker when output limits are reached.
/// </summary>
internal static class UnifiedDiffRenderer
{
    public static string Render(
        string oldPath,
        string newPath,
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines,
        TextDiffResult diff,
        UnifiedDiffRenderOptions? options = null)
    {
        options ??= new UnifiedDiffRenderOptions();

        if (!diff.HasChanges && !diff.IsTruncated)
            return string.Empty;

        var sb = new StringBuilder();
        int lineCount = 0;
        int byteCount = 0;
        bool stopped = false;

        void AppendLine(string text)
        {
            if (stopped) return;
            var textBytes = Encoding.UTF8.GetByteCount(text) + 1;
            if (lineCount >= options.MaxOutputLines
                || (byteCount > 0 && byteCount + textBytes > options.MaxOutputBytes))
            {
                sb.Append("... diff truncated (output limit reached)\n");
                stopped = true;
                return;
            }
            sb.Append(text).Append('\n');
            lineCount++;
            byteCount += textBytes;
        }

        if (diff.IsTruncated)
        {
            AppendLine($"--- {oldPath}");
            AppendLine($"+++ {newPath}");
            AppendLine($"... diff truncated (input too large: {diff.TruncationReason})");
            return sb.ToString();
        }

        var hunks = TextDiffHunkBuilder.BuildHunks(
            oldLines, newLines, diff, options.ContextLines);

        if (hunks.Count == 0)
            return string.Empty;

        AppendLine($"--- {oldPath}");
        AppendLine($"+++ {newPath}");

        foreach (var hunk in hunks)
        {
            if (stopped) break;

            AppendLine($"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@");

            foreach (var line in hunk.Lines)
            {
                if (stopped) break;

                var prefix = line.Kind switch
                {
                    TextDiffLineKind.Added => "+",
                    TextDiffLineKind.Removed => "-",
                    _ => " "
                };

                AppendLine($"{prefix}{line.Text}");
            }
        }

        return sb.ToString();
    }
}
