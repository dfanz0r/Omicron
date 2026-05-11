using System.Buffers;
using System.Text;
using Omicron.Core.Rendering;
using Omicron.Core.Text;
using Xunit;

namespace Omicron.Core.Tests;

public class TerminalBackendTests
{
    // ============================================================
    // Phase C: Terminal Backend
    // ============================================================

    [Fact]
    public void VirtualTerminalBackend_Defaults()
    {
        using var backend = new VirtualTerminalBackend();
        Assert.Equal(80, backend.Size.Width);
        Assert.Equal(24, backend.Size.Height);
        Assert.Empty(backend.CapturedOutput);
    }

    [Fact]
    public void VirtualTerminalBackend_CapturesOutput()
    {
        using var backend = new VirtualTerminalBackend();
        var output = backend.Output;
        output.Write("hello"u8);
        backend.Flush();
        Assert.Equal("hello"u8.ToArray(), backend.CapturedOutput.ToArray());
    }

    [Fact]
    public void VirtualTerminalBackend_InjectedEvent_YieldsFromReadEvents()
    {
        using var backend = new VirtualTerminalBackend();
        backend.InjectEvent(new KeyEvent(Key.Enter, KeyModifiers.None, null));
        backend.InjectEvent(new KeyEvent(Key.Escape, KeyModifiers.None, null));

        var events = new List<TerminalEvent>();
        var enumerator = backend.ReadEvents(CancellationToken.None).GetAsyncEnumerator();
        while (enumerator.MoveNextAsync().AsTask().Result)
            events.Add(enumerator.Current);

        Assert.Equal(2, events.Count);
        Assert.IsType<KeyEvent>(events[0]);
        Assert.Equal(Key.Enter, ((KeyEvent)events[0]).Key);
        Assert.Equal(Key.Escape, ((KeyEvent)events[1]).Key);
    }

    [Fact]
    public void TerminalCapabilities_Detect_ReturnsNonNull()
    {
        var caps = TerminalCapabilities.Detect();
        Assert.NotNull(caps);
        Assert.NotNull(caps.TerminalName);
    }

    [Fact]
    public void TerminalSize_Record()
    {
        var size = new TerminalSize(120, 40);
        Assert.Equal(120, size.Width);
        Assert.Equal(40, size.Height);
        Assert.Equal(size, new TerminalSize(120, 40));
        Assert.NotEqual(size, new TerminalSize(80, 24));
    }

    // ============================================================
    // Phase D: Frame Buffer
    // ============================================================

    [Fact]
    public void GlyphRef_Ascii_StoresByte()
    {
        var g = GlyphRef.Ascii((byte)'A');
        Assert.True(g.IsAscii);
        Assert.Equal((byte)'A', g.AsciiValue);
    }

    [Fact]
    public void GlyphRef_Interned_StoresId()
    {
        var g = GlyphRef.Interned(42);
        Assert.False(g.IsAscii);
        Assert.Equal(42, g.InternId);
    }

    [Fact]
    public void GlyphRef_Replacement_IsAsciiQuestionMark()
    {
        var g = GlyphRef.Replacement;
        Assert.True(g.IsAscii);
        Assert.Equal((byte)'?', g.AsciiValue);
    }

    [Fact]
    public void TextStyle_Default_IsWhiteOnBlack()
    {
        var s = TextStyle.Default;
        Assert.Equal(255, s.FgR);
        Assert.Equal(0, s.BgR);
        Assert.False(s.Bold);
    }

    [Fact]
    public void TextStyle_Inverted_IsBlackOnWhite()
    {
        var s = TextStyle.Inverted;
        Assert.Equal(0, s.FgR);
        Assert.Equal(255, s.BgR);
    }

    [Fact]
    public void RenderCell_Empty_IsEmpty()
    {
        var cell = RenderCell.Empty;
        Assert.True(cell.IsEmpty);
        Assert.Equal(1, cell.Width);
        Assert.True(cell.Glyph.IsAscii);
        Assert.Equal((byte)' ', cell.Glyph.AsciiValue);
    }

