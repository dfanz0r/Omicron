using System.Text.Json;
using Omicron.Core.Commands;
using Omicron.Core.Events;
using Omicron.Core.Extensions;
using Omicron.Core.Tools;
using Omicron.Core.Workspace;
using Xunit;

namespace Omicron.Core.Tests;

public class TransactionEditHarnessTests : IDisposable
{
    private readonly string _root;
    private readonly HostWorkspaceFileSystem _host;
    private readonly IWorkspaceTransactionManager _txManager;
    private readonly VfsWorkspaceAdapter _workspace;
    private readonly BuiltinWorkspaceToolsExtension _extension;
    private readonly ToolRegistry _registry;

    public TransactionEditHarnessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"omicron-edit-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _host = new HostWorkspaceFileSystem(_root);
        _txManager = new WorkspaceTransactionManager(_host);
        _workspace = new VfsWorkspaceAdapter(_host);
        _extension = new BuiltinWorkspaceToolsExtension(_workspace, _host, _txManager);
        _registry = new ToolRegistry();

        // Register extension tools into our test registry
        _extension.Register(new CapturingExtensionContext(_registry));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Extension context that captures registered tools into a ToolRegistry.
    /// </summary>
    private sealed class CapturingExtensionContext : IExtensionContext
    {
        private readonly IToolRegistry _registry;

        public CapturingExtensionContext(IToolRegistry registry)
        {
            _registry = registry;
        }

        public void RegisterTool(ToolDefinition tool) => _registry.Register(tool);
        public void RegisterCommand(CommandDefinition command) { }
    }

    // ============================================================
    // Helpers
    // ============================================================

    private void WriteFile(string path, string content)
    {
        var abs = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private async Task<ToolResult> InvokeReadHashlines(string path)
    {
        var tool = _registry.GetTool("read_file_hashlines");
        Assert.NotNull(tool);

        var ctx = new ToolInvocationContext(
            new ToolCallId("test"),
            new Dictionary<string, object?> { ["path"] = path },
            new SessionId(Guid.NewGuid()),
            new AgentId(Guid.NewGuid()),
            CancellationToken.None);

        return await tool!.InvokeAsync(ctx);
    }

    private async Task<ToolResult> InvokeEditHashline(
        string path,
        List<Dictionary<string, object?>> edits)
    {
        var tool = _registry.GetTool("edit_file_hashline");
        Assert.NotNull(tool);

        // Serialize edits to a JsonElement array to mimic the real LLM tool call path
        var editsJson = JsonSerializer.Serialize(edits);
        using var editsDoc = JsonDocument.Parse(editsJson);
        var editsElement = editsDoc.RootElement.Clone();

        var args = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["edits"] = editsElement
        };

        var ctx = new ToolInvocationContext(
            new ToolCallId("test"),
            args,
            new SessionId(Guid.NewGuid()),
            new AgentId(Guid.NewGuid()),
            CancellationToken.None);

        return await tool!.InvokeAsync(ctx);
    }

    /// <summary>Read a file from the host (not through tool).</summary>
    private string ReadHostFile(string path) =>
        File.ReadAllText(Path.Combine(_root, path));

    // ============================================================
    // LineHash tests
    // ============================================================

