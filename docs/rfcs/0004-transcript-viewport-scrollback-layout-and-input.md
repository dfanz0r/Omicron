# RFC 0004: Transcript Viewport, Scrollback, Layout, and Input

Status: **Canonical**

## Purpose

Define the TUI-facing application model for transcripts, scrollback, layout, and input. This RFC builds on RFC 0003's text and rendering engine.

## Scrollback Modes

Omicron should support two terminal modes.

### Fullscreen Mode

Primary mode. Uses alternate screen and app-owned scrollback.

Best for:

- long coding-agent sessions
- fixed input editor
- panels and panes
- markdown/code rendering
- smooth streaming
- virtual terminal panes

### Inline Mode

Secondary mode. Uses normal terminal buffer and append-oriented output.

Best for:

- one-shot commands
- non-interactive output
- CI-like logs
- simple command responses

Inline mode must avoid aggressive clears and should not destroy native terminal scrollback.

## App-Owned Scrollback

Fullscreen TUI should own transcript history, viewport state, and follow-tail behavior.

```text
TranscriptStore
  append-only messages/tool output

LayoutCache
  message block → wrapped terminal rows

Viewport
  first visible wrapped row
  height
  follow-tail mode

FrameBuffer
  visible rows only

Renderer
  diff and write ANSI
```

Viewport state:

```csharp
public sealed class ViewportState
{
    public int FirstVisibleWrappedLine { get; set; }
    public bool FollowTail { get; set; } = true;
}
```

Streaming behavior:

```csharp
void OnModelDelta(ReadOnlySpan<byte> utf8)
{
    transcript.Append(utf8);
    layout.InvalidateLastBlock();

    if (viewport.FollowTail)
        viewport.ScrollToBottom(layout.TotalWrappedRows);

    RenderVisibleViewport();
}
```

Scrolling behavior:

```csharp
void OnScrollUp(int rows)
{
    viewport.FollowTail = false;
    viewport.FirstVisibleWrappedLine = Math.Max(
        0,
        viewport.FirstVisibleWrappedLine - rows);

    RenderVisibleViewport();
}

void OnScrollDown(int rows)
{
    viewport.FirstVisibleWrappedLine += rows;

    if (viewport.Bottom >= layout.TotalWrappedRows)
        viewport.FollowTail = true;

    RenderVisibleViewport();
}
```

UX rule:

> New output must not yank the viewport if the user intentionally scrolled up.

Instead show an indicator:

```text
↓ 14 new lines — press End to follow
```

## Transcript Pipeline

The transcript viewport is performance-critical and should not be a generic text box.

```text
Network/model UTF-8 stream
  ↓
Append-only UTF-8 transcript store
  ↓
Message/block index
  ↓
Incremental markdown/tool parser
  ↓
Wrapped-line cache per terminal width
  ↓
Viewport slice
  ↓
Styled cells
  ↓
Frame diff renderer
```

## Block Model

```csharp
public abstract record TranscriptBlock(BlockId Id);

public sealed record UserMessageBlock(
    BlockId Id,
    IReadOnlyList<ContentBlock> Content) : TranscriptBlock(Id);

public sealed record AssistantMessageBlock(
    BlockId Id,
    IReadOnlyList<ContentBlock> Content,
    bool IsStreaming) : TranscriptBlock(Id);

public sealed record ToolCallBlock(
    BlockId Id,
    string ToolName,
    ToolCallState State,
    IReadOnlyList<ContentBlock> Output) : TranscriptBlock(Id);

public sealed record SystemNoticeBlock(
    BlockId Id,
    UiNotification Notification) : TranscriptBlock(Id);
```

## Layout Cache

Cache layout at block level.

```csharp
public sealed class TranscriptLayoutCache
{
    public int TerminalWidth { get; private set; }
    public int TotalWrappedRows { get; private set; }

    public void InvalidateBlock(BlockId blockId);
    public void InvalidateFrom(BlockId blockId);
    public void ReflowForWidth(int terminalWidth);
    public IReadOnlyList<WrappedLineInfo> GetVisibleLines(int firstRow, int height);
}
```

Resize changes wrapping and should invalidate width-dependent layout. It should not require reparsing all semantic blocks.

## Streaming Optimization

Most streaming frames only affect the bottom portion of the viewport.

Optimize for:

- appending deltas to the last assistant block
- rewrapping only affected lines
- marking only changed rows dirty
- keeping scroll position stable if user is not following tail

## TUI Layout System

A compact layout system is sufficient.

Primitives:

- Rect
- Stack layout
- Split layout
- Fixed size
- Flexible size
- Min/max constraints
- Scroll containers
- Clipping
- Padding/margin

```csharp
public readonly record struct Rect(int X, int Y, int Width, int Height);

public interface ITuiWidget
{
    Size Measure(Size available);
    void Arrange(Rect bounds);
    void Render(RenderContext context);
}
```

Suggested initial app layout:

```text
┌──────────────────────────────────────────────┐
│ transcript viewport                           │
│                                              │
│ assistant/tool output                         │
│                                              │
├──────────────────────────────────────────────┤
│ status bar / active tool / token info         │
├──────────────────────────────────────────────┤
│ input editor                                  │
└──────────────────────────────────────────────┘
```

Later layouts can include terminal panes and split panels.

## Input System

Normalize low-level terminal input into platform-neutral events.

```csharp
public abstract record TerminalEvent;

public sealed record KeyEvent(
    Key Key,
    KeyModifiers Modifiers,
    Rune? Text) : TerminalEvent;

public sealed record MouseEvent(
    int Row,
    int Column,
    MouseButton Button,
    MouseEventKind Kind,
    KeyModifiers Modifiers) : TerminalEvent;

public sealed record ResizeEvent(
    int Width,
    int Height) : TerminalEvent;
```

The app maps these to semantic commands:

```text
Ctrl+C        cancel current operation or exit confirmation
Ctrl+D        EOF / exit
Ctrl+L        clear visible viewport
PageUp        scroll transcript up
PageDown      scroll transcript down
Home/End      top/bottom of transcript or input line
Ctrl+R        command history search
/             command palette or slash command
```

GUI input should map to the same command system where possible.

## Design Decisions

1. Fullscreen mode uses app-owned scrollback.
2. Transcript viewport is custom and virtualized.
3. Resize is handled as a special invalidation path.
4. New output does not steal scroll focus.
5. Layout/input are frontend responsibilities, but commands are shared semantics.
