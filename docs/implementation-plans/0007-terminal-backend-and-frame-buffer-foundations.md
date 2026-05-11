# Implementation Plan 0007: Terminal Backend and Frame Buffer Foundations

Status: Proposed
Target: Build the lowest layer of Omicron's custom terminal rendering track (Track A per RFC 0003), enabling fullscreen TUI mode.

## Purpose

Omicron is currently a console-line application. Before it can support app-owned scrollback, virtualized transcript rendering, streaming updates, and rich content blocks, it needs a terminal backend, frame buffer, and differential renderer. This plan establishes the **custom renderer baseline** (Track A) that the OpenTUI spike (Track B / RFC 0015) would later be benchmarked against.

Even if OpenTUI is eventually adopted, Omicron must own its terminal correctness, UTF-8 storage, cell-width math, and diff-rendering policy. This plan builds that ownership.

## Primary RFCs

- RFC 0003 — Text Storage, Unicode Layout, and Terminal Rendering
- RFC 0004 — Transcript Viewport, Scrollback, Layout, and Input (terminal backend and input sections)
- RFC 0009 — Implementation Roadmap, Testing, and Performance (Phase Set 0c, 2a)

## Reference Analysis

`docs/reports/0001-tui-renderer-codebase-analysis.md` — analyzed Spectre.TUI, XenoAtom.Terminal, XenoAtom.Terminal.UI, ConsoleEx, Termina, and others. Key adoptions:

- **Cell as struct** (Plan 7) validated over Spectre.TUI's class-based `Cell` — avoids GC pressure.
- **Ref-counted terminal scopes** (XenoAtom.Terminal) — safer than single-use lifecycle for nested mode changes.
- **Capability detection** (XenoAtom.Terminal) — needed for color level, alternate screen, mouse, clipboard.
- **Measure/Arrange layout** (Termina) — adopted for Phase E widgets.
- **SwapChain double-buffering** (Spectre.TUI) — explicit pattern for frame diffing.
- **VT input decoder** (XenoAtom.Terminal) — reference for Phase C input parsing.
- **Inline mode** — support both fullscreen (alternate screen) and inline (reserved lines) modes.

## Depends On

- Plans 1–6 (core events, content blocks, persistence, workspace transactions, model metadata, session replay)
- Plan 5 `ContentBlock` model (transcript blocks will render through this stack)
- Plan 3.1 `TextLineSplitter` (reuse or unify)

## Non-Goals

- No OpenTUI C ABI bindings (separate research spike under RFC 0015)
- No markdown parser or syntax highlighting (Plan 8)
- No virtual terminal emulator / PTY panes (Plan 11)
- No GUI frontend (Phase Set 5a)
- No remoting protocol (Phase Set 4c)
- No plugin system (Phase Set 3c)
- No sandboxing (Plan 9)
- No edit harness v2 (Plan 10)

## Guiding Principles

1. **UTF-8 is the storage canonical.** All transcript text lives as UTF-8 bytes until the final ANSI encoder.
2. **No giant managed strings on hot paths.** Transcript chunks use pooled/native-friendly buffers.
3. **Render only visible cells.** The diff renderer emits the minimum ANSI/UTF-8 bytes per frame.
4. **Terminal lifecycle is non-negotiable.** Crash or `Ctrl+C` must restore the host terminal state.
5. **Keep `Omicron.Core` UI-agnostic.** Terminal types live in a new `Omicron.Text` namespace or project; only abstractions touch core.

---

## Proposed Project Layout

This plan introduces a new project only if code volume justifies it. Start inside `Omicron.Core` under new namespaces, then split when stable:

```text
Omicron.Core/Text/
  Utf8TextStore.cs
  TextChunk.cs
  TextPosition.cs
  LogicalLineIndex.cs
  GraphemeSegmenter.cs
  CellWidthCalculator.cs
  TerminalCluster.cs

Omicron.Core/Rendering/
  ITerminalBackend.cs
  TerminalSize.cs
  TerminalEvent.cs
  SystemTerminalBackend.cs
  TerminalLifecycle.cs
  TerminalFrame.cs
  RenderCell.cs
  GlyphRef.cs
  TextStyle.cs
  DifferentialRenderer.cs
  AnsiEncoder.cs

Omicron.Core/Rendering/Widgets/
  Rect.cs
  ITuiWidget.cs
  VStack.cs
  HStack.cs
  SplitLayout.cs

Omicron.CLI/Tui/
  TuiShell.cs
  TuiLayoutEngine.cs
  SimpleStatusBar.cs
  TranscriptViewportWidget.cs
```

---

