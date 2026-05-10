using Omicron.Core.Workspace;
using Xunit;

namespace Omicron.Core.Tests;

public class WorkspaceReadServiceTests : IDisposable
{
    private readonly string _root;
    private readonly HostWorkspaceFileSystem _vfs;
    private readonly WorkspaceReadService _service;

    public WorkspaceReadServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"omicron-read-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _vfs = new HostWorkspaceFileSystem(_root);
        _service = new WorkspaceReadService(_vfs);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteFile(string path, string content)
    {
        var abs = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private void CreateDir(string path) => Directory.CreateDirectory(Path.Combine(_root, path));

    [Fact]
    public async Task ReadFile_ReturnsContent()
    {
        WriteFile("test.txt", "line1\nline2\nline3");
        var content = await _service.ReadAsync("test.txt");
        var file = Assert.IsType<WorkspaceFileContent>(content);
        Assert.Equal(3, file.Lines.Count);
        Assert.Equal(3, file.TotalLines);
        Assert.Equal("line1", file.Lines[0].Text);
        Assert.Equal(1, file.Lines[0].Number);
    }

    [Fact]
    public async Task ReadFile_WithOffsetAndLimit()
    {
        WriteFile("test.txt", "a\nb\nc\nd\ne");
        var content = await _service.ReadAsync("test.txt", new ReadOptions { Offset = 2, Limit = 2 });
        var file = Assert.IsType<WorkspaceFileContent>(content);
        Assert.Equal(2, file.Lines.Count);
        Assert.Equal(5, file.TotalLines);
        Assert.Equal(2, file.Lines[0].Number);
        Assert.Equal("b", file.Lines[0].Text);
        Assert.Equal(3, file.Lines[1].Number);
        Assert.Equal("c", file.Lines[1].Text);
    }

    [Fact]
    public async Task ReadFile_WithChunk()
    {
        // Write a file large enough to span multiple chunks (50 KB each)
        // Each line is ~80 bytes, so 2000 lines ≈ 160 KB → 3+ chunks
        var lines = new List<string>();
        for (int i = 0; i < 2000; i++)
            lines.Add($"line-{i:D4} " + new string('x', 70));
        WriteFile("big.txt", string.Join("\n", lines));

        // Chunk 0 should start at line 1
        var chunk0 = await _service.ReadAsync("big.txt", new ReadOptions { Chunk = 0, Limit = 3 });
        var f0 = Assert.IsType<WorkspaceFileContent>(chunk0);
        Assert.Equal(1, f0.Lines[0].Number);

        // Chunk 1 should start significantly later (not in the first 100 lines)
        var chunk1 = await _service.ReadAsync("big.txt", new ReadOptions { Chunk = 1, Limit = 3 });
        var f1 = Assert.IsType<WorkspaceFileContent>(chunk1);
        Assert.NotEmpty(f1.Lines);
        Assert.True(f1.Lines[0].Number > 100, $"Expected chunk 1 to start well after line 100, got line {f1.Lines[0].Number}");

        // Offset relative to chunk: offset=1 should give the same as just chunk
        var chunk1off1 = await _service.ReadAsync("big.txt", new ReadOptions { Chunk = 1, Offset = 1, Limit = 3 });
        var f1o1 = Assert.IsType<WorkspaceFileContent>(chunk1off1);
        Assert.NotEmpty(f1o1.Lines);
        Assert.Equal(f1.Lines[0].Number, f1o1.Lines[0].Number);

        // Offset=2 relative to chunk should start one line later
        var chunk1off2 = await _service.ReadAsync("big.txt", new ReadOptions { Chunk = 1, Offset = 2, Limit = 3 });
        var f1o2 = Assert.IsType<WorkspaceFileContent>(chunk1off2);
        Assert.NotEmpty(f1o2.Lines);
        Assert.Equal(f1.Lines[0].Number + 1, f1o2.Lines[0].Number);

        // Chunk far past EOF returns empty
        var chunkFar = await _service.ReadAsync("big.txt", new ReadOptions { Chunk = 99999 });
        var fFar = Assert.IsType<WorkspaceFileContent>(chunkFar);
        Assert.Empty(fFar.Lines);
    }

    [Fact]
    public async Task ReadDirectory_TruncatesAtMax()
    {
        for (int i = 0; i < 250; i++)
            WriteFile($"file{i}.txt", "content");
        var content = await _service.ReadAsync("");
        var dir = Assert.IsType<WorkspaceDirectoryContent>(content);
        Assert.True(dir.Truncated);
        Assert.True(dir.Entries.Count <= 200);
        Assert.Equal(250, dir.TotalFileCount);
        Assert.Equal(0, dir.TotalDirCount);
    }

    [Fact]
    public async Task ReadDirectory_UnderLimit_NotTruncated()
    {
        WriteFile("a.txt", "a");
        WriteFile("b.txt", "b");
        var content = await _service.ReadAsync("");
        var dir = Assert.IsType<WorkspaceDirectoryContent>(content);
        Assert.False(dir.Truncated);
        Assert.Equal(2, dir.Entries.Count);
        Assert.Equal(2, dir.TotalFileCount);
        Assert.Equal(0, dir.TotalDirCount);
    }

    [Fact]
    public async Task ReadBinary_ReturnsBinaryContent()
    {
        WriteFile("image.png", "fake-png");
        var content = await _service.ReadAsync("image.png");
        Assert.IsType<WorkspaceBinaryFileContent>(content);
    }

    [Fact]
    public async Task ReadNotFound_ReturnsError()
    {
        var content = await _service.ReadAsync("nonexistent.txt");
        var err = Assert.IsType<WorkspaceReadErrorContent>(content);
        Assert.Equal(WorkspaceReadErrorKind.NotFound, err.Kind);
    }

    [Fact]
    public async Task ReadEscapesRoot_ReturnsError()
    {
        var content = await _service.ReadAsync("../../outside.txt");
        var err = Assert.IsType<WorkspaceReadErrorContent>(content);
        Assert.Equal(WorkspaceReadErrorKind.EscapesRoot, err.Kind);
    }

    [Fact]
    public async Task Renderer_FileOutput_IncludesHeaders()
    {
        WriteFile("test.txt", "hello\nworld");
        var content = await _service.ReadAsync("test.txt");
        var result = new WorkspaceLlmTextRenderer().Render(content);
        Assert.Contains("[FILE] test.txt", result.Content);
        Assert.Contains("Lines: 2", result.Content);
        Assert.Contains("     1| hello", result.Content);
        Assert.Contains("     2| world", result.Content);
    }

    [Fact]
    public async Task Renderer_FileWithOffset_ShowsCorrectRange()
    {
        WriteFile("test.txt", "a\nb\nc\nd\ne");
        var content = await _service.ReadAsync("test.txt", new ReadOptions { Offset = 3, Limit = 2 });
        var result = new WorkspaceLlmTextRenderer().Render(content);
        Assert.Contains("Showing: lines 3-4 of 5", result.Content);
        Assert.Contains("     3| c", result.Content);
        Assert.Contains("     4| d", result.Content);
    }

    [Fact]
    public async Task Renderer_DirectoryOutput_IncludesHeaders()
    {
        WriteFile("a.txt", "content");
        CreateDir("sub");
        var content = await _service.ReadAsync("");
        var result = new WorkspaceLlmTextRenderer().Render(content);
        Assert.Contains("[DIR]", result.Content);
        Assert.Contains("a.txt", result.Content);
        Assert.Contains("sub/", result.Content);
        Assert.Contains("1 files, 1 dirs", result.Content);
    }

    [Fact]
    public async Task Renderer_BinaryOutput()
    {
        WriteFile("img.png", "fake");
        var content = await _service.ReadAsync("img.png");
        var result = new WorkspaceLlmTextRenderer().Render(content);
        Assert.Contains("[FILE] img.png", result.Content);
        Assert.Contains("binary", result.Content);
    }

    [Fact]
    public async Task Renderer_NotFoundOutput()
    {
        var content = await _service.ReadAsync("missing.txt");
        var result = new WorkspaceLlmTextRenderer().Render(content);
        Assert.Contains("Error:", result.Content);
        Assert.Contains("not found", result.Content);
    }

    [Fact]
    public async Task Renderer_EscapesRootOutput()
    {
        var content = await _service.ReadAsync("../../outside.txt");
        var result = new WorkspaceLlmTextRenderer().Render(content);
        Assert.Contains("Error:", result.Content);
        Assert.Contains("escapes workspace root", result.Content);
    }
}
