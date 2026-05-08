# Omicron Agent UI Framework Design: Shared Core with High-Performance Terminal and GUI Frontends

> Superseded organization note: this seed document has been split into canonical RFCs under `docs/rfcs/`. Future planning edits should primarily target those RFCs. See `docs/rfcs/TRACEABILITY.md` for the section mapping.

## 1. Purpose

This document describes the framework architecture for Omicron, an AI coding-agent system with a single shared core that can power both a terminal UI and a graphical UI. The design emphasizes high-performance rendering for large LLM transcripts, plugin extensibility, structured UI contributions, and clean separation between the agent runtime and frontend-specific rendering systems.

Omicron should support:

- Long-running coding-agent sessions.
- Large LLM context windows and large visible transcripts.
- Streaming model output.
- Tool calls, logs, diffs, code blocks, markdown, and diagnostics.
- A high-performance terminal frontend.
- A GUI frontend using a native or cross-platform UI toolkit.
- Embedded virtual terminal panes for manual shell work.
- Tmux-like tabs/splits for shell, transcript, tool, and log panes.
- AI-assisted shell command composition with explicit user approval.
- Sandboxed command/code execution for AI-driven tools and shell workflows.
- Plugins that can contribute tools, commands, panels, status widgets, and structured output.
- Shared core behavior across terminal and GUI applications.

The key principle is:

> The agent core owns state, events, tools, permissions, and semantic UI contributions. Frontends own rendering, layout, input mechanics, scrolling, and platform-specific interaction.

The terminal renderer should not become the application framework. Likewise, GUI framework concepts should not leak into the agent core.

---

## 2. Core Architectural Goals

### 2.1 Shared agent core

The agent runtime should be independent of terminal and GUI rendering systems. It should expose:

- Session state.
- Conversation messages.
- Model streams.
- Tool-call lifecycle events.
- Permission requests.
- Plugin registration.
- Command dispatch.
- Persistence hooks.
- Shell/virtual-terminal session hooks.
- Sandboxed execution policy hooks.
- UI-neutral content and interaction primitives.

The core should be usable from:

- A terminal app.
- A GUI app.
- Tests.
- Headless automation.
- Future web or remote frontends.

### 2.2 Frontend-specific rendering

The terminal frontend and GUI frontend should render the same semantic state differently.

The terminal frontend owns:

- Raw mode.
- Alternate screen and inline mode.
- Input decoding.
- Resize handling.
- Terminal cell measurement.
- ANSI output.
- Diff rendering.
- Internal scrollback.
- Viewport virtualization.
- Embedded virtual-terminal pane rendering and input routing.

The GUI frontend owns:

- Native controls.
- Rich text rendering.
- Virtualized lists.
- Windowing/panels.
- Mouse/keyboard routing.
- Accessibility.
- Native dialogs.
- Virtual terminal pane widgets.
- Font shaping and layout through the GUI toolkit.

### 2.3 Plugin-driven extension

Plugins should be able to contribute functionality without knowing whether the user is in the terminal or GUI app.

Plugins should generally emit:

- Tools.
- Slash commands.
- Semantic panels.
- Structured result blocks.
- Status items.
- Notifications.
- Forms or permission requests.

Plugins should not normally emit terminal cells or GUI controls directly. Frontend-specific plugins may exist, but they should be explicitly marked as such.

### 2.4 Efficient text handling

Large LLM transcripts should not be represented as one giant `string` repeatedly sliced, concatenated, rewrapped, and repainted.

The recommended approach is:

- Store transcript text in append-only UTF-8 chunks.
- Index by byte offsets.
- Maintain block, line, and wrapping indexes.
- Decode incrementally for layout, search, cursor movement, and selection.
- Render only the visible viewport.
- Diff frames before writing terminal output.

UTF-8 should be treated as a storage and transport encoding, not as the layout unit.

---

## 3. High-Level Package Structure

A recommended solution layout:

```text
Omicron.Core
Omicron.UI.Abstractions
Omicron.Plugins
Omicron.Frontend.Tui
Omicron.Frontend.Gui
Omicron.Text
Omicron.Rendering.Terminal
Omicron.Terminal.Emulation
Omicron.Sandboxing
Omicron.Persistence
Omicron.Workspace
Omicron.Tests
```

A more detailed breakdown:

```text
Omicron.Core
  conversation/session model
  agent loop
  tool calls
  model adapters
  permission system
  command system
  plugin host
  shell session/pane primitives
  event stream

Omicron.UI.Abstractions
  semantic UI model
  content blocks
  panel abstractions
  command references
  input requests
  notifications
  status items

Omicron.Text
  UTF-8 text buffers
  grapheme segmentation
  Unicode scalar iteration
  terminal cell width calculation
  wrapping
  line indexing
  markdown/code block support

Omicron.Rendering.Terminal
  terminal backend
  ANSI encoder
  frame buffer
  cell model
  differential renderer
  damage tracking

Omicron.Terminal.Emulation
  pseudo-terminal/process adapters
  VT/ANSI parser
  terminal screen and scrollback buffers
  shell session model
  tmux-like pane/session orchestration
  AI-assisted command composition hooks

Omicron.Sandboxing
  sandbox policy model
  provider abstraction
  OS-native sandbox adapters
  process launch mediation
  filesystem/network/env controls
  sandbox audit events

Omicron.Frontend.Tui
  app shell
  terminal event loop
  TUI widgets
  transcript viewport
  input editor
  status bar
  command palette
  virtual terminal pane host

Omicron.Frontend.Gui
  GUI app shell
  native or cross-platform controls
  rich content renderers
  virtualized transcript view
  virtual terminal pane host
  settings UI

Omicron.Plugins
  built-in tools
  slash commands
  status providers
  semantic UI panels

Omicron.Persistence
  session catalog/history
  event logs
  transcripts
  session snapshots/checkpoints
  terminal pane/session records
  workspace snapshot metadata
  indexes
  settings
  plugin config

Omicron.Workspace
  workspace identity/root model
  virtual file system abstractions
  content-addressed blob store
  tree manifests
  overlay/transaction layers
  workspace snapshots/diffs
  file watch/change ingestion
```

Dependency rule:

```text
Omicron.Core
  depends on minimal primitives only

Omicron.UI.Abstractions
  depends on Omicron.Core primitives only

Omicron.Text
  depends on low-level runtime libraries only

Omicron.Rendering.Terminal
  depends on Omicron.Text

Omicron.Terminal.Emulation
  depends on Omicron.Core primitives, Omicron.Text, and low-level PTY/process adapters

Omicron.Sandboxing
  depends on Omicron.Core primitives, Omicron.Workspace abstractions, and provider-specific runtime libraries

Omicron.Plugins
  depend on Omicron.Core + Omicron.UI.Abstractions

Omicron.Workspace
  depends on low-level runtime libraries and persistence primitives

Omicron.Frontend.Tui
  depends on Omicron.Core + Omicron.UI.Abstractions + Omicron.Text + Omicron.Rendering.Terminal + Omicron.Terminal.Emulation + Omicron.Sandboxing

Omicron.Frontend.Gui
  depends on Omicron.Core + Omicron.UI.Abstractions + Omicron.Terminal.Emulation + Omicron.Sandboxing + GUI framework
```

The key rule:

> Do not let the terminal framework or virtual terminal emulator implementation become a dependency of `Omicron.Core`, `Omicron.UI.Abstractions`, or general-purpose plugins.