## Phase A: UTF-8 Transcript Store

### Goals
Create an append-only UTF-8 text store with byte-level indexing. This store backs the transcript viewport and all future text-heavy subsystems.

### Deliverables

#### A1. `TextChunk`
```csharp
namespace Omicron.Core.Text;

/// <summary>A single append-only UTF-8 chunk.</summary>
internal sealed class TextChunk
{
    public int Index { get; }           // stable chunk index in the store
    public long GlobalByteStart { get; } // cumulative byte offset of chunk[0]
    public int Length { get; }           // valid bytes in this chunk
    public Memory<byte> Data { get; }    // underlying buffer

    public ReadOnlySpan<byte> AsSpan() => Data.Span.Slice(0, Length);
    public ReadOnlyMemory<byte> AsMemory() => Data.Slice(0, Length);
}
```

- Chunks are ~8 KB or ~64 KB depending on profiling. Start with 8 KB.
- Use `ArrayPool<byte>.Shared.Rent(chunkSize)` for chunk backing.
- `GlobalByteStart` is the cumulative offset so `Slice` can jump directly to the right chunk.

#### A2. `TextPosition`
```csharp
namespace Omicron.Core.Text;

/// <summary>Absolute position inside the Utf8TextStore.</summary>
public readonly record struct TextPosition(long GlobalByteOffset, int ChunkIndex, int ChunkByteOffset);
```

#### A3. `Utf8TextStore`
```csharp
namespace Omicron.Core.Text;

public sealed class Utf8TextStore
{
    public long LengthBytes { get; }
    public int ChunkCount { get; }

    /// <summary>Append UTF-8 bytes and return the position of the first appended byte.</summary>
    public TextPosition Append(ReadOnlySpan<byte> utf8);

    /// <summary>Read a contiguous slice. May copy if crossing a chunk boundary.</summary>
    public ReadOnlyMemory<byte> Slice(long byteOffset, int byteLength);

    /// <summary>Get an individual chunk by index.</summary>
    public TextChunk GetChunk(int chunkIndex);

    /// <summary>Iterate all chunks.</summary>
    public IEnumerable<TextChunk> Chunks { get; }
}
```

Design rules:
- `Append` locks on a private `_appendLock` (or uses `Interlocked` for head pointer if we go lock-free later). For MVP, a `lock` is fine.
- When the current chunk is full, rent a new `ArrayPool` buffer, append it to the chunk list, and write there.
- `Slice` that spans chunks returns a copy into a pooled scratch buffer or a new small array. Document that callers should prefer chunk-wise iteration for zero-copy.
- `Slice` that fits inside one chunk returns `ReadOnlyMemory<byte>` over that chunk directly.

#### A4. `LogicalLineIndex`
Build a line index over the UTF-8 store without materializing strings:
```csharp
namespace Omicron.Core.Text;

public readonly record struct LogicalLineInfo(long ByteStart, int ByteLength, int LineIndex);

public sealed class LogicalLineIndex
{
    public IReadOnlyList<LogicalLineInfo> Lines { get; }
    public int LineCount { get; }

    /// <summary>Append a line index builder that scans new UTF-8 bytes.</summary>
    public void AppendScan(ReadOnlySpan<byte> utf8, long globalByteStart);

    /// <summary>Find the line containing a byte offset.</summary>
    public LogicalLineInfo? FindLineContaining(long byteOffset);
}
```

- Line endings: `\n` (LF), `\r\n` (CRLF), `\r` (CR alone). Treat all as line breaks.
- Do not include the line ending bytes in `ByteLength`.
- The index is append-only: when new text is appended, only scan the new bytes.

### Tests
- `Append_EmptyStore_ReturnsPositionZero`
- `Append_SingleChunk_ReturnsCorrectLength`
- `Append_MultipleChunks_CreatesNewChunkWhenFull`
- `Slice_InsideSingleChunk_ReturnsZeroCopyMemory`
- `Slice_AcrossChunks_ReturnsCopy`
- `Slice_PastEnd_Throws`
- `LineIndex_SimpleLf_SplitsCorrectly`
- `LineIndex_Crlf_SplitsCorrectly`
- `LineIndex_NoTrailingNewline_LastLineIncluded`
- `LineIndex_AppendOnly_ScansNewBytesOnly`
- `Stress_AppendOneMillionLines_NoLargeStringAllocations`

### Acceptance Criteria
- `Utf8TextStore` can append 1M lines without creating managed strings larger than 1 KB.
- `Slice` correctness verified for in-chunk, cross-chunk, and boundary cases.
- `LogicalLineIndex` correctly handles LF, CRLF, CR, and mixed endings.
- All tests pass with 0 warnings.

