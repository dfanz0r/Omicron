using Omicron.Core.Diff;
using Xunit;

namespace Omicron.Core.Tests;

public class UnifiedDiffRendererTests
{
    private static string Render(
        string[] oldLines,
        string[] newLines,
        int contextLines = 3,
        string oldPath = "old.txt",
        string newPath = "new.txt")
    {
        TextDiffResult diff = TextDiffEngine.DiffLines(oldLines, newLines);
        return UnifiedDiffRenderer.Render(oldPath,
            newPath,
            oldLines,
            newLines,
            diff,
            new UnifiedDiffRenderOptions
            {
                ContextLines = contextLines
            });
    }

    [Fact]
    public void NoChanges_ReturnsEmpty()
    {
        string[] lines = new[]
        {
            "a", "b", "c"
        };
        string result = Render(lines, lines);
        Assert.Empty(result);
    }

    [Fact]
    public void ModifiedFile_IncludesHeaders()
    {
        string[] oldLines = new[]
        {
            "a", "b", "c"
        };
        string[] newLines = new[]
        {
            "a", "x", "c"
        };
        string result = Render(oldLines, newLines);

        Assert.Contains("--- old.txt", result);
        Assert.Contains("+++ new.txt", result);
        Assert.Contains("@@", result);
    }

    [Fact]
    public void ModifiedFile_ShowsRemovedAndAdded()
    {
        string[] oldLines = new[]
        {
            "a", "b", "c"
        };
        string[] newLines = new[]
        {
            "a", "x", "c"
        };
        string result = Render(oldLines, newLines);

        Assert.Contains("-b", result);
        Assert.Contains("+x", result);
        // Exact line ordering
        string[] lines = result.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        string lineInfo = string.Join(", ", lines.Select(l => "'" + l + "'"));
        int idxB = Array.IndexOf(lines, "-b");
        int idxX = Array.IndexOf(lines, "+x");
        Assert.True(idxB >= 0, $"Expected '-b' line. Lines: {lineInfo}");
        Assert.True(idxX >= 0, $"Expected '+x' line. Lines: {lineInfo}");
        Assert.True(idxB < idxX, "Expected -b before +x");
    }

    [Fact]
    public void AddedFile_UsesDevNullOld()
    {
        string[] oldLines = Array.Empty<string>();
        string[] newLines = new[]
        {
            "line1", "line2"
        };
        string result = Render(oldLines, newLines, oldPath: "/dev/null", newPath: "new.txt");

        Assert.Contains("--- /dev/null", result);
        Assert.Contains("+++ new.txt", result);
        Assert.Contains("+line1", result);
        Assert.Contains("+line2", result);
    }

    [Fact]
    public void DeletedFile_UsesDevNullNew()
    {
        string[] oldLines = new[]
        {
            "line1", "line2"
        };
        string[] newLines = Array.Empty<string>();
        string result = Render(oldLines, newLines, oldPath: "old.txt", newPath: "/dev/null");

        Assert.Contains("--- old.txt", result);
        Assert.Contains("+++ /dev/null", result);
        Assert.Contains("-line1", result);
        Assert.Contains("-line2", result);
    }

    [Fact]
    public void ContextLines_AreRespected()
    {
        string[] oldLines = new[]
        {
            "a", "b", "c", "d", "e", "f", "g"
        };
        string[] newLines = new[]
        {
            "a", "b", "x", "d", "e", "f", "g"
        };
        // With 1 context line
        string result = Render(oldLines, newLines, 1);

        // Should see context around the change
        Assert.Contains(" b", result);
        Assert.Contains("-c", result);
        Assert.Contains("+x", result);
        Assert.Contains(" d", result);
    }

    [Fact]
    public void MultipleHunks_AreSeparated()
    {
        string[] oldLines = new[]
        {
            "a", "b", "c", "d", "e", "f", "g", "h", "i", "j"
        };
        string[] newLines = new[]
        {
            "x", "b", "c", "d", "e", "y", "g", "h", "i", "z"
        };
        string result = Render(oldLines, newLines, 1);

        // Should have 3 hunks (or more depending on context merging)
        Assert.Contains("@@", result);
        // Check that multiple change markers exist
        int hunkCount = result.Split("@@").Length - 1;
        Assert.True(hunkCount >= 2, $"Expected at least 2 hunks, got {hunkCount}");
    }

    [Fact]
    public void RendererTruncates_AtOutputLimit()
    {
        string[] oldLines = Enumerable.Range(0, 200).Select(i => $"line-{i:D4}").ToArray();
        string[] newLines = Enumerable.Range(0, 200).Select(i => $"line-{i:D4} modified").ToArray();

        TextDiffResult diff = TextDiffEngine.DiffLines(oldLines, newLines);
        string result = UnifiedDiffRenderer.Render("old.txt",
            "new.txt",
            oldLines,
            newLines,
            diff,
            new UnifiedDiffRenderOptions
            {
                MaxOutputLines = 10
            });

        Assert.Contains("diff truncated", result);
    }

    [Fact]
    public void EngineTruncated_RendererIncludesMarker()
    {
        // Use MaxLineCount to force engine-level truncation
        string[] oldLines = Enumerable.Range(0, 100).Select(i => $"old-{i}").ToArray();
        string[] newLines = Enumerable.Range(0, 100).Select(i => $"new-{i}").ToArray();

        TextDiffResult diff = TextDiffEngine.DiffLines(oldLines,
            newLines,
            new TextDiffOptions
            {
                MaxLineCount = 10
            });
        string result = UnifiedDiffRenderer.Render("old.txt", "new.txt", oldLines, newLines, diff);

        Assert.True(diff.IsTruncated);
        Assert.Contains("truncated", result);
    }

    [Fact]
    public void LargeDiff_RendererLimit_StillTruncates()
    {
        string[] oldLines = Enumerable.Range(0, 500).Select(i => $"line-{i}").ToArray();
        string[] newLines = Enumerable
            .Range(0, 500)
            .Select(i => i == 250 ? $"modified-{i}" : $"line-{i}")
            .ToArray();

        TextDiffResult diff = TextDiffEngine.DiffLines(oldLines, newLines);
        string result = UnifiedDiffRenderer.Render("old.txt",
            "new.txt",
            oldLines,
            newLines,
            diff,
            new UnifiedDiffRenderOptions
            {
                MaxOutputLines = 50
            });

        int lines = result.Split('\n').Length;
        Assert.True(lines <= 60, $"Expected < 60 lines, got {lines}");
    }

    [Fact]
    public void EmptyResult_ForNoChanges()
    {
        string[] oldLines = new[]
        {
            "a", "b"
        };
        string result = Render(oldLines, oldLines);
        Assert.Empty(result);
    }

    [Fact]
    public void TruncatedDiff_IncludesMarker()
    {
        string[] oldLines = Enumerable.Range(0, 100).Select(i => $"old-{i}").ToArray();
        string[] newLines = Enumerable.Range(0, 100).Select(i => $"new-{i}").ToArray();

        TextDiffResult diff = TextDiffEngine.DiffLines(oldLines,
            newLines,
            new TextDiffOptions
            {
                MaxLineCount = 10
            });
        string result = UnifiedDiffRenderer.Render("old.txt", "new.txt", oldLines, newLines, diff);

        Assert.Contains("input too large", result);
        Assert.Contains("truncated", result);
    }
}