Core may define shell session IDs, pane IDs, sandbox policy IDs, command references, and event shapes, but PTY handling, VT parsing, screen buffers, pane rendering, and provider-specific sandbox mechanics belong outside the core. Workspace and persistence abstractions should also remain frontend-neutral. Tools may depend on an agent-provided workspace file system abstraction, but general plugins should not reach directly into TUI/GUI state to read or mutate files.

---

## 4. Agent Core Model

The agent core should be event-driven and append-oriented. It should expose state transitions as events, allowing both TUI and GUI frontends to render the same session.

### 4.1 Omicron agent events

Example event model:

```csharp
public abstract record AgentEvent(DateTimeOffset Timestamp);

public sealed record SessionStarted(
    SessionId SessionId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record UserMessageAdded(
    MessageId MessageId,
    ReadOnlyMemory<byte> Utf8Content,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record AssistantMessageStarted(
    MessageId MessageId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record AssistantDelta(
    MessageId MessageId,
    ReadOnlyMemory<byte> Utf8Delta,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record AssistantMessageCompleted(
    MessageId MessageId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record ToolCallStarted(
    ToolCallId ToolCallId,
    string ToolName,
    JsonElement Args,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record ToolCallOutputDelta(
    ToolCallId ToolCallId,
    ReadOnlyMemory<byte> Utf8Delta,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record ToolCallFinished(
    ToolCallId ToolCallId,
    ToolResult Result,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record PermissionRequested(
    PermissionRequest Request,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record NotificationRaised(
    UiNotification Notification,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record SessionSnapshotCreated(
    SessionSnapshotId SnapshotId,
    SessionId SessionId,
    long EventSequence,
    WorkspaceSnapshotId? WorkspaceSnapshotId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record WorkspaceTransactionCommitted(
    WorkspaceTransactionId TransactionId,
    WorkspaceId WorkspaceId,
    WorkspaceSnapshotId? ParentSnapshotId,
    WorkspaceSnapshotId NewSnapshotId,
    IReadOnlyList<WorkspaceChangeSummary> Changes,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record ShellSessionStarted(
    ShellSessionId ShellSessionId,
    TerminalPaneId PaneId,
    ShellSessionSpec Spec,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record ShellCommandSubmitted(
    ShellSessionId ShellSessionId,
    string CommandText,
    bool WasAiComposed,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record ShellOutputAttached(
    ShellSessionId ShellSessionId,
    ReadOnlyMemory<byte> Utf8PlainTextOutput,
    WorkspaceSnapshotId? RelatedWorkspaceSnapshotId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record SandboxExecutionStarted(
    SandboxExecutionId ExecutionId,
    SandboxPolicyId PolicyId,
    string Executable,
    IReadOnlyList<string> Args,
    WorkspaceSnapshotId? InputWorkspaceSnapshotId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record SandboxExecutionFinished(
    SandboxExecutionId ExecutionId,
    int? ExitCode,
    SandboxResultKind ResultKind,
    WorkspaceSnapshotId? OutputWorkspaceSnapshotId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);
```

### 4.2 Why events?

An event stream allows:

- Streaming UI updates.
- Session replay.
- Debugging.
- Persistence.
- Deterministic tests.
- Multiple frontends observing the same session.
- Captured shell activity attached to sessions when useful.
- Headless execution with later visualization.

The core should not say “draw this line in the terminal.” It should say “an assistant delta arrived,” “a tool call started,” or “a permission is needed.”

---

## 5. Plugin System

Plugins should be categorized into three groups.

### 5.1 Headless plugins

Headless plugins work everywhere. They expose tools and behavior but no frontend-specific rendering.

Examples:

- Git tools.
- File search.
- Repo indexing.
- Linters.
- Formatters.
- Test runners.
- Model providers.
- Memory/session stores.
- Language server adapters.

Example interface:

```csharp
public interface IAgentPlugin
{
    void Configure(IPluginHost host);
}

public interface IPluginHost
{
    void RegisterTool(ITool tool);
    void RegisterCommand(ICommand command);
    void RegisterPanel(IPanelProvider panelProvider);
    void RegisterStatusItem(IStatusItemProvider statusItemProvider);
    void Publish(AgentEvent evt);
}
```

### 5.2 Semantic UI plugins

Semantic UI plugins contribute UI without knowing whether the frontend is terminal or GUI.

They return a frontend-neutral tree of UI primitives.

```csharp
public interface IPanelProvider
{
    string Id { get; }
    string Title { get; }

    UiNode Build(UiContext context);
}
```

Examples:

- Test result panel.
- Git diff panel.
- Tool-call inspector.
- Token usage panel.
- Project status panel.
- Settings page.

### 5.3 Frontend-specific plugins

Some plugins may genuinely need terminal-only or GUI-only behavior.

Examples:

- Terminal tmux integration.
- GUI image previewer.
- GUI webview.
- Native file picker.
- Terminal-specific shell integration.

Mark these explicitly:

```csharp
public interface ITuiPlugin { }
public interface IGuiPlugin { }
```

General plugins should not depend on these interfaces.

---

## 6. UI Abstractions

The shared UI layer should represent content and interactions semantically. It should not try to become a full cross-platform UI toolkit.

The goal is to cover the common agent UI surface:

- Text.
- Markdown.
- Code blocks.
- Diffs.
- Tables.
- Trees.
- Forms.
- Buttons/commands.
- Progress.
- Logs.
- Terminal pane references.
- Panels.
- Notifications.

### 6.1 `UiNode` model

Example:

```csharp
public abstract record UiNode;

public sealed record VStack(
    IReadOnlyList<UiNode> Children,
    UiLayoutOptions? Layout = null) : UiNode;

public sealed record HStack(
    IReadOnlyList<UiNode> Children,
    UiLayoutOptions? Layout = null) : UiNode;

public sealed record TextBlock(
    UiText Text) : UiNode;

public sealed record MarkdownBlock(
    ReadOnlyMemory<byte> Utf8Markdown) : UiNode;

public sealed record CodeBlock(
    string Language,
    ReadOnlyMemory<byte> Utf8Code) : UiNode;

public sealed record DiffBlock(
    DiffModel Diff) : UiNode;

public sealed record TableBlock(
    IReadOnlyList<TableColumn> Columns,
    IReadOnlyList<TableRow> Rows) : UiNode;

public sealed record TreeBlock(
    IReadOnlyList<TreeNode> Roots) : UiNode;

public sealed record ProgressBlock(
    double? Value,
    UiText? Label) : UiNode;

public sealed record ButtonBlock(
    string Id,
    UiText Label,
    CommandRef Command) : UiNode;

public sealed record FormBlock(
    string Id,
    IReadOnlyList<FormField> Fields,
    CommandRef SubmitCommand) : UiNode;

public sealed record TerminalPaneBlock(
    TerminalPaneId PaneId,
    TerminalPanePresentationOptions Options) : UiNode;
```

### 6.2 Content blocks

Messages and tool results should be represented as structured content blocks.

```csharp
public abstract record ContentBlock;

public sealed record PlainTextContentBlock(
    ReadOnlyMemory<byte> Utf8) : ContentBlock;

public sealed record MarkdownContentBlock(
    ReadOnlyMemory<byte> Utf8Markdown) : ContentBlock;

public sealed record CodeContentBlock(
    string Language,
    ReadOnlyMemory<byte> Utf8Code) : ContentBlock;

public sealed record DiffContentBlock(
    DiffModel Diff) : ContentBlock;

public sealed record ToolResultContentBlock(
    string ToolName,
    JsonElement Data) : ContentBlock;

public sealed record DiagnosticContentBlock(
    IReadOnlyList<DiagnosticItem> Diagnostics) : ContentBlock;

public sealed record TerminalOutputContentBlock(
    ShellSessionId ShellSessionId,
    ReadOnlyMemory<byte> Utf8PlainTextOutput,
    TerminalOutputKind Kind) : ContentBlock;
```

