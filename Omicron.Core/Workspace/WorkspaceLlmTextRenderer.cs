using Cysharp.Text;
using Omicron.Core.Content;
using Omicron.Core.Text;

namespace Omicron.Core.Workspace;

/// <summary>
/// Renders WorkspaceReadContent into deterministic LLM-context text.
/// </summary>
public interface IWorkspaceReadRenderer<out T>
{
    T Render(WorkspaceReadContent content);
}

/// <summary>
/// LLM-text renderer producing read_path output:
/// [FILE]/[DIR] headers, line numbers, truncation, etc.
/// </summary>
public sealed class WorkspaceLlmTextRenderer : IWorkspaceReadRenderer<WorkspaceReadResult>
{
    public WorkspaceReadResult Render(WorkspaceReadContent content)
    {
        var sb = ZString.CreateUtf8StringBuilder();
        try
        {
            AppendUtf8To(ref sb, content);
            return new WorkspaceReadResult(
                sb.ToString(),
                IsDirectory: content is WorkspaceDirectoryContent,
                IsBinary: content is WorkspaceBinaryFileContent,
                Truncated: content is WorkspaceFileContent { Truncated: true });
        }
        finally
        {
            sb.Dispose();
        }
    }

    /// <summary>
    /// Append the LLM text representation directly as UTF-8 into a caller-owned builder.
    /// Hot paths can reuse the builder across render operations and avoid
    /// intermediate <see cref="string"/> allocations.
    /// </summary>
    public static void AppendUtf8To(ref Utf8ValueStringBuilder sb, WorkspaceReadContent content)
    {
        switch (content)
        {
            case WorkspaceFileContent file:
                AppendFileUtf8(ref sb, file);
                break;
            case WorkspaceBinaryFileContent binary:
                AppendBinaryUtf8(ref sb, binary);
                break;
            case WorkspaceDirectoryContent dir:
                AppendDirectoryUtf8(ref sb, dir);
                break;
            case WorkspaceReadErrorContent error:
                AppendErrorUtf8(ref sb, error);
                break;
            default:
                sb.AppendLiteral("Error: unknown content type"u8);
                break;
        }
    }

    private static void AppendFileUtf8(ref Utf8ValueStringBuilder sb, WorkspaceFileContent file)
    {
        sb.AppendLiteral("[FILE] "u8);
        sb.AppendLine(file.RequestedPath);
        sb.AppendLiteral("  Size: "u8);
        FormatSize.AppendUtf8To(ref sb, file.Stat.Size);
        Utf8CompositeFormat.AppendFormatUtf8(ref sb, "  |  Lines: {0:N0}"u8, file.TotalLines);
        sb.AppendLine();

        if (file.Lines.Count > 0)
        {
            Utf8CompositeFormat.AppendFormatUtf8(ref sb, "  Showing: lines {0}-{1} of {2:N0}"u8, file.Lines[0].Number, file.Lines[^1].Number, file.TotalLines);
            sb.AppendLine();
        }

        sb.AppendLiteral("---"u8);

        foreach (var line in file.Lines)
        {
            sb.AppendLine();
            AppendNumberPaddedLeftUtf8(ref sb, line.Number, width: 6);
            sb.AppendLiteral("| "u8);
            sb.Append(line.Text);
        }

        if (file.Truncated)
        {
            sb.AppendLine();
            sb.AppendLiteral("---"u8);
            sb.AppendLine();
            Utf8CompositeFormat.AppendFormatUtf8(ref sb, "[Output truncated to {0:N0} lines / {1:N0} KB.]"u8, WorkspaceReadService.MaxOutputLines, WorkspaceReadService.MaxOutputBytes / 1024);
            if (file.NextOffset.HasValue)
            {
                sb.AppendLine();
                Utf8CompositeFormat.AppendFormatUtf8(ref sb, "[Use offset={0} to continue.]"u8, file.NextOffset.Value);
            }
        }
    }

