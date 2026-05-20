using System.Text;
using Omicron.CLI.Tui;
using Omicron.Core.Content;
using Omicron.Core.Events;
using Omicron.Core.Rendering;
using Omicron.Core.Rendering.Layout;
using Omicron.Core.Rendering.Transcript;
using Xunit;

namespace Omicron.Core.Tests;

public class TranscriptViewportTests
{
    // ============================================================
    // Phase A: TranscriptStore
    // ============================================================

    [Fact]
    public void TranscriptStore_AppendUserMessage_CreatesBlock()
    {
        var store = new TranscriptStore();
        UserMessageBlock block = store.AppendUserMessage("hello"u8);
        Assert.NotNull(block);
        Assert.Equal(5, block.ByteLength);
        Assert.Single(store.Blocks);
    }

    [Fact]
    public void TranscriptStore_AppendAssistantDelta_AppendsToExistingBlock()
    {
        var store = new TranscriptStore();
        store.AppendAssistantDelta("hello"u8);
        store.AppendAssistantDelta(" world"u8);

        Assert.Single(store.Blocks);
        AssistantMessageBlock block = Assert.IsType<AssistantMessageBlock>(store.Blocks[0]);
        Assert.Equal(11, block.ByteLength);
        Assert.True(block.IsStreaming);
    }

    [Fact]
    public void TranscriptStore_AppendAssistantDelta_AfterCompletion_CreatesNewBlock()
    {
        var store = new TranscriptStore();
        store.AppendAssistantDelta("first"u8);
        store.CompleteLastAssistantBlock();
        store.AppendAssistantDelta("second"u8);

        Assert.Equal(2, store.Blocks.Count);
        AssistantMessageBlock block1 = Assert.IsType<AssistantMessageBlock>(store.Blocks[0]);
        Assert.False(block1.IsStreaming);
        AssistantMessageBlock block2 = Assert.IsType<AssistantMessageBlock>(store.Blocks[1]);
        Assert.True(block2.IsStreaming);
    }

    [Fact]
    public void TranscriptStore_CompleteLastAssistantBlock_SetsIsStreamingFalse()
    {
        var store = new TranscriptStore();
        store.AppendAssistantDelta("hello"u8);
        store.CompleteLastAssistantBlock();

        AssistantMessageBlock block = Assert.IsType<AssistantMessageBlock>(store.Blocks[0]);
        Assert.False(block.IsStreaming);
    }

    [Fact]
    public void TranscriptStore_Clear_RemovesAllBlocks()
    {
        var store = new TranscriptStore();
        store.AppendUserMessage("hello"u8);
        store.AppendAssistantDelta("world"u8);
        store.Clear();

        Assert.Empty(store.Blocks);
    }

    [Fact]
    public void TranscriptStore_BlockPositions_AreMonotonic()
    {
        var store = new TranscriptStore();
        UserMessageBlock user = store.AppendUserMessage("hello\n"u8);
        AssistantMessageBlock assistant = store.AppendAssistantDelta("world\n"u8);

        Assert.True(assistant.Position.GlobalByteOffset > user.Position.GlobalByteOffset);
    }

    [Fact]
    public void TranscriptStore_AppendToolCall_CreatesBlock()
    {
        var store = new TranscriptStore();
        ToolCallBlock block = store.AppendToolCall("read_path");
        Assert.NotNull(block);
        Assert.Equal("read_path", block.ToolName);
        Assert.Equal(ToolCallState.Running, block.State);
    }

    [Fact]
    public void TranscriptStore_UpdateToolCall_ChangesState()
    {
        var store = new TranscriptStore();
        ToolCallBlock block = store.AppendToolCall("read_path");
        store.UpdateToolCall(block.Id, ToolCallState.Completed, "hello"u8);

        // Get the updated block
        var updated = (ToolCallBlock)store.Blocks[^1];
        Assert.Equal(ToolCallState.Completed, updated.State);
    }

    [Fact]
    public void TranscriptStore_UpdateToolCall_PutsOutputOnSeparateLine()
    {
        var store = new TranscriptStore();
        ToolCallBlock block = store.AppendToolCall("read_file_hashlines");

        store.UpdateToolCall(block.Id, ToolCallState.Completed, "[FILE] test.cs"u8);

        var updated = (ToolCallBlock)store.Blocks[^1];
        string rendered = Encoding.UTF8.GetString(
            store.Text.Slice(updated.Position.GlobalByteOffset, updated.ByteLength).Span);
        Assert.Equal("[tool: read_file_hashlines]\n[FILE] test.cs", rendered);
    }