The terminal frontend can render these as styled cells. The GUI frontend can render them as rich controls.

### 6.3 Keep UI abstractions small

Avoid creating a giant general-purpose UI framework in `Omicron.UI.Abstractions`.

Good primitives:

```text
Text
Markdown
Code
Diff
Table
Tree
Form
Button / Command
Progress
LogStream
TerminalPaneRef
Panel
Notification
```

Avoid exposing:

```text
Terminal cells
ANSI codes
GUI control classes
Font objects
Pixel geometry
Platform-specific keyboard types
```

---

## 7. Text Representation

### 7.1 Why not use one giant C# string?

A .NET `string` is UTF-16. A `char` is a 16-bit UTF-16 code unit, not a full Unicode character, not necessarily a Unicode scalar, not a grapheme cluster, and not a terminal cell.

For example:

```csharp
string emoji = "😄";
Console.WriteLine(emoji.Length); // 2
```

That emoji is two UTF-16 code units.

Additionally, terminal layout requires more than UTF-16 awareness:

```text
UTF-8 byte          storage / I/O
UTF-16 char         .NET string code unit
Unicode scalar      System.Text.Rune
Grapheme cluster    user-perceived character
Terminal cell width what a TUI needs
```

No raw encoding solves terminal layout by itself.

### 7.2 Recommended storage model

Use append-only UTF-8 chunks for large transcripts.

```csharp
public sealed class Utf8TextStore
{
    public long LengthBytes { get; }

    public TextPosition Append(ReadOnlySpan<byte> utf8);
    public ReadOnlyMemory<byte> GetChunk(int chunkIndex);
    public Utf8Slice Slice(long byteOffset, int byteLength);
}
```

Advantages:

- Efficient for ASCII-heavy model output.
- Natural for network/model streams.
- Natural for terminal output.
- Avoids large string allocations.
- Supports append-only transcript growth.
- Enables byte-offset indexing.

### 7.3 Layout index

Keep separate indexes over the UTF-8 store:

```csharp
public readonly record struct TextBlockInfo(
    BlockId BlockId,
    long ByteStart,
    int ByteLength,
    ContentKind Kind);

public readonly record struct LogicalLineInfo(
    long ByteStart,
    int ByteLength,
    int CellWidth);

public readonly record struct WrappedLineInfo(
    long ByteStart,
    int ByteLength,
    int CellStart,
    int CellWidth,
    bool IsContinuation);
```

The transcript renderer should use byte offsets into the original text rather than allocating substrings.

### 7.4 Grapheme and terminal-cell model

The terminal cannot treat bytes, UTF-16 chars, or Unicode scalars as columns. It needs terminal cell widths.

A useful primitive:

```csharp
public readonly record struct TerminalCluster(
    long ByteOffset,
    int ByteLength,
    int RuneCount,
    int CellWidth);
```

For actual frame rendering:

```csharp
public struct RenderCell
{
    public GlyphRef Glyph;
    public byte Width;
    public TextStyle Style;
}
```

A `GlyphRef` may point to:

- ASCII fast-path character.
- Interned grapheme cluster.
- Replacement glyph.
- Box drawing character.
- Ellipsis.
- Cursor placeholder.
- Synthesized UI symbol.

This is often more flexible than pointing every cell directly into the original transcript buffer.

---

## 8. Terminal Rendering Model

The terminal renderer should work like a small rendering engine, not like a sequence of `Console.WriteLine` calls.

Recommended pipeline:

```text
App state
  ↓
Semantic UI tree / transcript viewport
  ↓
TUI layout
  ↓
Visible text layout
  ↓
FrameBuffer
  ↓
Diff previous frame vs next frame
  ↓
ANSI + UTF-8 bytes
  ↓
Terminal
```

### 8.1 Terminal backend

Responsibilities:

- Enter/exit alternate screen.
- Enable/disable raw mode.
- Hide/show cursor.
- Parse keyboard input.
- Parse mouse input.
- Track terminal resize.
- Provide stdout/stderr byte writers.
- Restore terminal on crash or cancellation.

Example:

```csharp
public interface ITerminalBackend : IDisposable
{
    TerminalSize Size { get; }
    IAsyncEnumerable<TerminalEvent> ReadEvents(CancellationToken cancellationToken);
    IBufferWriter<byte> Output { get; }
    void Flush();
}
```

### 8.2 Frame buffer

The frame buffer is a rectangular array of styled terminal cells.

```csharp
public sealed class TerminalFrame
{
    public int Width { get; }
    public int Height { get; }
    public RenderCell[] Cells { get; }

    public ref RenderCell this[int row, int col] => ref Cells[row * Width + col];
}
```

### 8.3 Differential renderer

Maintain previous and current frames.

```text
previous frame
current frame
  ↓
changed cells / changed runs
  ↓
cursor movement + style changes + text bytes
```

Example interface:

```csharp
public sealed class DifferentialRenderer
{
    private TerminalFrame? _previous;

    public void Render(TerminalFrame next, IBufferWriter<byte> output)
    {
        // Compare previous and next.
        // Move cursor only where needed.
        // Emit ANSI style changes only when needed.
        // Write changed glyph runs as UTF-8.
        _previous = next;
    }
}
```

### 8.4 Avoid hot-path console APIs

Avoid in the render loop:

```csharp
Console.WriteLine(...);
Console.Clear();
Encoding.UTF8.GetString(...);
string.Concat(...);
string.Join(...);
Substring(...);
Regex over giant transcript text;
```

Prefer:

```text
ReadOnlySpan<byte>
ReadOnlyMemory<byte>
ArrayPool<byte>
IBufferWriter<byte>
Stream.Write(ReadOnlySpan<byte>)
Utf8Formatter
Incremental parsing
Cached layout
Dirty regions
```

### 8.5 UTF-8 literals for static terminal output

C# UTF-8 literals are useful for static ANSI sequences and static UI fragments.

```csharp
stdout.Write("\x1b[2J"u8);     // clear screen
stdout.Write("\x1b[H"u8);      // cursor home
stdout.Write("\x1b[?25l"u8);   // hide cursor
stdout.Write("\x1b[?25h"u8);   // show cursor
```

They produce UTF-8 byte data rather than `string` values.

---

## 9. Scrollback Design

Scrollback is one of the most important architectural choices for an agent TUI.

### 9.1 Native terminal scrollback

Native terminal scrollback is good for simple append-only command output.

Advantages:

- User can use terminal emulator scrolling.
- Simple implementation.
- Works naturally for logs.

Disadvantages:

- The app cannot reliably rewrite arbitrary historical scrollback.
- Live streaming updates can fight with user scrolling.
- Redraws can jump the viewport.
- Full clears may destroy scrollback.
- Fixed input areas are difficult.
- Rich panels, spinners, collapsible blocks, and resize behavior become fragile.

### 9.2 App-owned scrollback

A fullscreen TUI should generally use internal scrollback.

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

Example viewport state:

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

Important UX rule:

> New output should not yank the viewport if the user intentionally scrolled up.

Instead, show an indicator:

```text
↓ 14 new lines — press End to follow
```

### 9.3 Recommended modes

Support both modes, but make fullscreen app-owned scrollback primary.

#### Fullscreen mode

Use alternate screen and app-owned scrollback.

Best for:

- Coding agents.
- Long sessions.
- Input editor fixed at bottom.
- Tool panels.
- Markdown/code rendering.
- Smooth streaming.

#### Inline mode

