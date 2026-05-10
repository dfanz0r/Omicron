using System.Text;

namespace Omicron.Core.Workspace;

/// <summary>
/// Builds a structured WorkspaceReadContent from an IWorkspaceFileSystem source.
/// </summary>
public interface IWorkspaceReadService
{
    /// <summary>Read a path and produce structured content.</summary>
    ValueTask<WorkspaceReadContent> ReadAsync(
        string path,
        ReadOptions? options = null,
        CancellationToken ct = default);

    /// <summary>The workspace root path.</summary>
    string RootPath { get; }

    /// <summary>Resolve a raw path to a contained WorkspacePath.</summary>
    WorkspacePath? Resolve(string rawPath);
}

/// <summary>
/// Default implementation backed by IWorkspaceFileSystem.
/// </summary>
public sealed class WorkspaceReadService : IWorkspaceReadService
{
    public const int MaxOutputLines = 2000;
    public const int MaxOutputBytes = 50 * 1024;
    public const int MaxDirEntries = 200;
    public const int DefaultChunkSizeBytes = 50 * 1024;

    private readonly IWorkspaceFileSystem _vfs;

    public string RootPath => _vfs.RootPath;
    public WorkspacePath? Resolve(string rawPath) => _vfs.Resolve(rawPath);

    public WorkspaceReadService(IWorkspaceFileSystem vfs)
    {
        _vfs = vfs ?? throw new ArgumentNullException(nameof(vfs));
    }

    public async ValueTask<WorkspaceReadContent> ReadAsync(
        string path,
        ReadOptions? options = null,
        CancellationToken ct = default)
    {
        var wsPath = _vfs.Resolve(path);
        if (wsPath is null)
            return new WorkspaceReadErrorContent
            {
                RequestedPath = path,
                Kind = WorkspaceReadErrorKind.EscapesRoot,
                Message = $"Path escapes workspace root: '{path}' resolves outside '{_vfs.RootPath}'"
            };

        var stat = await _vfs.StatAsync(wsPath.Value, ct);
        if (stat is null)
            return new WorkspaceReadErrorContent
            {
                RequestedPath = path,
                Kind = WorkspaceReadErrorKind.NotFound,
                Message = $"Path not found: {path}"
            };

        if (stat.IsDirectory)
        {
            var allEntries = await _vfs.ReadDirectoryAsync(wsPath.Value, ct);
            // Sort first, then truncate — ensures deterministic visible set
            var sorted = allEntries
                .OrderBy(e => e.IsDirectory ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.Ordinal)
                .ToList();
            var shown = sorted.Take(MaxDirEntries).ToList();
            return new WorkspaceDirectoryContent
            {
                RequestedPath = path,
                ResolvedPath = wsPath.Value,
                Entries = shown,
                Truncated = allEntries.Count > MaxDirEntries,
                TotalFileCount = allEntries.Count(e => !e.IsDirectory),
                TotalDirCount = allEntries.Count(e => e.IsDirectory)
            };
        }

        if (stat.IsBinary)
            return new WorkspaceBinaryFileContent
            {
                RequestedPath = path,
                ResolvedPath = wsPath.Value,
                Stat = stat
            };

        var bytes = await _vfs.ReadFileAsync(wsPath.Value, ct);
        if (bytes.IsEmpty && stat.Size > 0)
            return new WorkspaceReadErrorContent
            {
                RequestedPath = path,
                Kind = WorkspaceReadErrorKind.Unreadable,
                Message = $"Error reading file: {path}"
            };

        var (lines, totalLines, truncated, nextOffset) = ParseLines(bytes, options);

        return new WorkspaceFileContent
        {
            RequestedPath = path,
            ResolvedPath = wsPath.Value,
            Stat = stat,
            Lines = lines,
            TotalLines = totalLines,
            Truncated = truncated,
            NextOffset = nextOffset
        };
    }

    /// <summary>Split file bytes into lines, applying offset/limit/chunk.</summary>
    internal static (IReadOnlyList<WorkspaceTextLine> Lines, int TotalLines, bool Truncated, int? NextOffset) ParseLines(
        ReadOnlyMemory<byte> bytes, ReadOptions? options)
    {
        if (bytes.IsEmpty)
            return (Array.Empty<WorkspaceTextLine>(), 0, false, null);

        var text = Encoding.UTF8.GetString(bytes.Span);
        var allLines = text.Replace("\r\n", "\n").Split('\n');
        var totalLines = allLines.Length;

        // Chunk: find the 0-based line index that corresponds to chunk * DefaultChunkSizeBytes
        int chunkStartLine = 0;
        if (options?.Chunk is > 0)
        {
            var targetByte = options.Chunk.Value * DefaultChunkSizeBytes;
            int byteCount = 0;
            int foundStart = -1;
            for (int i = 0; i < allLines.Length; i++)
            {
                int lineBytes = Encoding.UTF8.GetByteCount(allLines[i]) + 1;
                if (byteCount + lineBytes > targetByte)
                {
                    foundStart = i;
                    break;
                }
                byteCount += lineBytes;
            }
            if (foundStart < 0)
            {
                // Chunk is past EOF — return empty
                return (Array.Empty<WorkspaceTextLine>(), totalLines, false, null);
            }
            chunkStartLine = foundStart;
        }

        // Offset is 1-based line number relative to chunk start (when chunk is set).
        // When chunk is not set, offset is absolute (1-based from file start).
        int offsetLine = options?.Offset is > 0 ? options.Offset.Value - 1 : 0;
        int startLine = chunkStartLine + offsetLine;

        if (startLine >= allLines.Length)
            return (Array.Empty<WorkspaceTextLine>(), totalLines, false, null);

        int limit = options?.Limit is > 0 ? options.Limit.Value : int.MaxValue;
        int endLine = Math.Min(startLine + limit, allLines.Length);

        var sliceLines = allLines[startLine..endLine];
        var result = new List<WorkspaceTextLine>(sliceLines.Length);

        int byteCountWritten = 0;
        for (int i = 0; i < sliceLines.Length; i++)
        {
            var line = sliceLines[i];
            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;

            if (result.Count >= MaxOutputLines || byteCountWritten + lineBytes > MaxOutputBytes)
                break;

            result.Add(new WorkspaceTextLine(startLine + i + 1, line));
            byteCountWritten += lineBytes;
        }

        bool truncated = result.Count < sliceLines.Length || endLine < allLines.Length;
        int? nextOffset = truncated ? startLine + result.Count + 1 : null;

        return (result, totalLines, truncated, nextOffset);
    }
}