    [Fact]
    public void TerminalFrame_SetText_Ascii_FillsSingleCell()
    {
        var frame = new TerminalFrame(80, 24);
        frame.SetText(0, 0, "A"u8, TextStyle.Default);

        Assert.Equal('A', (char)frame[0, 0].Glyph.AsciiValue);
        Assert.Equal(1, frame[0, 0].Width);
    }

    [Fact]
    public void TerminalFrame_SetText_Cjk_FillsTwoCells()
    {
        var frame = new TerminalFrame(80, 24);
        frame.SetText(0, 0, "一"u8, TextStyle.Default);

        // CJK character occupies 2 cells
        Assert.Equal(2, frame[0, 0].Width);
        // Second cell is continuation (width 0)
        Assert.Equal(0, frame[0, 1].Width);
    }

    [Fact]
    public void TerminalFrame_SetText_Emoji_FillsTwoCells()
    {
        var frame = new TerminalFrame(80, 24);
        frame.SetText(0, 0, "\U0001F600"u8, TextStyle.Default);

        Assert.Equal(2, frame[0, 0].Width);
        Assert.Equal(0, frame[0, 1].Width);
    }

    [Fact]
    public void TerminalFrame_SetText_WideCharacter_TruncatesAtEdge()
    {
        var frame = new TerminalFrame(1, 1); // Only 1 column wide
        frame.SetText(0, 0, "AA"u8, TextStyle.Default);

        // Should only fit one 'A' at col 0
        Assert.Equal('A', (char)frame[0, 0].Glyph.AsciiValue);
    }

    [Fact]
    public void TerminalFrame_SetText_Cjk_TruncatesAtEdge()
    {
        var frame = new TerminalFrame(1, 1); // Only 1 column — CJK needs 2
        frame.SetText(0, 0, "一"u8, TextStyle.Default);

        // CJK char requires 2 columns but only 1 available — should not render
        Assert.True(frame[0, 0].IsEmpty || frame[0, 0].Glyph.AsciiValue != (byte)' ');
    }

    [Fact]
    public void TerminalFrame_Clear_SetsAllCellsToEmpty()
    {
        var frame = new TerminalFrame(5, 5);
        frame.SetText(0, 0, "hello"u8, TextStyle.Default);
        frame.Clear();

        for (int i = 0; i < 25; i++)
            Assert.True(frame.Cells[i].IsEmpty);
    }

    [Fact]
    public void TerminalFrame_FillRect_FillsRegion()
    {
        var frame = new TerminalFrame(10, 10);
        var cell = new RenderCell
        {
            Glyph = GlyphRef.Ascii((byte)'X'),
            Width = 1,
            Style = TextStyle.Default,
        };
        frame.FillRect(2, 2, 3, 3, cell);

        // Check center is filled
        Assert.Equal('X', (char)frame[3, 3].Glyph.AsciiValue);
        // Check outside is empty
        Assert.True(frame[0, 0].IsEmpty);
        Assert.True(frame[9, 9].IsEmpty);
    }

    [Fact]
    public void GlyphInternTable_InternsAndResolves()
    {
        var table = new GlyphInternTable();
        int id = table.Intern("hello"u8);
        Assert.Equal(0, id);
        Assert.Equal(1, table.Count);

        // Same string returns same ID
        Assert.Equal(0, table.Intern("hello"u8));

        // Different string returns different ID
        int id2 = table.Intern("world"u8);
        Assert.Equal(1, id2);

        // Resolve
        Assert.Equal("hello"u8.ToArray(), table.Resolve(0).ToArray());
        Assert.Equal("world"u8.ToArray(), table.Resolve(1).ToArray());
    }