Render in the normal terminal buffer.

Best for:

- One-shot commands.
- Non-interactive output.
- CI-like logs.
- Simple command responses.

Inline mode should avoid aggressive clears and should prefer append-only behavior where possible.

---

## 10. Transcript Viewport

The transcript viewport is the most performance-critical part of an LLM agent TUI.

It should not be implemented as a generic text box containing one huge string.

### 10.1 Recommended pipeline

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

### 10.2 Block model

Represent messages as blocks:

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

### 10.3 Layout cache

Cache layout at the block level.

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

Resize is expensive because wrapping changes. It should invalidate width-dependent layout, but not necessarily reparsing all semantic blocks.

### 10.4 Streaming updates

Most frames while streaming only change the bottom portion of the viewport.

Optimize for:

- Appending deltas to the last assistant block.
- Rewrapping only affected lines.
- Marking only changed rows dirty.
- Keeping scroll position stable if user is not following tail.

---

## 11. Layout System

For the terminal frontend, a small layout system is sufficient.

Recommended primitives:

- Rect.
- Stack layout.
- Split layout.
- Fixed size.
- Flexible size.
- Min/max constraints.
- Scroll containers.
- Clipping.
- Padding/margin.

Example:

```csharp
public readonly record struct Rect(int X, int Y, int Width, int Height);

public interface ITuiWidget
{
    Size Measure(Size available);
    void Arrange(Rect bounds);
    void Render(RenderContext context);
}
```

Suggested main app layout:

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

The transcript viewport should use custom virtualization and should not depend on a generic text widget.

---

## 12. Input System

The terminal backend should normalize low-level input into platform-neutral events.

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

The app shell maps these to semantic commands:

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

The GUI frontend should map GUI input events to the same semantic command system where possible.

---

## 13. Embedded Virtual Terminal and Shell Panels

Omicron should include a built-in virtual terminal emulator for manual shell work. This is distinct from the host terminal used to display the TUI. The virtual terminal runs child shells or commands behind a pseudo-terminal, parses their terminal output, maintains an emulated screen/scrollback buffer, and lets TUI/GUI frontends render that buffer as a pane.

Target capabilities:

- Spawn one or more shell sessions from Omicron.
- Arrange shell sessions as tmux-like panes, tabs, or split panels.
- Support manual command entry for normal developer workflow.
- Let the assistant propose, compose, explain, or insert shell commands.
- Allow explicit user approval before AI-generated commands are sent or executed.
- Capture terminal output as session context when the user chooses.
- Associate shell activity with workspace snapshots and session history.

Important boundary:

> A virtual terminal pane should not write child-process ANSI directly to the host terminal. Child output should flow through a PTY/process adapter into Omicron's terminal emulator buffer, then be rendered by the TUI or GUI frontend.

### 13.1 Terminal emulation model

Recommended pipeline:

```text
child shell/process
  ↓
pseudo-terminal adapter
  ↓
byte stream
  ↓
VT/ANSI parser
  ↓
emulated screen buffer + scrollback
  ↓
terminal pane model
  ↓
TUI frame buffer or GUI terminal control
```

Core concepts:

```csharp
public sealed record ShellSessionId(Guid Value);
public sealed record TerminalPaneId(Guid Value);

public sealed record ShellSessionSpec(
    string ShellExecutable,
    IReadOnlyList<string> Args,
    WorkspacePath? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

public interface IVirtualTerminalSession : IAsyncDisposable
{
    ShellSessionId Id { get; }
    TerminalSize Size { get; }
    TerminalScreenSnapshot Screen { get; }

    ValueTask ResizeAsync(TerminalSize size, CancellationToken ct);
    ValueTask SendInputAsync(ReadOnlyMemory<byte> utf8OrControlBytes, CancellationToken ct);
    IAsyncEnumerable<VirtualTerminalEvent> ReadEventsAsync(CancellationToken ct);
}
```

The emulator should handle at least:

- Cursor movement and erasing.
- SGR styles/colors.
- Alternate screen mode.
- Line wrapping and resizing.
- Scroll regions.
- OSC title updates where useful.
- Bracketed paste.
- Basic mouse reporting if enabled later.

### 13.2 Pane orchestration

Omicron can model terminal layouts similarly to tmux, but with frontend-neutral state:

```text
ShellWorkspace
  tabs
    split tree
      terminal pane
      transcript pane
      tool/log pane
```

Pane operations should be commands, not frontend-only actions:

```text
terminal.newPane
terminal.closePane
terminal.splitHorizontal
terminal.splitVertical
terminal.focusNextPane
terminal.resizePane
terminal.sendInput
terminal.captureSelectionToPrompt
terminal.attachOutputToSession
```

The TUI can render panes inside the alternate-screen app. The GUI can render the same shell sessions with native tabs, splitters, and richer terminal widgets.

### 13.3 AI-assisted command writing

AI command assistance should be user-mediated by default.

Recommended interaction modes:

```text
suggest      assistant proposes command text with explanation
insert       assistant inserts command into focused terminal input, not executed
edit         assistant rewrites selected/current command line
explain      assistant explains selected command/output
execute      assistant sends command only after policy/user approval
```

Important safety rule:

> AI-generated shell commands should not be sent to a live terminal pane silently. The default should be propose/insert, with explicit execution approval based on command risk and user settings.

The command-composition layer should understand:

- Focused terminal pane and current working directory.
- Workspace VFS/snapshot state.
- Recent terminal output selected by the user.
- Shell dialect where known: PowerShell, cmd, bash, zsh, fish, etc.
- Whether the command mutates files, installs packages, starts servers, or performs network/destructive operations.

### 13.4 Persistence and limitations

Virtual terminal state should be persisted carefully:

- Persist shell session metadata and pane layout.
- Persist terminal scrollback/output when configured or explicitly attached to the session.
- Persist AI command suggestions, approvals, and sent commands as events.
- Reference workspace snapshots before/after command batches when commands mutate files.

Do not assume live process state is snapshot-safe. Omicron can restore pane layout and scrollback, but restoring a running shell/process exactly is usually not practical without deeper OS/container support.

For crash recovery, prefer:

```text
restore pane layout
restore scrollback/output history
show process as disconnected if it is gone
allow user to restart shell in same working directory
link restarted shell to the same Omicron session
```

This feature complements, but does not replace, the later sandboxing layer. Sandboxing can eventually provide isolated execution for shell panes or AI-driven commands while preserving the same terminal-pane and VFS abstractions.

---

## 14. GUI Frontend

The GUI frontend should use the same core and semantic UI model, but it should not reuse the terminal renderer.

Potential GUI frameworks:

- Avalonia.
- WPF.
- WinUI.
- MAUI.
- Eto.Forms.
- A web frontend later, if desired.

For a cross-platform desktop app, Avalonia is likely the most natural candidate.

GUI rendering path:

```text
Omicron event stream
  ↓
Session view model
  ↓
Semantic content blocks and terminal pane models
  ↓
Virtualized GUI controls
  ↓
Native/rich rendering
```

The GUI should rely on the platform for:

- Font shaping.
- Unicode rendering.
- Selection.
- Accessibility.
- Scrollbars.
- Clipboard.
- Rich text.
- Image previews.
- Terminal pane controls/rendering surfaces.

But it can share:

- Message model.
- Tool-call model.
- Content blocks.
- Command system.
- Plugin panels.
- Session persistence.

---

## 15. Relationship to Existing .NET TUI Libraries

The .NET ecosystem has useful terminal libraries, but a high-throughput coding-agent TUI likely needs a custom render core.

### 15.1 Terminal.Gui

Mature general-purpose terminal GUI toolkit.

