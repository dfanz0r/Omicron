# Report 0001: TUI Renderer Codebase Analysis

**Date:** 2026-05-10
**Context:** Informing Omicron's Terminal Backend and Frame Buffer Foundations (Implementation Plan 0007)
**Sources:** READ_ONLY/ folder — 22 codebase directories analyzed

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Spectre.TUI](#2-spectretui)
3. [Spectre.Console](#3-spectreconsole)
4. [XenoAtom.Terminal](#4-xenoatomterminal)
5. [XenoAtom.Terminal.UI](#5-xenoatomterminalui)
6. [ConsoleEx](#6-consolex)
7. [Termina](#7-termina)
8. [Diff (Myers Algorithm)](#8-diff-myers-algorithm)
9. [OpenTUI](#9-opentui)
10. [Other Codebases (Brief Notes)](#10-other-codebases-brief-notes)
11. [Cross-Cutting Patterns](#11-cross-cutting-patterns)
12. [Recommendations for Omicron](#12-recommendations-for-omicron)

---

## 1. Executive Summary

This report analyzes **seven primary TUI renderer codebases** and several secondary ones found in the READ_ONLY folder. The goal is to identify patterns, architectural decisions, and concrete implementations that inform Omicron's custom rendering track (Track A per RFC 0003 / Plan 0007).

**Key findings:**

| Dimension | Consensus Best Practice |
|-----------|----------------------|
| **Rendering model** | Retained-mode cell buffer with frame diffing (Spectre.TUI, XenoAtom.Terminal.UI, ConsoleEx) |
| **Backend abstraction** | `ITerminalBackend` with platform-specific implementations (XenoAtom.Terminal, Plan 0007) |
| **Cell representation** | Struct-based `RenderCell` with `GlyphRef` + `TextStyle` (Plan 0007) or class-based `Cell` with string `Symbol` (Spectre.TUI) |
| **Diff strategy** | Row-scan with changed-run detection; skip trailing empty cells (Spectre.TUI, Plan 0007) |
| **Double buffering** | SwapChain pattern with two buffers (Spectre.TUI) or render-to-frontbuffer with `SparseBuffer` (XenoAtom.Terminal.UI) |
| **Layout system** | Measure/Arrange protocol with size constraints (Termina, XenoAtom.Terminal.UI, Plan 0007) |
| **ANSI encoding** | Dedicated `AnsiWriter` emitting raw UTF-8/ANSI bytes (XenoAtom.Terminal, Spectre.Console) |
| **Input handling** | Async event stream `IAsyncEnumerable<TerminalEvent>` (Plan 0007), channel-based (XenoAtom.Terminal) |
| **Terminal lifecycle** | Ref-counted scopes for alternate screen, raw mode, cursor (XenoAtom.Terminal) |

---

## 2. Spectre.TUI

**Path:** `READ_ONLY/spectre.tui/`
**Status:** Early-stage / Pre-release (warning in README: "under construction")
**Language:** C# (targets modern .NET)
**License:** MIT

### Architecture

Spectre.TUI is a **cell-buffer retained-mode** TUI framework being built by the Spectre team. It is the successor to Spectre.Console's live display capabilities, extracted into a full TUI framework.

### Rendering Pipeline

```
Renderer.Draw(callback)
  → callback fills RenderContext (writes to SwapChain.Current buffer)
  → SwapChain.Diff() compares Current vs Previous
  → For each changed cell: emit cursor move + write cell
  → Handle cursor visibility
  → Flush terminal
  → SwapChain.Swap() (Previous.Reset(), swap indices)
```

### Key Components

#### `Renderer.cs`
- Owns the render loop (polling model, not push)
- Holds `ITerminal`, `SwapChain`, `TargetFps`
- `Draw()`: measure elapsed → check FPS target → resize check → fill frame → diff → emit ANSI → flushes → swaps
- `SetTargetFps(int)` / `NoTargetFps()` — optional frame pacing
- `TimeUntilNextRender()` — for callers wanting precise timing

#### `SwapChain.cs`
- Double-buffer pattern: two `Buffer` instances (`_buffers[0]`, `_buffers[1]`)
- `Current` (back buffer being drawn to), `Previous` (front buffer already rendered)
- `Diff()` delegates to `Previous.Diff(Current)`
- `Swap()`: resets previous buffer, toggles index
- `Resize()`: resizes both buffers, resets previous

#### `Buffer.cs`
- Flat `Cell[]` array with row-major indexing: `_cells[(y * _screen.Width) + x]`
- `Diff(Buffer other)`: zips current cells with previous cells, yields `(x, y, Cell)` for non-equal cells
- `Reset()`: sets all cells to empty symbol + plain style
- Constructor validates cell count matches area

#### `Cell.cs`
- **Class (not struct)** with `Symbol` (string), `Style` (struct)
- Fluent setters: `SetSymbol()`, `SetStyle()`, `SetForeground()`, `SetBackground()`
- `IEquatable<Cell>` with `HashCode.Combine(Symbol, Decoration, Foreground, Background)`
- Static `EmptySymbol = " "` (single space string)
- `Clone()` method for buffer initialization

#### `RenderContext.cs`
- Record type wrapping `IBuffer` + `Rectangle Screen` + `Rectangle Viewport`
- `GetCell(x, y)` translates viewport-relative to buffer coordinates
- Extension methods provide: `SetString()`, `SetLine()`, `SetSpan()`, `Blit()`, `SetCursorPosition()`
- `CreateAreaRenderContext(area)` creates clipped sub-context
- Cursor position propagates through nested contexts via `Parent`

#### `SparseBuffer.cs`
- Alternative buffer using `Dictionary<Position, Cell>` — only stores non-empty cells
- Used by `RenderSurface` for offscreen rendering
- `GetCell(x, y)` creates cells lazily

#### `ITerminal.cs`
```csharp
public interface ITerminal : IDisposable {
    void Clear();
    Size GetSize();
    void MoveTo(int x, int y);
    void Write(Cell cell);
    void Flush();
    void HideCursor();
    void ShowCursor(Position? position);
}
```

#### `ITerminalMode.cs`
- `FullscreenMode`: enters alternate screen, hides cursor, uses CSI sequences
- `InlineMode`: reserves N lines in scrollback, saves/restores cursor position

### Strengths
- Simple, clean architecture — easy to understand
- SwapChain double-buffering pattern is straightforward
- FPS targeting is built-in
- Good separation of concerns (Renderer, SwapChain, Buffer, Cell)
- Blit operations with clipping for wide characters

### Weaknesses
- Cell is a **class** — heap allocation per cell, GC pressure on large frames
- Symbol is a **string** — more allocations than necessary
- Diff iterates cells with `Zip` + LINQ — not optimal for hot path
- No grapheme/Unicode width handling in core (delegates to `Graphemes()` extension)
- No backend abstraction beyond `ITerminal` — terminal lifecycle not fully managed
- Missing: platform-specific raw mode, resize signals, async input

### Relevance to Omicron
- Core swap chain + diff pattern is directly applicable
- `Cell` as struct (not class) with `GlyphRef` is better (adopted by Plan 0007)
- `RenderContext` sub-context pattern is useful for widget layout
- `ITerminalMode` (Fullscreen vs Inline) is a good abstraction

---

## 3. Spectre.Console

**Path:** `READ_ONLY/spectre.console/`
**Status:** Mature, widely used
**Language:** C#
**License:** MIT

### Architecture

Spectre.Console is **not a TUI framework** — it is a **console rendering library** for building structured console output (tables, trees, panels, progress bars, prompts). Its rendering pipeline is segment-based, not cell-based.

### Rendering Pipeline

```
IRenderable
  → Segment (text + style + link + flags)
  → SegmentLine (collection of segments)
  → RenderPipeline (hooks transform segments)
  → AnsiWriter (emits ANSI sequences)
```

### Key Components

#### `IRenderable.cs`
```csharp
public interface IRenderable {
    IEnumerable<Segment> Render(RenderOptions options, int maxWidth);
}
```

#### `Segment.cs`
- `Text` (string), `Style`, `Link`
- Flags: `IsLineBreak`, `IsWhiteSpace`, `IsControlCode`
- Static: `Segment.LineBreak`, `Segment.Empty`, `Segment.Padding(int)`

#### `RenderPipeline.cs`
- Chain of `IRenderHook` objects that process/enrich the renderable stream
- Used for adding overlays, live display wrapping, etc.

#### `AnsiConsoleBackend.cs`
- `IAnsiConsoleBackend` abstraction
- `AnsiConsoleBackend`: writes via `AnsiWriter`, wraps `IRenderable` emitting
- `LegacyConsoleBackend`: fallback for non-ANSI terminals (uses `ConsoleColor`)

### Strengths
- Highly composable widget system (IRenderable)
- Segment-based output is efficient for line-oriented output
- Rich styling system (colors, decorations, links)
- Mature, battle-tested

### Weaknesses
- Not a TUI framework — no frame buffer, no cell grid, no diffing
- No fullscreen mode, no raw input
- No grapheme/Unicode width handling (recent versions added some)
- Segment-based pipeline is designed for one-shot rendering, not incremental updates

### Relevance to Omicron
- `Segment` model with `Text + Style + Link` is useful for content blocks
- `RenderPipeline` hook pattern could be used for Omicron's content block rendering
- `AnsiWriter` (in internal code) provides ANSI encoding patterns worth studying
- Legacy → ANSI backend fallback pattern is instructive

---

## 4. XenoAtom.Terminal

**Path:** `READ_ONLY/XenoAtom.Terminal/`
**Status:** Mature (production-ready)
**Language:** C# (targets net10.0)
**License:** BSD-2-Clause

### Architecture

XenoAtom.Terminal is a **comprehensive terminal abstraction library** providing:
- Platform-specific backends (Windows, Unix/macOS, Virtual/Test)
- Rich capability detection (color levels, OSC8, alternate screen, mouse, clipboard, graphics protocols)
- VT input decoder (parses escape sequences into strongly-typed events)
- Ref-counted resource scopes for terminal modes
- `AnsiWriter` for emitting ANSI sequences

### Key Components

#### `ITerminalBackend.cs`
Massive interface (40+ members) covering:
- Output: `Out`, `Error` (TextWriter)
- Size: `GetSize()`, `GetWindowSize()`, `GetBufferSize()`
- Cursor: `Get/Set CursorPosition`, `Get/Set CursorVisible`
- Colors: `SetForegroundColor`, `SetBackgroundColor`, `ResetColors`
- Clipboard: `TryGet/Set ClipboardText`, `TryGet/Set ClipboardData`
- Input: `StartInput`, `StopInputAsync`, `ReadEventsAsync`
- Scopes: `UseRawMode`, `UseAlternateScreen`, `HideCursor`, `EnableMouse`, `EnableBracketedPaste`, `UseTitle`, `SetInputEcho`
- Lifecycle: `Initialize`, `Flush`, `Dispose`

#### Backend Implementations

**`WindowsConsoleTerminalBackend.cs`** (~2071 lines)
- Win32 P/Invoke: `GetConsoleMode`/`SetConsoleMode`, `ReadConsoleInputW`, `GetConsoleScreenBufferInfo`
- VT input decoder support via `ENABLE_VIRTUAL_TERMINAL_INPUT`
- Mouse mode ref-counting with `_vtMouseModeCounts`
- Bracketed paste via `SetBracketedPasteEnabled`
- Full clipboard support (Win32 API)
- Terminal detection: Windows Terminal, VSCode, Visual Studio, legacy console
- SGR mouse events (1006) with multi-level reporting

**`UnixTerminalBackend.cs`** (~1100 lines)
- POSIX: `tcgetattr`/`tcsetattr`, `poll`, `read`, `ioctl`
- Separate implementations for Linux and macOS (different `termios` struct layouts)
- Cursor position query via DSR `\x1b[6n` with polling fallback
- SIGWINCH handling via poll loop checking terminal size
- `UnixTerminfo` for terminfo capability detection
- Clipboard: native providers + OSC 52 fallback
- Color level detection: `COLORTERM`, `TERM`, terminfo

**`VirtualTerminalBackend.cs`** (~460 lines)
- In-memory implementation for testing
- Supports synthetic events via `PushEvent()`
- Configurable capabilities and size
- CI detection: GitHub Actions, Azure Pipelines, GitLab, etc.

#### `TerminalScope.cs`
- `IDisposable` scope pattern for terminal mode changes
- Ref-counted: `UseAlternateScreen()` increments a counter, only writes ANSI on first enter / last exit
- `TerminalScope.Empty` for no-op fallbacks

#### `VtInputDecoder.cs`
- Parses VT escape sequences into `TerminalEvent` objects
- Handles: keys (with modifiers), mouse (SGR, X10), resize, focus, bracketed paste, DSR, OSC, DCS
- Decoder pattern: `Decode(ReadOnlySpan<char> chunk, bool isFinalChunk, ...)`

#### `AnsiWriter.cs`
- Fluent API: `_ansi.EraseDisplay(2).CursorPosition(1, 1).Write("text")`
- Handles: cursor movement, erase operations, SGR colors (24-bit, 256, 16), private modes, alternate screen, title, clipboard (OSC 52)
- Capability-aware: respects color level, private mode support

#### `TerminalCapabilities.cs`
Rich capabilities model covering:
- `AnsiEnabled`, `ColorLevel` (None, Color16, Color256, TrueColor)
- `SupportsAlternateScreen`, `SupportsCursorVisibility`, `SupportsMouse`
- `SupportsBracketedPaste`, `SupportsPrivateModes`, `SupportsRawMode`
- `SupportsCursorPositionGet/Set`
- `SupportsClipboard*`, `SupportsOsc52Clipboard`
- `SupportsTitleGet/Set`, `SupportsWindowSize`, `SupportsBeep`
- `IsOutputRedirected`, `IsInputRedirected`
- `TerminalName` (Windows, WindowsTerminal, VSCode, Unix, etc.)
- `Graphics` (Kitty, Sixel, iTerm2 protocol support)

#### `TerminalEvent.cs` (event hierarchy)
```
TerminalEvent (abstract record)
  ├── TerminalKeyEvent (Key, Modifiers, Text, IsRepeat, IsAlt, IsCtrl...)
  ├── TerminalMouseEvent (Row, Column, Button, Kind, Modifiers)
  ├── TerminalResizeEvent (Size)
  ├── TerminalFocusEvent (Focused)
  ├── TerminalPasteEvent (Text, Kind)
  ├── TerminalSignalEvent (Kind: Interrupt, Break)
  ├── TerminalGraphicsEvent (Payload)
  └── TerminalUnknownEvent (Sequence)
```

### Strengths
- **Most complete terminal backend** in the entire READ_ONLY folder
- Cross-platform with proper platform detection
- Capability detection is extensive and well-designed
- Ref-counted scopes prevent mode corruption from nested usage
- Virtual backend enables full testability
- VT input decoder is comprehensive

### Weaknesses
- Large `ITerminalBackend` interface — high implementation burden
- Some operations are best-effort with silent fallbacks (risk of hidden failures)
- `TerminalScope.Empty` silently swallows unsupported operations
- Clipboard support adds significant complexity
- No frame buffer or cell model (it's a backend, not a UI framework)

### Relevance to Omicron
- **Blueprint for `SystemTerminalBackend`** in Plan 0007 Phase C
- Capability detection model should be adopted or adapted
- Ref-counted scope pattern solves terminal restoration guarantees
- `VtInputDecoder` can be reused or adapted for input parsing
- `AnsiWriter` patterns inform `AnsiEncoder` in Plan 0007 Phase D

---

## 5. XenoAtom.Terminal.UI

**Path:** `READ_ONLY/XenoAtom.Terminal.UI/`
**Status:** Mature (production-ready, 60+ controls)
**Language:** C# (targets net10.0, C# 14)
**License:** BSD-2-Clause

### Architecture

XenoAtom.Terminal.UI is a **full-featured retained-mode TUI framework** built on top of XenoAtom.Terminal. It provides:

- **Cell-buffer renderer with diffing** — efficient batched output with synchronization (DEC 2026)
- **Layout system** — measure/arrange protocol (integer cell-based)
- **Control library** — 60+ controls (TextBox, TextArea, DataGrid, TreeView, etc.)
- **Binding system** — bindable properties, `State<T>`, automatic dependency tracking
- **Styling/theming** — Theme + per-control styles, ColorScheme palettes, brush gradients
- **Two hosting models** — inline (`Terminal.Write`/`Terminal.Live`) and fullscreen (`Terminal.Run`)
- **Alpha-aware colors** (RGBA) with blending support

### Key Details (from README and samples)

- **Cell-buffer renderer + diffing**: renders to an offscreen buffer, diffs against previous frame, emits minimal ANSI
- **Synchronized output**: uses DEC 2026 synchronization sequences for atomic updates
- **`RenderContext`**: provides cell-level write operations with clipping
- **Layout system**: consistent measure/arrange protocol, panels and containers
- **Controls**: Button, CheckBox, RadioButton, TextBox, TextArea, CodeEditor, ListBox, DataGrid, TabControl, MenuBar, Calendar, ColorPicker, ProgressBar, Slider, etc.
- **Hosting**: `Terminal.Run(action)` enters alternate screen, starts input loop, runs the app
- **Inline mode**: `Terminal.Live(action)` renders in-place within existing scrollback
- **Debug overlay**: `F12` toggles performance overlay showing frame timings, invalidation, diff output

### Strengths
- Most feature-complete .NET TUI framework in the set
- Production-proven approach with real-world controls
- Cell-buffer + diffing is the same model as Plan 0007
- Alpha blending for modern UI effects
- Color schemes and theming system

### Weaknesses
- Source code not available in READ_ONLY (only samples, no src/ directory at expected location)
- Targets net10.0 / C# 14 — very new, may not be stable for Omicron
- Heavy dependency system (Terminal + UI + Extensions)
- Binding system adds complexity

### Relevance to Omicron
- Validates the cell-buffer + diff approach chosen in Plan 0007
- The measure/arrange layout protocol is a good model for Omicron's widget system
- Two hosting models (inline vs fullscreen) is directly applicable
- DEC 2026 synchronization is worth investigating for glitch-free rendering

---

## 6. ConsoleEx

**Path:** `READ_ONLY/ConsoleEx/`
**Status:** Mature (examples include AgentStudio, ClinicFlow)
**Language:** C#
**License:** MIT

### Architecture

ConsoleEx is a **window-based composited TUI system** — rather than a flat cell buffer, it manages overlapping windows with Z-ordering, transparency, and compositing effects (gradients, alpha blending, blur, particles).

### Rendering Pipeline

```
RenderCoordinator.UpdateDisplay()
  → Sort windows by Z-order
  → For each window: check if dirty
    → Clear area behind window (blit desktop background)
    → Render window content (borders, title bar, scrollbars, client area)
  → Compositing pass for transparent windows
  → Flush to console driver
```

### Key Components

#### `IConsoleDriver.cs`
- Low-level abstraction over console I/O
- Events: `KeyPressed`, `MouseEvent`, `ScreenResized`
- Methods: `Clear`, `Flush`, `FillCells`, `WriteBufferRegion`, `SetCursorPosition/Visible/Shape`
- Color support: `SetForegroundColor`, `SetBackgroundColor`, `DefineCustomColor`

#### `RenderCoordinator.cs`
- Coordinates all rendering operations
- Dirty region tracking per window
- Coverage cache to avoid redrawing occluded windows
- Desktop background caching (blit pattern, gradient, image)
- Z-order compositing

#### `Renderer.cs`
- Fills rectangular areas, renders borders/scrollbars
- Window content rendering with clipping
- `BlitDesktopRegion()` — copies cached background
- `FillRect()`, `ClearArea()`, `FillDesktopBackground()`

#### Compositing
- `TransparencyBrush` — alpha blending for semi-transparent windows
- `GradientBackground` — linear/radial gradients for window backgrounds
- `DesktopBackground` — pattern, gradient, or image backgrounds
- Desktop effects: `FadeInWindow`, `MatrixRainWindow`, `ParticleSystemWindow`, `WipeTransitionWindow`

#### Window System
- `Window` class with state machine (`Initialized`, `Visible`, `Closing`, etc.)
- Z-order management via `WindowStateService`
- Focus management via `FocusManager`
- Layout system with anchor-based positioning
- Region-based invalidation

### Strengths
- **Window compositing** — overlaps, transparency, effects — unique among analyzed codebases
- Desktop background caching is a smart optimization
- Region-based invalidation avoids full-frame redraws
- Pooled collections to avoid per-frame allocations
- Rich example applications (AgentStudio is a real TUI app)

### Weaknesses
- More complex than needed for Omicron's transcript viewport use case
- Compositing adds overhead for simple text rendering
- No grapheme/Unicode width handling evident in rendering path
- Win32-specific console driver (NetConsoleDriver) — less portable
- Some `System.Drawing` dependencies (legacy Windows-only)

### Relevance to Omicron
- Window compositing model is overkill for initial transcript viewport
- Desktop background caching pattern is useful for status bar backdrop
- Region-based invalidation could be adapted for viewport dirty tracking
- Pooled collection reuse is a good performance practice

---

## 7. Termina

**Path:** `READ_ONLY/termina/`
**Status:** Mature (v0.7+, production demos including LLM streaming chat)
**Language:** C# (R3 reactive library)
**License:** Apache 2.0

### Architecture

Termina is a **reactive MVVM TUI framework** with declarative layouts and surgical region-based rendering. It integrates with `Microsoft.Extensions.Hosting` for DI and lifecycle management.

### Rendering Pipeline

```
TerminaApplication
  → Layout tree (ILayoutNode hierarchy)
  → Measure: compute desired sizes
  → Arrange: assign positions
  → Render: IRenderContext.WriteAt() per node
  → IAnsiTerminal (low-level ANSI output)
```

### Key Components

#### `ILayoutNode.cs`
```csharp
public interface ILayoutNode : IDisposable {
    SizeConstraint WidthConstraint { get; }
    SizeConstraint HeightConstraint { get; }
    Size Measure(Size available);
    void Render(IRenderContext context, Rect bounds);
}
```
- `IContainerNode` adds `Children`
- `IInvalidatingNode` adds `Observable<Unit> Invalidated` for reactive re-renders
- `IAnimatedNode` adds `Start()`/`Stop()` for timed animations

#### Layout Primitives
- `VerticalLayout`, `HorizontalLayout`, `StackLayout` (overlapping)
- `GridNode`, `PanelNode`, `ModalNode`
- `DynamicLayoutNode` (lazy factory), `KeyedDynamicLayoutNode` (cached per key)
- `ScrollableContainerNode`, `WizardNode`
- `TextNode`, `TextInputNode`, `TextAreaNode`, `SelectionListNode<T>`, `SpinnerNode`
- `StreamingTextNode` (real-time content like LLM output)

#### `IRenderContext.cs`
```csharp
public interface IRenderContext {
    int Width { get; }
    int Height { get; }
    void WriteAt(int x, int y, string text);
    void WriteAt(int x, int y, char c);
    void SetForeground(Color color);
    void SetBackground(Color color);
    void ResetColors();
    void SetDecoration(TextDecoration decoration);
    void ApplyStyle(TextStyle style);
    void Fill(int x, int y, int width, int height, char c = ' ');
    void Clear();
    IRenderContext CreateSubContext(Rect bounds);
}
```

#### `RegionRenderContext.cs`
- Translates relative coordinates to absolute screen positions
- Clips all rendering to region bounds
- Writes directly to `IAnsiTerminal` (immediate-mode ANSI output)

#### `Component.cs`
- Base class for UI components
- `Render()` returns `ILayoutNode`
- `MarkDirty()` + `ContentChanged` (R3 `Observable<Unit>`) for reactive invalidation

#### Input System
- `IInputSource`, `PlatformInputSource`, `VirtualInputSource`
- `EscapeSequenceParser` for VT sequences
- `FocusManager` with `FocusPolicy`
- Events: `KeyPressed`, `MouseEvent`, `MouseScrollEvent`, `ResizeEvent`, `PasteEvent`

#### Streaming Support
- `StreamingTextNode` — optimized for real-time text streaming
- `PersistedStreamBuffer`, `WindowedStreamBuffer` — buffer management
- `StyledSegment`, `StyledLine`, `SpinnerSegment` — rich streaming content

### Strengths
- **Cleanest layout abstraction** — measure/arrange with size constraints
- **Reactive invalidation** — only dirty nodes re-render
- **StreamingTextNode** is directly relevant to Omicron's LLM output use case
- ASP.NET Core-style hosting/routing integration
- `Component` base class with `MarkDirty()` is a good simple pattern
- Good separation: application layer, layout layer, rendering layer, terminal layer

### Weaknesses
- No frame buffer / cell abstraction — writes ANSI directly per node (less efficient for large frames)
- No diffing — full tree render each frame (mitigated by region clipping)
- R3 dependency adds weight
- Documentation-driven analysis (full rendering internals not available in detail)

### Relevance to Omicron
- **Measure/Arrange layout protocol** should be adopted for Omicron's widget system
- `StreamingTextNode` model directly applicable to transcript rendering
- Reactive invalidation via `MarkDirty()` + observable is simpler than full dirty-tracking
- `IRenderContext.WriteAt` + clipping is good for widget rendering
- Input system structure (parser → events → focus manager) is a good model

---

## 8. Diff (Myers Algorithm)

**Path:** `READ_ONLY/Diff/`
**Status:** Reference implementation
**Language:** C#
**License:** BSD-style

### Code

A port of the Myers O(ND) difference algorithm to C#. The `Diff.DiffText()` method compares two string arrays (by line) and returns a list of `Item` structs describing insertions/deletions.

```csharp
public struct Item {
    public int StartA;
    public int StartB;
    public int deletedA;
    public int insertedB;
}
```

### Relevance to Omicron

The Myers algorithm is the foundation of Git's diff and can be applied at multiple levels in Omicron's rendering:

1. **Text-level diff** — comparing transcript text frames to minimize render output
2. **Cell-level diff** — comparing cell buffer arrays (though the per-row scan in Plan 0007 is simpler)
3. **Content block diff** — comparing layout trees

For the frame buffer, the row-scan approach (Spectre.TUI, Plan 0007) is simpler and fast enough. The Myers algorithm could be useful for higher-level content diffing (e.g., markdown document changes).

---

## 9. OpenTUI

**Path:** `READ_ONLY/opentui/`
**Status:** Production (powers OpenCode)
**Language:** Zig core + TypeScript bindings
**License:** Unknown

### Architecture

OpenTUI is a **native TUI core written in Zig** with TypeScript bindings. The native core exposes a C ABI. Key features:

- **Native rendering** — Zig core handles terminal queries, capability detection, and rendering
- **TypeScript bindings** — `@opentui/core` exposes imperative API + primitives
- **Framework bindings** — React reconciler (`@opentui/react`), SolidJS reconciler (`@opentui/solid`)
- **Web renderer** — Three.js-based WebGPU renderer (`@opentui/three`)
- **Terminal startup** — sophisticated capability detection with timeouts, palette detection, tmux detection
- **Font system** — bitmap font formats (block, grid, huge, pallet, shade, slick, tiny)
- **Tree-sitter integration** — syntax highlighting powered by tree-sitter

### Startup Sequence (from `terminal-startup.md`)

1. `createCliRenderer()` resolves streams, geometry, native library
2. `setupTerminal()` writes query sequences (XTVERSION, cursor position, capability queries)
3. 5000ms timeout for async capability responses
4. Palette detection with adaptive strategy (tmux version-aware OSC 4 passthrough)
5. Theme color query responses update renderer state

### Relevance to Omicron

- Validates the C ABI approach if Omicron later wants native performance
- Terminal startup sequence (queries → timeouts → fallbacks) is instructive
- Palette detection strategy with tmux awareness could be referenced
- Cannot directly reuse (different language ecosystem)

---

## 10. Other Codebases (Brief Notes)

### `oh-my-pi`
- Rust shell implementation (brush shell)
- Not a TUI renderer — unrelated

### `pi-mono`
- Empty directory (no C# files found)

### `dirac`
- TypeScript CLI agent (AC Protocol implementation)
- Not a TUI renderer — uses Ink/React for rendering

### `gemini-cli`
- Go CLI tool
- Not analyzed for TUI rendering

### `kilocode`, `codex`, `opencode`, `open-claude-code`
- TypeScript CLI coding agents
- Use stdio-based interaction, not custom TUI renderers
- Some may use Ink or similar React-based terminal rendering

### `npoi`
- .NET port of Apache POI (Office file format manipulation)
- Not a TUI renderer

### `spectre.console.cli`
- CLI argument parsing for Spectre.Console apps
- Not a TUI renderer

### `pi-opencode-provider`
- Integration provider between pi and OpenCode
- Not a TUI renderer

---

## 11. Cross-Cutting Patterns

### 11.1 Cell Buffer + Diff

Most mature TUI frameworks (Spectre.TUI, XenoAtom.Terminal.UI, ConsoleEx) use a **retained-mode cell buffer** with frame-to-frame diffing. The common pattern:

1. Widgets write to an offscreen cell buffer (back buffer)
2. A diff pass compares back buffer vs front buffer
3. Only changed cells are emitted as ANSI sequences
4. Buffers are swapped after rendering

**Plan 0007** adopts this model with `TerminalFrame` + `DifferentialRenderer` + `SwapChain`.

### 11.2 Backend Abstraction

Every framework wraps terminal I/O behind an interface:
- `ITerminalBackend` (XenoAtom.Terminal) — most comprehensive
- `ITerminal` (Spectre.TUI) — simplest, least capable
- `IConsoleDriver` (ConsoleEx) — mid-level
- `IAnsiTerminal` (Termina) — minimal

**Plan 0007's `ITerminalBackend`** aligns with XenoAtom.Terminal's model.

### 11.3 Layout Systems

Two dominant patterns:

1. **Measure/Arrange** (Termina, XenoAtom.Terminal.UI, Plan 0007's widgets):
   - `ILayoutNode.Measure(available) → Size`
   - `ILayoutNode.Arrange(bounds)`
   - `ILayoutNode.Render(context)`

2. **Absolute positioning** (ConsoleEx, Spectre.TUI):
   - Widgets write at specific (x,y) coordinates
   - Less flexible but simpler for fixed layouts

### 11.4 ANSI Encoding

Dedicated ANSI encoder/writer classes:
- `AnsiWriter` (XenoAtom.Terminal, Spectre.Console) — fluent API for ANSI sequences
- `AnsiEncoder` (Plan 0007) — static methods for specific operations

Both emit raw `byte` sequences to an `IBufferWriter<byte>` or `TextWriter`.

### 11.5 Input Handling

All frameworks implement an **event-driven input model**:
- Parse raw bytes from stdin into structured events
- `KeyEvent`, `MouseEvent`, `ResizeEvent` are universal
- `IAsyncEnumerable<TerminalEvent>` for async consumption (Plan 0007, XenoAtom.Terminal)

### 11.6 Terminal Lifecycle

Critical pattern for crash safety:
1. Save terminal state on entry
2. Enter alternate screen + raw mode + hide cursor
3. On exit (normal or crash): restore all state
4. Use `AppDomain.ProcessExit` + `Console.CancelKeyPress` as safety nets

**XenoAtom.Terminal** uses ref-counted `TerminalScope` for each mode change — safest pattern.

---

## 12. Recommendations for Omicron

### Confirm Plan 0007 Direction

The cell-buffer + diff model chosen in Plan 0007 is validated by Spectre.TUI, XenoAtom.Terminal.UI, and ConsoleEx. Proceed with this approach.

### Adopt Best Practices from Each Codebase

| Feature | Source | Recommendation |
|---------|--------|---------------|
| **Cell as struct** | Plan 0007 | Keep `RenderCell` as struct with `GlyphRef` + `TextStyle` (avoids GC pressure) |
| **SwapChain** | Spectre.TUI | Use double-buffer swap chain for frame diffing |
| **Capability detection** | XenoAtom.Terminal | Model `TerminalCapabilities` with color level, feature flags, terminal name |
| **Ref-counted scopes** | XenoAtom.Terminal | Use `TerminalScope` for nested alternate screen / raw mode / cursor hide |
| **Measure/Arrange layout** | Termina | Adopt `ILayoutNode` with `Measure`/`Arrange`/`Render` for widgets |
| **Streaming text** | Termina | Model `StreamingTextNode` for transcript viewport rendering |
| **Region-based invalidation** | ConsoleEx, Termina | Track dirty regions per-widget, not full-frame redraws |
| **AnsiWriter pattern** | XenoAtom.Terminal | Use fluent `AnsiWriter` on `IBufferWriter<byte>` |
| **VtInputDecoder** | XenoAtom.Terminal | Adapt VT input decoder for key/mouse/resize events |
| **Inline mode** | Spectre.TUI, XenoAtom.Terminal.UI | Support both fullscreen and inline (reserved lines) modes |
| **Desktop background caching** | ConsoleEx | Cache static background for status bars and panels |
| **FPS targeting** | Spectre.TUI | Add optional target FPS for power efficiency |
| **Myers diff** | Diff codebase | Consider for higher-level content diffing (future) |

### Specific Architecture Suggestions

#### Backend Layer (Phase C)

Adopt XenoAtom.Terminal's `ITerminalBackend` interface structure but keep it **smaller** — Plan 0007's version is adequate. Add capability detection similar to XenoAtom.Terminal's `TerminalCapabilities`.

#### Frame Buffer (Phase D)

Use Spectre.TUI's `SwapChain` pattern with Plan 0007's `RenderCell` struct. Key difference: Plan 0007 uses `GlyphRef` (ASCII inline, non-ASCII interned) rather than string-based `Cell.Symbol`. This is **better** — no string allocations per cell.

#### Layout System (Phase E)

Adopt Termina's `ILayoutNode` with `Measure`/`Arrange`/`Render` protocol. This gives Omicron:
- Composable widgets
- Automatic size computation
- Clipping via sub-contexts
- Dirty notification via `IInvalidatingNode`

#### Terminal Lifecycle

Implement XenoAtom.Terminal's ref-counted scope pattern:
```csharp
using var altScreen = terminal.UseAlternateScreen();
using var rawMode = terminal.UseRawMode();
using var hiddenCursor = terminal.HideCursor();
```
This guarantees restoration even with nested or interleaved usage.

### Open Questions for Omicron

1. **DEC 2026 synchronization** — XenoAtom.Terminal.UI uses synchronized output sequences for glitch-free updates. Worth investigating for Omicron's streaming transcript viewport.

2. **Glyph intern table eviction** — Plan 0007 proposes hard cap + full frame reset. Consider if an LRU eviction or generational approach would be better for long-running sessions.

3. **Inline mode for progress/status** — Omicron may want to display progress or status inline (within existing scrollback) without entering fullscreen mode. Both Spectre.TUI's `InlineMode` and XenoAtom.Terminal.UI's `Terminal.Live()` support this.

4. **AOT/trimming compatibility** — XenoAtom.Terminal and Termina target modern .NET with AOT support. Omicron's rendering stack should minimize reflection and avoid runtime code generation.

---

*End of Report 0001*
