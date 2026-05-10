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
}
