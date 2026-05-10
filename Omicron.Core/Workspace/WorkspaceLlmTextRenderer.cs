using System.Text;
using Omicron.Core.Content;

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
        return content switch
        {
            WorkspaceFileContent file => RenderFile(file),
            WorkspaceBinaryFileContent binary => RenderBinary(binary),
            WorkspaceDirectoryContent dir => RenderDirectory(dir),
            WorkspaceReadErrorContent error => RenderError(error),
            _ => new WorkspaceReadResult("Error: unknown content type", false, false, false)
        };
    }

    private static WorkspaceReadResult RenderFile(WorkspaceFileContent file)
    {
        var totalBytes = file.Stat.Size;

        var sb = new StringBuilder();
        sb.AppendLine($"[FILE] {file.RequestedPath}");
        sb.AppendLine($"  Size: {FormatSize.Format(totalBytes)}  |  Lines: {file.TotalLines:N0}");

        if (file.Lines.Count > 0)
        {
            sb.AppendLine($"  Showing: lines {file.Lines[0].Number}-{file.Lines[^1].Number} of {file.TotalLines:N0}");
        }
        sb.AppendLine("---");

        foreach (var line in file.Lines)
        {
            sb.AppendLine($"{line.Number,6}| {line.Text}");
        }

        if (file.Truncated)
        {
            sb.AppendLine("---");
            sb.AppendLine($"[Output truncated to {WorkspaceReadService.MaxOutputLines:N0} lines / {WorkspaceReadService.MaxOutputBytes / 1024:N0} KB.]");
            if (file.NextOffset.HasValue)
                sb.AppendLine($"[Use offset={file.NextOffset} to continue.]");
        }

        return new WorkspaceReadResult(sb.ToString().TrimEnd(), false, false, file.Truncated);
    }

    private static WorkspaceReadResult RenderBinary(WorkspaceBinaryFileContent binary)
    {
        return new WorkspaceReadResult(
            $"[FILE] {binary.RequestedPath}\n  Size: {FormatSize.Format(binary.Stat.Size)}\n  Type: binary (not displayed)",
            false, true, false);
    }

    private static WorkspaceReadResult RenderDirectory(WorkspaceDirectoryContent dir)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[DIR] {dir.RequestedPath}  ({dir.TotalFileCount} files, {dir.TotalDirCount} dirs)");

        foreach (var e in dir.Entries)
        {
            if (e.IsDirectory)
            {
                sb.AppendLine($"  [DIR]  {e.Name,-40}");
            }
            else if (e.LineCount.HasValue)
            {
                var sizeStr = FormatSize.Format(e.Size);
                sb.AppendLine($"  [FILE] {e.Name,-40} {sizeStr,10}  {e.LineCount,6} lines");
            }
            else
            {
                var sizeStr = FormatSize.Format(e.Size);
                sb.AppendLine($"  [FILE] {e.Name,-40} {sizeStr,10}  (binary)");
            }
        }

        if (dir.Truncated)
            sb.AppendLine($"  ... (listing truncated at {WorkspaceReadService.MaxDirEntries} entries)");

        return new WorkspaceReadResult(sb.ToString().TrimEnd(), true, false, false);
    }

    private static WorkspaceReadResult RenderError(WorkspaceReadErrorContent error)
    {
        var msg = error.Kind switch
        {
            WorkspaceReadErrorKind.EscapesRoot => error.Message,
            WorkspaceReadErrorKind.NotFound => error.Message,
            WorkspaceReadErrorKind.Unreadable => error.Message,
            WorkspaceReadErrorKind.InvalidPath => error.Message,
            _ => error.Message
        };
        return new WorkspaceReadResult($"Error: {msg}", false, false, false);
    }

    /// <summary>
    /// Convert a workspace read result into a list of semantic content blocks.
    /// The existing <see cref="Render"/> method remains the primary text path;
    /// this is an optional structured alternative for future UI consumers.
    /// </summary>
    public static IReadOnlyList<ContentBlock> ToContentBlocks(WorkspaceReadContent content)
    {
        return content switch
        {
            WorkspaceFileContent file => ToFileBlocks(file),
            WorkspaceBinaryFileContent binary => ToBinaryBlock(binary),
            WorkspaceDirectoryContent dir => ToDirectoryBlock(dir),
            WorkspaceReadErrorContent error => ToErrorBlock(error),
            _ => new ContentBlock[] { new ErrorContentBlock($"Unknown content type: {content.GetType().Name}") }
        };
    }

    private static IReadOnlyList<ContentBlock> ToFileBlocks(WorkspaceFileContent file)
    {
        var preview = new StringBuilder();
        foreach (var line in file.Lines)
            preview.AppendLine($"{line.Number,6}| {line.Text}");

        var blocks = new List<ContentBlock>
        {
            new FilePreviewContentBlock(
                Path: file.RequestedPath,
                Preview: preview.ToString().TrimEnd(),
                Size: file.Stat.Size,
                LineCount: file.TotalLines,
                IsBinary: false)
        };

        if (file.Truncated)
        {
            var msg = $"Output truncated — showing {file.Lines.Count} of {file.TotalLines:N0} lines.";
            if (file.NextOffset.HasValue)
                msg += $" Use offset={file.NextOffset} to continue.";
            blocks.Add(new PlainTextContentBlock(msg));
        }

        return blocks;
    }

    private static IReadOnlyList<ContentBlock> ToBinaryBlock(WorkspaceBinaryFileContent binary)
    {
        return new ContentBlock[]
        {
            new FilePreviewContentBlock(
                Path: binary.RequestedPath,
                Preview: "",
                Size: binary.Stat.Size,
                LineCount: null,
                IsBinary: true)
        };
    }

    private static IReadOnlyList<ContentBlock> ToDirectoryBlock(WorkspaceDirectoryContent dir)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[DIR] {dir.RequestedPath}");
        foreach (var e in dir.Entries)
        {
            if (e.IsDirectory)
                sb.AppendLine($"  [DIR]  {e.Name}");
            else if (e.LineCount.HasValue)
                sb.AppendLine($"  [FILE] {e.Name}  ({FormatSize.Format(e.Size)}, {e.LineCount} lines)");
            else
                sb.AppendLine($"  [FILE] {e.Name}  ({FormatSize.Format(e.Size)}, binary)");
        }
        if (dir.Truncated)
            sb.AppendLine("  ... (listing truncated)");

        return new ContentBlock[]
        {
            new FilePreviewContentBlock(
                Path: dir.RequestedPath,
                Preview: sb.ToString().TrimEnd(),
                Size: 0,
                LineCount: dir.Entries.Count,
                IsBinary: false)
        };
    }

    private static IReadOnlyList<ContentBlock> ToErrorBlock(WorkspaceReadErrorContent error)
    {
        return new ContentBlock[]
        {
            new ErrorContentBlock(error.Message)
        };
    }

}
