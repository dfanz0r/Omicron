using System.Text;
using System.Text.Json;

namespace Omicron.Core.Tools;

/// <summary>
/// Unified file/directory reading tool.
/// Replaces separate read_file, list_dir, ls tools with a single
/// context-rich operation that gives the LLM maximum information
/// per call while minimizing round-trips.
/// </summary>
public static class FileTools
{
    /// <summary>Default byte limit per chunk for file reads.</summary>
    public const int DefaultChunkSizeBytes = 50 * 1024;  // 50 KB

    /// <summary>Max directory entries to return (prevents context blowout).</summary>
    public const int MaxDirEntries = 200;

    /// <summary>Files and folders ignored during directory listing.</summary>
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea",
        "__pycache__", ".pytest_cache", "dist", "build", "target",
        ".next", ".nuget", "packages"
    };

    /// <summary>
    /// Create the read_path tool definition.
    /// </summary>
    public static Tool Create(string workspaceRoot)
    {
        return new Tool
        {
            Name = "read_path",
            Description =
                "Read a file or list a directory. " +
                "For files: returns content with line numbers, size, and line count. " +
                "Supports line offset/limit and byte-based chunk indexing for large files. " +
                "For directories: lists entries with per-file line count and size, " +
                "per-folder direct-child counts, and a summary. " +
                "Output is truncated at 50 KB / 2000 lines per call; use chunk or offset to continue.",

            Parameters = ToolSchema.Object(
                new Dictionary<string, JsonElement>
                {
                    ["path"] = ToolSchema.StringProperty(
                        "Path to read. If a file: reads content. If a directory: lists contents."),

                    ["offset"] = ToolSchema.IntegerProperty(
                        "1-based line number to start reading from. Ignored for directories."),

                    ["limit"] = ToolSchema.IntegerProperty(
                        "Maximum lines to return. Defaults to fit within truncation limit. Ignored for directories."),

                    ["chunk"] = ToolSchema.IntegerProperty(
                        "0-based chunk index for byte-based access. " +
                        $"Each chunk is ~{DefaultChunkSizeBytes / 1024} KB. " +
                        "When set, offset is relative to the start of this chunk. " +
                        "Useful for resuming after truncated output. Ignored for directories.")
                },
                required: new[] { "path" }
            ),

            ExecuteAsync = (id, args) =>
            {
                var path = args?.GetValueOrDefault("path")?.ToString() ?? "";
                var offset = TryGetInt(args, "offset");
                var limit = TryGetInt(args, "limit");
                var chunk = TryGetInt(args, "chunk");

                return Task.FromResult(ReadPath(workspaceRoot, path, offset, limit, chunk));
            }
        };
    }

    // -------------------------------------------------------
    // Core logic
    // -------------------------------------------------------

    private static string ReadPath(string root, string rawPath, int? offset, int? limit, int? chunk)
    {
        root = Path.GetFullPath(root);

        // Resolve and validate path stays under root
        var fullPath = ResolvePath(root, rawPath);
        if (fullPath is null)
            return $"Error: path escapes workspace root.\n  Requested: {rawPath}\n  Root: {root}";

        if (!Path.Exists(fullPath))
            return $"Error: path not found: {rawPath}";

        if (Directory.Exists(fullPath))
            return ReadDirectory(fullPath, rawPath);

        return ReadFile(fullPath, rawPath, offset, limit, chunk);
    }

    // -------------------------------------------------------
    // File reading
    // -------------------------------------------------------

    private const int MaxOutputLines = 2000;
    private const int MaxOutputBytes = 50 * 1024;

    private static string ReadFile(string fullPath, string displayPath, int? offset, int? limit, int? chunk)
    {
        // Quick binary check
        if (IsBinary(fullPath))
        {
            var info = new FileInfo(fullPath);
            return $"[FILE] {displayPath}\n  Size: {info.Length:N0} bytes\n  Type: binary (not displayed)";
        }

        string content;
        try
        {
            content = File.ReadAllText(fullPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }

        var allLines = content.Replace("\r\n", "\n").Split('\n');
        var totalLines = allLines.Length;
        var totalBytes = Encoding.UTF8.GetByteCount(content);

        // Handle chunk-based access
        int startLine;
        if (chunk.HasValue)
        {
            // Find the line closest to chunk * chunkSize bytes
            var targetByte = chunk.Value * DefaultChunkSizeBytes;
            int byteCount = 0;
            startLine = 0;
            for (int i = 0; i < allLines.Length; i++)
            {
                int lineBytes = Encoding.UTF8.GetByteCount(allLines[i]) + 1; // +1 for newline
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

        // Extract the slice
        var sliceLines = allLines[startLine..endLine];

        // Build output
        var sb = new StringBuilder();
        sb.AppendLine($"[FILE] {displayPath}");
        sb.AppendLine($"  Size: {totalBytes:N0} bytes  |  Lines: {totalLines:N0}");

        int displayStart = startLine + 1;
        int displayEnd = endLine;
        if (displayStart <= displayEnd)
            sb.AppendLine($"  Showing: lines {displayStart}-{displayEnd} of {totalLines:N0}");
        sb.AppendLine("---");

        // Write lines with line numbers
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

        return sb.ToString().TrimEnd();
    }

    // -------------------------------------------------------
    // Directory listing
    // -------------------------------------------------------

    private static string ReadDirectory(string fullPath, string displayPath)
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
                        Lines = subFiles,  // repurpose as file count for display
                        Bytes = subDirs    // repurpose as dir count for display
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
                        catch
                        {
                            // can't read, leave lines at 0
                        }
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

        // Sort: directories first, then alphabetically
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

    private static string? ResolvePath(string root, string raw)
    {
        // Normalize separators
        raw = raw.Replace('\\', '/').TrimStart('/');

        // Simple traversal check
        if (raw.Contains(".."))
        {
            // Allow .. but verify final path stays under root
            var resolved = Path.GetFullPath(Path.Combine(root, raw));
            return resolved.StartsWith(root + Path.DirectorySeparatorChar) ||
                   resolved == root
                ? resolved
                : null;
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, raw));
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar) ||
               fullPath == root
            ? fullPath
            : null;
    }

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
            ".pdb" or ".obj" or ".o" or ".a" or ".lib" => true,
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

    private static int? TryGetInt(Dictionary<string, object?>? args, string key)
    {
        if (args?.TryGetValue(key, out var val) != true || val is null) return null;
        if (val is int i) return i;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var ji))
            return ji;
        if (val is long l) return (int)l;
        if (val is double d) return (int)d;
        if (int.TryParse(val.ToString(), out var parsed)) return parsed;
        return null;
    }

    private class DirEntry
    {
        public string Name { get; init; } = "";
        public bool IsDirectory { get; init; }
        public int Lines { get; init; }
        public int Bytes { get; init; }
    }
}
