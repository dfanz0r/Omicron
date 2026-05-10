using Omicron.Core.Diff;
using Xunit;

namespace Omicron.Core.Tests;

public class UnifiedDiffRendererTests
{
    private static string Render(string[] oldLines, string[] newLines,
        int contextLines = 3, string oldPath = "old.txt", string newPath = "new.txt")
    {
        var diff = TextDiffEngine.DiffLines(oldLines, newLines);
        return UnifiedDiffRenderer.Render(oldPath, newPath, oldLines, newLines, diff,
            new UnifiedDiffRenderOptions { ContextLines = contextLines });
    }

    [Fact]
    public void NoChanges_ReturnsEmpty()
    {
        var lines = new[] { "a", "b", "c" };
        var result = Render(lines, lines);
        Assert.Empty(result);
    }

    [Fact]
    public void ModifiedFile_IncludesHeaders()
    {
        var oldLines = new[] { "a", "b", "c" };
        var newLines = new[] { "a", "x", "c" };
        var result = Render(oldLines, newLines);

        Assert.Contains("--- old.txt", result);
        Assert.Contains("+++ new.txt", result);
        Assert.Contains("@@", result);
    }

    [Fact]
    public void ModifiedFile_ShowsRemovedAndAdded()
    {
        var oldLines = new[] { "a", "b", "c" };
        var newLines = new[] { "a", "x", "c" };
        var result = Render(oldLines, newLines);

        Assert.Contains("-b", result);
        Assert.Contains("+x", result);
        // Exact line ordering
        var lines = result.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var lineInfo = string.Join(", ", lines.Select(l => "'" + l + "'"));
        int idxB = Array.IndexOf(lines, "-b");
        int idxX = Array.IndexOf(lines, "+x");
        Assert.True(idxB >= 0, $"Expected '-b' line. Lines: {lineInfo}");
        Assert.True(idxX >= 0, $"Expected '+x' line. Lines: {lineInfo}");
        Assert.True(idxB < idxX, "Expected -b before +x");
    }

    [Fact]
    public void AddedFile_UsesDevNullOld()
    {
        var oldLines = Array.Empty<string>();
        var newLines = new[] { "line1", "line2" };
        var result = Render(oldLines, newLines, oldPath: "/dev/null", newPath: "new.txt");

        Assert.Contains("--- /dev/null", result);
        Assert.Contains("+++ new.txt", result);
        Assert.Contains("+line1", result);
        Assert.Contains("+line2", result);
    }

    [Fact]
    public void DeletedFile_UsesDevNullNew()
    {
        var oldLines = new[] { "line1", "line2" };
        var newLines = Array.Empty<string>();
        var result = Render(oldLines, newLines, oldPath: "old.txt", newPath: "/dev/null");

        Assert.Contains("--- old.txt", result);
        Assert.Contains("+++ /dev/null", result);
        Assert.Contains("-line1", result);
        Assert.Contains("-line2", result);
    }

    [Fact]
    public void ContextLines_AreRespected()
    {
        var oldLines = new[] { "a", "b", "c", "d", "e", "f", "g" };
        var newLines = new[] { "a", "b", "x", "d", "e", "f", "g" };
        // With 1 context line
        var result = Render(oldLines, newLines, contextLines: 1);

        // Should see context around the change
        Assert.Contains(" b", result);
        Assert.Contains("-c", result);
        Assert.Contains("+x", result);
        Assert.Contains(" d", result);
    }

    [Fact]
    public void MultipleHunks_AreSeparated()
    {
        var oldLines = new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j" };
        var newLines = new[] { "x", "b", "c", "d", "e", "y", "g", "h", "i", "z" };
        var result = Render(oldLines, newLines, contextLines: 1);

        // Should have 3 hunks (or more depending on context merging)
        Assert.Contains("@@", result);
        // Check that multiple change markers exist
        int hunkCount = result.Split("@@").Length - 1;
        Assert.True(hunkCount >= 2, $"Expected at least 2 hunks, got {hunkCount}");
    }

    [Fact]
    public void RendererTruncates_AtOutputLimit()
    {
        var oldLines = Enumerable.Range(0, 200).Select(i => $"line-{i:D4}").ToArray();
        var newLines = Enumerable.Range(0, 200).Select(i => $"line-{i:D4} modified").ToArray();

        var diff = TextDiffEngine.DiffLines(oldLines, newLines);
        var result = UnifiedDiffRenderer.Render("old.txt", "new.txt", oldLines, newLines, diff,
            new UnifiedDiffRenderOptions { MaxOutputLines = 10 });

        Assert.Contains("diff truncated", result);
    }

    [Fact]
    public void EngineTruncated_RendererIncludesMarker()
    {
        // Use MaxLineCount to force engine-level truncation
        var oldLines = Enumerable.Range(0, 100).Select(i => $"old-{i}").ToArray();
        var newLines = Enumerable.Range(0, 100).Select(i => $"new-{i}").ToArray();

        var diff = TextDiffEngine.DiffLines(oldLines, newLines,
            new TextDiffOptions { MaxLineCount = 10 });
        var result = UnifiedDiffRenderer.Render("old.txt", "new.txt", oldLines, newLines, diff);

        Assert.True(diff.IsTruncated);
        Assert.Contains("truncated", result);
    }

    [Fact]
    public void LargeDiff_RendererLimit_StillTruncates()
    {
        var oldLines = Enumerable.Range(0, 500).Select(i => $"line-{i}").ToArray();
        var newLines = Enumerable.Range(0, 500).Select(i => i == 250 ? $"modified-{i}" : $"line-{i}").ToArray();

        var diff = TextDiffEngine.DiffLines(oldLines, newLines);
        var result = UnifiedDiffRenderer.Render("old.txt", "new.txt", oldLines, newLines, diff,
            new UnifiedDiffRenderOptions { MaxOutputLines = 50 });

        int lines = result.Split('\n').Length;
        Assert.True(lines <= 60, $"Expected < 60 lines, got {lines}");
    }

    [Fact]
    public void EmptyResult_ForNoChanges()
    {
        var oldLines = new[] { "a", "b" };
        var result = Render(oldLines, oldLines);
        Assert.Empty(result);
    }

    [Fact]
    public void TruncatedDiff_IncludesMarker()
    {
        var oldLines = Enumerable.Range(0, 100).Select(i => $"old-{i}").ToArray();
        var newLines = Enumerable.Range(0, 100).Select(i => $"new-{i}").ToArray();

        var diff = TextDiffEngine.DiffLines(oldLines, newLines,
            new TextDiffOptions { MaxLineCount = 10 });
        var result = UnifiedDiffRenderer.Render("old.txt", "new.txt", oldLines, newLines, diff);

        Assert.Contains("input too large", result);
        Assert.Contains("truncated", result);
    }

}