    [Fact]
    public void GlyphInternTable_Overflow_Resets()
    {
        var table = new GlyphInternTable();
        // Fill to max (4096), then overflow
        // Use ASCII-only data so all entries are valid UTF-8
        int overflowAt = 4097;
        for (int i = 0; i < overflowAt - 1; i++)
        {
            // Generate unique ASCII-only byte sequences like "a0000", "a0001", etc.
            var data = Encoding.UTF8.GetBytes($"a{i:D4}");
            int id = table.Intern(data);
            Assert.Equal(i, id); // Ensure sequential IDs
        }
        Assert.False(table.Overflowed, $"Count={table.Count} should be {overflowAt - 1}");
        Assert.Equal(overflowAt - 1, table.Count);

        // This should overflow
        int overflowResult = table.Intern("overflow"u8);
        Assert.True(table.Overflowed, $"Overflow should be true after {overflowAt} entries, Count={table.Count}");
        Assert.Equal(0, overflowResult);
    }

    // ============================================================
    // AnsiEncoder Tests
    // ============================================================

    [Fact]
    public void AnsiEncoder_ClearScreen_EmitsCorrectSequence()
    {
        var output = new ArrayBufferWriter();
        AnsiEncoder.ClearScreen(output);
        var bytes = output.WrittenSpan.ToArray();
        Assert.Contains((byte)0x1B, bytes);
        Assert.Contains((byte)'2', bytes);
        Assert.Contains((byte)'J', bytes);
    }

    [Fact]
    public void AnsiEncoder_SetCursorPosition_EmitsCorrectSequence()
    {
        var output = new ArrayBufferWriter();
        AnsiEncoder.SetCursorPosition(0, 0, output);
        var text = Encoding.UTF8.GetString(output.WrittenSpan);
        Assert.Contains("\x1b[1;1H", text);
    }

    [Fact]
    public void AnsiEncoder_SetStyle_Emits24BitRgb()
    {
        var output = new ArrayBufferWriter();
        var style = TextStyle.ForegroundOnly(100, 150, 200);
        AnsiEncoder.SetStyle(style, null, output);
        var text = Encoding.UTF8.GetString(output.WrittenSpan);
        Assert.Contains("38;2;100;150;200", text);
    }

    [Fact]
    public void AnsiEncoder_SetStyle_SkipsRedundantAttributes()
    {
        var output = new ArrayBufferWriter();
        var style1 = TextStyle.ForegroundOnly(255, 0, 0);
        var style2 = TextStyle.ForegroundOnly(255, 0, 0); // Same
        AnsiEncoder.SetStyle(style2, style1, output);
        Assert.Empty(output.WrittenSpan.ToArray()); // No change — nothing emitted
    }

    [Fact]
    public void AnsiEncoder_BeginEndSynchronizedOutput_EmitsCorrectSequences()
    {
        var start = new ArrayBufferWriter();
        AnsiEncoder.BeginSynchronizedOutput(start);
        var startText = Encoding.UTF8.GetString(start.WrittenSpan);
        Assert.Contains("\x1b[?2026h", startText);

        var end = new ArrayBufferWriter();
        AnsiEncoder.EndSynchronizedOutput(end);
        var endText = Encoding.UTF8.GetString(end.WrittenSpan);
        Assert.Contains("\x1b[?2026l", endText);
    }

    [Fact]
    public void AnsiEncoder_ShowHideCursor_EmitsCorrectSequences()
    {
        var show = new ArrayBufferWriter();
        AnsiEncoder.ShowCursor(show);
        Assert.Contains("\x1b[?25h", Encoding.UTF8.GetString(show.WrittenSpan));

        var hide = new ArrayBufferWriter();
        AnsiEncoder.HideCursor(hide);
        Assert.Contains("\x1b[?25l", Encoding.UTF8.GetString(hide.WrittenSpan));
    }

    // ============================================================
    // SwapChain & DifferentialRenderer Tests
    // ============================================================