    private static void AppendBinaryUtf8(ref Utf8ValueStringBuilder sb, WorkspaceBinaryFileContent binary)
    {
        sb.AppendLiteral("[FILE] "u8);
        sb.AppendLine(binary.RequestedPath);
        sb.AppendLiteral("  Size: "u8);
        FormatSize.AppendUtf8To(ref sb, binary.Stat.Size);
        sb.AppendLine();
        sb.AppendLiteral("  Type: binary (not displayed)"u8);
    }

    private static void AppendDirectoryUtf8(ref Utf8ValueStringBuilder sb, WorkspaceDirectoryContent dir)
    {
        sb.AppendLiteral("[DIR] "u8);
        sb.Append(dir.RequestedPath);
        Utf8CompositeFormat.AppendFormatUtf8(ref sb, "  ({0} files, {1} dirs)"u8, dir.TotalFileCount, dir.TotalDirCount);

        foreach (var e in dir.Entries)
        {
            sb.AppendLine();
            if (e.IsDirectory)
            {
                sb.AppendLiteral("  [DIR]  "u8);
                AppendPaddedRightUtf8(ref sb, e.Name, width: 40);
            }
            else if (e.LineCount.HasValue)
            {
                sb.AppendLiteral("  [FILE] "u8);
                AppendPaddedRightUtf8(ref sb, e.Name, width: 40);
                sb.Append(' ');
                AppendSizePaddedLeft(ref sb, e.Size, width: 10);
                sb.AppendLiteral("  "u8);
                AppendNumberPaddedLeftUtf8(ref sb, e.LineCount.Value, width: 6);
                sb.AppendLiteral(" lines"u8);
            }
            else
            {
                sb.AppendLiteral("  [FILE] "u8);
                AppendPaddedRightUtf8(ref sb, e.Name, width: 40);
                sb.Append(' ');
                AppendSizePaddedLeft(ref sb, e.Size, width: 10);
                sb.AppendLiteral("  (binary)"u8);
            }
        }

        if (dir.Truncated)
        {
            sb.AppendLine();
            Utf8CompositeFormat.AppendFormatUtf8(ref sb, "  ... (listing truncated at {0} entries)"u8, WorkspaceReadService.MaxDirEntries);
        }
    }

    private static void AppendErrorUtf8(ref Utf8ValueStringBuilder sb, WorkspaceReadErrorContent error)
    {
        sb.AppendLiteral("Error: "u8);
        sb.Append(error.Message);
    }

    /// <summary>
    /// Convert a workspace read result into a list of semantic content blocks.
    /// The existing <see cref="Render"/> method remains the primary text path;
    /// this is an optional structured alternative for future UI consumers.
    /// </summary>
    public static List<IContentBlock> ToContentBlocks(WorkspaceReadContent content)
    {
        return content switch
        {
            WorkspaceFileContent file => ToFileBlocks(file),
            WorkspaceBinaryFileContent binary => ToBinaryBlock(binary),
            WorkspaceDirectoryContent dir => ToDirectoryBlock(dir),
            WorkspaceReadErrorContent error => ToErrorBlock(error),
            _ => [new ErrorContentBlock($"Unknown content type: {content.GetType().Name}")]
        };
    }

    private static List<IContentBlock> ToFileBlocks(WorkspaceFileContent file)
    {
        var previewSb = ZString.CreateUtf8StringBuilder();
        AppendFilePreviewLinesUtf8(ref previewSb, file);

        var preview = new FilePreviewContentBlock(
            Path: file.RequestedPath,
            ref previewSb,
            Size: file.Stat.Size,
            LineCount: file.TotalLines,
            IsBinary: false);

        var blocks = new List<IContentBlock> { preview };

        if (file.Truncated)
        {
            var sb = ZString.CreateUtf8StringBuilder();
            Utf8CompositeFormat.AppendFormatUtf8(ref sb, "Output truncated — showing {0} of {1:N0} lines."u8, file.Lines.Count, file.TotalLines);
            if (file.NextOffset.HasValue)
                Utf8CompositeFormat.AppendFormatUtf8(ref sb, " Use offset={0} to continue."u8, file.NextOffset.Value);
            var warning = new PlainTextContentBlock(ref sb);
            blocks.Add(warning);
        }

        return blocks;
    }