---

## Phase B: Grapheme and Cell-Width Model

### Goals
Implement Unicode grapheme segmentation and terminal cell-width calculation so rendering knows how many columns a byte sequence occupies.

### Deliverables

#### B1. `GraphemeSegmenter`
```csharp
namespace Omicron.Core.Text;

public static class GraphemeSegmenter
{
    /// <summary>Segment a UTF-8 span into grapheme clusters.</summary>
    public static IEnumerable<GraphemeCluster> SegmentUtf8(ReadOnlySpan<byte> utf8);
}

public readonly record struct GraphemeCluster(long ByteOffset, int ByteLength, int RuneCount);
```

Implementation approach:
- Decode UTF-8 to `System.Text.Rune` sequence using `Rune.DecodeFromUtf8`.
- Use `System.Globalization.StringInfo` as a managed baseline for grapheme boundaries, or implement a simple Unicode boundary table for the most common cases.
- **MVP decision:** Start with `Rune` iteration + a minimal hardcoded table for:
  - zero-width joiner (U+200D) sequences (emoji ZWJ such as skin tones)
  - combining marks (General Category Mc, Me, Mn)
  - regional indicator pairs (flags)
- Document limitations: full Unicode grapheme clusters can be refined later.

#### B2. `CellWidthCalculator`
```csharp
namespace Omicron.Core.Text;

public static class CellWidthCalculator
{
    /// <summary>Return the terminal column width of a grapheme cluster.</summary>
    public static int GetWidth(ReadOnlySpan<byte> utf8);

    /// <summary>Return the width of a single Rune.</summary>
    public static int GetWidth(Rune rune);
}
```

Rules:
- ASCII printable (0x20–0x7E): width 1
- Tab (0x09): width 1 for MVP (or 8 later; treat as 1 column initially)
- Rune category control / C0 / C1: width 0
- East Asian Wide / Fullwidth: width 2
- East Asian Ambiguous: width 1 by default (configurable later)
- Combining marks / enclosing marks: width 0
- Emoji presentation sequences: width 2
- `ReadOnlySpan<byte>` version decodes the first `Rune` and looks up width; for multi-rune graphemes, width is determined by the base character.

**Unicode version note:** The East Asian Width and emoji width tables should target **Unicode 15.1** for the MVP hardcoded tables. Document the version explicitly in XML comments so future updates know which standard the tables implement. A full `EastAsianWidth.txt` parse is out of scope for MVP; use a compact hardcoded lookup for the most common ranges.

#### B3. `TerminalCluster`
```csharp
namespace Omicron.Core.Text;

/// <summary>A grapheme cluster annotated with terminal layout metadata.</summary>
public readonly record struct TerminalCluster(
    long ByteOffset,
    int ByteLength,
    int RuneCount,
    int CellWidth);
```

### Tests
- `CellWidth_Ascii_Width1`
- `CellWidth_Cjk_Width2`
- `CellWidth_CombiningMark_Width0`
- `CellWidth_Emoji_Width2`
- `CellWidth_FlagEmoji_Width2`
- `CellWidth_Tab_Width1`
- `GraphemeSegmenter_SingleAscii_OneCluster`
- `GraphemeSegmenter_EmojiWithSkinTone_OneCluster`
- `GraphemeSegmenter_CombiningMark_MergedWithBase`

### Acceptance Criteria
- CJK characters report width 2.
- Emoji with skin tone modifiers report width 2 and one grapheme cluster.
- Combining diacritics report width 0 and merge with base character.
- Tab reports width 1 (MVP simplification).
- Performance: 10k graphemes segmented in <10 ms.

---

## Phase C: Terminal Backend

### Goals
Abstract the host terminal so Omicron can enter alternate screen, read raw input, and write UTF-8/ANSI output without depending on `Console.WriteLine`.

### Deliverables

#### C1. `TerminalSize`
```csharp
namespace Omicron.Core.Rendering;

public readonly record struct TerminalSize(int Width, int Height);
```

#### C2. `TerminalEvent` hierarchy
```csharp
namespace Omicron.Core.Rendering;

public abstract record TerminalEvent;
public sealed record KeyEvent(Key Key, KeyModifiers Modifiers, Rune? Text) : TerminalEvent;
public sealed record MouseEvent(int Row, int Column, MouseButton Button, MouseEventKind Kind, KeyModifiers Modifiers) : TerminalEvent;
public sealed record ResizeEvent(int Width, int Height) : TerminalEvent;
```