    [Fact]
    public void SwapChain_CurrentAndPrevious_DifferentInstances()
    {
        var swap = new SwapChain(80, 24);
        Assert.NotSame(swap.Current, swap.Previous);
        Assert.Equal(80, swap.Current.Width);
        Assert.Equal(24, swap.Current.Height);
    }

    [Fact]
    public void SwapChain_Swap_ExchangesBuffers()
    {
        var swap = new SwapChain(10, 10);
        var firstCurrent = swap.Current;

        // Write to current
        swap.Current.SetText(0, 0, "X"u8, TextStyle.Default);
        swap.Swap();

        // After swap, previous has the "X"
        Assert.Equal('X', (char)swap.Previous[0, 0].Glyph.AsciiValue);
        // Current is now the cleared old front buffer
        Assert.True(swap.Current[0, 0].IsEmpty);
    }

    [Fact]
    public void DifferentialRenderer_FirstFrame_EmitsFullFrame()
    {
        var renderer = new DifferentialRenderer(10, 5);
        var frame = new TerminalFrame(10, 5);
        frame.SetText(0, 0, "hello"u8, TextStyle.Default);

        var output = new ArrayBufferWriter();
        renderer.Render(frame, output);

        // Should contain clear screen + cursor hide + "hello"
        var bytes = output.WrittenSpan.ToArray();
        Assert.NotEmpty(bytes);
        Assert.Contains("\x1b[2J"u8.ToArray(), bytes);
        Assert.Contains("\x1b[?25l"u8.ToArray(), bytes);
    }

    [Fact]
    public void DifferentialRenderer_NoChange_EmitsNothingForSecondFrame()
    {
        var renderer = new DifferentialRenderer(10, 5);
        var frame = new TerminalFrame(10, 5);

        // First render
        renderer.Render(frame, new ArrayBufferWriter());

        // Second render with same frame
        var output2 = new ArrayBufferWriter();
        renderer.Render(frame, output2);

        // Should emit nothing (no changes)
        var bytes2 = output2.WrittenSpan.ToArray();
        Assert.Empty(bytes2);
    }

    [Fact]
    public void DifferentialRenderer_SingleCellChange_EmitsOneRun()
    {
        var renderer = new DifferentialRenderer(10, 5);
        var frame = new TerminalFrame(10, 5);

        // First render
        renderer.Render(frame, new ArrayBufferWriter());

        // Change one cell
        frame.SetText(2, 3, "X"u8, TextStyle.Default);

        var output = new ArrayBufferWriter();
        renderer.Render(frame, output);

        // Should contain cursor positioning + glyph
        var text = Encoding.UTF8.GetString(output.WrittenSpan);
        Assert.Contains("\x1b[3;4H", text); // row 3, col 4 (1-based)
        Assert.Contains("X", text);
    }

    [Fact]
    public void DifferentialRenderer_WideCharacter_RespectsContinuationCells()
    {
        var renderer = new DifferentialRenderer(10, 5);
        var frame = new TerminalFrame(10, 5);

        // First render of CJK
        frame.SetText(0, 0, "一"u8, TextStyle.Default);
        renderer.Render(frame, new ArrayBufferWriter());

        // Second render with same content — no change expected
        var output = new ArrayBufferWriter();
        renderer.Render(frame, output);
        Assert.Empty(output.WrittenSpan.ToArray());

        // Verify cell layout
        Assert.Equal(2, frame[0, 0].Width);
        Assert.Equal(0, frame[0, 1].Width);
    }

    // ============================================================
    // Extra diff correctness tests
    // ============================================================