Good for:

- Forms.
- Menus.
- Dialogs.
- Tables.
- Desktop-like terminal UI.

Potential issue:

- May not be ideal as the core renderer for massive streaming transcripts unless heavily customized and benchmarked.

### 15.2 Spectre.Console

Excellent for rich CLI output.

Good for:

- Tables.
- Progress bars.
- Status displays.
- Prompts.
- Pretty one-shot output.

Potential issue:

- Not the right foundation for a fullscreen, virtualized, diff-rendered LLM transcript UI.

### 15.3 XenoAtom.Terminal.UI

A plausible starting point for a modern .NET retained/reactive terminal UI framework.

Potential use:

- Fork or borrow as a scaffold.
- Study terminal backend, layout, and component model.
- Replace or bypass text/rendering hot paths as needed.

Recommended stance:

> Treat XenoAtom as a bootstrap/reference implementation, not as a permanent architectural dependency.

### 15.4 Custom renderer

For Omicron, the most robust long-term route is likely:

```text
custom terminal rendering core
+ borrowed/forked framework ideas
+ custom transcript viewport
+ shared agent/UI abstractions
```

The custom part should be the thing that differentiates the app:

- High-throughput transcript rendering.
- Internal scrollback.
- UTF-8 storage.
- Unicode-aware layout.
- Diffed visible viewport.
- Streaming-friendly updates.

---

## 16. Suggested Internal Fork Strategy

If forking XenoAtom or a similar library internally, do not immediately rewrite everything.

Recommended sequence:

```text
1. Fork the library.
2. Add benchmarks before major changes.
3. Build a fake LLM transcript streaming benchmark.
4. Identify string-heavy and allocation-heavy hot paths.
5. Replace the renderer/diff layer first.
6. Add a custom TranscriptViewport that bypasses generic text controls.
7. Replace text measurement/wrapping if needed.
8. Decide later whether layout/input need replacement.
```

Benchmark scenarios:

```text
100k–1M lines or equivalent token volume
20–100 streaming chunks per second
terminal resize spam
scroll while streaming
emoji / CJK / combining marks
markdown and code blocks
long unbroken lines
large tool output bursts
diff-heavy output
autocomplete in input editor
```

Metrics:

- Allocations per frame.
- CPU time per frame.
- Worst-case resize time.
- Scroll latency.
- Input latency while streaming.
- Memory used by transcript storage.
- Memory used by layout cache.
- Dirty rows per frame.
- Bytes written to stdout per frame.

---

## 17. Rendering Performance Principles

### 17.1 Render only what is visible

The transcript can be huge. The frame is small.

If the terminal is 120×40, the renderer should primarily care about those 4,800 cells, plus cached nearby layout.

### 17.2 Do not rewrap everything on every token

When streaming output arrives, invalidate the affected block or affected line range, not the entire transcript.

### 17.3 Treat resize as a special expensive case

Resize changes wrapping. Optimize normal streaming first, then make resize acceptable through caching and incremental recomputation.

### 17.4 Separate storage from rendering

The transcript store is long-lived and append-only.

The frame buffer is short-lived and viewport-sized.

Do not make every visible cell directly own a string allocation.

### 17.5 Use dirty regions and frame diffing

Avoid clearing and redrawing the whole terminal for every update.

### 17.6 Avoid terminal scrollback destruction

Do not emit aggressive clear-scrollback sequences unless the user explicitly asks.

Especially avoid clearing terminal scrollback in inline mode.

---

## 18. Permissions and Interactive Requests

Coding agents need a clear permission model.

Core permission request:

```csharp
public sealed record PermissionRequest(
    PermissionRequestId Id,
    string Title,
    string Description,
    IReadOnlyList<PermissionOption> Options,
    PermissionRisk Risk,
    JsonElement? Details);
```

Frontends render this differently:

```text
TUI:
  modal overlay or inline confirmation block

GUI:
  dialog, side panel, or notification card
```

Plugins and tools should request permission through the core, not directly through a frontend.

---

## 19. Command System

Commands should be semantic and shared across frontends.

```csharp
public sealed record CommandRef(string Id, JsonElement? Args = null);

public interface ICommand
{
    string Id { get; }
    string Title { get; }
    string? Description { get; }
    ValueTask ExecuteAsync(CommandContext context, JsonElement? args, CancellationToken ct);
}
```

Examples:

```text
omicron.cancel
omicron.retry
omicron.newSession
omicron.openSettings
transcript.scrollToBottom
transcript.copyLastMessage
terminal.newPane
terminal.splitHorizontal
terminal.splitVertical
terminal.sendInput
terminal.aiSuggestCommand
terminal.aiInsertCommand
sandbox.explainPolicy
sandbox.approveExecution
sandbox.rejectExecution
tool.approve
tool.reject
git.showDiff
tests.run
```

TUI maps keybindings and slash commands to command IDs.

GUI maps menu items, buttons, toolbar actions, and shortcuts to the same command IDs.

---

## 20. Persistence, Session History, and Snapshots

Persistence should be independent from frontend rendering. Omicron should treat the persisted event stream and workspace snapshots as authoritative data, while UI layout and render caches remain disposable.

Persist:

- Session metadata.
- Session catalog/history.
- Omicron agent event stream.
- Message blocks.
- Tool calls.
- Tool outputs.
- Permission decisions.
- Session snapshots/checkpoints.
- Workspace snapshot references.
- Terminal pane layout and shell session metadata.
- Captured shell commands/output when attached to the session.
- User settings.
- Plugin settings.
- Optional derived indexes.

Do not persist terminal frame buffers.

Recommended model:

```text
authoritative persisted data:
  session metadata
  agent events + content blocks
  tool-call records
  permission decisions
  session snapshots
  terminal pane/session records
  captured shell command/output events
  workspace snapshot manifests
  workspace blobs

derived/cache data:
  transcript layout indexes
  markdown parse caches
  syntax highlighting caches
  search indexes
  GUI/TUI view state caches
```

Derived data can be rebuilt.

### 20.1 Session history

A session is a durable conversation/runtime history, not a frontend transcript. The TUI, GUI, tests, and headless runners should all be able to load the same session record.

Recommended entities:

```csharp
public sealed record SessionRecord(
    SessionId Id,
    string? Title,
    WorkspaceId? WorkspaceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    SessionSnapshotId? LatestSnapshotId,
    WorkspaceSnapshotId? LatestWorkspaceSnapshotId);

public sealed record PersistedAgentEvent(
    SessionId SessionId,
    long Sequence,
    AgentEvent Event);
```

Session history should support:

- Listing recent sessions by workspace, time, title, tags, or model.
- Reopening a session in either TUI or GUI.
- Replaying a session from the event stream.
- Forking a prior point in history into a new session branch.
- Pruning or archiving old sessions without corrupting workspace snapshots.

### 20.2 Session snapshots/checkpoints

The event stream is the source of truth, but replaying very long sessions from event zero may become expensive. A session snapshot is a checkpoint of reconstructed session state at a specific event sequence.

```csharp
public sealed record SessionSnapshot(
    SessionSnapshotId Id,
    SessionId SessionId,
    long EventSequence,
    WorkspaceSnapshotId? WorkspaceSnapshotId,
    SessionState State,
    DateTimeOffset CreatedAt);
```

Use session snapshots for:

- Fast session resume.
- Crash recovery.
- Time-travel/debug replay.
- Creating branches before risky operations.
- Capturing state before compaction or summarization.

Snapshot policy should be configurable, for example:

```text
create session snapshot when:
  session starts or resumes
  every N events or M minutes
  before/after tool batches that modify workspace state
  before context compaction/summarization
  when user explicitly creates a checkpoint
```

