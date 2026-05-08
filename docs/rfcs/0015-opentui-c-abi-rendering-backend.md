# RFC 0015: OpenTUI C ABI Rendering Backend

Status: **Research**

## Purpose

Evaluate OpenTUI's native Zig terminal UI core as an Omicron TUI rendering backend through its C ABI.

This RFC is the companion to RFC 0003's custom renderer path. RFC 0003 remains the baseline rendering architecture and fallback implementation. This RFC defines the alternate backend track: reuse OpenTUI for terminal correctness, buffers, rendering, layout primitives, input handling, and differential output where it fits Omicron's architecture.

## Motivation

OpenTUI is a native terminal UI core written in Zig with TypeScript bindings. Its project documentation states that the native core exposes a C ABI usable from any language. It powers OpenCode in production and is also intended to power other terminal products.

A local reference clone is available at:

```text
READ_ONLY/opentui/
```

Important source:

```text
READ_ONLY/opentui/packages/core/src/zig/lib.zig
```

Initial inspection of `lib.zig` shows a broad exported ABI covering renderer lifecycle, terminal setup/restore, optimized buffers, direct buffer pointers, drawing, hit testing, text buffers, editor buffers, selection, syntax styles, and callbacks. This makes a C# binding plausible.

## Goals

- Determine whether OpenTUI can be used directly from C# through P/Invoke/`LibraryImport`.
- Preserve Omicron's semantic UI, transcript virtualization, session model, plugins, remoting, and persistence boundaries.
- Measure whether OpenTUI can satisfy Omicron's high-throughput transcript and terminal-pane workloads.
- Decide whether OpenTUI should be the default TUI backend, an optional backend, or only a reference implementation.
- Avoid prematurely committing to a custom renderer if OpenTUI can save substantial work.

## Non-Goals

- Rewriting Omicron in TypeScript/SolidJS.
- Adopting OpenCode's frontend architecture wholesale.
- Letting renderer backend choices leak into `Omicron.Core` or plugin APIs.
- Requiring OpenTUI if packaging, ABI stability, or performance does not meet Omicron's needs.

## Relationship to the Custom Renderer Track

The rendering decision should be tracked as two implementation paths:

```text
Track A: Custom renderer
  Defined primarily by RFC 0003 and RFC 0004.
  Omicron owns terminal backend, buffers, diffing, input, layout, and transcript viewport.

Track B: OpenTUI backend
  Defined by this RFC.
  Omicron owns semantic/application state, but delegates native terminal rendering infrastructure to OpenTUI where practical.
```

Both tracks must satisfy the same product-level requirements:

- app-owned scrollback;
- virtualized transcript viewport;
- Unicode-aware layout;
- streaming-friendly updates;
- stable non-yanking scroll behavior;
- rich markdown/code rendering;
- virtual terminal panes;
- multi-agent/remoting UI states;
- clean terminal restoration on crash/cancel.

## Candidate Architecture

```text
Omicron.Core / Omicron backend events
  ↓
Omicron.UI.Abstractions semantic content
  ↓
Omicron.Frontend.Tui state, routing, command handling
  ↓
Omicron.OpenTui safe C# wrapper
  ↓
Omicron.OpenTui.Native P/Invoke bindings
  ↓
OpenTUI Zig C ABI
  ↓
Host terminal
```

Potential packages:

```text
Omicron.OpenTui.Native
  raw generated/manual P/Invoke bindings
  native library loading
  raw extern structs/enums

Omicron.OpenTui
  SafeHandle wrappers
  UTF-8 marshaling helpers
  RGBA/style helpers
  renderer/buffer/text/editor wrappers
  callback and error handling

Omicron.Frontend.Tui
  Omicron-specific TUI state and components
  adapter from semantic UI/transcript/terminal panes to OpenTUI primitives
```

## Integration Levels

### Level 1: Terminal Backend and Frame Buffers

Omicron keeps its own layout and transcript viewport, but writes into OpenTUI buffers and asks OpenTUI to render/diff/output.

Relevant ABI surface includes:

```text
createRenderer / destroyRenderer
setupTerminal / restoreTerminalModes
getNextBuffer / getCurrentBuffer
createOptimizedBuffer / destroyOptimizedBuffer
bufferDrawText / bufferSetCell / bufferFillRect / bufferDrawBox
bufferGetCharPtr / bufferGetFgPtr / bufferGetBgPtr / bufferGetAttributesPtr
render / resizeRenderer
```

This is the least invasive option and likely the best first spike.

### Level 2: Layout/Renderable Backend

Omicron maps its TUI widgets into OpenTUI renderables/layout primitives. This may save more work but increases coupling to OpenTUI's object/component model.

This should only be pursued if the C ABI exposes the needed primitives ergonomically and efficiently.

### Level 3: Text and Editor Backend

Omicron evaluates OpenTUI's text and editor primitives for transcript and prompt-input workloads.

Relevant ABI surface includes:

```text
createTextBuffer
textBufferAppend
createTextBufferView
textBufferViewSetViewport
textBufferViewSetWrapWidth
bufferDrawTextBufferView

createEditBuffer
createEditorView
bufferDrawEditorView
```

This may save prompt-editor and wrapping work, but must not compromise Omicron's persistence model, content block model, markdown/code rendering, or transcript virtualization requirements.

## C# Binding Requirements

Bindings should be layered and safe:

- raw native bindings hidden behind `internal` APIs;
- `SafeHandle` for renderer, buffer, text buffer, views, edit buffers, and syntax styles;
- explicit ownership rules for every pointer returned by native code;
- UTF-8 pointer+length marshaling, avoiding managed string churn on hot paths;
- callback lifetime management for log/event callbacks;
- deterministic terminal restoration in `Dispose`/crash paths;
- native library resolution compatible with `dotnet publish`.

Hot paths must avoid one P/Invoke call per glyph/token. The spike should prefer bulk calls or direct buffer pointer writes.

## Spike Plan

### Spike 1: Native Load and Hello World

- Build or obtain OpenTUI native libraries.
- Load from C#.
- Create renderer.
- Enter alternate screen.
- Draw text and a box.
- Render.
- Restore terminal cleanly.

### Spike 2: Input and Terminal Lifecycle

- Key input.
- Ctrl+C/interrupt behavior.
- Resize events.
- Mouse events if available.
- Terminal title and capabilities.
- Crash/cancel terminal restoration.

### Spike 3: Buffer Throughput

- Draw a full-screen frame using `bufferDrawText`/`bufferSetCell`.
- Draw via direct buffer pointers where safe.
- Measure C#↔native call overhead.
- Compare bulk writes vs per-cell calls.

### Spike 4: Transcript Workload

Benchmark:

- 20–100 streaming chunks/sec;
- 100k+ lines or equivalent token volume;
- scroll while streaming;
- resize while streaming;
- long unbroken lines;
- emoji/CJK/combining marks;
- markdown and code blocks;
- large tool-output bursts.

### Spike 5: TextBuffer/EditBuffer Evaluation

- Determine whether OpenTUI text buffers can satisfy transcript view requirements.
- Determine whether OpenTUI edit buffers can satisfy prompt input requirements.
- Verify selection/copy behavior.
- Verify syntax/highlight integration cost.

### Spike 6: Packaging and Licensing

- Package native binaries for Windows/Linux/macOS x64/arm64.
- Validate `dotnet publish` integration.
- Check license compatibility and attribution requirements.
- Decide whether to pin a version or maintain an internal fork.

## Decision Criteria

OpenTUI should be adopted as the default TUI backend only if:

- C# bindings are maintainable;
- packaging is practical;
- terminal lifecycle is robust;
- transcript and terminal-pane benchmarks meet targets;
- direct/bulk buffer writes avoid excessive FFI overhead;
- ABI stability is acceptable or an internal fork is justified;
- Omicron's semantic UI and persistence/remoting boundaries remain clean.

If these criteria are not met, OpenTUI remains a reference implementation and Omicron continues with the custom renderer track.

## Initial Scan: Likely Failure Points

A first pass over `READ_ONLY/opentui/packages/core` suggests OpenTUI is viable, but several risks must be validated early.

### TypeScript Owns More Than the Native Core

The Zig C ABI is broad, but OpenTUI's production stack still has substantial TypeScript logic:

- renderable tree and component lifecycle;
- Yoga-based layout via `yoga-layout`;
- stdin byte parsing and key/mouse/paste decoding;
- focus routing and keyboard handler priority;
- selection and clipboard orchestration;
- render scheduling/frame pacing;
- console overlay and stdout capture modes;
- Tree-sitter/markdown renderables.

Therefore, using OpenTUI from C# does not automatically give Omicron the full OpenCode/OpenTUI frontend stack. The safest first integration is Level 1: native renderer/buffers/terminal lifecycle, while Omicron owns layout/input routing or ports only the pieces it needs.

### ABI Stability and Versioning

`lib.zig` exposes many functions, but the scan did not identify an explicit ABI version/compatibility function beyond build-option queries. Omicron should require either:

- an upstream ABI version contract;
- a generated ABI manifest checked into Omicron;
- or a pinned/forked OpenTUI revision.

### Build and Packaging Constraints

OpenTUI currently builds native libraries with Zig 0.15.2 and packages prebuilt binaries through npm optional packages for six targets:

```text
darwin-x64 / darwin-arm64
linux-x64 / linux-arm64
win32-x64 / win32-arm64
```

This is promising, but Omicron must prove consumption without Bun/npm at runtime and integrate native binaries into `dotnet publish`.

### Direct Buffer Pointer Hazards

The ABI exposes direct pointers for char/fg/bg/attribute planes:

```text
bufferGetCharPtr
bufferGetFgPtr
bufferGetBgPtr
bufferGetAttributesPtr
```

This is valuable for bulk C# writes, but raw writes may bypass OpenTUI's grapheme/link tracking. It may be safe for ASCII/simple cells, but complex graphemes, wide characters, hyperlinks, and continuation cells may require `bufferDrawText`, `bufferSetCell`, or another native path. The spike must determine which writes are legal and when cached pointers become invalid after resize/realloc.

### Input Parsing Is Not Native-Only

The robust stdin parser is TypeScript (`src/lib/stdin-parser.ts`), not obviously exposed as a C ABI service. C# may need to:

- keep Omicron's own input parser;
- port/adapt OpenTUI's parser;
- or add/request native/parser ABI support.

This is a major consideration if the goal is to avoid custom terminal input work.

### Layout Is TS/Yoga-Side

The renderable tree uses `yoga-layout` from TypeScript. The C ABI scan did not show direct native Yoga/layout exports. If Omicron wants OpenTUI layout behavior, it may need to reimplement a layout adapter in C#, bind Yoga separately, or use only OpenTUI's lower-level buffer drawing APIs.

### Threading and Callbacks

OpenTUI supports optional threaded rendering and global callbacks for logs/events. C# bindings must handle delegate lifetime, callback threading, and shutdown ordering carefully. Initial spikes should disable threaded rendering until callback safety is proven.

### Global State and Multi-Renderer Behavior

The native layer uses global allocator/pool/callback state in several places. Omicron should assume one active renderer for the first spike and explicitly test destroy/recreate behavior before supporting multiple TUI renderers in one process.

### Fixed Output Buffer Limits

`renderer.zig` uses preallocated native output buffers. Large full repaints, unusual terminal sizes, or pathological output could hit buffer limits. This should be included in stress tests.

## Open Questions

- Is OpenTUI's C ABI versioned/stable enough for external consumers?
- Should bindings be handwritten, generated, or produced from a small checked-in ABI manifest?
- Can OpenTUI be consumed without Bun/npm in Omicron's build pipeline?
- Should Omicron vendor/fork OpenTUI or consume prebuilt releases?
- Can OpenTUI's text buffer model coexist with Omicron's UTF-8 transcript store, or would it duplicate storage?
- Which side should own Unicode width policy if OpenTUI is used?
- Can virtual terminal pane screens be efficiently copied into OpenTUI buffers?

## Design Decisions

1. OpenTUI is a first-class rendering backend candidate.
2. Omicron must keep semantic UI and application state independent from the renderer backend.
3. The first spike should target Level 1: terminal backend and frame buffers.
4. TextBuffer/EditBuffer adoption is optional and benchmark-driven.
5. The custom renderer path remains the fallback until OpenTUI proves binding, performance, and packaging viability.
