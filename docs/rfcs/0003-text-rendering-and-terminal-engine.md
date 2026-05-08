# RFC 0003: Text Storage, Unicode Layout, and Terminal Rendering

Status: **Canonical**

## Purpose

Define Omicron's text and terminal rendering strategy. Large LLM transcripts must remain responsive under streaming output, large histories, Unicode text, code blocks, diffs, and tool logs.

## Why Not One Giant String

A .NET `string` is UTF-16. A `char` is a 16-bit UTF-16 code unit, not a user-perceived character and not a terminal cell.

```csharp
string emoji = "😄";
Console.WriteLine(emoji.Length); // 2
```

Terminal layout requires distinct concepts:

```text
UTF-8 byte          storage / I/O
UTF-16 char         .NET string code unit
Unicode scalar      System.Text.Rune
Grapheme cluster    user-perceived character
Terminal cell width what a TUI needs
```

No raw encoding solves terminal layout by itself.

## UTF-8 Transcript Storage

Large transcript text should use append-only UTF-8 chunks. For high-volume/hot-path data, these chunks should be backed by native/off-heap slabs as described in RFC 0011 so transcript storage does not create massive managed-string or managed-array GC pressure.

```csharp
public sealed class Utf8TextStore
{
    public long LengthBytes { get; }

    public TextPosition Append(ReadOnlySpan<byte> utf8);
    public ReadOnlyMemory<byte> GetChunk(int chunkIndex);
    public Utf8Slice Slice(long byteOffset, int byteLength);
}
```

Benefits:

- natural for model/network streams
- efficient for ASCII-heavy output
- avoids large string allocations
- reduces GC pressure when backed by native slabs
- supports append-only history
- enables byte-offset indexes
- aligns with terminal output paths

UTF-8 is storage/transport encoding, not the layout unit.

## Layout Indexes

Keep indexes over the UTF-8 store rather than slicing strings.

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

## Grapheme and Terminal Cell Model

Terminal columns are not bytes, UTF-16 chars, or Unicode scalars.

```csharp
public readonly record struct TerminalCluster(
    long ByteOffset,
    int ByteLength,
    int RuneCount,
    int CellWidth);
```

For rendered frames:

```csharp
public struct RenderCell
{
    public GlyphRef Glyph;
    public byte Width;
    public TextStyle Style;
}
```

`GlyphRef` can point to:

- ASCII fast-path character
- interned grapheme cluster
- replacement glyph
- box drawing character
- ellipsis
- cursor placeholder
- synthesized UI symbol

## Terminal Rendering Pipeline

The renderer is a small rendering engine, not `Console.WriteLine` loops.

```text
App state
  ↓
Semantic UI tree / transcript viewport / terminal pane model
  ↓
TUI layout
  ↓
Visible text and pane layout
  ↓
FrameBuffer
  ↓
Diff previous frame vs next frame
  ↓
ANSI + UTF-8 bytes
  ↓
Host terminal
```

## Terminal Backend

```csharp
public interface ITerminalBackend : IDisposable
{
    TerminalSize Size { get; }
    IAsyncEnumerable<TerminalEvent> ReadEvents(CancellationToken cancellationToken);
    IBufferWriter<byte> Output { get; }
    void Flush();
}
```

Responsibilities:

- enter/exit alternate screen
- enable/disable raw mode
- hide/show cursor
- parse keyboard input
- parse mouse input
- track resize
- provide stdout/stderr byte writers
- restore terminal on crash/cancellation

## Frame Buffer

```csharp
public sealed class TerminalFrame
{
    public int Width { get; }
    public int Height { get; }
    public RenderCell[] Cells { get; }

    public ref RenderCell this[int row, int col] => ref Cells[row * Width + col];
}
```

## Differential Renderer

Maintain previous/current frames and emit only changed runs.

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

## Hot Path Rules

Avoid in render loops:

```text
Console.WriteLine(...)
Console.Clear()
Encoding.UTF8.GetString(...)
string.Concat(...)
string.Join(...)
Substring(...)
Regex over giant transcript text
```

Prefer:

```text
ReadOnlySpan<byte>
ReadOnlyMemory<byte>
ArrayPool<byte>
IBufferWriter<byte>
Stream.Write(ReadOnlySpan<byte>)
Utf8Formatter
incremental parsing
cached layout
dirty regions
```

C# UTF-8 literals are useful for static ANSI sequences:

```csharp
stdout.Write("\x1b[2J"u8);
stdout.Write("\x1b[H"u8);
stdout.Write("\x1b[?25l"u8);
stdout.Write("\x1b[?25h"u8);
```

## Performance Principles

1. Render only visible cells.
2. Do not rewrap everything on each token.
3. Treat resize as an expensive special case.
4. Separate transcript storage from frame rendering; visible cells should reference byte slices/handles and layout metadata rather than owning strings.
5. Use dirty regions and frame diffing.
6. Avoid clearing host terminal scrollback unless explicitly requested.

## Rendering Backend Strategy

Omicron should keep the semantic UI, transcript virtualization, markdown/code pipeline, and agent-facing state model independent from any particular terminal renderer. The terminal rendering backend is now an explicit research decision with two viable tracks:

```text
Track A: Omicron custom renderer
  Omicron owns terminal backend, frame buffers, diffing, input, and layout primitives.

Track B: OpenTUI native backend
  Omicron owns semantic UI and transcript policy, but uses OpenTUI's Zig core through its C ABI for terminal correctness, buffers, layout/rendering primitives, input, and diff output where appropriate.
```

