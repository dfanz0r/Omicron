using System.Text;
using Omicron.Core.Diff;
using Xunit;

namespace Omicron.Core.Tests;

public sealed class TextLineSplitterTests
{
    [Fact]
    public void TryReadNextLine_String_SlicesWithoutAllocatingLines()
    {
        string text = "alpha\r\nbeta\ngamma";
        int index = 0;

        Assert.True(TextLineSplitter.TryReadNextLine(text.AsSpan(), ref index, out ReadOnlySpan<char> line));
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
        byte[] bytes = Encoding.UTF8.GetBytes("alpha\r\nbeta\ngamma");
        int index = 0;

        Assert.True(TextLineSplitter.TryReadNextLine(bytes, ref index, out ReadOnlySpan<byte> line));
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
        Utf8LineIndex lines = TextLineSplitter.CreateUtf8LineIndex("alpha\n"u8.ToArray());

        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].SequenceEqual("alpha"u8));
        Assert.True(lines[1].IsEmpty);
    }

    [Fact]
    public void Utf8DiffRenderer_MatchesStringRenderer_ForInsertionDeletionAndContext()
    {
        string oldText = "a\nb\nc\nd\n";
        string newText = "a\nB\nc\nd\ne\n";

        string[] oldStringLines = oldText.Replace("\r\n", "\n").Split('\n');
        string[] newStringLines = newText.Replace("\r\n", "\n").Split('\n');
        Utf8LineIndex oldUtf8Lines = TextLineSplitter.CreateUtf8LineIndex(Encoding.UTF8.GetBytes(oldText));
        Utf8LineIndex newUtf8Lines = TextLineSplitter.CreateUtf8LineIndex(Encoding.UTF8.GetBytes(newText));

        TextDiffResult stringDiff = TextDiffEngine.DiffLines(oldStringLines, newStringLines);
        TextDiffResult utf8Diff = TextDiffEngine.DiffLines(oldUtf8Lines, newUtf8Lines);

        string stringRendered = UnifiedDiffRenderer.Render("old",
            "new",
            oldStringLines,
            newStringLines,
            stringDiff);
        string utf8Rendered = UnifiedDiffRenderer.Render("old",
            "new",
            oldUtf8Lines,
            newUtf8Lines,
            utf8Diff);

        Assert.Equal(stringRendered, utf8Rendered);
    }
}