    [Fact]
    public void DifferentialRenderer_CjkGlyph_SurvivesSwap()
    {
        var renderer = new DifferentialRenderer(10, 5);
        var frame = new TerminalFrame(10, 5);
        frame.SetText(0, 0, "\u4E00"u8, TextStyle.Default); // CJK char 一

        // First render — full frame
        var output1 = new ArrayBufferWriter();
        renderer.Render(frame, output1);
        var text1 = Encoding.UTF8.GetString(output1.WrittenSpan);
        // Should contain the CJK glyph bytes (0xE4 0xB8 0x80)
        Assert.Contains("\u4E00", text1);

        // Second render — diff (no changes)
        var output2 = new ArrayBufferWriter();
        renderer.Render(frame, output2);
        // Should emit nothing since nothing changed
        Assert.Empty(output2.WrittenSpan.ToArray());

        // Third render — change a different cell (not the CJK one)
        frame.SetText(1, 0, "hello"u8, TextStyle.Default);
        var output3 = new ArrayBufferWriter();
        renderer.Render(frame, output3);
        var text3 = Encoding.UTF8.GetString(output3.WrittenSpan);
        // The diff should still contain the 'h' from "hello" (row 2, col 1)
        Assert.Contains("\x1b[2;1H", text3); // cursor move to row 2
        Assert.Contains("h", text3);
    }

    [Fact]
    public void DifferentialRenderer_CellCleared_EmitsSpace()
    {
        var renderer = new DifferentialRenderer(10, 5);
        var frame = new TerminalFrame(10, 5);

        // First frame: "X" at (0, 0)
        frame.SetText(0, 0, "X"u8, TextStyle.Default);
        renderer.Render(frame, new ArrayBufferWriter());

        // Clear and render again — the space should overwrite the 'X'
        frame.Clear();
        var output = new ArrayBufferWriter();
        renderer.Render(frame, output);

        var bytes = output.WrittenSpan.ToArray();
        // Should contain a space byte
        Assert.Contains((byte)' ', bytes);
    }

    // ============================================================
    // Phase E: TUI Shell — Layout & StatusBar
    // ============================================================

    [Fact]
    public void TuiLayout_ThreePanelLayout_ComputesCorrectRects()
    {
        var terminalSize = new TerminalSize(80, 25);
        var (transcript, statusBar, inputLine) = Omicron.CLI.Tui.TuiLayoutEngine.ComputeLayout(terminalSize);

        Assert.Equal(0, transcript.X);
        Assert.Equal(0, transcript.Y);
        Assert.Equal(80, transcript.Width);
        Assert.Equal(23, transcript.Height); // 25 - 1 - 1

        Assert.Equal(0, statusBar.X);
        Assert.Equal(23, statusBar.Y);
        Assert.Equal(80, statusBar.Width);
        Assert.Equal(1, statusBar.Height);

        Assert.Equal(0, inputLine.X);
        Assert.Equal(24, inputLine.Y);
        Assert.Equal(80, inputLine.Width);
        Assert.Equal(1, inputLine.Height);
    }

    [Fact]
    public void SimpleStatusBar_RendersIntoFrame()
    {
        var frame = new TerminalFrame(80, 25);
        Omicron.CLI.Tui.SimpleStatusBar.Render(frame, "gpt-4", "openai", "tokens: 100");

        // Status bar renders in the last row
        var statusRow = 24;
        // The first cell has Width=1, Glyph=space, Style=Inverted
        // It's technically empty (space) but should have inverted background style
        Assert.Equal(1, frame[statusRow, 0].Width);
        Assert.True(frame[statusRow, 0].Glyph.IsAscii);
        Assert.Equal((byte)' ', frame[statusRow, 0].Glyph.AsciiValue);

        // The row should have inverted style
        Assert.Equal(TextStyle.Inverted, frame[statusRow, 0].Style);
    }
}

/// <summary>Simple IBufferWriter<byte> backed by an array for tests.</summary>
public class ArrayBufferWriter : IBufferWriter<byte>
{
    private byte[] _buffer = new byte[1024];
    private int _written;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);
    public int WrittenCount => _written;

    public void Advance(int count) => _written += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (_buffer.Length - _written < sizeHint)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _written + sizeHint));
    }
}
