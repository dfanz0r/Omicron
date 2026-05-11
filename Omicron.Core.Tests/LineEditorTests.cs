using Omicron.CLI;
using Xunit;

namespace Omicron.Core.Tests;

public class LineEditorTests
{
    private static (LineEditorEngine engine, LineEditorState state) Create()
    {
        return (new LineEditorEngine(), new LineEditorState());
    }

    // ---- Typing ----

    [Fact]
    public void TypingLetters_AppendsToBuffer()
    {
        var (engine, state) = Create();

        foreach (var c in "hello")
            engine.ProcessKey(EditorKeyInfo.Char(c), state);

        Assert.Equal("hello", state.Buffer.ToString());
        Assert.Equal(5, state.Cursor);
    }

    [Fact]
    public void TypingMultipleLines_WithShiftEnter()
    {
        var (engine, state) = Create();

        engine.ProcessKey(EditorKeyInfo.Char('a'), state);
        engine.ProcessKey(EditorKeyInfo.ShiftEnter, state);
        engine.ProcessKey(EditorKeyInfo.Char('b'), state);

        Assert.Equal("a\nb", state.Buffer.ToString());
    }

    // ---- Navigation ----

    [Fact]
    public void LeftArrow_MovesCursorBack()
    {
        var (engine, state) = Create();
        foreach (var c in "abc")
            engine.ProcessKey(EditorKeyInfo.Char(c), state);

        Assert.Equal(3, state.Cursor);

        engine.ProcessKey(EditorKeyInfo.LeftArrow, state);
        Assert.Equal(2, state.Cursor);

        engine.ProcessKey(EditorKeyInfo.LeftArrow, state);
        Assert.Equal(1, state.Cursor);
    }

    [Fact]
    public void LeftArrow_AtStart_DoesNothing()
    {
        var (engine, state) = Create();
        engine.ProcessKey(EditorKeyInfo.LeftArrow, state);
        Assert.Equal(0, state.Cursor);
    }

    [Fact]
    public void RightArrow_MovesCursorForward()
    {
        var (engine, state) = Create();
        foreach (var c in "abc")
            engine.ProcessKey(EditorKeyInfo.Char(c), state);

        engine.ProcessKey(EditorKeyInfo.LeftArrow, state);
        engine.ProcessKey(EditorKeyInfo.LeftArrow, state);
        Assert.Equal(1, state.Cursor);

        engine.ProcessKey(EditorKeyInfo.RightArrow, state);
        Assert.Equal(2, state.Cursor);
    }

    [Fact]
    public void Home_MovesToStart()
    {
        var (engine, state) = Create();
        foreach (var c in "test")
            engine.ProcessKey(EditorKeyInfo.Char(c), state);

        engine.ProcessKey(EditorKeyInfo.Home, state);
        Assert.Equal(0, state.Cursor);
    }

    [Fact]
    public void End_MovesToEnd()
    {
        var (engine, state) = Create();
        foreach (var c in "test")
            engine.ProcessKey(EditorKeyInfo.Char(c), state);

        engine.ProcessKey(EditorKeyInfo.Home, state);
        engine.ProcessKey(EditorKeyInfo.End, state);
        Assert.Equal(4, state.Cursor);
    }

    // ---- Editing ----

    [Fact]
    public void Backspace_RemovesPreviousCharacter()
    {
        var (engine, state) = Create();
        foreach (var c in "abcd")
            engine.ProcessKey(EditorKeyInfo.Char(c), state);

        engine.ProcessKey(EditorKeyInfo.Backspace, state);
        Assert.Equal("abc", state.Buffer.ToString());
        Assert.Equal(3, state.Cursor);
    }

    [Fact]
    public void Backspace_AtStart_DoesNothing()
    {
        var (engine, state) = Create();
        engine.ProcessKey(EditorKeyInfo.Backspace, state);
        Assert.Equal("", state.Buffer.ToString());
        Assert.Equal(0, state.Cursor);
    }

    [Fact]
    public void Delete_RemovesCharacterAtCursor()
    {
        var (engine, state) = Create();
        foreach (var c in "abcd")
            engine.ProcessKey(EditorKeyInfo.Char(c), state);

        engine.ProcessKey(EditorKeyInfo.LeftArrow, state);
        engine.ProcessKey(EditorKeyInfo.Delete, state);
        // Cursor at 3 ('d'), delete removes character at cursor (cursor stays)
        Assert.Equal("abc", state.Buffer.ToString());
        Assert.Equal(3, state.Cursor);
    }