Important rule:

> A session snapshot should reference immutable content and workspace snapshot IDs. It should not copy terminal UI state or frontend frame buffers.

### 20.3 Workspace virtual file system

Omicron should route agent/tool file access through a workspace file system abstraction rather than letting tools casually mutate the host file system. This is separate from the later sandboxing layer, but it gives the core a stable boundary for snapshots, diffs, permissions, replay, and future isolation.

```csharp
public interface IWorkspaceFileSystem
{
    ValueTask<FileStat?> StatAsync(WorkspacePath path, CancellationToken ct);
    ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(WorkspacePath path, CancellationToken ct);
    ValueTask<ReadOnlyMemory<byte>> ReadFileAsync(WorkspacePath path, CancellationToken ct);
    ValueTask WriteFileAsync(WorkspacePath path, ReadOnlyMemory<byte> content, CancellationToken ct);
    ValueTask DeleteAsync(WorkspacePath path, CancellationToken ct);
    ValueTask MoveAsync(WorkspacePath from, WorkspacePath to, CancellationToken ct);
}
```

The first implementation can be a host-backed file system with change tracking. Later implementations can add stricter overlays, remote workspaces, containers, or sandboxes without changing tools and agent workflows.

### 20.4 Workspace snapshots

A workspace snapshot captures the file tree visible to Omicron at a point in time. Prefer content-addressed immutable storage for snapshot data.

```text
WorkspaceSnapshot
  id
  workspace id
  parent snapshot id(s)
  root tree hash
  created timestamp
  reason/user label

TreeManifest
  path entries
  file metadata
  blob hashes

BlobStore
  hash -> compressed file bytes
```

Workspace snapshots should support:

- Pre-tool and post-tool checkpoints.
- Diffs between any two snapshots.
- Rollback of workspace changes.
- Session replay with the workspace state that existed at the time.
- Branching/experimentation from a known workspace state.
- Future sandbox promotion, where approved changes are merged from an isolated layer into the real workspace.

### 20.5 Overlay and transaction model

Workspace mutations should happen through transactions so Omicron can preview, approve, commit, rollback, and persist changes consistently.

```csharp
public interface IWorkspaceTransaction : IAsyncDisposable
{
    WorkspaceTransactionId Id { get; }
    IWorkspaceFileSystem Files { get; }

    ValueTask<WorkspaceDiff> GetDiffAsync(CancellationToken ct);
    ValueTask<WorkspaceSnapshotId> CommitAsync(string reason, CancellationToken ct);
    ValueTask RollbackAsync(CancellationToken ct);
}
```

Recommended flow:

```text
tool requests workspace write
  ↓
core opens workspace transaction / overlay
  ↓
tool writes through IWorkspaceFileSystem
  ↓
core computes diff
  ↓
permission policy decides auto-commit vs ask user
  ↓
commit creates WorkspaceTransactionCommitted + workspace snapshot
  ↓
session snapshot references latest workspace snapshot
```

This model prepares Omicron for sandboxing without making sandboxing a prerequisite for the first version.

---

## 21. Sandboxing Strategy

Omicron should eventually include a sandboxing layer for AI-driven command execution, tool execution, generated code execution, and optionally virtual terminal panes. Sandboxing should be treated as an execution/provider layer that plugs into the existing permission, VFS, workspace transaction, and session-history systems.

The core principle is:

> Tools, shell commands, and generated code should be launched through an Omicron execution broker. The broker applies policy, creates workspace overlays/snapshots, invokes a sandbox provider when required, records audit events, and returns structured output.

### 21.1 Candidate sandbox implementations

Several existing projects are relevant as either direct dependencies or starting points for Omicron-specific providers:

```text
OpenAI Codex sandbox runtime / windows-sandbox-rs entry point
  https://github.com/openai/codex/tree/main/codex-rs/windows-sandbox-rs/src
  Potential role: reference implementation and low-level sandboxing source material.
  The linked path is a useful Windows-focused entry point, but Codex's sandboxing work should be evaluated as a broader runtime/design rather than treated as Windows-only.

zerobox
  https://github.com/afshinm/zerobox
  Potential role: more refined/generalized version of the Codex sandboxing ideas, usable as a cross-platform process sandboxing layer or provider scaffold.
  Public README describes file, network, environment, and credential controls.

heel / leash candidate
  https://github.com/lexoliu/heel
  Potential role: native OS sandboxing reference/provider for LLM-generated code.
  Verify repository name, API, platform support, and license during evaluation.
```

Evaluation criteria:

- License compatibility with Omicron.
- Supported platforms and required OS versions.
- Rust/.NET interop cost and deployment complexity.
- File-system policy expressiveness.
- Network allow/deny controls.
- Environment and credential scrubbing.
- PTY support for interactive commands and virtual terminal panes.
- Performance overhead for short-lived tool calls.
- Auditability and debuggability.
- Failure mode behavior when a provider is unavailable.

### 21.2 Sandbox provider abstraction

Omicron should define a provider-neutral sandbox interface and keep OS-specific implementations behind it.

```csharp
public sealed record SandboxPolicy(
    SandboxPolicyId Id,
    SandboxFileSystemPolicy FileSystem,
    SandboxNetworkPolicy Network,
    SandboxEnvironmentPolicy Environment,
    SandboxResourceLimits ResourceLimits,
    SandboxInteractivity Interactivity);

public sealed record SandboxExecutionRequest(
    string Executable,
    IReadOnlyList<string> Args,
    WorkspacePath? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    SandboxPolicyId PolicyId,
    WorkspaceSnapshotId? InputWorkspaceSnapshotId,
    bool RequiresPty);

public interface ISandboxProvider
{
    string Id { get; }
    bool IsAvailable { get; }
    IReadOnlyList<SandboxCapability> Capabilities { get; }

    ValueTask<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        IWorkspaceFileSystem workspace,
        CancellationToken ct);
}
```

Provider examples:

```text
Omicron.Sandboxing.None
  no isolation; explicit unsafe/local mode only

Omicron.Sandboxing.CodexRuntime
  adapter/fork/reference provider based on the broader OpenAI Codex sandbox runtime design, including the linked windows-sandbox-rs area where applicable

Omicron.Sandboxing.WindowsNative
  Windows OS-native provider if Omicron needs a separate Windows-specific implementation

Omicron.Sandboxing.ZeroBox
  adapter around zerobox if licensing/API/deployment fit; likely a more polished starting point than using the raw Codex sandbox code directly

Omicron.Sandboxing.Heel
  adapter or fork/reference implementation after evaluation

Omicron.Sandboxing.ContainerOrMicroVm
  future provider for stronger isolation where available
```

### 21.3 Policy model

Sandbox policy should be explicit and inspectable. Suggested default posture:

```text
filesystem:
  read workspace: allowed by default
  write workspace: through overlay/transaction only
  read home/profile: denied by default
  read credentials/cloud config/ssh keys: denied by default
  write outside workspace: denied by default

network:
  denied by default for generated code/tool execution
  allow by explicit policy or user approval

environment:
  minimal allowlist
  scrub secrets by default

process:
  resource limits where provider supports them
  child process policy controlled by provider

interactive:
  non-interactive by default for tools/code
  PTY allowed only for terminal-pane workflows or explicit command execution
```

### 21.4 Relationship to VFS, snapshots, and terminal panes

Sandboxing should compose with existing Omicron layers:

```text
execution request
  ↓
permission/risk policy
  ↓
workspace snapshot before execution
  ↓
workspace transaction/overlay mounted into sandbox
  ↓
sandbox provider launches process
  ↓
stdout/stderr/PTY output captured as events
  ↓
workspace diff computed
  ↓
commit, rollback, or ask user
  ↓
workspace snapshot after execution if committed
```

For virtual terminal panes, there should be two modes:

```text
normal pane:
  host shell with clear unsafe/local indication

sandboxed pane:
  shell runs under sandbox provider with visible policy badges
```

A sandboxed pane may have limitations depending on provider support for PTYs, interactive processes, networking, and file-system mounts.

### 21.5 Adoption strategy

Recommended path:

```text
1. Define Omicron's sandbox policy and provider interfaces first.
2. Implement a no-sandbox/local provider for development with explicit warnings.
3. Implement process launch brokering and audit events.
4. Add workspace overlay/snapshot integration.
5. Spike candidate providers:
   - OpenAI Codex sandbox runtime, with the linked windows-sandbox-rs path as one concrete entry point
   - zerobox as a more refined/generalized Codex-derived sandbox provider candidate
   - heel/leash-style native sandboxing for additional reference
6. Choose per-platform defaults based on security, reliability, license, and deployment cost.
7. Add stronger providers later, such as containers or microVMs, without changing tools.
```

Sandboxing is not the first architectural boundary; the first boundary is Omicron's brokered execution, VFS, permission, and snapshot model. That lets the sandbox provider evolve over time.

---

## 22. Error Handling and Recovery

The terminal frontend must carefully restore terminal state.

On crash, cancellation, or unhandled exception:

- Show cursor.
- Exit raw mode.
- Exit alternate screen if active.
- Reset styles.
- Flush output.

Use a guard object:

```csharp
public sealed class TerminalSession : IDisposable
{
    public void Dispose()
    {
        // reset style
        // show cursor
        // disable mouse tracking
        // exit raw mode
        // exit alternate screen
    }
}
```

The agent core should isolate plugin failures:

- Failed plugin commands should produce structured errors.
- Failed tools should emit `ToolCallFinished` with error state.
- UI panel failures should render fallback error nodes.

Sandbox and virtual-terminal failures should also degrade cleanly:

- Unavailable sandbox providers should produce clear capability/policy errors.
- Provider crashes should not corrupt the session event stream.
- Workspace overlays should rollback on failed or cancelled execution unless explicitly committed.
- Disconnected virtual terminal processes should leave recoverable pane/session records.

---

## 23. Testing Strategy

### 23.1 Core tests

- Omicron agent event ordering.
- Tool-call lifecycle.
- Permission flow.
- Command dispatch.
- Plugin registration.
- Session replay.
- Session snapshot creation/resume.
- Workspace transaction event ordering.
- Sandbox execution event ordering.

### 23.2 Text tests

- UTF-8 decoding.
- Invalid UTF-8 handling.
- Grapheme segmentation.
- Combining marks.
- Emoji sequences.
- CJK width.
- Ambiguous-width characters.
- Tabs.
- Long lines.
- Newline variants.

### 23.3 Terminal renderer tests

- Frame diff correctness.
- Style reset correctness.
- Cursor movement minimization.
- Dirty-region rendering.
- Resize behavior.
- Alternate screen lifecycle.

### 23.4 Virtual terminal tests

- VT/ANSI parser correctness.
- PTY resize propagation.
- Screen buffer and scrollback behavior.
- Alternate-screen child application behavior.
- Bracketed paste/input encoding.
- Pane split/focus/resize command behavior.
- AI command insert vs execute safety policy.

### 23.5 Golden frame tests

Given a semantic UI tree and terminal size, produce an expected frame.

```text
input:
  transcript blocks + terminal size

expected:
  styled cell grid
```

### 23.6 Performance tests

- Large transcript append.
- Streaming deltas.
- Scroll while streaming.
- Resize stress.
- Tool output bursts.
- Markdown/code rendering.
- High-volume shell output in virtual terminal panes.

### 23.7 Persistence and workspace tests

- Event stream append/read integrity.
- Snapshot restore equivalence to full replay.
- Session branching/forking from prior event sequences.
- Workspace snapshot diff correctness.
- Workspace transaction commit/rollback behavior.
- VFS path normalization and traversal prevention.
- Host-backed workspace reconciliation after external file changes.

### 23.8 Sandboxing tests

- Policy serialization and risk explanation.
- Provider capability detection.
- Deny-by-default filesystem/network/environment behavior.
- Workspace overlay mount and diff behavior.
- Secret/environment scrubbing.
- Sandbox audit event integrity.
- Provider unavailable/fallback behavior.
- Cross-platform provider conformance suite.

---

## 24. Recommended Initial Implementation Plan

### Phase 1: Core and event stream

Build:

- `Omicron.Core`.
- Omicron agent event model.
- Session state.
- Simple tool interface.
- Command interface.
- Plugin registration.
- Headless test harness.

Goal:

> The agent can run and emit structured events without any UI.

### Phase 2: Semantic UI abstractions

Build:

- `ContentBlock` model.
- `UiNode` model.
- Panel provider interface.
- Status item provider interface.
- Notification model.

Goal:

> Plugins can contribute structured content and panels without knowing the frontend.

### Phase 3: Persistence and workspace foundations

Build:

- Session catalog/history store.
- Append-only event log.
- Session snapshot/checkpoint model.
- Basic host-backed `IWorkspaceFileSystem`.
- Workspace snapshot manifest format.
- Workspace transaction/diff model.

Goal:

> Omicron can resume, replay, checkpoint, and diff sessions/workspaces before UI complexity grows.

### Phase 4: Sandboxing foundations

Build:

- `Omicron.Sandboxing` policy and provider interfaces.
- Explicit local/no-sandbox provider for development.
- Execution broker with audit events.
- Workspace transaction/overlay integration.
- Risk explanation model for user approvals.
- Evaluation spike for the Codex sandbox runtime, zerobox, and heel/leash-style providers.

Goal:

> Omicron can broker command/code execution consistently before committing to a specific OS sandbox provider.

### Phase 5: Minimal terminal backend

Build:

- Raw mode.
- Alternate screen.
- Input events.
- Resize events.
- ANSI writer.
- Simple frame buffer.
- Full redraw renderer.

Goal:

> Basic fullscreen terminal shell works.

### Phase 6: Differential renderer

Build:

- Previous/current frame diff.
- Style run emission.
- Cursor movement.
- Dirty rows.
- UTF-8 output path.

Goal:

> Terminal rendering avoids full clears during normal updates.

### Phase 7: Transcript viewport

Build:

- Append-only UTF-8 transcript store.
- Message/block index.
- Basic wrapping.
- Internal scrollback.
- Follow-tail behavior.
- Streaming append.

Goal:

> Large streaming transcripts are usable.

### Phase 8: Rich content

Build:

- Markdown rendering.
- Code blocks.
- Syntax highlighting abstraction.
- Tool-call panels.
- Diff blocks.
- Tables/trees.

Goal:

> Coding-agent output becomes useful and readable.

### Phase 9: Embedded virtual terminal

Build:

- PTY/process adapter abstraction.
- VT/ANSI parser.
- Emulated terminal screen and scrollback buffer.
- Terminal pane model with tabs/splits.
- TUI pane renderer backed by the normal frame renderer.
- AI command suggestion/insert flow with approval gates.

Goal:

> Omicron can host manual shell sessions in tmux-like panes without bypassing its renderer, permissions, or session model.

### Phase 10: GUI frontend

Build:

- GUI session view model.
- Virtualized transcript list.
- Renderers for content blocks.
- Shared command bindings.
- Plugin panel host.
- GUI terminal pane host.