    [Fact]
    public void TranscriptStore_UpdateToolCall_DoesNotDoublePrefixExistingNewline()
    {
        var store = new TranscriptStore();
        ToolCallBlock block = store.AppendToolCall("read_file_hashlines");

        store.UpdateToolCall(block.Id, ToolCallState.Completed, "\n[FILE] test.cs"u8);

        var updated = (ToolCallBlock)store.Blocks[^1];
        string rendered = Encoding.UTF8.GetString(
            store.Text.Slice(updated.Position.GlobalByteOffset, updated.ByteLength).Span);
        Assert.Equal("[tool: read_file_hashlines]\n[FILE] test.cs", rendered);
    }

    [Fact]
    public void TranscriptViewport_DuplicateToolCompletion_DoesNotDuplicateHashlineOutput()
    {
        var widget = new TranscriptViewportWidget();
        widget.Arrange(new Rect(0, 0, 120, 20));
        var sessionId = new SessionId(Guid.NewGuid());
        var callId = new ToolCallId("call-hashlines");
        byte[] result = "[FILE] test.cs  (1 lines, hashline anchors)\n\n1ab|class C {}"u8.ToArray();

        widget.UpdateFromEvent(new ToolInvocationStartedEvent(EventEnvelope.ForSession(sessionId),
            callId,
            "read_file_hashlines",
            new Dictionary<string, object?>
            {
                ["path"] = "test.cs"
            }));
        widget.UpdateFromEvent(new ToolInvocationCompletedEvent(EventEnvelope.ForSession(sessionId),
            callId,
            "read_file_hashlines",
            result,
            false));
        widget.UpdateFromEvent(new ToolInvocationCompletedEvent(EventEnvelope.ForSession(sessionId),
            callId,
            "read_file_hashlines",
            result,
            false));

        string rendered = Encoding.UTF8.GetString(widget.Store.Text.ToArray());
        Assert.Equal(1, CountOccurrences(rendered, "[FILE] test.cs"));
        Assert.Equal(1, CountOccurrences(rendered, "1ab|class C {}"));
    }

    [Fact]
    public void TranscriptViewport_StaleDuplicateCompletion_DoesNotAttachToLaterSameNamedToolCall()
    {
        var widget = new TranscriptViewportWidget();
        widget.Arrange(new Rect(0, 0, 120, 20));
        var sessionId = new SessionId(Guid.NewGuid());
        var firstCallId = new ToolCallId("call-read-1");
        var secondCallId = new ToolCallId("call-read-2");
        byte[] firstResult = "[FILE] a.txt  (1 lines)\n\n     1| first"u8.ToArray();

        widget.UpdateFromEvent(new ToolInvocationStartedEvent(EventEnvelope.ForSession(sessionId),
            firstCallId,
            "read_path",
            new Dictionary<string, object?>
            {
                ["path"] = "a.txt"
            }));
        widget.UpdateFromEvent(new ToolInvocationCompletedEvent(EventEnvelope.ForSession(sessionId),
            firstCallId,
            "read_path",
            firstResult,
            false));
        widget.UpdateFromEvent(new ToolInvocationStartedEvent(EventEnvelope.ForSession(sessionId),
            secondCallId,
            "read_path",
            new Dictionary<string, object?>
            {
                ["path"] = "b.txt"
            }));

        // A duplicated completion for the first call must not be matched by
        // tool name and appended to the still-running second read_path call.
        widget.UpdateFromEvent(new ToolInvocationCompletedEvent(EventEnvelope.ForSession(sessionId),
            firstCallId,
            "read_path",
            firstResult,
            false));

        string rendered = Encoding.UTF8.GetString(widget.Store.Text.ToArray());
        Assert.Equal(1, CountOccurrences(rendered, "[FILE] a.txt"));
        Assert.Equal(1, CountOccurrences(rendered, "     1| first"));
    }