Types:
```csharp
public enum Key { None, Enter, Escape, Backspace, Tab, Space, Up, Down, Left, Right, Home, End, PageUp, PageDown, Delete, Insert, F1, /* … */ F12, Character }
public enum KeyModifiers { None = 0, Shift = 1, Alt = 2, Control = 4 }
public enum MouseButton { None, Left, Middle, Right, ScrollUp, ScrollDown }
public enum MouseEventKind { Pressed, Released, Moved, Dragged }
```

For `Key.Character`, the `Rune? Text` carries the actual Unicode scalar.

#### C3. `ITerminalBackend`
```csharp
namespace Omicron.Core.Rendering;

public interface ITerminalBackend : IDisposable
{
    TerminalSize Size { get; }
    IAsyncEnumerable<TerminalEvent> ReadEvents(CancellationToken cancellationToken);
    IBufferWriter<byte> Output { get; }
    void Flush();
}
```

- `ReadEvents` is an async stream because terminal input blocks.
- `Output` is an `IBufferWriter<byte>` so the renderer writes raw UTF-8/ANSI bytes directly.
- `Flush` pushes buffered output to stdout.

#### C4. `SystemTerminalBackend`
Implementation using `System.Console` + platform-specific raw mode.

**Windows path:**
- P/Invoke `GetConsoleMode` / `SetConsoleMode` on `STD_OUTPUT_HANDLE` and `STD_INPUT_HANDLE`
- Enable `ENABLE_VIRTUAL_TERMINAL_PROCESSING` (0x0004) on output
- Enable `ENABLE_WINDOW_INPUT` (0x0008) and `ENABLE_MOUSE_INPUT` (0x0010) on input
- Disable `ENABLE_LINE_INPUT`, `ENABLE_ECHO_INPUT`, `ENABLE_PROCESSED_INPUT`
- Save original mode in constructor; restore on `Dispose`

**Unix path:**
- P/Invoke `tcgetattr` / `tcsetattr` via `libc` (`DllImport("libc")`)
- Set `termios.c_lflag` to disable `ECHO`, `ICANON`, `ISIG`
- Set `termios.c_iflag` to disable `ICRNL`, `INLCR`
- Enable `termios.c_cc[VMIN] = 0; c_cc[VTIME] = 0` for non-blocking read
- Save original `termios`; restore on `Dispose`

**Alternate screen:**
- Enter: write `\x1b[?1049h`
- Exit: write `\x1b[?1049l`

**Cursor:**
- Hide: `\x1b[?25l`
- Show: `\x1b[?25h`

**Title:**
- OSC 2: `\x1b]2;{title}\x07`

**Resize detection:**
- Windows: `WINDOW_BUFFER_SIZE_EVENT` from console input.
- Unix: handle `SIGWINCH` via a self-pipe or poll `ioctl(TIOCGWINSZ)` in the input loop.

#### C5. `TerminalScope` (ref-counted lifecycle)

Adopt XenoAtom.Terminal's ref-counted scope pattern for terminal mode changes. This is safer than a single `TerminalLifecycle` because modes can be entered/exited in nested or interleaved order:

```csharp
namespace Omicron.Core.Rendering;

public sealed class TerminalScope : IDisposable
{
    public static TerminalScope UseAlternateScreen(ITerminalBackend backend);
    public static TerminalScope UseRawMode(ITerminalBackend backend);
    public static TerminalScope HideCursor(ITerminalBackend backend);
    public static TerminalScope UseBracketedPaste(ITerminalBackend backend);
    public static TerminalScope UseMouse(ITerminalBackend backend);

    public void Dispose(); // decrements ref count, restores on last exit
}
```

Each scope increments a counter on the backend. The ANSI enter sequence is written only on first acquire (counter 0→1). The exit sequence is written only on last release (counter 1→0).

Safety nets: `AppDomain.ProcessExit` + `Console.CancelKeyPress` force-restore all scopes regardless of ref counts.

#### C6. `TerminalCapabilities`

Detect terminal features before using them. Modeled after XenoAtom.Terminal's capability system:

```csharp
namespace Omicron.Core.Rendering;

public enum ColorLevel { None, Color16, Color256, TrueColor }

public sealed record TerminalCapabilities(
    bool AnsiEnabled,
    ColorLevel ColorLevel,
    bool SupportsAlternateScreen,
    bool SupportsCursorVisibility,
    bool SupportsMouse,
    bool SupportsBracketedPaste,
    bool SupportsSynchronizedOutput,  // DEC 2026
    bool SupportsWindowSize,
    string TerminalName);  // "Windows Terminal", "VSCode", "iTerm2", etc.
```

