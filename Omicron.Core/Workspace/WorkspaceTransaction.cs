using System.Text;
using Omicron.Core.Diff;
using Omicron.Core.Events;

namespace Omicron.Core.Workspace;

/// <summary>
/// A workspace transaction that stages file changes and can produce diffs,
/// then commit to the host VFS or roll back.
/// Provides a read-through overlay VFS via Files so staged changes are visible.
/// </summary>
public interface IWorkspaceTransaction : IAsyncDisposable
{
    WorkspaceTransactionId Id { get; }
    IWorkspaceFileSystem Host { get; }
    IWorkspaceFileSystem Files { get; }
    ValueTask<WorkspaceDiff> GetDiffAsync(CancellationToken ct = default);
    ValueTask CommitAsync(CancellationToken ct = default);
    ValueTask RollbackAsync(CancellationToken ct = default);
}

/// <summary>
/// Default workspace transaction implementation.
/// Provides overlay VFS via Files, computes diffs, and commits or rolls back.
/// Emits lifecycle events when an <see cref="IEventSink"/> is provided.
/// Commit uses operation journaling: on failure, attempts to reverse completed operations.
/// </summary>
public sealed class WorkspaceTransaction : IWorkspaceTransaction
{
    private readonly HostWorkspaceFileSystem _host;
    private readonly TransactionFileSystem _overlay;
    private readonly IEventSink? _eventSink;
    private bool _committed;
    private bool _disposed;
    private bool _anyStaged;
    private readonly List<CommittedOperation> _journal = [];

    public WorkspaceTransactionId Id { get; }
    public IWorkspaceFileSystem Host => _host;
    public IWorkspaceFileSystem Files => _overlay;

    public WorkspaceTransaction(HostWorkspaceFileSystem host, IEventSink? eventSink = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _eventSink = eventSink;
        Id = WorkspaceTransactionId.New();
        _overlay = new TransactionFileSystem(this, _host);
    }

    // ============================================================
    // Staging internals (called by TransactionFileSystem)
    // ============================================================

    private readonly Dictionary<string, StagedEntry> _staged = new(StringComparer.Ordinal);

    /// <summary>Normalize path to forward slashes for consistent cross-platform lookup.</summary>
    private static string NormalizePath(string path) => path.Replace('\\', '/');

    internal void StageWrite(WorkspacePath path, ReadOnlyMemory<byte> content)
    {
        ThrowIfFinalized();
        var key = NormalizePath(path.Value);

        if (_staged.TryGetValue(key, out var existing))
        {
            if (existing.Action == StagedAction.Delete)
                throw new InvalidOperationException($"Cannot write to a path staged for deletion: {key}");
            if (existing.Action == StagedAction.MoveFrom)
                throw new InvalidOperationException($"Cannot write to a move source: {key}");
            if (existing.Action == StagedAction.MoveTo)
                throw new InvalidOperationException($"Cannot write to a move destination: {key}");
        }
        _staged[key] = new StagedEntry(StagedAction.Write, content);
        EmitStagedEvent(key, "write");
    }

    internal void StageDelete(WorkspacePath path)
    {
        ThrowIfFinalized();
        var key = NormalizePath(path.Value);

        if (_staged.TryGetValue(key, out var existing))
        {
            if (existing.Action == StagedAction.Write)
            {
                _staged.Remove(key); // Delete undoes a staged write
                return;
            }
            if (existing.Action == StagedAction.MoveTo)
                throw new InvalidOperationException($"Cannot delete a move destination: {key}");
            if (existing.Action == StagedAction.MoveFrom)
                throw new InvalidOperationException($"Cannot delete a move source: {key}");
            return; // Already delete, no-op
        }

        _staged[key] = new StagedEntry(StagedAction.Delete, default);
        EmitStagedEvent(key, "delete");
    }

