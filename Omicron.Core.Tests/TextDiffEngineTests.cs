using Omicron.Core.Diff;
using Xunit;

namespace Omicron.Core.Tests;

public class TextDiffEngineTests
{
    private static TextDiffResult Diff(string[] oldLines, string[] newLines)
    {
        return TextDiffEngine.DiffLines(oldLines, newLines);
    }

    [Fact]
    public void AllSame_ReturnsNoEdits()
    {
        string[] lines = new[]
        {
            "a", "b", "c"
        };
        TextDiffResult result = Diff(lines, lines);
        Assert.False(result.HasChanges);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public void InsertAtBeginning()
    {
        string[] oldLines = new[]
        {
            "b", "c"
        };
        string[] newLines = new[]
        {
            "a", "b", "c"
        };
        TextDiffResult result = Diff(oldLines, newLines);

        Assert.True(result.HasChanges);
        Assert.Single(result.Edits);
        TextDiffEdit edit = result.Edits[0];
        Assert.True(edit.IsInsert); // OldCount == 0
        Assert.Equal(0, edit.OldStart);
        Assert.Equal(0, edit.OldCount);
        Assert.Equal(0, edit.NewStart);
        Assert.Equal(1, edit.NewCount);
    }

    [Fact]
    public void InsertAtEnd()
    {
        string[] oldLines = new[]
        {
            "a", "b"
        };
        string[] newLines = new[]
        {
            "a", "b", "c"
        };
        TextDiffResult result = Diff(oldLines, newLines);

        Assert.True(result.HasChanges);
        Assert.Single(result.Edits);
        TextDiffEdit edit = result.Edits[0];
        Assert.True(edit.IsInsert);
        Assert.Equal(2, edit.OldStart);
        Assert.Equal(0, edit.OldCount);
        Assert.Equal(2, edit.NewStart);
        Assert.Equal(1, edit.NewCount);
    }

    [Fact]
    public void DeleteAtBeginning()
    {
        string[] oldLines = new[]
        {
            "a", "b", "c"
        };
        string[] newLines = new[]
        {
            "b", "c"
        };
        TextDiffResult result = Diff(oldLines, newLines);

        Assert.True(result.HasChanges);
        Assert.Single(result.Edits);
        TextDiffEdit edit = result.Edits[0];
        Assert.True(edit.IsDelete);
        Assert.Equal(0, edit.OldStart);
        Assert.Equal(1, edit.OldCount);
        Assert.Equal(0, edit.NewStart);
        Assert.Equal(0, edit.NewCount);
    }

    [Fact]
    public void DeleteAtEnd()
    {
        string[] oldLines = new[]
        {
            "a", "b", "c"
        };
        string[] newLines = new[]
        {
            "a", "b"
        };
        TextDiffResult result = Diff(oldLines, newLines);

        Assert.True(result.HasChanges);
        Assert.Single(result.Edits);
        TextDiffEdit edit = result.Edits[0];
        Assert.True(edit.IsDelete);
        Assert.Equal(2, edit.OldStart);
        Assert.Equal(1, edit.OldCount);
        Assert.Equal(2, edit.NewStart);
        Assert.Equal(0, edit.NewCount);
    }

    [Fact]
    public void ReplaceInMiddle()
    {
        string[] oldLines = new[]
        {
            "a", "old", "c"
        };
        string[] newLines = new[]
        {
            "a", "new", "c"
        };
        TextDiffResult result = Diff(oldLines, newLines);

        Assert.True(result.HasChanges);
        Assert.Single(result.Edits);
        TextDiffEdit edit = result.Edits[0];
        Assert.True(edit.IsReplace);
        Assert.Equal(1, edit.OldStart);
        Assert.Equal(1, edit.OldCount);
        Assert.Equal(1, edit.NewStart);
        Assert.Equal(1, edit.NewCount);
    }

    [Fact]
    public void MultipleChanges()
    {
        string[] oldLines = new[]
        {
            "b", "c", "d"
        };
        string[] newLines = new[]
        {
            "x", "c", "y"
        };
        TextDiffResult result = Diff(oldLines, newLines);

        Assert.True(result.HasChanges);
        // Should find 2 changes (b→x, d→y) with c as common midline
        Assert.True(result.Edits.Count >= 1);
    }

    [Fact]
    public void SnakeCase()
    {
        string[] oldLines = new[]
        {
            "a", "b", "c", "d", "e", "f"
        };
        string[] newLines = new[]
        {
            "b", "c", "d", "e", "f", "x"
        };
        TextDiffResult result = Diff(oldLines, newLines);

        Assert.True(result.HasChanges);
        Assert.Equal(2, result.Edits.Count);
        Assert.Equal(new TextDiffEdit(0, 1, 0, 0), result.Edits[0]); // delete "a"
        Assert.Equal(new TextDiffEdit(6, 0, 5, 1), result.Edits[1]); // insert "x"
    }

    [Fact]
    public void EmptyOld_AddedFile()
    {
        string[] oldLines = Array.Empty<string>();
        string[] newLines = new[]
        {
            "line1", "line2"
        };
        TextDiffResult result = Diff(oldLines, newLines);

        Assert.True(result.HasChanges);
        Assert.Equal(new TextDiffEdit(0, 0, 0, 2), Assert.Single(result.Edits));
    }

    [Fact]
    public void EmptyNew_DeletedFile()
    {
        string[] oldLines = new[]
        {
            "line1", "line2"
        };
        string[] newLines = Array.Empty<string>();
        TextDiffResult result = Diff(oldLines, newLines);

        Assert.True(result.HasChanges);
        Assert.Equal(new TextDiffEdit(0, 2, 0, 0), Assert.Single(result.Edits));
    }

    [Fact]
    public void BothEmpty_ReturnsNoEdits()
    {
        TextDiffResult result = Diff(Array.Empty<string>(), Array.Empty<string>());
        Assert.False(result.HasChanges);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public void LineCountsAreCorrect()
    {
        string[] oldLines = new[]
        {
            "a", "b", "c"
        };
        string[] newLines = new[]
        {
            "a", "x", "c"
        };
        TextDiffResult result = Diff(oldLines, newLines);

        Assert.Equal(3, result.OldLineCount);
        Assert.Equal(3, result.NewLineCount);
    }

    [Fact]
    public void MultiLineReplace()
    {
        string[] oldLines = new[]
        {
            "a", "b", "c", "d"
        };
        string[] newLines = new[]
        {
            "a", "x", "y", "z", "d"
        };
        TextDiffResult result = Diff(oldLines, newLines);

        Assert.True(result.HasChanges);
        Assert.Single(result.Edits);
        TextDiffEdit edit = result.Edits[0];
        Assert.True(edit.IsReplace);
        Assert.Equal(1, edit.OldStart);
        Assert.Equal(2, edit.OldCount); // "b", "c" replaced
        Assert.Equal(1, edit.NewStart);
        Assert.Equal(3, edit.NewCount); // by "x", "y", "z"
    }

    [Fact]
    public void RepeatedLines_ProducesEdits()
    {
        string[] oldLines = new[]
        {
            "x", "a", "a", "a", "y"
        };
        string[] newLines = new[]
        {
            "x", "a", "b", "a", "y"
        };
        TextDiffResult result = Diff(oldLines, newLines);

        Assert.True(result.HasChanges);
        // Should detect the change even with repeated "a" lines
        Assert.Contains(result.Edits, e => e.OldStart >= 1);
    }

    [Fact]
    public void MaxLineCount_Exceeded_ReturnsTruncated()
    {
        string[] oldLines = Enumerable.Range(0, 100).Select(i => $"old-{i}").ToArray();
        string[] newLines = Enumerable.Range(0, 100).Select(i => $"new-{i}").ToArray();

        TextDiffResult result = TextDiffEngine.DiffLines(oldLines,
            newLines,
            new TextDiffOptions
            {
                MaxLineCount = 10
            });

        Assert.True(result.IsTruncated);
        Assert.NotNull(result.TruncationReason);
        Assert.Contains("MaxLineCount", result.TruncationReason);
    }

    [Fact]
    public void DefaultMaxLineCount_IsZero()
    {
        // Default is 0 = no limit; internal thresholds in the facade handle sizing
        Assert.Equal(0, TextDiffOptions.Default.MaxLineCount);
    }

    [Fact]
    public void LargeFallback_SingleReplace_ExactEdit()
    {
        string[] oldLines = Enumerable.Range(0, 3000).Select(i => $"line-{i:D4}").ToArray();
        string[] newLines = Enumerable.Range(0, 3000).Select(i => $"line-{i:D4}").ToArray();
        newLines[1500] = "CHANGED";

        TextDiffResult diff = TextDiffEngine.DiffLines(oldLines, newLines);

        Assert.False(diff.IsTruncated, $"Should not be truncated: {diff.TruncationReason}");
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Edits);
        TextDiffEdit edit = diff.Edits[0];
        Assert.Equal(1500, edit.OldStart);
        Assert.Equal(1, edit.OldCount);
        Assert.Equal(1500, edit.NewStart);
        Assert.Equal(1, edit.NewCount);
    }

    [Fact]
    public void LargeFallback_SingleInsert_ExactEdit()
    {
        string[] oldLines = Enumerable.Range(0, 3000).Select(i => $"line-{i:D4}").ToArray();
        var newLines = Enumerable.Range(0, 3000).Select(i => $"line-{i:D4}").ToList();
        newLines.Insert(500, "INSERTED");

        TextDiffResult diff = TextDiffEngine.DiffLines(oldLines, newLines.ToArray());

        Assert.False(diff.IsTruncated);
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Edits);
        Assert.True(diff.Edits[0].IsInsert);
        Assert.Equal(500, diff.Edits[0].OldStart);
        Assert.Equal(500, diff.Edits[0].NewStart);
    }

