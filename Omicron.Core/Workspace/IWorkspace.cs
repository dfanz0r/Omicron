using System.Text;

namespace Omicron.Core.Workspace;

/// <summary>
/// Identifier for a workspace instance.
/// </summary>
public readonly record struct WorkspaceId(Guid Value)
{
    public static WorkspaceId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Options for reading a file path.
/// </summary>
public sealed record ReadOptions
{
    public int? Offset { get; init; }
    public int? Limit { get; init; }
    public int? Chunk { get; init; }
}

/// <summary>
/// Result of a workspace read operation.
/// </summary>
public sealed record WorkspaceReadResult(
    string Content,
    bool IsDirectory,
    bool IsBinary,
    bool Truncated);

/// <summary>
/// Abstraction for file system access within a workspace root.
/// </summary>
public interface IWorkspace
{
    /// <summary>Workspace identity.</summary>
    WorkspaceId Id { get; }

    /// <summary>Root directory of the workspace.</summary>
    string RootPath { get; }

    /// <summary>
    /// Resolve a path relative to the workspace root, ensuring it does not escape.
    /// Returns the full resolved path, or null if the path escapes.
    /// </summary>
    string? ResolvePath(string raw);

    /// <summary>
    /// Read a file or list a directory within the workspace.
    /// Path resolution must prevent traversal outside the root.
    /// </summary>
    Task<WorkspaceReadResult> ReadPathAsync(string path, ReadOptions? options = null, CancellationToken ct = default);
}

/// <summary>
/// Default workspace implementation wrapping the local file system.
/// Provides rich file/directory reading with line numbers, sizes, truncation,
/// chunk-based access, and path containment. Logic adapted from FileTools.
/// </summary>
public sealed class HostWorkspace : IWorkspace
{
    /// <summary>Default byte limit per chunk for file reads.</summary>
    public const int DefaultChunkSizeBytes = 50 * 1024; // 50 KB

    /// <summary>Max directory entries to return (prevents context blowout).</summary>
    public const int MaxDirEntries = 200;

    /// <summary>Max output lines.</summary>
    public const int MaxOutputLines = 2000;

    /// <summary>Max output bytes.</summary>
    public const int MaxOutputBytes = 50 * 1024;

    /// <summary>Files and folders ignored during directory listing.</summary>
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea",
        "__pycache__", ".pytest_cache", "dist", "build", "target",
        ".next", ".nuget", "packages"
    };

    public WorkspaceId Id { get; }
    public string RootPath { get; }

    public HostWorkspace(string rootPath)
    {
        Id = WorkspaceId.New();
        RootPath = Path.GetFullPath(rootPath);
    }

    public Task<WorkspaceReadResult> ReadPathAsync(
        string path,
        ReadOptions? options = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var root = RootPath;
        var fullPath = ResolvePath(root, path);
        if (fullPath is null)
        {
            return Task.FromResult(new WorkspaceReadResult(
                $"Error: path escapes workspace root.\n  Requested: {path}\n  Root: {root}",
                IsDirectory: false, IsBinary: false, Truncated: false));
        }

        if (!Path.Exists(fullPath))
        {
            return Task.FromResult(new WorkspaceReadResult(
                $"Error: path not found: {path}",
                IsDirectory: false, IsBinary: false, Truncated: false));
        }

        if (Directory.Exists(fullPath))
        {
            var dirContent = ReadDirectory(fullPath, path);
            return Task.FromResult(new WorkspaceReadResult(
                dirContent, IsDirectory: true, IsBinary: false, Truncated: false));
        }

        if (IsBinary(fullPath))
        {
            var info = new FileInfo(fullPath);
            return Task.FromResult(new WorkspaceReadResult(
                $"[FILE] {path}\n  Size: {info.Length:N0} bytes\n  Type: binary (not displayed)",
                IsDirectory: false, IsBinary: true, Truncated: false));
        }

        var (fileContent, truncated) = ReadFile(fullPath, path, options?.Offset, options?.Limit, options?.Chunk);
        return Task.FromResult(new WorkspaceReadResult(
            fileContent, IsDirectory: false, IsBinary: false, Truncated: truncated));
    }

    /// <summary>
    /// Resolve a path relative to the workspace root with traversal prevention.
    /// </summary>
    public string? ResolvePath(string raw)
    {
        return ResolvePath(RootPath, raw);
    }

    /// <summary>
    /// Resolve a path relative to an explicit root with traversal prevention.
    /// </summary>
    private string? ResolvePath(string root, string raw)
    {
        raw = raw.Replace('\\', '/').TrimStart('/');

        if (raw.Contains(".."))
        {
            var resolved = Path.GetFullPath(Path.Combine(root, raw));
            return resolved.StartsWith(root + Path.DirectorySeparatorChar) || resolved == root
                ? resolved
                : null;
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, raw));
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar) || fullPath == root
            ? fullPath
            : null;
    }

    // -------------------------------------------------------
    // File reading
    // -------------------------------------------------------

    private (string Content, bool Truncated) ReadFile(string fullPath, string displayPath, int? offset, int? limit, int? chunk)
    {
        string content;
        try
        {
            content = File.ReadAllText(fullPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return ($"Error reading file: {ex.Message}", false);
        }

        var allLines = content.Replace("\r\n", "\n").Split('\n');
        var totalLines = allLines.Length;
        var totalBytes = Encoding.UTF8.GetByteCount(content);

        int startLine;
        if (chunk.HasValue)
        {
            var targetByte = chunk.Value * DefaultChunkSizeBytes;
            int byteCount = 0;
            startLine = 0;
            for (int i = 0; i < allLines.Length; i++)
            {
                int lineBytes = Encoding.UTF8.GetByteCount(allLines[i]) + 1;
                if (byteCount + lineBytes > targetByte && i > 0)
                {
                    startLine = i;
                    break;
                }
                byteCount += lineBytes;
                startLine = i;
            }
            if (offset.HasValue)
                startLine = Math.Min(startLine + offset.Value - 1, allLines.Length - 1);
        }
        else if (offset.HasValue)
        {
            startLine = Math.Max(0, offset.Value - 1);
        }
        else
        {
            startLine = 0;
        }

        int endLine;
        if (limit.HasValue)
        {
            endLine = Math.Min(startLine + limit.Value, allLines.Length);
        }
        else
        {
            endLine = allLines.Length;
        }

        var sliceLines = allLines[startLine..endLine];

        var sb = new StringBuilder();
        sb.AppendLine($"[FILE] {displayPath}");
        sb.AppendLine($"  Size: {totalBytes:N0} bytes  |  Lines: {totalLines:N0}");

        int displayStart = startLine + 1;
        int displayEnd = endLine;
        if (displayStart <= displayEnd)
            sb.AppendLine($"  Showing: lines {displayStart}-{displayEnd} of {totalLines:N0}");
        sb.AppendLine("---");

        int lineCount = 0;
        int byteCountWritten = 0;
        bool truncated = false;

        for (int i = 0; i < sliceLines.Length; i++)
        {
            var line = sliceLines[i];
            var numberedLine = $"{startLine + i + 1,6}| {line}";
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
            {
                var nextChunk = (chunk ?? 0) + 1;
                sb.AppendLine($"[Use offset={displayEnd + 1} to continue, or chunk={nextChunk} for next byte chunk.]");
            }
        }

        return (sb.ToString().TrimEnd(), truncated || displayEnd < totalLines);
    }

    // -------------------------------------------------------
    // Directory listing
    // -------------------------------------------------------

    private string ReadDirectory(string fullPath, string displayPath)
    {
        var entries = new List<DirEntry>();
        int fileCount = 0;
        int dirCount = 0;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath))
            {
                var name = Path.GetFileName(entry);
                if (SkipNames.Contains(name)) continue;

                if (Directory.Exists(entry))
                {
                    dirCount++;
                    var subFiles = CountFiles(entry);
                    var subDirs = CountDirs(entry);
                    entries.Add(new DirEntry
                    {
                        Name = name + "/",
                        IsDirectory = true,
                        Lines = subFiles,
                        Bytes = subDirs
                    });
                }
                else
                {
                    fileCount++;
                    var fi = new FileInfo(entry);
                    int lines = 0;
                    if (!IsBinary(entry))
                    {
                        try
                        {
                            var text = File.ReadAllText(entry, Encoding.UTF8);
                            lines = text.Replace("\r\n", "\n").Split('\n').Length;
                        }
                        catch { }
                    }
                    entries.Add(new DirEntry
                    {
                        Name = name,
                        IsDirectory = false,
                        Lines = lines,
                        Bytes = (int)Math.Min(fi.Length, int.MaxValue)
                    });
                }

                if (entries.Count >= MaxDirEntries) break;
            }
        }
        catch (Exception ex)
        {
            return $"Error listing directory: {ex.Message}";
        }

        entries.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        var sb = new StringBuilder();
        sb.AppendLine($"[DIR] {displayPath}  ({fileCount} files, {dirCount} dirs)");

        foreach (var e in entries)
        {
            if (e.IsDirectory)
            {
                var fileLabel = e.Lines == 1 ? "file" : "files";
                var dirLabel = e.Bytes == 1 ? "dir" : "dirs";
                sb.AppendLine($"  [DIR]  {e.Name,-40} ({e.Lines} {fileLabel}, {e.Bytes} {dirLabel})");
            }
            else
            {
                var sizeStr = FormatSize(e.Bytes);
                if (e.Lines > 0)
                    sb.AppendLine($"  [FILE] {e.Name,-40} {sizeStr,10}  {e.Lines,6} lines");
                else
                    sb.AppendLine($"  [FILE] {e.Name,-40} {sizeStr,10}  (binary)");
            }
        }

        if (entries.Count >= MaxDirEntries)
            sb.AppendLine($"  ... (listing truncated at {MaxDirEntries} entries)");

        return sb.ToString().TrimEnd();
    }

    // -------------------------------------------------------
    // Helpers
    // -------------------------------------------------------

    private static bool IsBinary(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".dll" or ".exe" or ".so" or ".dylib" or ".node" => true,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" or ".webp" => true,
            ".zip" or ".tar" or ".gz" or ".7z" or ".rar" => true,
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" => true,
            ".mp3" or ".mp4" or ".avi" or ".mov" or ".wav" => true,
            ".ttf" or ".otf" or ".woff" or ".woff2" => true,
            _ => false
        };
    }

    private static string FormatSize(int bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private static int CountFiles(string dir)
    {
        try
        {
            int count = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (SkipNames.Contains(Path.GetFileName(entry))) continue;
                if (File.Exists(entry)) count++;
                if (count >= 1000) break;
            }
            return count;
        }
        catch { return 0; }
    }

    private static int CountDirs(string dir)
    {
        try
        {
            int count = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (SkipNames.Contains(Path.GetFileName(entry))) continue;
                if (Directory.Exists(entry)) count++;
                if (count >= 1000) break;
            }
            return count;
        }
        catch { return 0; }
    }

    private class DirEntry
    {
        public string Name { get; init; } = "";
        public bool IsDirectory { get; init; }
        public int Lines { get; init; }
        public int Bytes { get; init; }
    }
}