    internal void StageMove(WorkspacePath from, WorkspacePath to)
    {
        ThrowIfFinalized();
        var fromKey = NormalizePath(from.Value);
        var toKey = NormalizePath(to.Value);

        // Reject move of staged writes (content would be lost)
        if (_staged.TryGetValue(fromKey, out var fromExisting))
        {
            if (fromExisting.Action == StagedAction.Write)
                throw new InvalidOperationException($"Cannot move a path that has a staged write: {fromKey}");
            if (fromExisting.Action is StagedAction.Delete or StagedAction.MoveTo)
                throw new InvalidOperationException($"Cannot move a path with conflicting staged operation: {fromKey}");
        }
        if (_staged.TryGetValue(toKey, out var toExisting))
        {
            if (toExisting.Action is StagedAction.Write or StagedAction.Delete or StagedAction.MoveFrom)
                throw new InvalidOperationException($"Cannot move to a path with conflicting staged operation: {toKey}");
        }

        _staged[fromKey] = new StagedEntry(StagedAction.MoveFrom, default, toKey);
        _staged[toKey] = new StagedEntry(StagedAction.MoveTo, default, fromKey);
        EmitStagedEvent(fromKey, "move");
    }

    internal bool TryGetStaged(WorkspacePath path, out StagedEntry entry)
    {
        var found = _staged.TryGetValue(NormalizePath(path.Value), out var value);
        entry = value!;
        return found;
    }

    internal bool IsStaged(WorkspacePath path) => _staged.ContainsKey(NormalizePath(path.Value));

    internal IEnumerable<KeyValuePair<string, StagedEntry>> EnumerateStaged() => _staged;

    // ============================================================
    // IWorkspaceTransaction
    // ============================================================