Goal:

> Same core powers a GUI app.

### Phase 11: Advanced features

Build:

- Command palette.
- Settings UI.
- Plugin marketplace or local plugin discovery.
- Theme system.
- Session replay viewer.
- Search.
- Collapsible tool blocks.
- Inline diffs.
- Workspace snapshot browser.
- Session branching UI.
- Snapshot compare/restore workflows.
- Sandbox provider diagnostics/settings.
- Per-workspace sandbox policy profiles.

---

## 25. Example End-to-End Flow

### 25.1 User submits prompt

```text
TUI input editor captures Enter
  ↓
Command: omicron.submitPrompt
  ↓
Omicron.Core appends UserMessageAdded
  ↓
Frontend observes event and updates transcript
  ↓
Agent starts model stream
```

### 25.2 Model streams response

```text
Model provider emits UTF-8 deltas
  ↓
Omicron.Core emits AssistantDelta events
  ↓
Transcript store appends bytes
  ↓
Layout cache invalidates last assistant block
  ↓
Viewport updates if FollowTail is true
  ↓
Frame is rendered and diffed
  ↓
Terminal receives minimal ANSI/UTF-8 update
```

### 25.3 Tool call starts

```text
Model requests tool
  ↓
Omicron.Core emits ToolCallStarted
  ↓
Frontend shows tool-call block
  ↓
Permission may be requested
  ↓
User approves in TUI or GUI
  ↓
Tool runs and emits output deltas
  ↓
ToolCallFinished updates block state
```

### 25.4 Tool modifies workspace

```text
tool requests file write
  ↓
Omicron.Core opens workspace transaction
  ↓
tool writes through IWorkspaceFileSystem overlay
  ↓
Omicron computes WorkspaceDiff
  ↓
permission policy approves, rejects, or asks user
  ↓
commit creates immutable workspace snapshot
  ↓
Omicron.Core emits WorkspaceTransactionCommitted
  ↓
session snapshot references workspace snapshot when checkpointed
```

### 25.5 AI assists with shell command

```text
user focuses virtual terminal pane
  ↓
user asks Omicron to suggest a command
  ↓
assistant uses workspace/session context and selected terminal output
  ↓
Omicron presents command + explanation + risk
  ↓
user chooses insert, edit, execute, or reject
  ↓
insert writes command text to terminal input without pressing Enter
  ↓
execute sends command only after permission policy allows it
  ↓
command/output may be attached to session history if configured
```

### 25.6 Sandboxed command/code execution

```text
tool or assistant requests command/code execution
  ↓
Omicron execution broker classifies risk and selects sandbox policy
  ↓
workspace snapshot is captured before execution
  ↓
workspace transaction/overlay is prepared
  ↓
sandbox provider launches process with filesystem/network/env controls
  ↓
stdout/stderr/PTY output is streamed as structured events
  ↓
workspace diff is computed after execution
  ↓
user/policy commits or rolls back changes
  ↓
Omicron emits SandboxExecutionFinished and optional workspace snapshot event
```

Same core flow, different frontend rendering.

---

## 26. Key Design Decisions

### Decision 1: Shared core, separate frontends

The core does not depend on terminal or GUI libraries.

### Decision 2: Plugins emit semantic UI

Plugins should usually return `UiNode` and `ContentBlock` structures, not terminal cells or GUI controls.

### Decision 3: UTF-8 transcript storage

Large LLM text should use append-only UTF-8 chunks with byte-offset indexes.

### Decision 4: Terminal layout is Unicode-aware

Do not treat bytes, UTF-16 chars, or Unicode scalars as terminal columns.

### Decision 5: App-owned scrollback in fullscreen mode

The TUI should own transcript history, viewport state, and follow-tail behavior.

### Decision 6: Custom transcript viewport

The transcript viewport is performance-critical and should bypass generic text controls if necessary.

### Decision 7: Differential terminal rendering

Render visible cells into a frame buffer and diff against the previous frame.

### Decision 8: Embedded terminal panes use an emulator, not raw passthrough

Child shell output should flow through a PTY adapter and virtual terminal emulator, then be rendered by Omicron's TUI/GUI frontends. This enables tmux-like panes, persistence, AI assistance, and consistent rendering.

### Decision 9: AI shell assistance is user-mediated by default

AI-generated commands should normally be suggested or inserted rather than executed silently. Execution requires explicit approval or a configured permission policy.

### Decision 10: Existing TUI libraries are scaffolding, not the foundation

A fork of a library like XenoAtom may accelerate development, but the framework should preserve control over rendering, text layout, transcript virtualization, and virtual terminal pane rendering.

### Decision 11: Event stream plus snapshots for session history

The event stream remains authoritative, while session snapshots provide fast resume, crash recovery, replay checkpoints, and branch points.

### Decision 12: Workspace access goes through a VFS boundary

Tools should use Omicron's workspace file system abstraction. This enables diffs, rollback, snapshotting, permission review, replay, and future sandbox promotion.

### Decision 13: Workspace snapshots are immutable and content-addressed

Workspace snapshots should record file-tree state by manifest and blob hashes rather than by frontend state or transient process state.

### Decision 14: Sandboxing is an execution provider layer, not the core architecture boundary

Omicron should broker execution through policy, permissions, VFS overlays, and snapshots first. OS-specific sandbox providers can then be added, swapped, or improved without rewriting tools or frontends.

### Decision 15: Evaluate existing sandbox systems before building from scratch

OpenAI Codex's broader sandbox runtime/design, `zerobox` as a more refined Codex-derived option, and the `heel`/`leash`-style native sandboxing work should be evaluated as direct dependencies, provider adapters, or reference implementations before Omicron commits to a custom sandbox runtime.

---

## 27. Summary

Omicron should be built around a UI-agnostic agent core and semantic UI abstractions. Terminal and GUI frontends should consume the same event stream and content model, but each frontend should own its rendering strategy.

For the terminal frontend, the most important technical choices are:

- Use UTF-8 transcript storage.
- Maintain block and layout indexes.
- Implement internal scrollback.
- Render only visible rows.
- Use a styled cell frame buffer.
- Diff frames before writing ANSI output.
- Avoid string-heavy hot paths.
- Build a custom transcript viewport.

For embedded terminal panes, the most important technical choices are:

- Use PTY/process adapters feeding a virtual terminal emulator.
- Render emulated terminal buffers through the same TUI/GUI pane system.
- Support tmux-like tabs/splits as semantic pane state.
- Make AI command writing propose/insert by default, with approval before execution.
- Persist pane metadata and selected output, but do not pretend live process state is fully snapshot-safe.

For persistence and workspace state, the most important technical choices are:

- Use an append-only event stream as authoritative history.
- Add session snapshots for fast resume and branch points.
- Route file access through an `IWorkspaceFileSystem` boundary.
- Create immutable workspace snapshots for checkpoint, diff, rollback, and replay.

For sandboxing, the most important technical choices are:

- Define Omicron's broker/policy/provider interface first.
- Keep deny-by-default policies for filesystem, network, environment, and credentials.
- Integrate sandbox execution with workspace overlays and snapshots.
- Evaluate the Codex sandbox runtime/design, `zerobox`, and `heel`/`leash`-style systems as providers or starting points.
- Allow stronger providers later without changing tools, plugins, or frontends.

For plugins, the key rule is:

> Plugins should contribute capabilities and semantic UI, not frontend-specific rendering, unless explicitly marked as frontend-specific.

This design allows one Omicron agent/plugin ecosystem to power both a high-performance coding-agent TUI and a richer GUI application without binding the core system to either rendering model.