Detection: on Windows, check `ENABLE_VIRTUAL_TERMINAL_PROCESSING` flag + `WT_SESSION` env. On Unix, check `TERM`, `COLORTERM`, `TERM_PROGRAM`. Fall back to `AnsiEnabled = false` → plain `Console` output.

### Tests
- `SystemTerminalBackend_EntersAlternateScreen`
- `SystemTerminalBackend_RestoresOnDispose`
- `SystemTerminalBackend_ReadsKeyEvents`
- `SystemTerminalBackend_EmitsResizeEvent`
- `Lifecycle_RestoresTerminalOnUnhandledException`
- `AnsiEncoder_ClearScreen_EmitsCorrectSequence`
- `AnsiEncoder_SetCursorPosition_EmitsCorrectSequence`
- `AnsiEncoder_SetStyle_EmitsCorrectSequence`

*Note: Some tests may need to be integration-style with a mock `IBufferWriter<byte>` because actual terminal interaction requires a real console. Use a `TestTerminalBackend` that captures bytes and yields synthetic events.*

### Acceptance Criteria
- `SystemTerminalBackend` enters and exits alternate screen without corrupting the host terminal scrollback.
- `Ctrl+C` restores cursor visibility and alternate screen state.
- `ReadEvents` yields `KeyEvent` for typed keys and `ResizeEvent` for terminal resizing.
- Output is written as raw UTF-8 bytes, not `Console.WriteLine`.

---

## Phase D: Frame Buffer and Differential Renderer

### Goals
Build the rendering core: a cell grid, a frame differ, and an ANSI encoder that emits minimal updates.

### Deliverables

#### D1. `GlyphRef`
```csharp
namespace Omicron.Core.Rendering;

/// <summary>A reference to a displayable glyph without owning a string.</summary>
public readonly record struct GlyphRef
{
    public static GlyphRef Ascii(byte ascii) => new(ascii);
    public static GlyphRef Interned(int internId) => new((uint)internId | 0x80000000);
    public static GlyphRef Replacement => new(0xFFFD);

    private readonly uint _value;
    private GlyphRef(uint value) => _value = value;
    public bool IsAscii => (_value & 0x80000000) == 0;
    public byte AsciiValue => (byte)_value;
    public int InternId => (int)(_value & 0x7FFFFFFF);
}
```

ASCII fast path stores the byte inline. Non-ASCII glyphs live in an intern table.

#### D2. `TextStyle`
```csharp
namespace Omicron.Core.Rendering;

public readonly record struct TextStyle(
    byte FgR, byte FgG, byte FgB,
    byte BgR, byte BgG, byte BgB,
    bool Bold,
    bool Italic,
    bool Underline);
```

MVP: support 24-bit RGB only. Future: detect terminal capability and fall back to 256-color or 16-color.

#### D3. `RenderCell`
```csharp
namespace Omicron.Core.Rendering;

public struct RenderCell
{
    public GlyphRef Glyph;
    public byte Width;          // 0, 1, or 2
    public TextStyle Style;
    public bool IsEmpty => Width == 0 && Glyph.IsAscii && Glyph.AsciiValue == (byte)' ';
}
```

Cells are `struct` for dense array storage. A 200×60 terminal = 12,000 cells = ~144 KB.

#### D4. `TerminalFrame`
```csharp
namespace Omicron.Core.Rendering;

public sealed class TerminalFrame
{
    public int Width { get; }
    public int Height { get; }
    public RenderCell[] Cells { get; }

    public ref RenderCell this[int row, int col] => ref Cells[row * Width + col];

    public void Clear();
    public void SetText(int row, int col, ReadOnlySpan<byte> utf8, TextStyle style);
    public void FillRect(int row, int col, int w, int h, RenderCell cell);
}
```

- `SetText` iterates grapheme clusters, writes each into its cell(s), and marks continuation cells for wide characters.
- Wide characters (width 2) occupy `col` and `col+1`. The second cell gets a special `Width = 0` continuation marker.
- Truncation: if text exceeds the row width, clip at the right edge.

#### D5. `AnsiEncoder`
```csharp
namespace Omicron.Core.Rendering;

public static class AnsiEncoder
{
    public static void ClearScreen(IBufferWriter<byte> output);
    public static void SetCursorPosition(int row, int col, IBufferWriter<byte> output);
    public static void SetStyle(TextStyle style, TextStyle? previous, IBufferWriter<byte> output);
    public static void WriteGlyph(GlyphRef glyph, IBufferWriter<byte> output, GlyphInternTable? internTable);
    public static void ShowCursor(IBufferWriter<byte> output);
    public static void HideCursor(IBufferWriter<byte> output);
    public static void ResetStyle(IBufferWriter<byte> output);
}
```