    public async ValueTask<WorkspaceDiff> GetDiffAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var fileDiffs = new List<WorkspaceFileDiff>();
        var handled = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (pathStr, entry) in _staged)
        {
            if (handled.Contains(pathStr)) continue;
            handled.Add(pathStr);
            var wsPath = new WorkspacePath(pathStr);

            switch (entry.Action)
            {
                case StagedAction.Write:
                {
                    var fileDiff = await BuildDiff(wsPath, entry, ct);
                    if (fileDiff is not null)
                        fileDiffs.Add(fileDiff);
                    break;
                }
                case StagedAction.Delete:
                {
                    var oldStat = await _host.StatAsync(wsPath, ct);
                    if (oldStat is null) break;
                    if (oldStat.IsBinary)
                    {
                        fileDiffs.Add(new WorkspaceFileDiff(wsPath, WorkspaceChangeKind.Deleted, pathStr, null, true, false));
                    }
                    else
                    {
                        var oldBytes = await _host.ReadFileAsync(wsPath, ct);
                        var oldLines = TextLineSplitter.CreateUtf8LineIndex(oldBytes);
                        var diff = TextDiffEngine.DiffLines(oldLines, Utf8LineIndex.Empty);
                        var textDiff = UnifiedDiffRenderer.Render(pathStr, "/dev/null", oldLines, Utf8LineIndex.Empty, diff);
                        fileDiffs.Add(new WorkspaceFileDiff(wsPath, WorkspaceChangeKind.Deleted, pathStr, textDiff, false, false));
                    }
                    break;
                }
                case StagedAction.MoveTo:
                {
                    handled.Add(entry.MoveFromPath!);
                    var fromPath = entry.MoveFromPath!;
                    var fromWsPath = new WorkspacePath(fromPath);
                    var oldStat = await _host.StatAsync(fromWsPath, ct);
                    bool isBinary = oldStat?.IsBinary ?? false;

                    if (isBinary)
                    {
                        fileDiffs.Add(new WorkspaceFileDiff(wsPath, WorkspaceChangeKind.Moved, fromPath, null, true, false));
                    }
                    else
                    {
                        var oldBytes = await _host.ReadFileAsync(fromWsPath, ct);
                        var newContent = entry.Content.IsEmpty ? oldBytes : entry.Content;

                        var oldLines = TextLineSplitter.CreateUtf8LineIndex(oldBytes);
                        var newLines = TextLineSplitter.CreateUtf8LineIndex(newContent);
                        var diff = TextDiffEngine.DiffLines(oldLines, newLines);
                        var textDiff = diff.HasChanges
                            ? UnifiedDiffRenderer.Render(fromPath, pathStr, oldLines, newLines, diff)
                            : null;

                        fileDiffs.Add(new WorkspaceFileDiff(wsPath, WorkspaceChangeKind.Moved, fromPath, textDiff, false, false));
                    }
                    break;
                }
            }
        }

        return new WorkspaceDiff(Id, fileDiffs);
    }

    public async ValueTask CommitAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_committed) throw new InvalidOperationException("Transaction already committed.");
        _committed = true;

        try
        {
            foreach (var (pathStr, entry) in _staged)
            {
                var wsPath = new WorkspacePath(pathStr);
                switch (entry.Action)
                {
                    case StagedAction.Write:
                        await _host.WriteFileAsync(wsPath, entry.Content, ct);
                        _journal.Add(new CommittedOperation(CommittedAction.Write, pathStr));
                        break;
                    case StagedAction.Delete:
                        await _host.DeleteAsync(wsPath, ct);
                        _journal.Add(new CommittedOperation(CommittedAction.Delete, pathStr));
                        break;
                    case StagedAction.MoveTo:
                        var fromPath = entry.MoveFromPath!;
                        await _host.MoveAsync(new WorkspacePath(fromPath), wsPath, ct);
                        _journal.Add(new CommittedOperation(CommittedAction.Move, pathStr, fromPath));
                        break;
                }
            }
            _staged.Clear();

            if (_eventSink is not null)
            {
                var envelope = new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, SessionId.Empty);
                _eventSink.Emit(new TransactionCommittedEvent(envelope, Id));
            }
        }
        catch
        {
            // Attempt to reverse completed operations in reverse order
            Exception? rollbackEx = null;
            for (int i = _journal.Count - 1; i >= 0; i--)
            {
                try
                {
                    var op = _journal[i];
                    switch (op.Action)
                    {
                        case CommittedAction.Write:
                            await _host.DeleteAsync(new WorkspacePath(op.Path), ct);
                            break;
                        case CommittedAction.Delete:
                            // Cannot undelete without backup; best-effort
                            break;
                        case CommittedAction.Move:
                            // Move back
                            await _host.MoveAsync(new WorkspacePath(op.Path), new WorkspacePath(op.FromPath!), ct);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    rollbackEx = ex;
                }
            }

            _committed = false;

            if (_eventSink is not null)
            {
                var envelope = new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, SessionId.Empty);
                _eventSink.Emit(new TransactionRolledBackEvent(envelope, Id));
            }

            throw new InvalidOperationException(
                "Transaction commit failed and was rolled back." +
                (rollbackEx is not null ? $" Rollback error: {rollbackEx.Message}" : ""),
                rollbackEx);
        }
    }

    public ValueTask RollbackAsync(CancellationToken ct = default)
    {
        if (_disposed) return ValueTask.CompletedTask;
        _staged.Clear();

        if (_eventSink is not null && _anyStaged)
        {
            var envelope = new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, SessionId.Empty);
            _eventSink.Emit(new TransactionRolledBackEvent(envelope, Id));
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_committed && _anyStaged && _eventSink is not null)
        {
            var envelope = new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, SessionId.Empty);
            _eventSink.Emit(new TransactionRolledBackEvent(envelope, Id));
        }

        _staged.Clear();
    }

    // ============================================================
    // Diff helpers
    // ============================================================

    private async Task<WorkspaceFileDiff?> BuildDiff(WorkspacePath path, StagedEntry entry, CancellationToken ct)
    {
        var hostStat = await _host.StatAsync(path, ct);
        var content = entry.Content;

        bool isBinary = BinaryExtensions.Contains(Path.GetExtension(path.Value))
            || (content.Length > 0 && IsBinaryContent(content))
            || (hostStat?.IsBinary ?? false);

        if (hostStat is null)
        {
            if (isBinary)
                return new WorkspaceFileDiff(path, WorkspaceChangeKind.Added, null, null, true, false);

            var addLines = TextLineSplitter.CreateUtf8LineIndex(content);
            var addDiff = TextDiffEngine.DiffLines(Utf8LineIndex.Empty, addLines);
            var addRendered = UnifiedDiffRenderer.Render("/dev/null", path.Value, Utf8LineIndex.Empty, addLines, addDiff);
            return new WorkspaceFileDiff(path, WorkspaceChangeKind.Added, null, addRendered, false, false);
        }

        if (isBinary)
            return new WorkspaceFileDiff(path, WorkspaceChangeKind.Modified, path.Value, null, true, false);

        var oldBytes = await _host.ReadFileAsync(path, ct);

        var oldLines = TextLineSplitter.CreateUtf8LineIndex(oldBytes);
        var newLines = TextLineSplitter.CreateUtf8LineIndex(content);
        var diff = TextDiffEngine.DiffLines(oldLines, newLines);

        if (!diff.HasChanges && !diff.IsTruncated)
            return null;

        var textDiff = UnifiedDiffRenderer.Render(path.Value, path.Value, oldLines, newLines, diff);
        return new WorkspaceFileDiff(path, WorkspaceChangeKind.Modified, path.Value, textDiff, false, false);
    }

    /// <summary>Count lines in UTF-8 bytes without decoding to a string.</summary>
    internal static int CountLinesUtf8(ReadOnlySpan<byte> span)
    {
        if (span.Length == 0)
            return 0;

        int count = 1; // at least one line even with no newline
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == (byte)'\n')
            {
                count++;
            }
        }

        // If the file ends with \n, the final newline does not start a new line.
        if (span[^1] == (byte)'\n')
            count--;

        return count;
    }

    private static bool IsBinaryContent(ReadOnlyMemory<byte> content)
    {
        int len = Math.Min(content.Length, 8192);
        for (int i = 0; i < len; i++)
            if (content.Span[i] == 0)
                return true;
        return false;
    }

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp",
        ".dll", ".exe", ".so", ".dylib", ".node",
        ".zip", ".tar", ".gz", ".7z", ".rar",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx",
        ".mp3", ".mp4", ".avi", ".mov", ".wav",
        ".ttf", ".otf", ".woff", ".woff2"
    };

    // ============================================================
    // Event helpers
    // ============================================================

    private void EmitTransactionEvent<T>(T evt) where T : OmicronEvent
    {
        if (_eventSink is not null)
        {
            _eventSink.Emit(evt);
        }
    }

    private void EmitStagedEvent(string path, string action)
    {
        if (!_anyStaged)
        {
            _anyStaged = true;
            var startEnvelope = new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, SessionId.Empty);
            EmitTransactionEvent(new TransactionStartedEvent(startEnvelope, Id));
        }

        var envelope = new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, SessionId.Empty);
        EmitTransactionEvent(new TransactionStagedEvent(envelope, Id, path, action));
    }

    // ============================================================
    // Types
    // ============================================================

    internal enum StagedAction { Write, Delete, MoveFrom, MoveTo }

    internal enum CommittedAction { Write, Delete, Move }

    internal sealed record StagedEntry(
        StagedAction Action,
        ReadOnlyMemory<byte> Content,
        string? MoveFromPath = null);

    internal sealed record CommittedOperation(
        CommittedAction Action,
        string Path,
        string? FromPath = null);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WorkspaceTransaction));
    }

    private void ThrowIfFinalized()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WorkspaceTransaction));
        if (_committed) throw new InvalidOperationException("Transaction already committed.");
    }
}