The RFCs should preserve both options until benchmarks and binding spikes decide the default. Track A remains the portable fallback and design baseline. Track B may save substantial work if the C ABI supports Omicron's hot paths efficiently.

## Existing Library Position

Terminal.Gui and Spectre.Console are useful, but not foundations for Omicron's transcript hot path.

### Terminal.Gui

Good for:

- forms
- menus
- dialogs
- tables
- desktop-like terminal UI

Potential issue:

- may not be ideal as the core renderer for massive streaming transcripts unless heavily customized and benchmarked.

### Spectre.Console

Good for:

- tables
- progress bars
- status displays
- prompts
- pretty one-shot output

Potential issue:

- not the right foundation for a fullscreen, virtualized, diff-rendered LLM transcript UI.

### XenoAtom.Terminal.UI

XenoAtom.Terminal.UI can be studied or forked as scaffolding, but Omicron must preserve control over:

- renderer/diff layer or renderer backend abstraction
- text layout
- transcript virtualization
- virtual terminal pane rendering

### OpenTUI

OpenTUI is a native terminal UI core written in Zig with TypeScript bindings. Its project documentation states that the native core exposes a C ABI usable from any language. It powers OpenCode in production and is a serious candidate for Omicron's native terminal rendering backend.

A local reference clone is kept at:

```text
READ_ONLY/opentui/
```

Important source area:

```text
READ_ONLY/opentui/packages/core/src/zig/lib.zig
```

The exported C ABI includes renderer lifecycle, terminal setup/restore, optimized buffers, direct buffer pointers, text drawing, box/grid drawing, hit grids, text buffers, text-buffer views, edit buffers, editor views, selection, syntax styles, and callbacks. This makes C# bindings plausible through P/Invoke/SafeHandle wrappers.

Recommended stance:

> Existing general-purpose TUI libraries are references or scaffolding. OpenTUI is stronger than a reference: it is a candidate renderer backend to be evaluated through a dedicated C ABI binding spike.

## Track A: Custom Renderer Direction

The robust fallback route is:

```text
custom terminal rendering core
+ borrowed/forked framework ideas
+ custom transcript viewport
+ shared core/UI abstractions
```

The custom part is what differentiates Omicron:

- high-throughput transcript rendering
- internal scrollback
- UTF-8 storage
- Unicode-aware layout
- diffed visible viewport
- streaming-friendly updates
- virtual terminal pane rendering

## Track B: OpenTUI C ABI Backend

The OpenTUI track should evaluate using the Zig core directly from C#:

```text
Omicron.Frontend.Tui
  ↓ C# state/widgets/adapters
Omicron.OpenTui
  ↓ safe C# wrapper
Omicron.OpenTui.Native
  ↓ P/Invoke / LibraryImport
OpenTUI Zig C ABI
  ↓
terminal
```

Possible use levels:

```text
terminal backend only:
  OpenTUI handles terminal setup, input, buffers, and final diffed output.
  Omicron owns layout and frame construction.

rendering + layout backend:
  Omicron maps semantic widgets into OpenTUI renderables/layout primitives.

text/editor backend:
  Omicron additionally evaluates OpenTUI TextBuffer/View and EditBuffer/EditorView for transcript viewport and prompt input.
```

OpenTUI must not force Omicron to give up:

- semantic UI boundaries from RFC 0002;
- transcript virtualization and follow-tail behavior from RFC 0004;
- rich content parsing/highlighting model from RFC 0013;
- virtual terminal semantics from RFC 0005;
- remoting/multi-agent state from RFC 0014.

### OpenTUI Binding Spike

The spike should answer:

1. Can C# load the native OpenTUI library and cleanly enter/exit alternate screen?
2. Can C# create a renderer, get next/current buffers, draw text/boxes/cells, and render?
3. Can input, resize, mouse, clipboard, and terminal capability events be consumed safely from C#?
4. Can Omicron batch updates to avoid one FFI call per glyph/token?
5. Can C# efficiently write large custom transcript/terminal regions through direct buffer pointers or bulk draw calls?
6. Can OpenTUI TextBuffer/View satisfy transcript viewport needs without losing Omicron's persistence/layout model?
7. Can OpenTUI EditBuffer/EditorView satisfy prompt-editor needs?
8. Can native binaries be packaged with `dotnet publish` for Windows/Linux/macOS x64/arm64?
9. Is the C ABI stable enough to target, or should Omicron pin/fork a known OpenTUI version?
10. Are license and redistribution terms compatible?

Success criteria:

- hello-world C# render;
- clean terminal restore on crash/cancel;
- keyboard and resize events;
- basic split layout/status/input shell;
- streaming transcript benchmark at 20–100 chunks/sec;
- scroll while streaming;
- resize while streaming;
- high-volume terminal-pane-like output benchmark;
- measured C#↔native call overhead;
- packaging proof of concept.

## Internal Fork Strategy

If Omicron forks XenoAtom, OpenTUI, or a similar library internally, do not immediately rewrite everything.

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

Benchmark scenarios should include:

- 100k–1M lines or equivalent token volume
- 20–100 streaming chunks per second
- terminal resize spam
- scroll while streaming
- emoji / CJK / combining marks
- markdown and code blocks
- long unbroken lines
- large tool output bursts
- diff-heavy output
- autocomplete in input editor
- high-volume virtual terminal output