Use UTF-8 literal spans for static sequences:
```csharp
output.Write("\x1b[2J"u8);   // clear screen
output.Write("\x1b[H"u8);    // cursor home
output.Write("\x1b[?25l"u8); // hide cursor
```

Style encoding:
- If 24-bit RGB is assumed: `\x1b[38;2;{r};{g};{b}m` for foreground, `48;2;...` for background.
- Bold: `\x1b[1m`, italic: `\x1b[3m`, underline: `\x1b[4m`.
- Reset: `\x1b[0m`.
- Optimize: only emit style changes when `previous` differs.

#### D6. `GlyphInternTable`
```csharp
namespace Omicron.Core.Rendering;

public sealed class GlyphInternTable
{
    public int Intern(ReadOnlySpan<byte> utf8);
    public ReadOnlySpan<byte> Resolve(int internId);
}
```

- Maps grapheme cluster UTF-8 bytes → stable integer ID.
- Used by `TerminalFrame` and `AnsiEncoder` for non-ASCII glyphs.
- Use `Dictionary<byte[], int>` with `ByteArrayEqualityComparer` or similar.
- **Lifetime:** One `GlyphInternTable` instance per render frame (or per `TerminalFrame` instance). Intern IDs are stable only within a single frame. The `DifferentialRenderer` compares `GlyphRef` values across frames, so IDs must remain consistent for identical glyphs within one frame but may be rebuilt on the next frame.
- **Eviction policy:** Hard cap at 4096 entries. If exceeded, clear the entire table and emit a full frame reset (clear + redraw). Do not implement LRU per glyph for MVP — the frame reset is simpler and correct.

#### D7. `SwapChain` (double-buffering)

Adopt Spectre.TUI's `SwapChain` pattern explicitly:

```csharp
namespace Omicron.Core.Rendering;

public sealed class SwapChain
{
    public TerminalFrame Current { get; }   // back buffer (being drawn to)
    public TerminalFrame Previous { get; }  // front buffer (last rendered)

    public void Swap();                     // reset previous, swap indices
    public void Resize(int width, int height);
    public void Diff(IBufferWriter<byte> output); // emit only changed cells
}
```

`Diff()` uses the same row-scan + changed-run algorithm. `Swap()` resets the previous buffer by swapping indices — no cell-by-cell copy.

#### D8. `DifferentialRenderer`
```csharp
namespace Omicron.Core.Rendering;

public sealed class DifferentialRenderer
{
    private readonly SwapChain _swapChain;

    /// <summary>Render the next frame through the swap chain.</summary>
    public void Render(TerminalFrame next, IBufferWriter<byte> output);
}
```

Algorithm per row:
1. Scan left to right.
2. Find first changed cell.
3. Collect a run of changed cells.
4. Move cursor to the start of the run (`\x1b[{row+1};{col+1}H`).
5. For each cell in the run: if style changed from previous cell, emit style ANSI. Write glyph UTF-8.
6. Continue scanning.

Optimizations:
- Skip trailing empty cells on each row.
- If a row is entirely unchanged, skip it.
- If previous frame is null (first render), emit full frame with minimal cursor moves.
- Handle wide-character continuation cells: do not emit ANSI for width-0 continuation cells.

### Tests
- `TerminalFrame_SetText_Ascii_FillsSingleCell`
- `TerminalFrame_SetText_Cjk_FillsTwoCells`
- `TerminalFrame_SetText_Emoji_FillsTwoCells`
- `TerminalFrame_SetText_WideCharacter_TruncatesAtEdge`
- `TerminalFrame_Clear_SetsAllCellsToEmpty`
- `DifferentialRenderer_FirstFrame_EmitsFullFrame`
- `DifferentialRenderer_SingleCellChange_EmitsOneRun`
- `DifferentialRenderer_StyleChangeOnly_EmitsStyleAnsi`
- `DifferentialRenderer_NoChange_EmitsNothing`
- `DifferentialRenderer_WideCharacter_RespectsContinuationCells`
- `AnsiEncoder_SetStyle_Emits24BitRgb`
- `AnsiEncoder_SetStyle_SkipsRedundantAttributes`
- `AnsiEncoder_BeginEndSynchronizedOutput_EmitsCorrectSequences`
- `DifferentialRenderer_BracketsDiffWithSyncOutput`