// ============================================================
// Transaction overlay file system
// ============================================================

internal sealed class TransactionFileSystem : IWorkspaceFileSystem
{
    private readonly WorkspaceTransaction _tx;
    private readonly HostWorkspaceFileSystem _host;

    public string RootPath => _host.RootPath;

    public TransactionFileSystem(WorkspaceTransaction tx, HostWorkspaceFileSystem host)
    {
        _tx = tx;
        _host = host;
    }

    public WorkspacePath? Resolve(string rawPath) => _host.Resolve(rawPath);

    public async ValueTask<FileStat?> StatAsync(WorkspacePath path, CancellationToken ct = default)
    {
        if (_tx.TryGetStaged(path, out var entry))
        {
            if (entry.Action == WorkspaceTransaction.StagedAction.Delete
                || entry.Action == WorkspaceTransaction.StagedAction.MoveFrom)
                return null;

            if (entry.Action == WorkspaceTransaction.StagedAction.MoveTo)
            {
                // Move destination: stat the source path on host, project to dest
                var fromPath = new WorkspacePath(entry.MoveFromPath!);
                var sourceStat = await _host.StatAsync(fromPath, ct);
                if (sourceStat is null) return null;
                return sourceStat with { Path = path.Value };
            }

            if (entry.Action == WorkspaceTransaction.StagedAction.Write)
            {
                var hostStat = await _host.StatAsync(path, ct);
                if (hostStat is not null)
                    return hostStat with { Size = entry.Content.Length };
                bool isBinary = entry.Content.Length > 0 && entry.Content.Span.IndexOf((byte)0) >= 0;
                return new FileStat(path.Value, entry.Content.Length, DateTime.UtcNow, false, isBinary);
            }
        }

        return await _host.StatAsync(path, ct);
    }

