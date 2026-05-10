using System.Text;

namespace Omicron.Core.Workspace;

/// <summary>
/// Adapts IWorkspaceFileSystem (VFS) to the IWorkspace (rich read/directory) interface.
/// This lets the existing read_path tool operate through the VFS layer
/// while preserving display formatting (line numbers, truncation, etc.).
/// </summary>
public sealed class VfsWorkspaceAdapter : IWorkspace
{
    private readonly IWorkspaceFileSystem _vfs;

    public WorkspaceId Id { get; }
    public string RootPath => _vfs.RootPath;

    public VfsWorkspaceAdapter(IWorkspaceFileSystem vfs)
    {
        Id = WorkspaceId.New();
        _vfs = vfs ?? throw new ArgumentNullException(nameof(vfs));
    }

    public string? ResolvePath(string raw)
    {
        var result = _vfs.Resolve(raw);
        return result?.Value;
    }

    public async Task<WorkspaceReadResult> ReadPathAsync(
        string path,
        ReadOptions? options = null,
        CancellationToken ct = default)
    {
        var wsPath = _vfs.Resolve(path);
        if (wsPath is null)
        {
            return new WorkspaceReadResult(
                $"Error: path escapes workspace root.\n  Requested: {path}\n  Root: {RootPath}",
                IsDirectory: false, IsBinary: false, Truncated: false);
        }

        var stat = await _vfs.StatAsync(wsPath.Value);
        if (stat is null)
        {
            return new WorkspaceReadResult(
                $"Error: path not found: {path}",
                IsDirectory: false, IsBinary: false, Truncated: false);
        }

        if (stat.IsDirectory)
        {
            var dirContent = await FormatDirectoryAsync(wsPath.Value, path, ct);
            return new WorkspaceReadResult(dirContent, IsDirectory: true, IsBinary: false, Truncated: false);
        }

        if (stat.IsBinary)
        {
            return new WorkspaceReadResult(
                $"[FILE] {path}\n  Size: {stat.Size:N0} bytes\n  Type: binary (not displayed)",
                IsDirectory: false, IsBinary: true, Truncated: false);
        }

        var bytes = await _vfs.ReadFileAsync(wsPath.Value, ct);
        if (bytes.IsEmpty)
        {
            return new WorkspaceReadResult(
                $"Error reading file: {path}",
                IsDirectory: false, IsBinary: false, Truncated: false);
        }

        var text = Encoding.UTF8.GetString(bytes.Span);
        var formatted = FormatFileContent(text, path, stat.Size, options);
        return new WorkspaceReadResult(formatted.Content, IsDirectory: false, IsBinary: false, Truncated: formatted.Truncated);
    }

    /// <summary>
    /// Format file content with line numbers, size info, and truncation.
    /// Mirrors HostWorkspace formatting logic.
    /// </summary>
    private static (string Content, bool Truncated) FormatFileContent(
        string content, string displayPath, long size, ReadOptions? options)
    {
        const int MaxOutputLines = 2000;
        const int MaxOutputBytes = 50 * 1024;

        var allLines = content.Replace("\r\n", "\n").Split('\n');
        var totalLines = allLines.Length;

        int startLine = Math.Max(0, (options?.Offset ?? 1) - 1);
        int endLine = options?.Limit is not null
            ? Math.Min(startLine + options.Limit.Value, allLines.Length)
            : allLines.Length;

        var sliceLines = allLines[startLine..endLine];
        var sb = new StringBuilder();
        sb.AppendLine($"[FILE] {displayPath}");
        sb.AppendLine($"  Size: {size:N0} bytes  |  Lines: {totalLines:N0}");

        int displayStart = startLine + 1;
        int displayEnd = endLine;
        if (displayStart <= displayEnd)
            sb.AppendLine($"  Showing: lines {displayStart}-{displayEnd} of {totalLines:N0}");
        sb.AppendLine("---");

        int lineCount = 0;
        int byteCountWritten = 0;
        bool truncated = false;

        foreach (var line in sliceLines)
        {
            var numberedLine = $"{startLine + lineCount + 1,6}| {line}";
            var lineBytes = Encoding.UTF8.GetByteCount(numberedLine) + 1;

            if (lineCount >= MaxOutputLines || byteCountWritten + lineBytes > MaxOutputBytes)
            {
                truncated = true;
                break;
            }

            sb.AppendLine(numberedLine);
            lineCount++;
            byteCountWritten += lineBytes;
        }

        if (truncated || displayEnd < totalLines)
        {
            sb.AppendLine("---");
            if (truncated)
                sb.AppendLine($"[Output truncated to {MaxOutputLines:N0} lines / {MaxOutputBytes / 1024:N0} KB.]");
            if (displayEnd < totalLines)
                sb.AppendLine($"[Use offset={displayEnd + 1} to continue.]");
        }

        return (sb.ToString().TrimEnd(), truncated || displayEnd < totalLines);
    }

    /// <summary>
    /// Format directory listing. Mirrors HostWorkspace formatting logic.
    /// </summary>
    private async Task<string> FormatDirectoryAsync(WorkspacePath wsPath, string displayPath, CancellationToken ct)
    {
        const int MaxDirEntries = 200;

        var entries = await _vfs.ReadDirectoryAsync(wsPath, ct);
        var fileCount = entries.Count(e => !e.IsDirectory);
        var dirCount = entries.Count(e => e.IsDirectory);

        var sb = new StringBuilder();
        sb.AppendLine($"[DIR] {displayPath}  ({fileCount} files, {dirCount} dirs)");

        int shown = 0;
        foreach (var e in entries.OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Name))
        {
            if (shown >= MaxDirEntries) break;
            shown++;

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

        if (entries.Count > MaxDirEntries)
            sb.AppendLine($"  ... (listing truncated at {MaxDirEntries} entries)");

        return sb.ToString().TrimEnd();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