### Acceptance Criteria
- `TerminalFrame` correctly handles ASCII, CJK, emoji, combining marks, and truncation.
- `DifferentialRenderer` emits no bytes when frames are identical.
- `DifferentialRenderer` emits only changed runs when one cell differs.
- `AnsiEncoder` produces valid UTF-8 + ANSI sequences for 24-bit color.
- DEC 2026 synchronized output brackets every frame diff when supported.
- A 200×60 frame with 100 changed cells emits <2 KB of ANSI.

### DEC 2026 Synchronized Output

Wrap every frame diff in DEC 2026 synchronization sequences for glitch-free atomic updates (`\x1b[?2026h` before diff, `\x1b[?2026l` after). When the terminal supports this (detected via `TerminalCapabilities`), the terminal emulator renders the entire frame atomically — no visible tearing.

- `AnsiEncoder.BeginSynchronizedOutput(output)` → `\x1b[?2026h`
- `AnsiEncoder.EndSynchronizedOutput(output)` → `\x1b[?2026l`
- `DifferentialRenderer.Render()` brackets the diff with these calls.
- If the terminal does not support DEC 2026 (legacy console, some tmux configs), skip the sequences — the diff still works, just without the atomic guarantee.
- Detection: `TerminalCapabilities.SupportsSynchronizedOutput` inferred from terminal name (Windows Terminal ≥1.22, iTerm2 ≥3.5, WezTerm, Kitty, foot, Ghostty, VSCode integrated terminal).

---

## Phase E: Basic App Shell (TUI Integration)

### Goals
Wire the terminal backend, frame buffer, and diff renderer into a minimal fullscreen app that can display the existing CLI transcript.

### Deliverables

#### E1. `TuiShell`
```csharp
namespace Omicron.CLI.Tui;

public sealed class TuiShell : IDisposable
{
    private readonly ITerminalBackend _backend;
    private readonly DifferentialRenderer _renderer;
    private TerminalFrame _currentFrame;
    private readonly CancellationTokenSource _cts;

    public TuiShell(ITerminalBackend backend);

    /// <summary>Run the main event/render loop until cancellation.</summary>
    public async Task RunAsync(Func<TerminalEvent, Task> onEvent, Func<TerminalFrame, Task> onRender);

    public void RequestRender();
    public void Dispose();

    /// <summary>Target FPS for frame pacing. 0 = unlimited.</summary>
    public int TargetFps { get; set; } = 60;
}
```

- `RunAsync` loops:
  1. Wait for either a terminal event or a render request signal.
  2. On event: call `onEvent`.
  3. On render request: call `onRender(currentFrame)`, then `_renderer.Render(currentFrame, _backend.Output)`, then `_backend.Flush()`.
  4. If `TargetFps > 0`, sleep until the next frame time (`1000 / TargetFps` ms). Skip sleep if the frame already took longer.
- Uses a `Channel<bool>` or `AsyncAutoResetEvent` for render signaling.
- FPS targeting prevents burning CPU on idle loops and gives consistent frame pacing during streaming.

#### E2. `TuiLayoutEngine`
Minimal layout:
```csharp
namespace Omicron.CLI.Tui;

public readonly record struct Rect(int X, int Y, int Width, int Height);

public interface ITuiWidget
{
    Size Measure(Size available);
    void Arrange(Rect bounds);
    void Render(RenderContext context);
}
```

For MVP, skip full widget system. Just compute three rects directly:
```text
┌──────────────────────────────┐
│ transcript viewport (flex)   │  height = total - statusHeight - inputHeight
│                              │
├──────────────────────────────┤
│ status bar (1 row)           │
├──────────────────────────────┤
│ input editor (1–3 rows)      │
└──────────────────────────────┘
```

#### E3. `SimpleStatusBar`
Renders into the bottom-1 row:
- Model name (left)
- Provider / API type (center)
- Token count or last event (right)

#### E4. `TranscriptViewportWidget` (stub)
For this plan, a minimal stub that renders a few lines of plain text into the transcript rect. Full virtualization comes in Plan 7.1.

#### E5. `VirtualTerminalBackend` (test-only)
```csharp
namespace Omicron.Core.Rendering;

public sealed class VirtualTerminalBackend : ITerminalBackend
{
    public TerminalSize Size { get; set; } = new(80, 24);
    public List<TerminalEvent> InjectedEvents { get; } = new();
    public List<byte> CapturedOutput { get; } = new();

    public void InjectEvent(TerminalEvent evt);
    public TerminalFrame ReadFrame(); // parses captured ANSI into cells
}
```

- Used for golden-frame tests: render a widget, capture output, parse back into cells, assert expected grid.
- Supports synthetic events via `InjectEvent()`.
- No real terminal needed — runs in CI and headless environments.

