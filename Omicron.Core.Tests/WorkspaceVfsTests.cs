using Omicron.Core;
using Omicron.Core.Workspace;
using Xunit;

namespace Omicron.Core.Tests;

public class HostWorkspaceFileSystemTests : IDisposable
{
    private readonly string _rootDir;
    private readonly HostWorkspaceFileSystem _vfs;

    public HostWorkspaceFileSystemTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), $"omicron-vfs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDir);
        _vfs = new HostWorkspaceFileSystem(_rootDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    private WorkspacePath Resolve(string path)
    {
        var resolved = _vfs.Resolve(path);
        Assert.NotNull(resolved);
        return resolved!.Value;
    }

    private string WriteTestFile(string relativePath, string content)
    {
        var abs = Path.Combine(_rootDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
        return relativePath;
    }

    private string CreateTestDir(string relativePath)
    {
        var abs = Path.Combine(_rootDir, relativePath);
        Directory.CreateDirectory(abs);
        return relativePath;
    }

    [Fact]
    public async Task Stat_ExistingFile_ReturnsMetadata()
    {
        WriteTestFile("test.txt", "hello world");
        var path = Resolve("test.txt");

        var stat = await _vfs.StatAsync(path);

        Assert.NotNull(stat);
        Assert.Equal("test.txt", stat!.Path);
        Assert.Equal(11, stat.Size);
        Assert.False(stat.IsDirectory);
        Assert.False(stat.IsBinary);
    }

    [Fact]
    public async Task Stat_MissingFile_ReturnsNull()
    {
        var path = Resolve("nonexistent.txt");
        var stat = await _vfs.StatAsync(path);
        Assert.Null(stat);
    }

    [Fact]
    public async Task Stat_Directory_ReturnsDirectory()
    {
        CreateTestDir("subdir");
        var path = Resolve("subdir");
        var stat = await _vfs.StatAsync(path);
        Assert.NotNull(stat);
        Assert.True(stat!.IsDirectory);
    }

    [Fact]
    public async Task Stat_BinaryFile_DetectsBinary()
    {
        WriteTestFile("image.png", "fake-png-content");
        var path = Resolve("image.png");
        var stat = await _vfs.StatAsync(path);
        Assert.NotNull(stat);
        Assert.True(stat!.IsBinary);
    }

    [Fact]
    public async Task ReadFile_ExistingFile_ReturnsContent()
    {
        WriteTestFile("data.txt", "Hello, VFS!");
        var path = Resolve("data.txt");
        var content = await _vfs.ReadFileAsync(path);
        Assert.Equal("Hello, VFS!"u8.ToArray(), content.ToArray());
    }

    [Fact]
    public async Task ReadFile_MissingFile_ReturnsEmpty()
    {
        var path = Resolve("missing.txt");
        var content = await _vfs.ReadFileAsync(path);
        Assert.True(content.IsEmpty);
    }

    [Fact]
    public async Task ReadDirectory_ExistingDir_ReturnsEntries()
    {
        WriteTestFile("a.txt", "aaa");
        WriteTestFile("b.txt", "bbb");
        CreateTestDir("sub");

        var path = Resolve("");
        var entries = await _vfs.ReadDirectoryAsync(path);

        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.Name == "a.txt" && !e.IsDirectory);
        Assert.Contains(entries, e => e.Name == "b.txt" && !e.IsDirectory);
        Assert.Contains(entries, e => e.Name == "sub/" && e.IsDirectory);
    }

    [Fact]
    public async Task ReadDirectory_NonexistentPath_ReturnsEmpty()
    {
        var path = Resolve("nonexistent");
        var entries = await _vfs.ReadDirectoryAsync(path);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task WriteFile_CreatesFile()
    {
        var path = Resolve("newfile.txt");
        var content = "written content"u8.ToArray();
        await _vfs.WriteFileAsync(path, content);

        var abs = Path.Combine(_rootDir, "newfile.txt");
        Assert.True(File.Exists(abs));
        Assert.Equal("written content", File.ReadAllText(abs));
    }

    [Fact]
    public async Task WriteFile_CreatesParentDirectories()
    {
        var path = Resolve("a/b/c/deep.txt");
        var content = "deep"u8.ToArray();
        await _vfs.WriteFileAsync(path, content);

        var abs = Path.Combine(_rootDir, "a/b/c/deep.txt");
        Assert.True(File.Exists(abs));
    }

    [Fact]
    public async Task Delete_File_RemovesIt()
    {
        WriteTestFile("todelete.txt", "delete me");
        var path = Resolve("todelete.txt");
        await _vfs.DeleteAsync(path);
        Assert.False(File.Exists(Path.Combine(_rootDir, "todelete.txt")));
    }

    [Fact]
    public async Task Delete_EmptyDirectory_RemovesIt()
    {
        CreateTestDir("emptydir");
        var path = Resolve("emptydir");
        await _vfs.DeleteAsync(path);
        Assert.False(Directory.Exists(Path.Combine(_rootDir, "emptydir")));
    }

    [Fact]
    public async Task Delete_NonEmptyDirectory_Throws()
    {
        WriteTestFile("nonempty/file.txt", "content");
        var path = Resolve("nonempty");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vfs.DeleteAsync(path).AsTask());
    }

    [Fact]
    public async Task Move_File_Renames()
    {
        WriteTestFile("source.txt", "move me");
        var from = Resolve("source.txt");
        var to = Resolve("dest.txt");
        await _vfs.MoveAsync(from, to);

        Assert.False(File.Exists(Path.Combine(_rootDir, "source.txt")));
        Assert.True(File.Exists(Path.Combine(_rootDir, "dest.txt")));
        Assert.Equal("move me", File.ReadAllText(Path.Combine(_rootDir, "dest.txt")));
    }

    [Fact]
    public async Task Move_CreatesParentDirectories()
    {
        WriteTestFile("source.txt", "move to subdir");
        var from = Resolve("source.txt");
        var to = Resolve("sub/dest.txt");
        await _vfs.MoveAsync(from, to);
        Assert.True(File.Exists(Path.Combine(_rootDir, "sub/dest.txt")));
    }

    [Fact]
    public void Resolve_TraversalUp_ReturnsNull()
    {
        Assert.Null(_vfs.Resolve("../outside.txt"));
    }

    [Fact]
    public void Resolve_TraversalComplex_ReturnsNull()
    {
        Assert.Null(_vfs.Resolve("sub/../../outside.txt"));
    }

    [Fact]
    public void Resolve_NormalPath_ReturnsWorkspacePath()
    {
        WriteTestFile("normal.txt", "content");
        var result = _vfs.Resolve("normal.txt");
        Assert.NotNull(result);
        Assert.Equal("normal.txt", result!.Value.Value);
    }

    [Fact]
    public void Resolve_RootDirectory_ReturnsEmptyPath()
    {
        var result = _vfs.Resolve("");
        Assert.NotNull(result);
        Assert.Equal("", result!.Value.Value);
    }

    [Fact]
    public void Resolve_Whitespace_Resolves()
    {
        // Whitespace path segments are valid (file system may allow them)
        var result = _vfs.Resolve("   ");
        // Path.GetFullPath normalizes the result, so we just verify it resolves
        Assert.NotNull(result);
    }

    [Fact]
    public void Resolve_Null_ReturnsNull()
    {
        Assert.Null(_vfs.Resolve(null!));
    }

    [Fact]
    public async Task ManuallyConstructedPath_WithTraversal_Throws()
    {
        var badPath = new WorkspacePath("../../outside.txt");

        var statEx = await Assert.ThrowsAsync<InvalidOperationException>(() => _vfs.StatAsync(badPath).AsTask());
        Assert.Contains("escapes", statEx.Message);

        Assert.Throws<InvalidOperationException>(() => { _vfs.DeleteAsync(badPath).GetAwaiter().GetResult(); });

        var readEx = await Assert.ThrowsAsync<InvalidOperationException>(() => _vfs.ReadFileAsync(badPath).AsTask());
        Assert.Contains("escapes", readEx.Message);
    }

    [Fact]
    public async Task ReadDirectory_Traversal_Throws()
    {
        var badPath = new WorkspacePath("../../outside");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _vfs.ReadDirectoryAsync(badPath).AsTask());
        Assert.Contains("escapes", ex.Message);
    }

    [Fact]
    public async Task Move_TraversalDestination_Throws()
    {
        WriteTestFile("safe.txt", "content");
        var from = Resolve("safe.txt");
        var badTo = new WorkspacePath("../../outside.txt");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _vfs.MoveAsync(from, badTo).AsTask());
        Assert.Contains("escapes", ex.Message);
    }

    [Fact]
    public async Task Move_TraversalSource_Throws()
    {
        var badFrom = new WorkspacePath("../../outside.txt");
        var to = Resolve("dest.txt");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _vfs.MoveAsync(badFrom, to).AsTask());
        Assert.Contains("escapes", ex.Message);
    }

    [Fact]
    public async Task Write_Traversal_Throws()
    {
        var badPath = new WorkspacePath("../../outside.txt");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _vfs.WriteFileAsync(badPath, "data"u8.ToArray()).AsTask());
        Assert.Contains("escapes", ex.Message);
    }

    [Fact]
    public async Task OmicronHost_WorkspaceIsVfsBacked_AndReadsFile()
    {
        // Prove OmicronHost.Workspace is VFS-backed and preserves formatted output
        var host = new OmicronHost(Environment.CurrentDirectory);

        // Verify Workspace uses the VFS-backed adapter
        Assert.IsType<VfsWorkspaceAdapter>(host.Workspace);

        // Write a test file via VFS
        var testPath = "_vfs_integration_test.txt";
        var content = "line1\nline2\nline3"u8.ToArray();
        await host.FileSystem.WriteFileAsync(new WorkspacePath(testPath), content);

        try
        {
            // Read via Workspace (which goes through VfsWorkspaceAdapter -> VFS)
            var result = await host.Workspace.ReadPathAsync(testPath);

            Assert.False(result.IsDirectory);
            Assert.False(result.IsBinary);
            Assert.Contains("[FILE] " + testPath, result.Content);
            Assert.Contains("line1", result.Content);
            Assert.Contains("line2", result.Content);
            Assert.Contains("line3", result.Content);
            Assert.Contains("Lines: 3", result.Content);
        }
        finally
        {
            try { File.Delete(Path.Combine(host.FileSystem.RootPath, testPath)); } catch { }
        }
    }
}