    // ---- Submission ----

    [Fact]
    public void Enter_ReturnsSubmitAction()
    {
        var (engine, state) = Create();
        var result = engine.ProcessKey(EditorKeyInfo.Enter, state);
        Assert.Equal(LineEditorAction.Submit, result);
    }

    [Fact]
    public void CtrlC_ReturnsCancelAction()
    {
        var (engine, state) = Create();
        var result = engine.ProcessKey(EditorKeyInfo.CtrlC, state);
        Assert.Equal(LineEditorAction.Cancel, result);
    }

    // ---- Escape ----

    [Fact]
    public void Escape_ClearsBuffer()
    {
        var (engine, state) = Create();
        foreach (var c in "hello")
            engine.ProcessKey(EditorKeyInfo.Char(c), state);

        Assert.Equal("hello", state.Buffer.ToString());

        var result = engine.ProcessKey(EditorKeyInfo.Escape, state);
        Assert.Equal(LineEditorAction.None, result);
        Assert.Equal("", state.Buffer.ToString());
        Assert.Equal(0, state.Cursor);
        Assert.True(state.EscapePressed);
    }

    // ---- Tab completion ----

    [Fact]
    public void TabCompletion_SingleMatch_ReplacesBuffer()
    {
        var completions = new Dictionary<string, IReadOnlyList<string>>
        {
            ["hel"] = new[] { "help" }
        };
        var engine = new LineEditorEngine(text =>
            completions.TryGetValue(text, out var c) ? c : []);
        var state = new LineEditorState();

        foreach (var c in "hel")
            engine.ProcessKey(EditorKeyInfo.Char(c), state);

        Assert.Equal("hel", state.Buffer.ToString());

        engine.ProcessKey(EditorKeyInfo.Tab, state);
        Assert.Equal("help", state.Buffer.ToString());
        Assert.Equal(4, state.Cursor);
    }

    [Fact]
    public void TabCompletion_MultipleMatches_CompletesCommonPrefix()
    {
        var engine = new LineEditorEngine(text =>
            text == "he" ? new[] { "help", "hello" } : []);
        var state = new LineEditorState();

        foreach (var c in "he")
            engine.ProcessKey(EditorKeyInfo.Char(c), state);

        Assert.Equal("he", state.Buffer.ToString());

        engine.ProcessKey(EditorKeyInfo.Tab, state);
        // Common prefix "hel" is completed even with multiple matches
        Assert.Equal("hel", state.Buffer.ToString());
    }

    [Fact]
    public void TabCompletion_MultipleMatchesNoCommonPrefix_DoesNotModifyBuffer()
    {
        var engine = new LineEditorEngine(text =>
            text == "x" ? new[] { "xyz", "xab" } : []);
        var state = new LineEditorState();

        engine.ProcessKey(EditorKeyInfo.Char('x'), state);
        Assert.Equal("x", state.Buffer.ToString());

        engine.ProcessKey(EditorKeyInfo.Tab, state);
        // No common prefix beyond "x" — buffer unchanged
        Assert.Equal("x", state.Buffer.ToString());
    }

    [Fact]
    public void TabCompletion_NoCompletions_DoesNothing()
    {
        var engine = new LineEditorEngine(_ => []);
        var state = new LineEditorState();

        foreach (var c in "xyz")
            engine.ProcessKey(EditorKeyInfo.Char(c), state);

        Assert.Equal("xyz", state.Buffer.ToString());

        engine.ProcessKey(EditorKeyInfo.Tab, state);
        Assert.Equal("xyz", state.Buffer.ToString());
    }

    // ---- Insert in middle of buffer ----

    [Fact]
    public void InsertionInMiddle_ShiftsTextRight()
    {
        var (engine, state) = Create();
        foreach (var c in "acd")
            engine.ProcessKey(EditorKeyInfo.Char(c), state);

        engine.ProcessKey(EditorKeyInfo.LeftArrow, state);
        engine.ProcessKey(EditorKeyInfo.LeftArrow, state);
        engine.ProcessKey(EditorKeyInfo.Char('b'), state);

        Assert.Equal("abcd", state.Buffer.ToString());
        Assert.Equal(2, state.Cursor);
    }
}
