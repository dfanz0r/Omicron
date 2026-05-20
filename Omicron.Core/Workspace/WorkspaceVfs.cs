using Omicron.Core.IO;

namespace Omicron.Core.Workspace;

// ============================================================
// VFS path and metadata types
// ============================================================

/// <summary>
///     A path within the workspace.
///     Callers should obtain instances via IWorkspaceFileSystem.ResolveAsync
///     to guarantee containment. Manually constructing with unsafe values
///     will be rejected by VFS operations at the boundary.
/// </summary>
public readonly record struct WorkspacePath(string Value)
{
    public override string ToString()
    {
        return Value;
    }
}

/// <summary>
///     Metadata for a file system entry.
/// </summary>
public sealed record FileStat(
    string Path,
    long Size,
    DateTime LastModified,
    bool IsDirectory,
    bool IsBinary)
{
    public bool IsFile => !IsDirectory;
}

/// <summary>
///     A directory entry (file or subdirectory).
/// </summary>
public sealed record DirectoryEntry(
    string Name,
    bool IsDirectory,
    long Size,
    int? LineCount = null);

// ============================================================
// VFS interface
// ============================================================

/// <summary>
///     Host-backed virtual file system.
///     All paths are workspace-relative and resolved with containment protection.
///     Operations validate containment at the boundary even if a WorkspacePath
///     was manually constructed with unsafe content.
///     Audit events are deferred (not yet emitted through IEventSink).
/// </summary>
public interface IWorkspaceFileSystem
{
    /// <summary>Workspace root directory.</summary>
    string RootPath { get; }

    /// <summary>
    ///     Resolve a raw path to a contained WorkspacePath, or null if
    ///     the path escapes the workspace root or is invalid.
    /// </summary>
    WorkspacePath? Resolve(string rawPath);

    /// <summary>Get file/directory metadata, or null if not found.</summary>
    ValueTask<FileStat?> StatAsync(WorkspacePath path, CancellationToken ct = default);

    /// <summary>List directory contents. Returns empty list if path is not a directory or doesn't exist.</summary>
    ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(
        WorkspacePath path,
        CancellationToken ct = default);

    /// <summary>Read raw file bytes. Returns empty memory if file not found or unreadable.</summary>
    ValueTask<ReadOnlyMemory<byte>> ReadFileAsync(
        WorkspacePath path,
        CancellationToken ct = default);

    /// <summary>Write content to a file, creating parent directories if needed.</summary>
    ValueTask WriteFileAsync(
        WorkspacePath path,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default);

    /// <summary>Delete a file or empty directory. Throws if directory is not empty.</summary>
    ValueTask DeleteAsync(WorkspacePath path, CancellationToken ct = default);

    /// <summary>Move/rename a file or directory. Creates parent directories if needed.</summary>
    ValueTask MoveAsync(WorkspacePath from, WorkspacePath to, CancellationToken ct = default);
}

// ============================================================
// Host-backed implementation
// ============================================================