    [Fact]
    public void TranscriptStore_Clear_DoesNotDisposeOrMutateBorrowedContentBlocks()
    {
        var store = new TranscriptStore();
        ToolCallBlock block = store.AppendToolCall("read_path", "call-1");
        var blocks = new List<IContentBlock>
        {
            new PlainTextContentBlock("hello")
        };

        store.UpdateToolCall(block.Id, ToolCallState.Completed, "hello"u8, blocks);
        store.Clear();

        Assert.Single(blocks);
        Assert.Equal("hello", blocks[0].Text);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    // ============================================================
    // Phase B: TranscriptLayoutCache
    // ============================================================

    [Fact]
    public void LayoutCache_SingleLine_FitsWidth_OneWrappedLine()
    {
        var store = new TranscriptStore();
        store.AppendAssistantDelta("hello"u8);
        store.CompleteLastAssistantBlock();

        var cache = new TranscriptLayoutCache();
        cache.ReflowForWidth(80, store);

        Assert.Equal(1, cache.TotalWrappedRows);
    }

    [Fact]
    public void LayoutCache_LongLine_WrapsCorrectly()
    {
        var store = new TranscriptStore();
        string text = new('x', 200);
        store.AppendAssistantDelta(Encoding.UTF8.GetBytes(text));
        store.CompleteLastAssistantBlock();

        var cache = new TranscriptLayoutCache();
        cache.ReflowForWidth(80, store);

        // 200 chars at 80 cols = 3 wrapped rows (80 + 80 + 40)
        Assert.Equal(3, cache.TotalWrappedRows);
    }

    [Fact]
    public void LayoutCache_Resize_ChangesWrappedRows()
    {
        var store = new TranscriptStore();
        string text = new('x', 200);
        store.AppendAssistantDelta(Encoding.UTF8.GetBytes(text));
        store.CompleteLastAssistantBlock();

        var cache = new TranscriptLayoutCache();
        cache.ReflowForWidth(80, store);
        Assert.Equal(3, cache.TotalWrappedRows);

        cache.ReflowForWidth(40, store);
        Assert.Equal(5, cache.TotalWrappedRows); // 200 / 40 = 5
    }

    [Fact]
    public void LayoutCache_GetVisibleLines_ReturnsCorrectWindow()
    {
        var store = new TranscriptStore();
        store.AppendAssistantDelta("hello\nworld\nfoo\nbar"u8);
        store.CompleteLastAssistantBlock();

        var cache = new TranscriptLayoutCache();
        cache.ReflowForWidth(80, store);

        IReadOnlyList<WrappedLineInfo> lines = cache.GetVisibleLines(1, 2);
        Assert.Equal(2, lines.Count);
    }

    // ============================================================
    // Phase C: ViewportState
    // ============================================================

    [Fact]
    public void ViewportState_FollowTail_AutoScrollsToBottomOnAppend()
    {
        var vp = new ViewportState();
        Assert.True(vp.FollowTail);
        Assert.Equal(0, vp.FirstVisibleWrappedRow);

        vp.UpdateTotalRows(100);
        Assert.Equal(99, vp.FirstVisibleWrappedRow); // Auto-scrolled to bottom
    }

    [Fact]
    public void ViewportState_ScrolledUp_DoesNotYankOnAppend()
    {
        var vp = new ViewportState();
        vp.ScrollUp(10);
        Assert.False(vp.FollowTail);

        int oldTop = vp.FirstVisibleWrappedRow;
        vp.UpdateTotalRows(200);
        Assert.Equal(oldTop, vp.FirstVisibleWrappedRow); // No yank
    }

    [Fact]
    public void ViewportState_ScrolledUp_TracksUnseenLines()
    {
        var vp = new ViewportState();
        vp.UpdateTotalRows(50);
        vp.ScrollUp(5);

        // Simulate content append
        vp.OnContentAppended(100);
        Assert.Equal(50, vp.UnseenLineCount);
    }

    [Fact]
    public void ViewportState_ScrollDown_ReachesBottom_ResumesFollowTail()
    {
        var vp = new ViewportState();
        vp.UpdateTotalRows(100);
        vp.ScrollUp(10);
        Assert.False(vp.FollowTail);

        vp.ScrollToBottom(100);
        Assert.True(vp.FollowTail);
        Assert.Equal(0, vp.UnseenLineCount);
    }

    [Fact]
    public void ViewportState_ScrollToTop()
    {
        var vp = new ViewportState();
        vp.UpdateTotalRows(100);
        vp.ScrollToTop();
        Assert.Equal(0, vp.FirstVisibleWrappedRow);
        Assert.False(vp.FollowTail);
    }

    // ============================================================
    // Phase D: Widget Layout System
    // ============================================================

    [Fact]
    public void Rect_Contains_ReturnsCorrect()
    {
        var r = new Rect(5, 5, 10, 10);
        Assert.True(r.Contains(5, 5));
        Assert.True(r.Contains(14, 14));
        Assert.False(r.Contains(4, 5));
        Assert.False(r.Contains(5, 15));
    }

    [Fact]
    public void Rect_Intersect_ReturnsCorrect()
    {
        var a = new Rect(0, 0, 10, 10);
        var b = new Rect(5, 5, 10, 10);
        Rect i = a.Intersect(b);
        Assert.Equal(5, i.X);
        Assert.Equal(5, i.Y);
        Assert.Equal(5, i.Width);
        Assert.Equal(5, i.Height);
    }

    [Fact]
    public void VStack_MeasuresSumOfChildren()
    {
        var stack = new VStack();
        stack.Add(new FixedSizeWidget
        {
            Height = 3,
            Child = new NullWidget()
        });
        stack.Add(new FixedSizeWidget
        {
            Height = 2,
            Child = new NullWidget()
        });

        Size size = stack.Measure(new Size(80, 25));
        Assert.Equal(5, size.Height);
    }

    [Fact]
    public void VStack_ArrangesChildrenVertically()
    {
        var stack = new VStack();
        var child1 = new CaptureWidget();
        var child2 = new CaptureWidget();
        stack.Add(new FixedSizeWidget
        {
            Height = 3,
            Child = child1
        });
        stack.Add(new FixedSizeWidget
        {
            Height = 2,
            Child = child2
        });

        stack.Measure(new Size(80, 25));
        stack.Arrange(new Rect(0, 0, 80, 25));

        Assert.Equal(0, child1.LastBounds.Y);
        Assert.Equal(3, child1.LastBounds.Height);
        Assert.Equal(3, child2.LastBounds.Y);
        Assert.Equal(2, child2.LastBounds.Height);
    }

    [Fact]
    public void FlexSizeWidget_ReceivesRemainingSpace()
    {
        var stack = new VStack();
        var flexChild = new CaptureWidget();
        stack.Add(new FixedSizeWidget
        {
            Height = 2,
            Child = new NullWidget()
        });
        stack.Add(new FlexSizeWidget
        {
            Child = flexChild
        });

        stack.Measure(new Size(80, 25));
        stack.Arrange(new Rect(0, 0, 80, 25));

        Assert.Equal(2, flexChild.LastBounds.Y);
        Assert.Equal(23, flexChild.LastBounds.Height); // 25 - 2
    }

    [Fact]
    public void RenderContext_DrawText_ClipsToRect()
    {
        var frame = new TerminalFrame(80, 25);
        var clip = new Rect(10, 10, 5, 5);
        var ctx = new RenderContext(frame, clip, TextStyle.Default);

        // Draw at a position inside the clip rect
        ctx.DrawText(10, 10, "hello"u8, TextStyle.Default);
        Assert.NotEqual(0, frame[10, 10].Glyph.AsciiValue);

        // Draw at a position outside the clip rect (should be clipped)
        ctx.DrawText(0, 0, "world"u8, TextStyle.Default);
        Assert.True(frame[0, 0].IsEmpty);
    }

    [Fact]
    public void RenderContext_FillRect_FillsRegion()
    {
        var frame = new TerminalFrame(80, 25);
        var clip = new Rect(0, 0, 80, 25);
        var ctx = new RenderContext(frame, clip, TextStyle.Default);

        var cell = new RenderCell
        {
            Glyph = GlyphRef.Ascii((byte)'X'),
            Width = 1,
            Style = TextStyle.Default
        };
        ctx.FillRect(new Rect(5, 5, 3, 3), cell);

        Assert.Equal('X', (char)frame[6, 6].Glyph.AsciiValue);
        Assert.True(frame[0, 0].IsEmpty);
    }

    // ============================================================
    // Phase E: InputEditorWidget
    // ============================================================

    [Fact]
    public void InputEditor_Insert_AddsText()
    {
        var editor = new InputEditorWidget();
        editor.Insert("hello");
        Assert.Equal("hello", editor.Text);
        Assert.Equal(5, editor.CursorColumn);
    }

    [Fact]
    public void InputEditor_Backspace_RemovesChar()
    {
        var editor = new InputEditorWidget();
        editor.Insert("hello");
        editor.MoveLeft();
        editor.Backspace();
        Assert.Equal("helo", editor.Text);
    }

    [Fact]
    public void InputEditor_Delete_RemovesCharAtCursor()
    {
        var editor = new InputEditorWidget();
        editor.Insert("hello");
        editor.MoveHome();
        editor.Delete();
        Assert.Equal("ello", editor.Text);
    }

    [Fact]
    public void InputEditor_MoveHomeEnd()
    {
        var editor = new InputEditorWidget();
        editor.Insert("hello");
        editor.MoveHome();
        Assert.Equal(0, editor.CursorColumn);
        editor.MoveEnd();
        Assert.Equal(5, editor.CursorColumn);
    }

    [Fact]
    public void InputEditor_Submit_RaisesEvent()
    {
        var editor = new InputEditorWidget();
        string? submitted = null;
        editor.OnSubmit += text => submitted = text;

        editor.Insert("hello");
        editor.Submit();

        Assert.Equal("hello", submitted);
        Assert.Empty(editor.Text);
        Assert.Equal(0, editor.CursorColumn);
    }

    [Fact]
    public void InputEditor_HandleKey_Character_Inserts()
    {
        var editor = new InputEditorWidget();
        var ke = new KeyEvent(Key.Character, KeyModifiers.None, new Rune('A'));
        Assert.True(editor.HandleKey(ke));
        Assert.Equal("A", editor.Text);
    }

    // ============================================================
    // Phase E: StatusBarWidget
    // ============================================================

    [Fact]
    public void StatusBarWidget_Render_ShowsModelName()
    {
        var frame = new TerminalFrame(80, 25);
        var widget = new StatusBarWidget
        {
            ModelName = "gpt-4",
            ProviderName = "openai",
            StatusText = "ready"
        };

        widget.Arrange(new Rect(0, 0, 80, 1));
        var ctx = new RenderContext(frame, new Rect(0, 0, 80, 1), TextStyle.Default);
        widget.Render(ctx);

        // Status bar uses gradient background (dark amber at left edge)
        RenderCell cell = frame[0, 0];
        Assert.True(cell.Style.BgR > 0 || cell.Style.BgG > 0 || cell.Style.BgB > 0,
            "Status bar should have non-black background (gradient)");
        Assert.Equal(1, cell.Width);

        // Text foreground should be pure black (0,0,0) for consistent rendering
        // across VS Code and Windows Terminal.  Dark brown was auto-adjusted to
        // white by VS Code's minimum-contrast logic.
        Assert.True(cell.Style.FgR == 0 && cell.Style.FgG == 0 && cell.Style.FgB == 0,
            $"Status bar text should be pure black (got {cell.Style.FgR},{cell.Style.FgG},{cell.Style.FgB})");
    }

    [Fact]
    public void StatusBarWidget_TextForeground_IsNotWhite()
    {
        // Regression: every cell in the status bar should share the full-bar
        // text gradient. No cell should retain the white foreground from an
        // old Fill() call.
        var frame = new TerminalFrame(80, 1);
        var widget = new StatusBarWidget
        {
            ModelName = "gpt-4",
            ProviderName = "",
            StatusText = ""
        };

        widget.Arrange(new Rect(0, 0, 80, 1));
        var ctx = new RenderContext(frame, new Rect(0, 0, 80, 1), TextStyle.Default);
        widget.Render(ctx);

        // Every cell must be pure black.  VS Code auto-adjusts low-contrast
        // colours to white; pure black on the amber background avoids this.
        for (int col = 0; col < 80; col++)
        {
            RenderCell cell = frame[0, col];
            Assert.True(cell.Style.FgR == 0 && cell.Style.FgG == 0 && cell.Style.FgB == 0,
                $"Cell at col {col} should be pure black (got {cell.Style.FgR},{cell.Style.FgG},{cell.Style.FgB})");
        }

        RenderCell left = frame[0, 0];
        RenderCell right = frame[0, 79];
        Assert.True(left.Style.FgR == 0 && left.Style.FgG == 0 && left.Style.FgB == 0,
            $"Left edge should be black (got {left.Style.FgR},{left.Style.FgG},{left.Style.FgB})");
        Assert.True(right.Style.FgR == 0 && right.Style.FgG == 0 && right.Style.FgB == 0,
            $"Right edge should be black (got {right.Style.FgR},{right.Style.FgG},{right.Style.FgB})");
    }

    [Fact]
    public void StatusBarWidget_DiffRenderer_EmitsCorrectAnsi()
    {
        // Ensure the differential renderer emits the correct ANSI sequences
        // for the status bar text — every text cell should have the text gradient fg.
        var renderer = new DifferentialRenderer(80, 1);
        var widget = new StatusBarWidget
        {
            ModelName = "gpt-4",
            ProviderName = "",
            StatusText = ""
        };

        // First render (empty frame → establishes baseline)
        renderer.Render(new TerminalFrame(80, 1), new ArrayBufferWriter());

        // Second render with status bar content
        var curFrame = new TerminalFrame(80, 1);
        widget.Arrange(new Rect(0, 0, 80, 1));
        var ctx = new RenderContext(curFrame, new Rect(0, 0, 80, 1), TextStyle.Default);
        widget.Render(ctx);

        var output = new ArrayBufferWriter();
        renderer.Render(curFrame, output);

        string text = Encoding.UTF8.GetString(output.WrittenSpan);

        // Text is pure black (0, 0, 0) — no cream, no white.
        Assert.Contains("38;2;0;0;0", text);
        Assert.DoesNotContain("38;2;255;255;255", text);
    }

    [Fact]
    public void StatusBarWidget_FullFrame_NoWhiteForeground()
    {
        // The full-frame ANSI output must not contain white foreground
        // (255,255,255) — every cell should use the text gradient.
        var renderer = new DifferentialRenderer(80, 1);
        var widget = new StatusBarWidget
        {
            ModelName = "OpenAI: GPT-5.4 Mini",
            ProviderName = "openai",
            StatusText = "ready"
        };

        renderer.Render(new TerminalFrame(80, 1), new ArrayBufferWriter());

        var curFrame = new TerminalFrame(80, 1);
        widget.Arrange(new Rect(0, 0, 80, 1));
        var ctx = new RenderContext(curFrame, new Rect(0, 0, 80, 1), TextStyle.Default);
        widget.Render(ctx);

        var output = new ArrayBufferWriter();
        renderer.Render(curFrame, output);

        string text = Encoding.UTF8.GetString(output.WrittenSpan);

        // No white foreground anywhere in the bar
        Assert.DoesNotContain("38;2;255;255;255", text);

        // Text is pure black (0, 0, 0) — no cream, no white.
        Assert.Contains("38;2;0;0;0", text);
    }

    [Fact]
    public void Scrollbar_NotVisible_WhenContentFits()
    {
        var widget = new TranscriptViewportWidget();
        widget.Arrange(new Rect(0, 0, 80, 10));

        // Add a small amount of content that fits entirely
        widget.Store.AppendNotice("Hello world");
        widget.Layout.ReflowForWidth(80, widget.Store);
        widget.Viewport.UpdateTotalRows(widget.Layout.TotalWrappedRows, 10);

        var frame = new TerminalFrame(80, 10);
        var ctx = new RenderContext(frame, new Rect(0, 0, 80, 10), TextStyle.Default);
        widget.Render(ctx);

        // Rightmost column should NOT have scrollbar track color (50,45,38)
        RenderCell rightCell = frame[0, 79];
        Assert.False(rightCell.Style.BgR == 50 && rightCell.Style.BgG == 45 && rightCell.Style.BgB == 38,
            "Scrollbar should not appear when content fits viewport");
    }

    [Fact]
    public void Scrollbar_Visible_AfterScroll()
    {
        var widget = new TranscriptViewportWidget();
        widget.Arrange(new Rect(0, 0, 80, 5));

        // Add enough content to overflow the viewport
        for (int i = 0; i < 50; i++)
        {
            widget.Store.AppendNotice($"Line {i}: " + new string('x', 70));
        }

        widget.Layout.ReflowForWidth(80, widget.Store);
        widget.Viewport.UpdateTotalRows(widget.Layout.TotalWrappedRows, 5);

        // Scroll up to trigger visibility
        widget.ScrollUp(5);

        var frame = new TerminalFrame(80, 5);
        var ctx = new RenderContext(frame, new Rect(0, 0, 80, 5), TextStyle.Default);
        widget.Render(ctx);

        // Rightmost column should have scrollbar track or thumb color
        RenderCell rightCell = frame[0, 79];
        bool isTrack =
            rightCell.Style.BgR == 50 && rightCell.Style.BgG == 45 && rightCell.Style.BgB == 38;
        bool isThumb =
            rightCell.Style.BgR == 140 && rightCell.Style.BgG == 115 && rightCell.Style.BgB == 60;
        Assert.True(isTrack || isThumb,
            $"Scrollbar should be visible after scroll (got bg {rightCell.Style.BgR},{rightCell.Style.BgG},{rightCell.Style.BgB})");
    }

    [Fact]
    public void Scrollbar_ThumbPosition_NearTop()
    {
        var widget = new TranscriptViewportWidget();
        widget.Arrange(new Rect(0, 0, 80, 10));

        for (int i = 0; i < 100; i++)
        {
            widget.Store.AppendNotice($"Line {i}: " + new string('x', 70));
        }

        widget.Layout.ReflowForWidth(80, widget.Store);
        widget.Viewport.UpdateTotalRows(widget.Layout.TotalWrappedRows, 10);

        // Scroll to top
        widget.ScrollToTop();

        var frame = new TerminalFrame(80, 10);
        var ctx = new RenderContext(frame, new Rect(0, 0, 80, 10), TextStyle.Default);
        widget.Render(ctx);

        // Thumb should be near the top (rows 0-2)
        int thumbRow = -1;
        for (int row = 0; row < 10; row++)
        {
            RenderCell cell = frame[row, 79];
            if (cell.Style.BgR == 140 && cell.Style.BgG == 115 && cell.Style.BgB == 60)
            {
                thumbRow = row;
                break;
            }
        }

        Assert.True(thumbRow >= 0 && thumbRow <= 2,
            $"Thumb should be near top when scrolled to top (found at row {thumbRow})");
    }

    [Fact]
    public void Scrollbar_ThumbPosition_NearBottom()
    {
        var widget = new TranscriptViewportWidget();
        widget.Arrange(new Rect(0, 0, 80, 10));

        for (int i = 0; i < 100; i++)
        {
            widget.Store.AppendNotice($"Line {i}: " + new string('x', 70));
        }

        widget.Layout.ReflowForWidth(80, widget.Store);
        widget.Viewport.UpdateTotalRows(widget.Layout.TotalWrappedRows, 10);

        // Scroll to bottom
        widget.ScrollToBottom();

        var frame = new TerminalFrame(80, 10);
        var ctx = new RenderContext(frame, new Rect(0, 0, 80, 10), TextStyle.Default);
        widget.Render(ctx);

        // Thumb should be near the bottom (rows 7-9)
        int thumbRow = -1;
        for (int row = 0; row < 10; row++)
        {
            RenderCell cell = frame[row, 79];
            if (cell.Style.BgR == 140 && cell.Style.BgG == 115 && cell.Style.BgB == 60)
            {
                thumbRow = row;
                break;
            }
        }

        Assert.True(thumbRow >= 7 && thumbRow <= 9,
            $"Thumb should be near bottom when scrolled to bottom (found at row {thumbRow})");
    }

    // ============================================================
    // Helper widgets
    // ============================================================

    private sealed class NullWidget : ITuiWidget
    {
        public Size Measure(Size available)
        {
            return new Size(available.Width, 0);
        }

        public void Arrange(Rect bounds) { }

        public void Render(RenderContext context) { }
    }

    private sealed class CaptureWidget : ITuiWidget
    {
        public Rect LastBounds { get; private set; }

        public Size Measure(Size available)
        {
            return new Size(available.Width, 0);
        }

        public void Arrange(Rect bounds)
        {
            LastBounds = bounds;
        }

        public void Render(RenderContext context) { }
    }
}
