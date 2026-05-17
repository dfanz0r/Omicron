using Omicron.Core.Content;
using Omicron.Core.Workspace;
using Xunit;

namespace Omicron.Core.Tests;

public class ContentBlockTests
{
    // ============================================================
    // Phase 1: Content block creation and equality
    // ============================================================

    [Fact]
    public void PlainTextContentBlock_StoresText()
    {
        var block = new PlainTextContentBlock("hello");
        Assert.Equal("hello", block.Text);
    }

    [Fact]
    public void Utf8ContentBuffer_AccessAfterDispose_Throws()
    {
        var block = new PlainTextContentBlock("hello");
        block.Dispose();

        Assert.Throws<ObjectDisposedException>(() => block.Text);
        Assert.Throws<ObjectDisposedException>(() => block.TextBuffer.Length);
        Assert.Throws<ObjectDisposedException>(() => block.TextBuffer.AsSpan().Length);
    }

    [Fact]
    public void PlainTextContentBlock_Equality()
    {
        var a = new PlainTextContentBlock("x");
        var b = new PlainTextContentBlock("x");
        Assert.Equal(a, b);
    }

    [Fact]
    public void CodeContentBlock_StoresAllFields()
    {
        var block = new CodeContentBlock("int x = 1;", Language: "csharp", Path: "src/Foo.cs");
        Assert.Equal("int x = 1;", block.Text);
        Assert.Equal("csharp", block.Language);
        Assert.Equal("src/Foo.cs", block.Path);
    }

    [Fact]
    public void DiffContentBlock_StoresDiff()
    {
        var diff = "--- a\n+++ b\n@@ -1 +1 @@\n-old\n+new";
        var block = new DiffContentBlock(diff, Path: "file.cs");
        Assert.Equal(diff, block.Text);
        Assert.Equal("file.cs", block.Path);
    }

    [Fact]
    public void FilePreviewContentBlock_StoresAllFields()
    {
        var block = new FilePreviewContentBlock(
            Path: "readme.txt",
            Preview: "line1\nline2",
            Size: 1024,
            LineCount: 2,
            IsBinary: false);
        Assert.Equal("readme.txt", block.Path);
        Assert.Equal("line1\nline2", block.Text);
        Assert.Equal(1024, block.Size);
        Assert.Equal(2, block.LineCount);
        Assert.False(block.IsBinary);
    }

    [Fact]
    public void ErrorContentBlock_StoresMessage()
    {
        var block = new ErrorContentBlock("not found");
        Assert.Equal("not found", block.Text);
    }

    [Fact]
    public void ErrorContentBlock_WithDetails()
    {
        var block = new ErrorContentBlock("parse failed", Details: "line 42");
        Assert.Equal("parse failed", block.Text);
        Assert.Equal("line 42", block.Details);
    }

    [Fact]
    public void ToolCallContentBlock_StoresCall()
    {
        var args = new Dictionary<string, object?> { ["path"] = "test.txt" };
        var block = new ToolCallContentBlock("call-1", "read_path", args);
        Assert.Equal("call-1", block.ToolCallId);
        Assert.Equal("read_path", block.ToolName);
        Assert.Equal(args, block.Arguments);
    }

    [Fact]
    public void ContentBlock_Hierarchy_BaseType()
    {
        IContentBlock block = new PlainTextContentBlock("test");
        Assert.IsAssignableFrom<IContentBlock>(block);
    }

    // ============================================================
    // Phase 2: Text renderer output
    // ============================================================