/// <summary>
///     IWorkspaceFileSystem backed by the local file system with path containment.
///     Every method re-validates containment at the boundary, so even manually
///     constructed WorkspacePath values with traversal segments are rejected.
/// </summary>
public class HostWorkspaceFileSystem : IWorkspaceFileSystem
{
    public HostWorkspaceFileSystem(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath { get; }

    public WorkspacePath? Resolve(string rawPath)
    {
        if (rawPath is null)
        {
            return null;
        }

        string normalized = rawPath.Replace('\\', '/').TrimStart('/');
        string combined = Path.Combine(RootPath, normalized);
        string fullPath = Path.GetFullPath(combined);

        if (
            !fullPath.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !fullPath.Equals(RootPath, StringComparison.Ordinal)
        )
        {
            return null;
        }

        string relative = fullPath.Equals(RootPath, StringComparison.Ordinal)
            ? ""
            : fullPath[(RootPath.Length + 1)..];

        return new WorkspacePath(relative);
    }

    public ValueTask<FileStat?> StatAsync(WorkspacePath path, CancellationToken ct = default)
    {
        string abs = ToAbsolute(path); // may throw containment violation
        try
        {
            if (File.Exists(abs))
            {
                var fi = new FileInfo(abs);
                return ValueTask.FromResult<FileStat?>(new FileStat(path.Value,
                    fi.Length,
                    fi.LastWriteTimeUtc,
                    false,
                    IsBinaryExtension(fi.Extension)));
            }

            if (Directory.Exists(abs))
            {
                var di = new DirectoryInfo(abs);
                return ValueTask.FromResult<FileStat?>(new FileStat(path.Value, 0, di.LastWriteTimeUtc, true, false));
            }

            return ValueTask.FromResult<FileStat?>(null);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            return ValueTask.FromResult<FileStat?>(null);
        }
    }

    public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(
        WorkspacePath path,
        CancellationToken ct = default)
    {
        string abs = ToAbsolute(path); // may throw containment violation
        try
        {
            if (!Directory.Exists(abs))
            {
                return ValueTask.FromResult<IReadOnlyList<DirectoryEntry>>(Array.Empty<DirectoryEntry>());
            }

            var entries = new List<DirectoryEntry>();
            foreach (string entry in Directory.EnumerateFileSystemEntries(abs))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string name = Path.GetFileName(entry);

                    if (Directory.Exists(entry))
                    {
                        entries.Add(new DirectoryEntry(name + "/", true, 0));
                    }
                    else
                    {
                        var fi = new FileInfo(entry);
                        int? lineCount = null;

                        // Use content-based detection (more reliable than extension alone).
                        // Only count lines for text files; binary files show "(binary)" in listings.
                        bool isBinaryByExtension = IsBinaryExtension(fi.Extension);
                        bool isText = !isBinaryByExtension && TextEncodingDetector.IsTextFile(entry);

                        if (isText)
                        {
                            try
                            {
                                lineCount = File.ReadLines(entry).Count();
                            }
                            catch { }
                        }

                        entries.Add(new DirectoryEntry(name, false, fi.Length, lineCount));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Skip entries that can't be accessed (device files, permission errors, etc.)
                }
            }

            return ValueTask.FromResult<IReadOnlyList<DirectoryEntry>>(entries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VFS] ReadDirectoryAsync failed for '{abs}': {ex.GetType().Name}: {ex.Message}");
            return ValueTask.FromResult<IReadOnlyList<DirectoryEntry>>(Array.Empty<DirectoryEntry>());
        }
    }

    public virtual async ValueTask<ReadOnlyMemory<byte>> ReadFileAsync(
        WorkspacePath path,
        CancellationToken ct = default)
    {
        string abs = ToAbsolute(path); // may throw containment violation
        try
        {
            if (!File.Exists(abs))
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            byte[] bytes = await File.ReadAllBytesAsync(abs, ct);
            return new ReadOnlyMemory<byte>(bytes);
        }
        catch
        {
            return ReadOnlyMemory<byte>.Empty;
        }
    }

    public virtual async ValueTask WriteFileAsync(
        WorkspacePath path,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default)
    {
        string abs = ToAbsolute(path);
        string? dir = Path.GetDirectoryName(abs);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllBytesAsync(abs, content.ToArray(), ct);
    }

    public virtual ValueTask DeleteAsync(WorkspacePath path, CancellationToken ct = default)
    {
        string abs = ToAbsolute(path);
        if (File.Exists(abs))
        {
            File.Delete(abs);
        }
        else if (Directory.Exists(abs))
        {
            if (Directory.EnumerateFileSystemEntries(abs).Any())
            {
                throw new InvalidOperationException($"Directory is not empty: {path.Value}");
            }

            Directory.Delete(abs);
        }

        return ValueTask.CompletedTask;
    }

    public virtual async ValueTask MoveAsync(
        WorkspacePath from,
        WorkspacePath to,
        CancellationToken ct = default)
    {
        string absFrom = ToAbsolute(from);
        string absTo = ToAbsolute(to);
        string? dir = Path.GetDirectoryName(absTo);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(absFrom))
        {
            File.Move(absFrom, absTo);
        }
        else if (Directory.Exists(absFrom))
        {
            Directory.Move(absFrom, absTo);
        }
    }

    /// <summary>
    ///     Convert a WorkspacePath to an absolute filesystem path.
    ///     Rejects paths that escape the workspace root.
    /// </summary>
    private string ToAbsolute(WorkspacePath path)
    {
        string combined = Path.Combine(RootPath, path.Value);
        string fullPath = Path.GetFullPath(combined);

        if (
            !fullPath.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !fullPath.Equals(RootPath, StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException(
                $"Path escapes workspace root: '{path.Value}' resolves to '{fullPath}' outside '{RootPath}'");
        }

        return fullPath;
    }

    private static bool IsBinaryExtension(string extension)
    {
        string ext = extension.ToLowerInvariant();
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
}