#### E6. Integration into CLI
Add a command-line flag or config option to launch in TUI mode:
```bash
omicron --tui
```

In TUI mode:
- Skip the model picker menu (use last-used model or first visible).
- Enter `TuiShell`.
- Stream events from `AgentSession.PromptAsync` into a simple transcript list.
- Render plain text transcript + status bar + input line.
- `Escape` cancels current operation.
- `Ctrl+D` exits TUI.

**Inline mode follow-up (future):** Both Spectre.TUI (`InlineMode`) and XenoAtom.Terminal.UI (`Terminal.Live()`) support rendering without alternate screen — reserving N lines in the existing scrollback. This is useful for progress bars, status updates, or one-shot rich output without destroying scrollback history. Document as a post-MVP enhancement: add `--tui-inline` flag and `InlineTerminalMode` that uses `Console.SetCursorPosition` to overwrite reserved lines instead of entering alternate screen.

### Tests
- `TuiShell_EntersAlternateScreen`
- `TuiShell_ExitsOnCtrlD`
- `TuiShell_RendersStatusBar`
- `TuiShell_RestoresTerminalOnDispose`
- `TuiLayout_ThreePanelLayout_ComputesCorrectRects`
- `VirtualTerminalBackend_CapturesAnsiOutput`
- `VirtualTerminalBackend_InjectedEvent_YieldsFromReadEvents`
- `VirtualTerminalBackend_ReadFrame_ParsesCellsCorrectly`
- `TuiShell_TargetFps_RespectsFramePacing`

### Acceptance Criteria
- `TuiShell` starts and exits without corrupting the host terminal.
- Transcript text renders as plain text in the top panel.
- Status bar shows current model.
- Input line accepts typing and Enter submits.
- Existing console mode continues to work when `--tui` is not passed.

---

## Performance Targets

| Scenario | Target |
|----------|--------|
| Append 100k UTF-8 lines to store | <100 ms, no LOH allocations |
| Segment 10k graphemes | <10 ms |
| Render 200×60 frame (first) | <16 ms (60 FPS budget) |
| Render 200×60 frame (diff, 1% changed) | <2 ms |
| Resize reflow 200×60 | <50 ms |
| Streaming 50 chunks/sec | No dropped frames, viewport stable |

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Raw terminal mode is platform-specific and fragile | Abstract behind `ITerminalBackend`; keep a mock backend for tests; test on Windows, Linux, macOS. |
| Unicode width/cell math has edge cases | Start with hardcoded tables; add golden tests with known CJK/emoji cases; accept minor glitches in MVP. |
| Frame diff is buggy and produces garbage output | Extensive golden tests; keep a `--debug-tui` mode that renders borders around changed cells. |
| TUI crashes and leaves terminal corrupted | `TerminalLifecycle` with `Dispose`, `AppDomain.ProcessExit`, `CancelKeyPress`; always show cursor and exit alternate screen. |
| `Utf8TextStore` chunking is slower than a giant string | Benchmark against `StringBuilder`; tune chunk size; document tradeoffs. |
| OpenTUI spike later proves much faster | This is the fallback baseline; keep abstraction layers clean so swapping backends later is possible. |

---

## Definition of Done

- `Utf8TextStore` append-only, chunked, byte-indexed, tested.
- `CellWidthCalculator` handles ASCII, CJK, emoji, combining marks.
- `ITerminalBackend` abstraction exists with a `SystemTerminalBackend` for Windows and Unix.
- `TerminalFrame` + `DifferentialRenderer` + `AnsiEncoder` produce correct, minimal ANSI updates.
- `TuiShell` can launch in fullscreen mode, render a basic layout, and restore the terminal on exit.
- `VirtualTerminalBackend` enables golden-frame tests without a real console.
- `TuiShell` respects `TargetFps` for frame pacing.
- All tests pass with 0 warnings.
- Build passes on Windows, Linux, and macOS (or at least compiles; platform-specific runtime tests may be conditional).

---

## Suggested First PR

Keep the first PR small:
1. `Utf8TextStore` + `TextPosition` + tests.
2. `CellWidthCalculator` + `TerminalCluster` + tests.
3. `ITerminalBackend` + `SystemTerminalBackend` stub (alternate screen + raw mode + dispose) + tests.
4. `TerminalFrame` + `RenderCell` + `TextStyle` + `AnsiEncoder` + tests.
5. `DifferentialRenderer` + tests.
6. `TuiShell` minimal integration.

Do **not** wire the full transcript viewport or markdown parsing in the first PR. Prove the rendering stack works first.
