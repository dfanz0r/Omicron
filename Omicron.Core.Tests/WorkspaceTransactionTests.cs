using Omicron.Core.Events;
using Omicron.Core.Workspace;
using Xunit;

namespace Omicron.Core.Tests;

public class WorkspaceTransactionTests : IDisposable
{
    private readonly string _root;
    private readonly HostWorkspaceFileSystem _host;
    private readonly WorkspaceTransaction _tx;

    public WorkspaceTransactionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"omicron-tx-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _host = new HostWorkspaceFileSystem(_root);
        _tx = new WorkspaceTransaction(_host);
    }

    public void Dispose()
    {
        _tx.DisposeAsync().GetAwaiter().GetResult();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string ReadHostFile(string path) =>
        File.ReadAllText(Path.Combine(_root, path));

    private bool HostFileExists(string path) =>
        File.Exists(Path.Combine(_root, path));

    private void WriteHostFile(string path, string content)
    {
        var abs = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private WorkspacePath Resolve(string path) => _host.Resolve(path)!.Value;

    [Fact]
    public async Task StageWrite_VisibleInDiff_NotInHost()
    {
        var path = Resolve("new.txt");
        var content = "hello"u8.ToArray();

        _tx.StageWrite(path, content);

        // Check host does NOT have it yet
        var stat = await _host.StatAsync(path);
        Assert.Null(stat);

        // Diff should show it as added
        var diff = await _tx.GetDiffAsync();
        Assert.True(diff.HasChanges);
        var fileDiff = Assert.Single(diff.FileDiffs);
        Assert.Equal(WorkspaceChangeKind.Added, fileDiff.Kind);
        Assert.Equal("new.txt", fileDiff.Path.Value);
        Assert.NotNull(fileDiff.TextDiff);
        Assert.Contains("+hello", fileDiff.TextDiff);
    }

    [Fact]
    public async Task Commit_WritesToHost()
    {
        var path = Resolve("commit.txt");
        _tx.StageWrite(path, "committed content"u8.ToArray());

        await _tx.CommitAsync();

        Assert.True(HostFileExists("commit.txt"));
        Assert.Equal("committed content", ReadHostFile("commit.txt"));
    }

    [Fact]
    public async Task Rollback_DiscardsStagedChanges()
    {
        var path = Resolve("rollback.txt");
        _tx.StageWrite(path, "will be lost"u8.ToArray());

        await _tx.RollbackAsync();

        Assert.False(HostFileExists("rollback.txt"));
    }

    [Fact]
    public async Task StageWrite_ModifiedFile_ShowsDiff()
    {
        WriteHostFile("modified.txt", "line1\nline2\nline3");
        var path = Resolve("modified.txt");

        _tx.StageWrite(path, "line1\nchanged\nline3"u8.ToArray());

        var diff = await _tx.GetDiffAsync();
        var fileDiff = Assert.Single(diff.FileDiffs);
        Assert.Equal(WorkspaceChangeKind.Modified, fileDiff.Kind);
        Assert.NotNull(fileDiff.TextDiff);
        Assert.Contains("-line2", fileDiff.TextDiff);
        Assert.Contains("+changed", fileDiff.TextDiff);
    }

    [Fact]
    public async Task StageDelete_DeletedFile_ShowsDeletion()
    {
        WriteHostFile("todelete.txt", "delete me");
        var path = Resolve("todelete.txt");

        _tx.StageDelete(path);

        var diff = await _tx.GetDiffAsync();
        var fileDiff = Assert.Single(diff.FileDiffs);
        Assert.Equal(WorkspaceChangeKind.Deleted, fileDiff.Kind);
        Assert.Equal("todelete.txt", fileDiff.Path.Value);
    }

    [Fact]
    public async Task CommitDelete_RemovesFromHost()
    {
        WriteHostFile("todelete.txt", "delete me");
        var path = Resolve("todelete.txt");

        _tx.StageDelete(path);
        await _tx.CommitAsync();

        Assert.False(HostFileExists("todelete.txt"));
    }

    [Fact]
    public async Task DisposeWithoutCommit_RollsBack()
    {
        var path = Resolve("dispose.txt");
        _tx.StageWrite(path, "data"u8.ToArray());

        // Dispose without commit
        await _tx.DisposeAsync();

        Assert.False(HostFileExists("dispose.txt"));
    }

    [Fact]
    public async Task BinaryFile_ShowsBinaryMarker()
    {
        WriteHostFile("image.png", "fake-png-content");
        var path = Resolve("image.png");

        _tx.StageWrite(path, "modified-png"u8.ToArray());

        var diff = await _tx.GetDiffAsync();
        var fileDiff = Assert.Single(diff.FileDiffs);
        Assert.Equal(WorkspaceChangeKind.Modified, fileDiff.Kind);
        Assert.True(fileDiff.IsBinary);
        Assert.Null(fileDiff.TextDiff);
    }

    [Fact]
    public async Task AddedFile_ShowsAddedDiff()
    {
        var path = Resolve("added.txt");
        _tx.StageWrite(path, "new line"u8.ToArray());

        var diff = await _tx.GetDiffAsync();
        var fileDiff = Assert.Single(diff.FileDiffs);
        Assert.Equal(WorkspaceChangeKind.Added, fileDiff.Kind);
        Assert.NotNull(fileDiff.TextDiff);
        Assert.Contains("/dev/null", fileDiff.TextDiff);
        Assert.Contains("+new line", fileDiff.TextDiff);
    }

    [Fact]
    public async Task CommitTwice_Throws()
    {
        var path = Resolve("once.txt");
        _tx.StageWrite(path, "data"u8.ToArray());

        await _tx.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _tx.CommitAsync().AsTask());
    }

    [Fact]
    public async Task StageWrite_CreatesParentDirectories()
    {
        var path = Resolve("a/b/c/deep.txt");
        _tx.StageWrite(path, "deep"u8.ToArray());
        await _tx.CommitAsync();

        Assert.True(HostFileExists("a/b/c/deep.txt"));
    }

    [Fact]
    public async Task Overlay_ReadFile_ShowsStagedContent()
    {
        var path = Resolve("overlay.txt");
        _tx.StageWrite(path, "staged content"u8.ToArray());

        var content = await _tx.Files.ReadFileAsync(path);
        Assert.Equal("staged content"u8.ToArray(), content.ToArray());
    }

    [Fact]
    public async Task Overlay_Stat_ShowsStagedFile()
    {
        WriteHostFile("existing.txt", "original");
        var path = Resolve("existing.txt");

        var statBefore = await _tx.Files.StatAsync(path);
        Assert.NotNull(statBefore);
        Assert.Equal(8, statBefore!.Size); // "original"

        _tx.StageWrite(path, "modified content"u8.ToArray());

        var statAfter = await _tx.Files.StatAsync(path);
        Assert.NotNull(statAfter);
        Assert.Equal(16, statAfter!.Size); // "modified content"
    }

    [Fact]
    public async Task Overlay_Stat_StagedDelete_ReturnsNull()
    {
        WriteHostFile("todelete.txt", "data");
        var path = Resolve("todelete.txt");

        _tx.StageDelete(path);

        var stat = await _tx.Files.StatAsync(path);
        Assert.Null(stat);
    }

    [Fact]
    public async Task Diff_Paths_UseRealNames()
    {
        WriteHostFile("myfile.txt", "line1\nline2\nline3");
        var path = Resolve("myfile.txt");

        _tx.StageWrite(path, "line1\nmodified\nline3"u8.ToArray());

        var diff = await _tx.GetDiffAsync();
        var fileDiff = Assert.Single(diff.FileDiffs);
        Assert.NotNull(fileDiff.TextDiff);
        Assert.Contains("--- myfile.txt", fileDiff.TextDiff);
        Assert.Contains("+++ myfile.txt", fileDiff.TextDiff);
    }

    [Fact]
    public async Task AddedBinary_ShowsBinaryMarker()
    {
        var path = Resolve("image.png");
        // Content with null byte triggers binary detection
        _tx.StageWrite(path, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var diff = await _tx.GetDiffAsync();
        var fileDiff = Assert.Single(diff.FileDiffs);
        Assert.Equal(WorkspaceChangeKind.Added, fileDiff.Kind);
        Assert.True(fileDiff.IsBinary);
        Assert.Null(fileDiff.TextDiff);
    }

    [Fact]
    public async Task MovedDiff_UsesMovedKind()
    {
        WriteHostFile("source.txt", "move me");
        var from = Resolve("source.txt");
        var to = Resolve("dest.txt");

        _tx.StageMove(from, to);

        var diff = await _tx.GetDiffAsync();
        Assert.Contains(diff.FileDiffs, d => d.Kind == WorkspaceChangeKind.Moved);
        Assert.Contains(diff.FileDiffs, d => d.Path.Value == "dest.txt");
        Assert.Contains(diff.FileDiffs, d => d.OldPath == "source.txt");
    }

    [Fact]
    public async Task CommitMove_Works()
    {
        WriteHostFile("source.txt", "move me");
        var from = Resolve("source.txt");
        var to = Resolve("dest.txt");

        _tx.StageMove(from, to);
        await _tx.CommitAsync();

        Assert.False(HostFileExists("source.txt"));
        Assert.True(HostFileExists("dest.txt"));
        Assert.Equal("move me", ReadHostFile("dest.txt"));
    }

    [Fact]
    public async Task Files_WriteThroughOverlay_StagesChange()
    {
        var path = Resolve("through-overlay.txt");
        await _tx.Files.WriteFileAsync(path, "via overlay"u8.ToArray());

        // Should be visible in overlay but not host
        var content = await _tx.Files.ReadFileAsync(path);
        Assert.Equal("via overlay"u8.ToArray(), content.ToArray());
        Assert.False(HostFileExists("through-overlay.txt"));

        // Commit should persist
        await _tx.CommitAsync();
        Assert.True(HostFileExists("through-overlay.txt"));
        Assert.Equal("via overlay", ReadHostFile("through-overlay.txt"));
    }

    [Fact]
    public async Task Overlay_StagedMove_SourceHidden_DestinationVisible()
    {
        WriteHostFile("source.txt", "move content");
        var from = Resolve("source.txt");
        var to = Resolve("dest.txt");

        _tx.StageMove(from, to);

        // Source should be hidden
        var sourceStat = await _tx.Files.StatAsync(from);
        Assert.Null(sourceStat);

        // Destination should be visible
        var destStat = await _tx.Files.StatAsync(to);
        Assert.NotNull(destStat);
        Assert.Equal("dest.txt", destStat!.Path);

        // Reading destination should return source content
        var content = await _tx.Files.ReadFileAsync(to);
        Assert.Equal("move content"u8.ToArray(), content.ToArray());
    }

    [Fact]
    public async Task Overlay_SubdirectoryStagedDelete_NotListed()
    {
        WriteHostFile("sub/a.txt", "content");
        var subPath = Resolve("sub");
        var filePath = Resolve("sub/a.txt");

        _tx.StageDelete(filePath);

        var entries = await _tx.Files.ReadDirectoryAsync(subPath);
        Assert.DoesNotContain(entries, e => e.Name == "a.txt");
    }

    [Fact]
    public async Task Overlay_SubdirectoryStagedWrite_IsListed()
    {
        var filePath = Resolve("sub/new.txt");
        var subPath = Resolve("sub");
        _tx.StageWrite(filePath, "new file"u8.ToArray());

        var entries = await _tx.Files.ReadDirectoryAsync(subPath);
        Assert.Contains(entries, e => e.Name == "new.txt");
    }

    [Fact]
    public void StageWrite_AfterDelete_Throws()
    {
        var path = Resolve("conflict.txt");
        _tx.StageDelete(path);
        Assert.Throws<InvalidOperationException>(() =>
            _tx.StageWrite(path, "data"u8.ToArray()));
    }

    [Fact]
    public void StageDelete_AfterWrite_StagesDelete()
    {
        var path = Resolve("write-then-delete.txt");
        _tx.StageWrite(path, "data"u8.ToArray());
        // Delete after write should remove the write (no-op or cancel)
        _tx.StageDelete(path);
        // Should not be in diff since write was cancelled
        Assert.False(_tx.IsStaged(path));
    }

    [Fact]
    public void StageMove_ConflictingDelete_Throws()
    {
        var from = Resolve("from.txt");
        var to = Resolve("to.txt");
        _tx.StageDelete(to);
        Assert.Throws<InvalidOperationException>(() =>
            _tx.StageMove(from, to));
    }

    [Fact]
    public void StageWrite_ThenMove_Throws()
    {
        var from = Resolve("a.txt");
        var to = Resolve("b.txt");
        _tx.StageWrite(from, "hello"u8.ToArray());
        Assert.Throws<InvalidOperationException>(() =>
            _tx.StageMove(from, to));
    }

    [Fact]
    public void StageMove_ThenWriteSource_Throws()
    {
        var from = Resolve("from.txt");
        var to = Resolve("to.txt");
        _tx.StageMove(from, to);
        Assert.Throws<InvalidOperationException>(() =>
            _tx.StageWrite(from, "new content"u8.ToArray()));
    }

    [Fact]
    public void StageMove_ThenWriteDestination_Throws()
    {
        var from = Resolve("src.txt");
        var to = Resolve("dst.txt");
        _tx.StageMove(from, to);
        Assert.Throws<InvalidOperationException>(() =>
            _tx.StageWrite(to, "data"u8.ToArray()));
    }

    [Fact]
    public async Task Overlay_StagedMove_ThenReadDir_ShowsDestination()
    {
        WriteHostFile("move_me.txt", "move test");
        var from = Resolve("move_me.txt");
        var to = Resolve("moved.txt");

        _tx.StageMove(from, to);

        // Root directory should show moved.txt but not move_me.txt
        var root = Resolve("");
        var entries = await _tx.Files.ReadDirectoryAsync(root);
        Assert.Contains(entries, e => e.Name == "moved.txt");
        Assert.DoesNotContain(entries, e => e.Name == "move_me.txt");
    }

    // ============================================================
    // Transaction lifecycle event tests (FH-0018)
    // ============================================================

    [Fact]
    public async Task TransactionEvent_LazyStart_EmitsStartedOnce()
    {
        var sink = new InMemoryEventSink();
        var tx = new WorkspaceTransaction(_host, sink);

        await tx.Files.WriteFileAsync(Resolve("a.txt"), "hello"u8.ToArray());

        var events = sink.GetAllEvents();
        Assert.Single(events.OfType<TransactionStartedEvent>());
        Assert.Single(events.OfType<TransactionStagedEvent>());

        await tx.DisposeAsync();
    }

    [Fact]
    public async Task TransactionEvent_PerOperation_EmitsStagedEvents()
    {
        var sink = new InMemoryEventSink();
        var tx = new WorkspaceTransaction(_host, sink);

        await tx.Files.WriteFileAsync(Resolve("a.txt"), "content a"u8.ToArray());
        await tx.Files.WriteFileAsync(Resolve("b.txt"), "content b"u8.ToArray());

        var events = sink.GetAllEvents();
        var staged = events.OfType<TransactionStagedEvent>().ToList();
        Assert.Equal(2, staged.Count);
        Assert.All(staged, s => Assert.Equal("write", s.Action));
        Assert.Contains(staged, s => s.Path.EndsWith("a.txt"));
        Assert.Contains(staged, s => s.Path.EndsWith("b.txt"));

        await tx.DisposeAsync();
    }

    [Fact]
    public async Task TransactionEvent_CommitSuccess_EmitsCommitted()
    {
        var sink = new InMemoryEventSink();
        var tx = new WorkspaceTransaction(_host, sink);

        await tx.Files.WriteFileAsync(Resolve("c.txt"), "data"u8.ToArray());
        await tx.CommitAsync();

        var events = sink.GetAllEvents();
        Assert.Contains(events, e => e is TransactionCommittedEvent);
        Assert.DoesNotContain(events, e => e is TransactionRolledBackEvent);

        await tx.DisposeAsync();
    }

    [Fact]
    public async Task TransactionEvent_Rollback_EmitsRolledBack()
    {
        var sink = new InMemoryEventSink();
        var tx = new WorkspaceTransaction(_host, sink);

        await tx.Files.WriteFileAsync(Resolve("d.txt"), "data"u8.ToArray());
        await tx.RollbackAsync();

        var events = sink.GetAllEvents();
        Assert.Contains(events, e => e is TransactionRolledBackEvent);
        Assert.DoesNotContain(events, e => e is TransactionCommittedEvent);

        await tx.DisposeAsync();
    }

    [Fact]
    public async Task TransactionEvent_DisposeWithoutCommit_EmitsRolledBack()
    {
        var sink = new InMemoryEventSink();
        var tx = new WorkspaceTransaction(_host, sink);

        await tx.Files.WriteFileAsync(Resolve("e.txt"), "data"u8.ToArray());
        await tx.DisposeAsync();

        var events = sink.GetAllEvents();
        Assert.Contains(events, e => e is TransactionRolledBackEvent);
    }

    // ============================================================
    // Commit failure recovery tests (FH-0023)
    // ============================================================

    /// <summary>
    /// Test double that extends HostWorkspaceFileSystem and throws IOException
    /// after a configurable number of <b>successful</b> write/move calls.
    /// Delete operations are never counted (they are used by rollback).
    /// </summary>
    private sealed class FailingHostFileSystem : HostWorkspaceFileSystem
    {
        private int _callCount;
        private readonly int _failAfter;
        private readonly object _lock = new();

        public FailingHostFileSystem(string root, int failAfter) : base(root)
        {
            _failAfter = failAfter;
        }

        private int NextCount()
        {
            lock (_lock) { return ++_callCount; }
        }

        public override async ValueTask WriteFileAsync(WorkspacePath path, ReadOnlyMemory<byte> content, CancellationToken ct = default)
        {
            if (NextCount() > _failAfter)
                throw new IOException($"Simulated write failure after {_failAfter} writes.");
            await base.WriteFileAsync(path, content, ct);
        }

        public override ValueTask DeleteAsync(WorkspacePath path, CancellationToken ct = default)
        {
            // Delete is used by rollback — never count it toward failures
            return base.DeleteAsync(path, ct);
        }

        public override ValueTask MoveAsync(WorkspacePath from, WorkspacePath to, CancellationToken ct = default)
        {
            // Move is used by rollback — only count it toward failures if we want
            // For simplicity, don't fail on move during commit either
            return base.MoveAsync(from, to, ct);
        }
    }

    [Fact]
    public async Task CommitFailure_MidCommit_RollsBackCompletedWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), $"omicron-tx-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var failingHost = new FailingHostFileSystem(root, failAfter: 1);
            var sink = new InMemoryEventSink();
            var tx = new WorkspaceTransaction(failingHost, sink);

            // Stage 3 writes; the 2nd host write will throw (failAfter: 1)
            await tx.Files.WriteFileAsync(new WorkspacePath("f1.txt"), "file1"u8.ToArray());
            await tx.Files.WriteFileAsync(new WorkspacePath("f2.txt"), "file2"u8.ToArray());
            await tx.Files.WriteFileAsync(new WorkspacePath("f3.txt"), "file3"u8.ToArray());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CommitAsync().AsTask());
            Assert.Contains("rolled back", ex.Message);

            // The 1st file should have been rolled back (deleted)
            Assert.False(File.Exists(Path.Combine(root, "f1.txt")), "f1.txt should have been rolled back");

            // The 2nd file never succeeded (threw on write)
            Assert.False(File.Exists(Path.Combine(root, "f2.txt")), "f2.txt should never have been written");

            // The 3rd file was never reached
            Assert.False(File.Exists(Path.Combine(root, "f3.txt")), "f3.txt was never reached");

            // TransactionRolledBackEvent should be emitted
            var events = sink.GetAllEvents();
            Assert.Contains(events, e => e is TransactionRolledBackEvent);
            Assert.DoesNotContain(events, e => e is TransactionCommittedEvent);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CommitFailure_Move_RollbackReversesMove()
    {
        var root = Path.Combine(Path.GetTempPath(), $"omicron-tx-move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // Write source file on host directly
            var srcPath = Path.Combine(root, "source.txt");
            File.WriteAllText(srcPath, "original");

            var failingHost = new FailingHostFileSystem(root, failAfter: 0);
            var sink = new InMemoryEventSink();
            var tx = new WorkspaceTransaction(failingHost, sink);

            // Stage a move + a write; the write will throw (failAfter: 0, first write fails)
            await tx.Files.MoveAsync(new WorkspacePath("source.txt"), new WorkspacePath("dest.txt"));
            await tx.Files.WriteFileAsync(new WorkspacePath("other.txt"), "data"u8.ToArray());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CommitAsync().AsTask());
            Assert.Contains("rolled back", ex.Message);

            // The move should be reversed: source should still exist, dest removed
            Assert.True(File.Exists(srcPath), "source.txt should have been restored");
            Assert.False(File.Exists(Path.Combine(root, "dest.txt")), "dest.txt should have been removed");
            Assert.Equal("original", File.ReadAllText(srcPath));

            var events = sink.GetAllEvents();
            Assert.Contains(events, e => e is TransactionRolledBackEvent);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