    private static List<IContentBlock> ToBinaryBlock(WorkspaceBinaryFileContent binary)
    {
        return
        [
            new FilePreviewContentBlock(
                Path: binary.RequestedPath,
                Size: binary.Stat.Size,
                LineCount: null,
                IsBinary: true)
        ];
    }

    private static List<IContentBlock> ToDirectoryBlock(WorkspaceDirectoryContent dir)
    {
        var sb = ZString.CreateUtf8StringBuilder();
        AppendDirectoryPreviewUtf8(ref sb, dir);

        var block = new PlainTextContentBlock(ref sb);
        return [block];
    }

    private static List<IContentBlock> ToErrorBlock(WorkspaceReadErrorContent error)
    {
        return [new ErrorContentBlock(error.Message)];
    }

    private static void AppendFilePreviewLinesUtf8(ref Utf8ValueStringBuilder sb, WorkspaceFileContent file)
    {
        for (int i = 0; i < file.Lines.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            var line = file.Lines[i];
            AppendNumberPaddedLeftUtf8(ref sb, line.Number, width: 6);
            sb.AppendLiteral("| "u8);
            sb.Append(line.Text);
        }
    }

    private static void AppendDirectoryPreviewUtf8(ref Utf8ValueStringBuilder sb, WorkspaceDirectoryContent dir)
    {
        sb.AppendLiteral("[DIR] "u8);
        sb.Append(dir.RequestedPath);

        foreach (var e in dir.Entries)
        {
            sb.AppendLine();
            if (e.IsDirectory)
            {
                sb.AppendLiteral("  [DIR]  "u8);
                sb.Append(e.Name);
            }
            else if (e.LineCount.HasValue)
            {
                sb.AppendLiteral("  [FILE] "u8);
                sb.Append(e.Name);
                sb.AppendLiteral("  ("u8);
                FormatSize.AppendUtf8To(ref sb, e.Size);
                sb.AppendLiteral(", "u8);
                sb.Append(e.LineCount.Value);
                sb.AppendLiteral(" lines)"u8);
            }
            else
            {
                sb.AppendLiteral("  [FILE] "u8);
                sb.Append(e.Name);
                sb.AppendLiteral("  ("u8);
                FormatSize.AppendUtf8To(ref sb, e.Size);
                sb.AppendLiteral(", binary)"u8);
            }
        }

        if (dir.Truncated)
        {
            sb.AppendLine();
            sb.AppendLiteral("  ... (listing truncated)"u8);
        }
    }

    private static void AppendPaddedRightUtf8(ref Utf8ValueStringBuilder sb, string value, int width)
    {
        sb.Append(value);
        int padding = width - value.Length;
        if (padding > 0)
            sb.Append(' ', padding);
    }

    private static void AppendNumberPaddedLeftUtf8(ref Utf8ValueStringBuilder sb, int value, int width)
    {
        int digits = CountDecimalDigits(value);
        int padding = width - digits;
        if (padding > 0)
            sb.Append(' ', padding);
        sb.Append(value);
    }

    private static void AppendSizePaddedLeft(ref Utf8ValueStringBuilder sb, long bytes, int width)
    {
        // Format size into a temp builder to measure its length, then pad.
        var temp = ZString.CreateUtf8StringBuilder();
        try
        {
            FormatSize.AppendUtf8To(ref temp, bytes);
            int padding = width - temp.Length;
            if (padding > 0)
                sb.Append(' ', padding);
            sb.AppendLiteral(temp.AsSpan());
        }
        finally
        {
            temp.Dispose();
        }
    }

    private static int CountDecimalDigits(int value)
    {
        if (value == 0) return 1;

        int digits = value < 0 ? 1 : 0;
        uint remaining = value < 0 ? (uint)-value : (uint)value;
        while (remaining > 0)
        {
            remaining /= 10;
            digits++;
        }
        return digits;
    }
}
