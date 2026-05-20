using System.Text;
using Omicron.Core.Text;

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
        var builder = Utf8Text.CreateBuilder();
        try
        {
            AppendUtf8To(ref builder, oldPath, newPath, oldLines, newLines, diff, options);
            return builder.ToString();
        }
        finally
        {
            builder.Dispose();
        }
    }

    public static string Render(
        string oldPath,
        string newPath,
        Utf8LineIndex oldLines,
        Utf8LineIndex newLines,
        TextDiffResult diff,
        UnifiedDiffRenderOptions? options = null)
    {
        var builder = Utf8Text.CreateBuilder();
        try
        {
            AppendUtf8To(ref builder, oldPath, newPath, oldLines, newLines, diff, options);
            return builder.ToString();
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>
    /// Append a unified diff directly to a <see cref="Utf8Builder"/>,
    /// avoiding intermediate string allocations for callers that already use UTF-8 builders.
    /// </summary>
    public static void AppendUtf8To(
        ref Utf8Builder builder,
        string oldPath,
        string newPath,
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines,
        TextDiffResult diff,
        UnifiedDiffRenderOptions? options = null)
    {
        options ??= new UnifiedDiffRenderOptions();

        if (!diff.HasChanges && !diff.IsTruncated)
            return;

        int lineCount = 0;
        int byteCount = 0;

        // Inline append-line logic to avoid capturing ref parameter in a local function.
        if (diff.IsTruncated)
        {
            AppendLine(ref builder, ref lineCount, ref byteCount, options, $"--- {oldPath}");
            AppendLine(ref builder, ref lineCount, ref byteCount, options, $"+++ {newPath}");
            AppendLine(ref builder, ref lineCount, ref byteCount, options, $"... diff truncated (input too large: {diff.TruncationReason})");
            return;
        }

        var hunks = TextDiffHunkBuilder.BuildHunks(
            oldLines, newLines, diff, options.ContextLines);

        if (hunks.Count == 0)
            return;

        AppendLine(ref builder, ref lineCount, ref byteCount, options, $"--- {oldPath}");
        AppendLine(ref builder, ref lineCount, ref byteCount, options, $"+++ {newPath}");

        foreach (var hunk in hunks)
        {
            if (byteCount > 0 && byteCount >= options.MaxOutputBytes)
                break;

            AppendLine(ref builder, ref lineCount, ref byteCount, options,
                $"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@");

            foreach (var line in hunk.Lines)
            {
                if (byteCount > 0 && byteCount >= options.MaxOutputBytes)
                    break;

                var prefix = line.Kind switch
                {
                    TextDiffLineKind.Added => "+",
                    TextDiffLineKind.Removed => "-",
                    _ => " "
                };

                AppendLine(ref builder, ref lineCount, ref byteCount, options, $"{prefix}{line.Text}");
            }
        }
    }

    public static void AppendUtf8To(
        ref Utf8Builder builder,
        string oldPath,
        string newPath,
        Utf8LineIndex oldLines,
        Utf8LineIndex newLines,
        TextDiffResult diff,
        UnifiedDiffRenderOptions? options = null)
    {
        options ??= new UnifiedDiffRenderOptions();

        if (!diff.HasChanges && !diff.IsTruncated)
            return;

        var lineCount = 0;
        var byteCount = 0;

        if (diff.IsTruncated)
        {
            AppendLine(ref builder, ref lineCount, ref byteCount, options, $"--- {oldPath}");
            AppendLine(ref builder, ref lineCount, ref byteCount, options, $"+++ {newPath}");
            AppendLine(ref builder, ref lineCount, ref byteCount, options, $"... diff truncated (input too large: {diff.TruncationReason})");
            return;
        }

        if (diff.Edits.Count == 0)
            return;

        AppendLine(ref builder, ref lineCount, ref byteCount, options, $"--- {oldPath}");
        AppendLine(ref builder, ref lineCount, ref byteCount, options, $"+++ {newPath}");

        var editIdx = 0;
        while (editIdx < diff.Edits.Count)
        {
            if (byteCount > 0 && byteCount >= options.MaxOutputBytes)
                break;

            var firstEdit = diff.Edits[editIdx];
            var hunkOldStart = Math.Max(0, firstEdit.OldStart - options.ContextLines);
            var hunkNewStart = Math.Max(0, firstEdit.NewStart - options.ContextLines);

            var hunkEdits = new List<TextDiffEdit> { firstEdit };
            editIdx++;

            while (editIdx < diff.Edits.Count)
            {
                var prev = hunkEdits[^1];
                var next = diff.Edits[editIdx];

                var gapOld = next.OldStart - (prev.OldStart + prev.OldCount);
                var gapNew = next.NewStart - (prev.NewStart + prev.NewCount);

                if (gapOld <= 2 * options.ContextLines && gapNew <= 2 * options.ContextLines)
                {
                    hunkEdits.Add(next);
                    editIdx++;
                }
                else
                    break;
            }

            var lastEdit = hunkEdits[^1];
            var hunkOldEnd = Math.Min(oldLines.Count, lastEdit.OldStart + lastEdit.OldCount + options.ContextLines);
            var hunkNewEnd = Math.Min(newLines.Count, lastEdit.NewStart + lastEdit.NewCount + options.ContextLines);

            AppendLine(ref builder, ref lineCount, ref byteCount, options,
                $"@@ -{hunkOldStart + 1},{hunkOldEnd - hunkOldStart} +{hunkNewStart + 1},{hunkNewEnd - hunkNewStart} @@");

            var li = hunkOldStart;
            var lj = hunkNewStart;
            var hei = 0;

            while (li < hunkOldEnd || lj < hunkNewEnd)
            {
                if (byteCount > 0 && byteCount >= options.MaxOutputBytes)
                    break;

                var inEdit = hei < hunkEdits.Count
                    && li >= hunkEdits[hei].OldStart
                    && li < hunkEdits[hei].OldStart + hunkEdits[hei].OldCount;

                var inNewEdit = hei < hunkEdits.Count
                    && lj >= hunkEdits[hei].NewStart
                    && lj < hunkEdits[hei].NewStart + hunkEdits[hei].NewCount;

                if (inEdit || inNewEdit)
                {
                    var edit = hunkEdits[hei];

                    for (var r = 0; r < edit.OldCount; r++)
                        AppendLine(ref builder, ref lineCount, ref byteCount, options, (byte)'-', oldLines[li++]);

                    for (var a = 0; a < edit.NewCount; a++)
                        AppendLine(ref builder, ref lineCount, ref byteCount, options, (byte)'+', newLines[lj++]);

                    hei++;
                }
                else
                {
                    if (li < oldLines.Count && lj < newLines.Count)
                    {
                        AppendLine(ref builder, ref lineCount, ref byteCount, options, (byte)' ', oldLines[li]);
                        li++;
                        lj++;
                    }
                    else
                    {
                        if (li < hunkOldEnd) li++;
                        if (lj < hunkNewEnd) lj++;
                    }
                }
            }
        }
    }

    private static void AppendLine(
        ref Utf8Builder builder,
        ref int lineCount,
        ref int byteCount,
        UnifiedDiffRenderOptions options,
        string text)
    {
        var textBytes = Encoding.UTF8.GetByteCount(text) + 1;
        if (lineCount >= options.MaxOutputLines
            || (byteCount > 0 && byteCount + textBytes > options.MaxOutputBytes))
        {
            if (byteCount > 0 || lineCount > 0)
            {
                builder.Append("... diff truncated (output limit reached)");
                builder.AppendLine();
            }
            byteCount = int.MaxValue; // Prevent further output
            return;
        }
        builder.Append(text);
        builder.AppendLine();
        lineCount++;
        byteCount += textBytes;
    }

    private static void AppendLine(
        ref Utf8Builder builder,
        ref int lineCount,
        ref int byteCount,
        UnifiedDiffRenderOptions options,
        byte prefix,
        ReadOnlySpan<byte> utf8Text)
    {
        var textBytes = 1 + utf8Text.Length + 1;
        if (lineCount >= options.MaxOutputLines
            || (byteCount > 0 && byteCount + textBytes > options.MaxOutputBytes))
        {
            if (byteCount > 0 || lineCount > 0)
            {
                builder.Append("... diff truncated (output limit reached)");
                builder.AppendLine();
            }
            byteCount = int.MaxValue;
            return;
        }

        builder.Append((char)prefix);
        builder.AppendLiteral(utf8Text);
        builder.AppendLine();
        lineCount++;
        byteCount += textBytes;
    }
}