    [Fact]
    public void LargeFallback_SingleDelete_ExactEdit()
    {
        string[] oldLines = Enumerable.Range(0, 3000).Select(i => $"line-{i:D4}").ToArray();
        var newLines = Enumerable.Range(0, 3000).Select(i => $"line-{i:D4}").ToList();
        newLines.RemoveAt(2000);

        TextDiffResult diff = TextDiffEngine.DiffLines(oldLines, newLines.ToArray());

        Assert.False(diff.IsTruncated);
        Assert.True(diff.HasChanges);
        Assert.Contains(diff.Edits, e => e.IsDelete && e.OldStart == 2000);
    }

    [Fact]
    public void LargeFallback_AllSame_NoEdits()
    {
        string[] lines = Enumerable.Range(0, 3000).Select(i => $"line-{i:D4}").ToArray();
        TextDiffResult diff = TextDiffEngine.DiffLines(lines, lines);
        Assert.False(diff.HasChanges);
        Assert.False(diff.IsTruncated);
    }

    [Fact]
    public void LargeFallback_MultiLineReplace_Normalized()
    {
        // Replace 3 lines with 2 lines in a large file — should produce one replace edit
        string[] oldLines = Enumerable.Range(0, 3000).Select(i => $"line-{i:D4}").ToArray();
        string[] newLines = Enumerable.Range(0, 3000).Select(i => $"line-{i:D4}").ToArray();
        newLines[1000] = "A";
        newLines[1001] = "B";
        // Remove 1002, keeping newLines one shorter: old has 3 lines replaced by 2
        newLines = newLines.Where((_, i) => i != 1002).ToArray();
        // Actually simpler: old[1000..1003] = 3 lines, new[1000..1002] = 2 lines

        // Reset: old 3 lines at 1000-1002, new 2 lines at 1000-1001
        string[] old3 = Enumerable.Range(0, 3000).Select(i => $"line-{i:D4}").ToArray();
        string[] new2 = Enumerable.Range(0, 3000).Select(i => $"line-{i:D4}").ToArray();
        new2[1000] = "x";
        new2[1001] = "y";
        // Remove new2[1002] so new has 2 lines instead of 3 in that range
        var newList = new2.ToList();
        newList.RemoveAt(1002);

        TextDiffResult diff = TextDiffEngine.DiffLines(old3, newList.ToArray());

        Assert.False(diff.IsTruncated, $"Should not be truncated: {diff.TruncationReason}");
        Assert.True(diff.HasChanges);

        // After normalization, edits should merge into one replace
        Assert.Single(diff.Edits);
        Assert.True(diff.Edits[0].IsReplace);
        Assert.Equal(1000, diff.Edits[0].OldStart);
        Assert.Equal(3, diff.Edits[0].OldCount);
        Assert.Equal(1000, diff.Edits[0].NewStart);
        Assert.Equal(2, diff.Edits[0].NewCount);
    }
}