    [Fact]
    public void LineHash_Deterministic()
    {
        var h1 = LineHash.ComputeAnchor("hello world");
        var h2 = LineHash.ComputeAnchor("hello world");
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void LineHash_DifferentContent_DifferentAnchors()
    {
        var h1 = LineHash.ComputeAnchor("abc");
        var h2 = LineHash.ComputeAnchor("xyz");
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void LineHash_TwoLettersOnly()
    {
        for (int i = 0; i < 1000; i++)
        {
            var anchor = LineHash.ComputeAnchor($"test line {i}");
            Assert.Matches("^[a-z]{2}$", anchor);
        }
    }

    [Fact]
    public void LineHash_FormatLine()
    {
        var formatted = LineHash.FormatLine(42, "ab", "return x;");
        Assert.Equal("42ab|return x;", formatted);
    }

    // ============================================================
    // read_file_hashlines tests
    // ============================================================

    [Fact]
    public async Task ReadHashlines_ReturnsLinesWithAnchors()
    {
        WriteFile("test.txt", "line1\nline2\nline3");
        var result = await InvokeReadHashlines("test.txt");

        Assert.False(result.IsError);
        Assert.Contains("1", result.GetText());
        Assert.Contains("2", result.GetText());
        Assert.Contains("3", result.GetText());
        Assert.Contains("|", result.GetText()); // anchor separator
    }

    [Fact]
    public async Task ReadHashlines_DoesNotRenderExtraAnchorForTrailingNewline()
    {
        WriteFile("test.txt", "line1\nline2\n");
        var result = await InvokeReadHashlines("test.txt");

        Assert.False(result.IsError);
        var anchoredLines = result.GetText().Split('\n')
            .Where(l => l.Contains('|') && char.IsDigit(l[0]))
            .ToArray();
        Assert.Equal(2, anchoredLines.Length);
        Assert.StartsWith("1", anchoredLines[0]);
        Assert.StartsWith("2", anchoredLines[1]);
    }

    [Fact]
    public async Task ReadHashlines_RejectsBinary()
    {
        // Write content that fails content-based text detection (null bytes).
        var bytes = new byte[] { 0x50, 0x4E, 0x47, 0x00, 0x0D, 0x0A };
        File.WriteAllBytes(Path.Combine(_root, "image.png"), bytes);
        var result = await InvokeReadHashlines("image.png");
        Assert.True(result.IsError);
        Assert.Contains("binary", result.GetText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadHashlines_PathNotFound()
    {
        var result = await InvokeReadHashlines("nonexistent.txt");
        Assert.True(result.IsError);
        Assert.Contains("not found", result.GetText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadHashlines_EscapesRoot()
    {
        var result = await InvokeReadHashlines("../../outside.txt");
        Assert.True(result.IsError);
        Assert.Contains("escapes", result.GetText(), StringComparison.OrdinalIgnoreCase);
    }

    // ============================================================
    // edit_file_hashline tests
    // ============================================================

    [Fact]
    public async Task EditHashline_SingleEdit_CommitsSuccessfully()
    {
        WriteFile("edit.txt", "line1\nline2\nline3");

        // Read to get hashes
        var readResult = await InvokeReadHashlines("edit.txt");
        Assert.False(readResult.IsError);

        // Extract hash for line 2 from the output
        // Lines are formatted as: {line}{anchor}|{content}
        var lines = readResult.GetText().Split('\n');
        var line2Entry = lines.First(l => l.StartsWith("2") && l.Contains("|"));
        var hash2 = line2Entry.Substring(1, 2); // after "2", the anchor is 2 chars

        // Edit line 2
        var edits = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["start_line"] = 2,
                ["start_hash"] = hash2,
                ["old_text"] = "line2",
                ["new_text"] = "modified"
            }
        };

        var editResult = await InvokeEditHashline("edit.txt", edits);
        Assert.False(editResult.IsError, $"Edit failed: {editResult.GetText()}");

        // Verify host file was updated
        var content = ReadHostFile("edit.txt");
        Assert.Contains("modified", content);
        Assert.DoesNotContain("line2\nline3", content); // old content gone
        Assert.Equal("line1\nmodified\nline3", content);
    }

    [Fact]
    public async Task EditHashline_MultipleNonOverlappingEdits()
    {
        WriteFile("multi.txt", "a\nb\nc\nd\ne");

        var readResult = await InvokeReadHashlines("multi.txt");
        var lines = readResult.GetText().Split('\n');

        // Extract hashes for lines 1, 3, 5
        var hash1 = lines.First(l => l.StartsWith("1") && l.Contains("|")).Substring(1, 2);
        var hash3 = lines.First(l => l.StartsWith("3") && l.Contains("|")).Substring(1, 2);
        var hash5 = lines.First(l => l.StartsWith("5") && l.Contains("|")).Substring(1, 2);

        var edits = new List<Dictionary<string, object?>>
        {
            new() { ["start_line"] = 1, ["start_hash"] = hash1, ["old_text"] = "a", ["new_text"] = "A" },
            new() { ["start_line"] = 3, ["start_hash"] = hash3, ["old_text"] = "c", ["new_text"] = "C" },
            new() { ["start_line"] = 5, ["start_hash"] = hash5, ["old_text"] = "e", ["new_text"] = "E" }
        };

        var editResult = await InvokeEditHashline("multi.txt", edits);
        Assert.False(editResult.IsError, $"Edit failed: {editResult.GetText()}");

        var content = ReadHostFile("multi.txt");
        Assert.Equal("A\nb\nC\nd\nE", content);
    }

    [Fact]
    public async Task EditHashline_StaleHash_Rejected()
    {
        WriteFile("stale.txt", "original line");

        // Read to get valid hashes
        var readResult = await InvokeReadHashlines("stale.txt");
        var lines = readResult.GetText().Split('\n');
        var line1Entry = lines.First(l => l.StartsWith("1") && l.Contains("|"));
        var validHash = line1Entry.Substring(1, 2);

        // Modify the file behind the tool's back
        WriteFile("stale.txt", "changed content");

        // Now try to edit with the stale hash
        var edits = new List<Dictionary<string, object?>>
        {
            new() { ["start_line"] = 1, ["start_hash"] = validHash, ["old_text"] = "changed content", ["new_text"] = "new" }
        };

        var editResult = await InvokeEditHashline("stale.txt", edits);
        Assert.True(editResult.IsError);
        Assert.Contains("mismatch", editResult.GetText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EditHashline_OldTextMismatch_Rejected()
    {
        WriteFile("mismatch.txt", "the quick brown fox");

        var readResult = await InvokeReadHashlines("mismatch.txt");
        var lines = readResult.GetText().Split('\n');
        var line1Entry = lines.First(l => l.StartsWith("1") && l.Contains("|"));
        var hash = line1Entry.Substring(1, 2);

        // Provide wrong old_text
        var edits = new List<Dictionary<string, object?>>
        {
            new() { ["start_line"] = 1, ["start_hash"] = hash, ["old_text"] = "wrong text", ["new_text"] = "new" }
        };

        var editResult = await InvokeEditHashline("mismatch.txt", edits);
        Assert.True(editResult.IsError);
        Assert.Contains("old_text mismatch", editResult.GetText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EditHashline_OverlappingEdits_Rejected()
    {
        WriteFile("overlap.txt", "line1\nline2\nline3\nline4");

        var readResult = await InvokeReadHashlines("overlap.txt");
        var lines = readResult.GetText().Split('\n');
        var hash1 = lines.First(l => l.StartsWith("1") && l.Contains("|")).Substring(1, 2);
        var hash2 = lines.First(l => l.StartsWith("2") && l.Contains("|")).Substring(1, 2);

        // Two edits that overlap (line 2 is inside the first edit's span)
        var edits = new List<Dictionary<string, object?>>
        {
            new() { ["start_line"] = 1, ["start_hash"] = hash1, ["old_text"] = "line1\nline2\nline3", ["new_text"] = "replaced" },
            new() { ["start_line"] = 2, ["start_hash"] = hash2, ["old_text"] = "line2", ["new_text"] = "modified" }
        };

        var editResult = await InvokeEditHashline("overlap.txt", edits);
        Assert.True(editResult.IsError);
        Assert.Contains("overlap", editResult.GetText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EditHashline_BinaryFile_Rejected()
    {
        // Write content that fails content-based text detection (null bytes).
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 };
        File.WriteAllBytes(Path.Combine(_root, "data.bin"), bytes);
        var edits = new List<Dictionary<string, object?>>
        {
            new() { ["start_line"] = 1, ["start_hash"] = "aa", ["old_text"] = "not-really-binary", ["new_text"] = "changed" }
        };

        var result = await InvokeEditHashline("data.bin", edits);
        Assert.True(result.IsError);
        Assert.Contains("binary", result.GetText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EditHashline_ValidationFailure_LeavesHostUnchanged()
    {
        WriteFile("safe.txt", "original content");

        var readResult = await InvokeReadHashlines("safe.txt");
        var lines = readResult.GetText().Split('\n');
        var line1Entry = lines.First(l => l.StartsWith("1") && l.Contains("|"));
        var hash = line1Entry.Substring(1, 2);

        // Provide a mismatched old_text that will fail validation
        var edits = new List<Dictionary<string, object?>>
        {
            new() { ["start_line"] = 1, ["start_hash"] = hash, ["old_text"] = "wrong", ["new_text"] = "new content" }
        };

        var editResult = await InvokeEditHashline("safe.txt", edits);
        Assert.True(editResult.IsError);

        // Verify host file is untouched
        Assert.Equal("original content", ReadHostFile("safe.txt"));
    }

    [Fact]
    public async Task EditHashline_Success_ReturnsUnifiedDiff()
    {
        WriteFile("diff_test.txt", "before\nmiddle\nafter");

        var readResult = await InvokeReadHashlines("diff_test.txt");
        var lines = readResult.GetText().Split('\n');
        var hash2 = lines.First(l => l.StartsWith("2") && l.Contains("|")).Substring(1, 2);

        var edits = new List<Dictionary<string, object?>>
        {
            new() { ["start_line"] = 2, ["start_hash"] = hash2, ["old_text"] = "middle", ["new_text"] = "replaced" }
        };

        var editResult = await InvokeEditHashline("diff_test.txt", edits);
        Assert.False(editResult.IsError);

        // Should contain unified diff markers and no error
        Assert.DoesNotContain("Error", editResult.GetText());
        Assert.Contains("---", editResult.GetText());
        Assert.Contains("+++", editResult.GetText());
        Assert.Contains("@@", editResult.GetText());
        Assert.Contains("-middle", editResult.GetText());
        Assert.Contains("+replaced", editResult.GetText());
        Assert.Contains("read_file_hashlines", editResult.GetText());
    }

    [Fact]
    public async Task EditHashline_PathNotFound_ReturnsError()
    {
        var edits = new List<Dictionary<string, object?>>
        {
            new() { ["start_line"] = 1, ["start_hash"] = "aa", ["old_text"] = "x", ["new_text"] = "y" }
        };

        var result = await InvokeEditHashline("nonexistent.txt", edits);
        Assert.True(result.IsError);
        Assert.Contains("not found", result.GetText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EditHashline_EscapesRoot_ReturnsError()
    {
        var edits = new List<Dictionary<string, object?>>
        {
            new() { ["start_line"] = 1, ["start_hash"] = "aa", ["old_text"] = "x", ["new_text"] = "y" }
        };

        var result = await InvokeEditHashline("../../outside.txt", edits);
        Assert.True(result.IsError);
        Assert.Contains("escapes", result.GetText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EditHashline_MultiLineEdit()
    {
        WriteFile("multiline.txt", "keep\nline1\nline2\nline3\nkeep2");

        var readResult = await InvokeReadHashlines("multiline.txt");
        var lines = readResult.GetText().Split('\n');
        var hash2 = lines.First(l => l.StartsWith("2") && l.Contains("|")).Substring(1, 2);

        // Replace 3 lines starting at line 2
        var edits = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["start_line"] = 2,
                ["start_hash"] = hash2,
                ["old_text"] = "line1\nline2\nline3",
                ["new_text"] = "replacement1\nreplacement2"
            }
        };

        var editResult = await InvokeEditHashline("multiline.txt", edits);
        Assert.False(editResult.IsError, $"Edit failed: {editResult.GetText()}");

        var content = ReadHostFile("multiline.txt");
        Assert.Equal("keep\nreplacement1\nreplacement2\nkeep2", content);
    }

    [Fact]
    public async Task EditHashline_MultiEdit_BottomUpRespectsShiftedLines()
    {
        // Two edits in one batch: first expands (shifts lines below),
        // second targets a line that would be shifted if applied top-down.
        // Bottom-up application ensures the later edit (line 5) is applied first
        // while line numbers are still valid, then the earlier edit (line 1).
        WriteFile("shift.txt", "a\nb\nc\nd\ne");

        var readResult = await InvokeReadHashlines("shift.txt");
        var lines = readResult.GetText().Split('\n');
        var hashA = lines.First(l => l.StartsWith("1") && l.Contains("|")).Substring(1, 2);
        var hashE = lines.First(l => l.StartsWith("5") && l.Contains("|")).Substring(1, 2);

        var edits = new List<Dictionary<string, object?>>
        {
            new() { ["start_line"] = 1, ["start_hash"] = hashA, ["old_text"] = "a", ["new_text"] = "X\nY\nZ" },
            new() { ["start_line"] = 5, ["start_hash"] = hashE, ["old_text"] = "e", ["new_text"] = "E" }
        };

        var editResult = await InvokeEditHashline("shift.txt", edits);
        Assert.False(editResult.IsError, $"Edit failed: {editResult.GetText()}");

        // Bottom-up: edit 5 ("e" → "E") applied first, then edit 1 ("a" → "X\nY\nZ")
        var content = ReadHostFile("shift.txt");
        Assert.Equal("X\nY\nZ\nb\nc\nd\nE", content);
    }

    [Fact]
    public async Task EditHashline_MultiEdit_TwoEditsSameRegionDontInterfere()
    {
        // Two edits in the same batch, non-overlapping, where the first edit
        // replaces a block and the second edits a different part of the file.
        WriteFile("two_edits.txt", "start\nmiddle\nend");

        var readResult = await InvokeReadHashlines("two_edits.txt");
        var lines = readResult.GetText().Split('\n');
        var hashStart = lines.First(l => l.StartsWith("1") && l.Contains("|")).Substring(1, 2);
        var hashEnd = lines.First(l => l.StartsWith("3") && l.Contains("|")).Substring(1, 2);

        var edits = new List<Dictionary<string, object?>>
        {
            new() { ["start_line"] = 1, ["start_hash"] = hashStart, ["old_text"] = "start", ["new_text"] = "begin" },
            new() { ["start_line"] = 3, ["start_hash"] = hashEnd, ["old_text"] = "end", ["new_text"] = "finish" }
        };

        var editResult = await InvokeEditHashline("two_edits.txt", edits);
        Assert.False(editResult.IsError, $"Edit failed: {editResult.GetText()}");

        var content = ReadHostFile("two_edits.txt");
        Assert.Equal("begin\nmiddle\nfinish", content);
    }

    [Fact]
    public async Task EditHashline_Rebase_WithinPlusMinus5()
    {
        WriteFile("rebase.txt", "a\nb\nc\nd\ne");

        var readResult = await InvokeReadHashlines("rebase.txt");
        var lines = readResult.GetText().Split('\n');
        var hashE = lines.First(l => l.StartsWith("5") && l.Contains("|")).Substring(1, 2);

        // Add 2 lines before, shifting line 5 to line 7
        // But we haven't edited yet, so let's just test rebase by
        // providing a wrong line number but correct hash that exists ±5

        // hashE belongs to what is now line 5, but we provide line 4 or 6
        var edits = new List<Dictionary<string, object?>>
        {
            new() { ["start_line"] = 6, ["start_hash"] = hashE, ["old_text"] = "e", ["new_text"] = "E" }
        };

        var editResult = await InvokeEditHashline("rebase.txt", edits);
        Assert.False(editResult.IsError, $"Rebase edit failed: {editResult.GetText()}");

        // Verify the correct line was edited
        var content = ReadHostFile("rebase.txt");
        Assert.Equal("a\nb\nc\nd\nE", content);
    }
}
