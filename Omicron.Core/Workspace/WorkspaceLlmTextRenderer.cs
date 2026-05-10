using System.Text;

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
        sb.AppendLine($"  Size: {FormatSize(totalBytes)}  |  Lines: {file.TotalLines:N0}");

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
            $"[FILE] {binary.RequestedPath}\n  Size: {FormatSize(binary.Stat.Size)}\n  Type: binary (not displayed)",
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
                var sizeStr = FormatSize(e.Size);
                sb.AppendLine($"  [FILE] {e.Name,-40} {sizeStr,10}  {e.LineCount,6} lines");
            }
            else
            {
                var sizeStr = FormatSize(e.Size);
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

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