    public async ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(WorkspacePath path, CancellationToken ct = default)
    {
        var hostEntries = await _host.ReadDirectoryAsync(path, ct);
        var result = hostEntries.ToList();
        var prefix = string.IsNullOrEmpty(path.Value) ? "" : path.Value.Replace('\\', '/') + "/";

        // Add staged files in this directory
        foreach (var (p, entry) in _tx.EnumerateStaged())
        {
            var dir = Path.GetDirectoryName(p)?.Replace('\\', '/') ?? "";
            dir = string.IsNullOrEmpty(dir) ? "" : dir + "/";

            if (dir == prefix)
            {
                var name = Path.GetFileName(p);
                if (entry.Action == WorkspaceTransaction.StagedAction.Write && !result.Any(e => e.Name == name))
                {
                    bool isBinary = entry.Content.Length > 0 && entry.Content.Span.IndexOf((byte)0) >= 0;
                    int? lineCount = null;
                    if (!isBinary)
                    {
                        try
                        {
                            // Count lines from raw bytes without decoding the whole file to a string
                            lineCount = WorkspaceTransaction.CountLinesUtf8(entry.Content.Span);
                        }
                        catch { }
                    }
                    result.Add(new DirectoryEntry(name, false, entry.Content.Length, lineCount));
                }
                else if (entry.Action == WorkspaceTransaction.StagedAction.MoveTo && !result.Any(e => e.Name == name))
                {
                    var fromPath = new WorkspacePath(entry.MoveFromPath!);
                    var fromStat = await _host.StatAsync(fromPath, ct);
                    result.Add(new DirectoryEntry(name, false, fromStat?.Size ?? 0, null));
                }
            }
        }

        // Remove staged deletes/moves from this directory
        result.RemoveAll(e =>
        {
            var fullPath = prefix + e.Name.TrimEnd('/');
            var wsPath = new WorkspacePath(fullPath);
            return _tx.TryGetStaged(wsPath, out var entry)
                && (entry.Action == WorkspaceTransaction.StagedAction.Delete
                    || entry.Action == WorkspaceTransaction.StagedAction.MoveFrom);
        });

        return result;
    }

    public ValueTask<ReadOnlyMemory<byte>> ReadFileAsync(WorkspacePath path, CancellationToken ct = default)
    {
        if (_tx.TryGetStaged(path, out var entry))
        {
            if (entry.Action == WorkspaceTransaction.StagedAction.Write)
                return ValueTask.FromResult(entry.Content);

            if (entry.Action == WorkspaceTransaction.StagedAction.MoveTo)
            {
                // Read from source path
                var fromPath = new WorkspacePath(entry.MoveFromPath!);
                return _host.ReadFileAsync(fromPath, ct);
            }

            if (entry.Action == WorkspaceTransaction.StagedAction.Delete
                || entry.Action == WorkspaceTransaction.StagedAction.MoveFrom)
                return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
        }

        return _host.ReadFileAsync(path, ct);
    }

    public ValueTask WriteFileAsync(WorkspacePath path, ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        _tx.StageWrite(path, content);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(WorkspacePath path, CancellationToken ct = default)
    {
        _tx.StageDelete(path);
        return ValueTask.CompletedTask;
    }

    public ValueTask MoveAsync(WorkspacePath from, WorkspacePath to, CancellationToken ct = default)
    {
        _tx.StageMove(from, to);
        return ValueTask.CompletedTask;
    }
}