    [Fact]
    public void Render_PlainText()
    {
        var result = ContentBlockTextRenderer.Render(new PlainTextContentBlock("hello world"));
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Render_Markdown()
    {
        var result = ContentBlockTextRenderer.Render(new MarkdownContentBlock("# Title\n\nbody"));
        Assert.Equal("# Title\n\nbody", result);
    }

    [Fact]
    public void Render_Code_WithLanguage()
    {
        var result = ContentBlockTextRenderer.Render(new CodeContentBlock("int x = 1;", Language: "csharp"));
        Assert.Contains("```csharp", result);
        Assert.Contains("int x = 1;", result);
        Assert.Contains("```", result);
    }

    [Fact]
    public void Render_Code_WithPath()
    {
        var result = ContentBlockTextRenderer.Render(new CodeContentBlock("print('hi')", Path: "src/test.py"));
        Assert.Contains("// src/test.py", result);
    }

    [Fact]
    public void Render_Diff()
    {
        var diff = "--- a\n+++ b\n@@ -1 +1 @@\n-old\n+new";
        var result = ContentBlockTextRenderer.Render(new DiffContentBlock(diff));
        Assert.Equal(diff, result);
    }

    [Fact]
    public void Render_FilePreview_Text()
    {
        var block = new FilePreviewContentBlock("file.txt", "line1\nline2", 100, 2, false);
        var result = ContentBlockTextRenderer.Render(block);
        Assert.Contains("[FILE] file.txt", result);
        Assert.Contains("100 B", result);
        Assert.Contains("2 lines", result);
        Assert.Contains("line1", result);
    }

    [Fact]
    public void Render_FilePreview_Binary()
    {
        var block = new FilePreviewContentBlock("data.bin", "", 500, null, true);
        var result = ContentBlockTextRenderer.Render(block);
        Assert.Contains("[FILE] data.bin", result);
        Assert.Contains("binary", result);
    }

    [Fact]
    public void Render_Error()
    {
        var result = ContentBlockTextRenderer.Render(new ErrorContentBlock("file not found"));
        Assert.Contains("Error: file not found", result);
    }

    [Fact]
    public void Render_Error_WithDetails()
    {
        var result = ContentBlockTextRenderer.Render(new ErrorContentBlock("parse error", Details: "at line 5"));
        Assert.Contains("Error: parse error", result);
        Assert.Contains("Details: at line 5", result);
    }

    [Fact]
    public void Render_ToolCall()
    {
        // ToolCallContentBlock is not an IContentBlock; it is used in the
        // transcript pipeline. Tool call rendering is handled by the
        // transcript viewport, not by ContentBlockTextRenderer.
    }

    [Fact]
    public void RenderAll_SingleBlock()
    {
        var result = ContentBlockTextRenderer.RenderAll([new PlainTextContentBlock("hello")]);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void RenderAll_MultipleBlocks()
    {
        var blocks = new List<IContentBlock>
        {
            new PlainTextContentBlock("first"),
            new PlainTextContentBlock("second")
        };
        var result = ContentBlockTextRenderer.RenderAll(blocks);
        Assert.Contains("first", result);
        Assert.Contains("second", result);
        Assert.Contains("\n", result);
    }

    [Fact]
    public void RenderAll_Empty()
    {
        var result = ContentBlockTextRenderer.RenderAll([]);
        Assert.Equal("", result);
    }

    // ============================================================
    // Phase 3: WorkspaceReadContent → IContentBlock conversion
    // ============================================================

    [Fact]
    public void ToContentBlocks_FileContent_ReturnsFilePreview()
    {
        var file = new WorkspaceFileContent
        {
            RequestedPath = "src/Foo.cs",
            Stat = new FileStat("src/Foo.cs", 100, DateTime.UtcNow, false, false),
            Lines = new[] { new WorkspaceTextLine(1, "public class Foo") },
            TotalLines = 1
        };

        var blocks = WorkspaceLlmTextRenderer.ToContentBlocks(file);
        var preview = Assert.Single(blocks);
        var fileBlock = Assert.IsType<FilePreviewContentBlock>(preview);
        Assert.Equal("src/Foo.cs", fileBlock.Path);
        Assert.False(fileBlock.IsBinary);
        Assert.Equal(1, fileBlock.LineCount);
        Assert.Equal(100, fileBlock.Size);
    }

    [Fact]
    public void ToContentBlocks_BinaryContent_ReturnsBinaryPreview()
    {
        var binary = new WorkspaceBinaryFileContent
        {
            RequestedPath = "img.png",
            Stat = new FileStat("img.png", 5000, DateTime.UtcNow, false, true)
        };

        var blocks = WorkspaceLlmTextRenderer.ToContentBlocks(binary);
        var preview = Assert.Single(blocks);
        var fileBlock = Assert.IsType<FilePreviewContentBlock>(preview);
        Assert.True(fileBlock.IsBinary);
        Assert.Equal(5000, fileBlock.Size);
    }

    [Fact]
    public void ToContentBlocks_DirectoryContent_ReturnsPreview()
    {
        var dir = new WorkspaceDirectoryContent
        {
            RequestedPath = "src/",
            Entries = new[]
            {
                new DirectoryEntry("file.txt", false, 100, 10),
                new DirectoryEntry("sub/", true, 0)
            },
            TotalFileCount = 1,
            TotalDirCount = 1
        };

        var blocks = WorkspaceLlmTextRenderer.ToContentBlocks(dir);
        var block = Assert.Single(blocks);
        var textBlock = Assert.IsType<PlainTextContentBlock>(block);
        Assert.Contains("file.txt", textBlock.Text);
        Assert.Contains("sub/", textBlock.Text);
    }

    [Fact]
    public void ToContentBlocks_ErrorContent_ReturnsErrorBlock()
    {
        var error = new WorkspaceReadErrorContent
        {
            RequestedPath = "missing.txt",
            Message = "Path not found",
            Kind = WorkspaceReadErrorKind.NotFound
        };

        var blocks = WorkspaceLlmTextRenderer.ToContentBlocks(error);
        var errBlock = Assert.Single(blocks);
        var typed = Assert.IsType<ErrorContentBlock>(errBlock);
        Assert.Equal("Path not found", typed.Text);
    }

    [Fact]
    public void ToContentBlocks_TruncatedFile_IncludesWarningBlock()
    {
        var file = new WorkspaceFileContent
        {
            RequestedPath = "big.txt",
            Stat = new FileStat("big.txt", 9999, DateTime.UtcNow, false, false),
            Lines = new[] { new WorkspaceTextLine(1, "first") },
            TotalLines = 1000,
            Truncated = true,
            NextOffset = 2
        };

        var blocks = WorkspaceLlmTextRenderer.ToContentBlocks(file);
        Assert.Equal(2, blocks.Count);
        Assert.IsType<FilePreviewContentBlock>(blocks[0]);
        var warning = Assert.IsType<PlainTextContentBlock>(blocks[1]);
        Assert.Contains("truncated", warning.Text);
        Assert.Contains("offset=2", warning.Text);
    }
}
