using System.Text;
using Omicron.Core.Diff;
using Xunit;

namespace Omicron.Core.Tests;

public sealed class TextLineSplitterTests
{
    [Fact]
    public void TryReadNextLine_String_SlicesWithoutAllocatingLines()
    {
        var text = "alpha\r\nbeta\ngamma";
        var index = 0;

        Assert.True(TextLineSplitter.TryReadNextLine(text.AsSpan(), ref index, out var line));
        Assert.True(line.SequenceEqual("alpha"));
        Assert.True(TextLineSplitter.TryReadNextLine(text.AsSpan(), ref index, out line));
        Assert.True(line.SequenceEqual("beta"));
        Assert.True(TextLineSplitter.TryReadNextLine(text.AsSpan(), ref index, out line));
        Assert.True(line.SequenceEqual("gamma"));
        Assert.False(TextLineSplitter.TryReadNextLine(text.AsSpan(), ref index, out _));
    }

    [Fact]
    public void TryReadNextLine_Utf8_SlicesWithoutDecodingLines()
    {
        var bytes = Encoding.UTF8.GetBytes("alpha\r\nbeta\ngamma");
        var index = 0;

        Assert.True(TextLineSplitter.TryReadNextLine(bytes, ref index, out var line));
        Assert.True(line.SequenceEqual("alpha"u8));
        Assert.True(TextLineSplitter.TryReadNextLine(bytes, ref index, out line));
        Assert.True(line.SequenceEqual("beta"u8));
        Assert.True(TextLineSplitter.TryReadNextLine(bytes, ref index, out line));
        Assert.True(line.SequenceEqual("gamma"u8));
        Assert.False(TextLineSplitter.TryReadNextLine(bytes, ref index, out _));
    }

    [Fact]
    public void CreateUtf8LineIndex_PreservesTrailingEmptyLineForDiffs()
    {
        var lines = TextLineSplitter.CreateUtf8LineIndex("alpha\n"u8.ToArray());

        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].SequenceEqual("alpha"u8));
        Assert.True(lines[1].IsEmpty);
    }

    [Fact]
    public void Utf8DiffRenderer_MatchesStringRenderer_ForInsertionDeletionAndContext()
    {
        var oldText = "a\nb\nc\nd\n";
        var newText = "a\nB\nc\nd\ne\n";

        var oldStringLines = oldText.Replace("\r\n", "\n").Split('\n');
        var newStringLines = newText.Replace("\r\n", "\n").Split('\n');
        var oldUtf8Lines = TextLineSplitter.CreateUtf8LineIndex(Encoding.UTF8.GetBytes(oldText));
        var newUtf8Lines = TextLineSplitter.CreateUtf8LineIndex(Encoding.UTF8.GetBytes(newText));

        var stringDiff = TextDiffEngine.DiffLines(oldStringLines, newStringLines);
        var utf8Diff = TextDiffEngine.DiffLines(oldUtf8Lines, newUtf8Lines);

        var stringRendered = UnifiedDiffRenderer.Render("old", "new", oldStringLines, newStringLines, stringDiff);
        var utf8Rendered = UnifiedDiffRenderer.Render("old", "new", oldUtf8Lines, newUtf8Lines, utf8Diff);

        Assert.Equal(stringRendered, utf8Rendered);
    }
}
