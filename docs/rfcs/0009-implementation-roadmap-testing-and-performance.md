# RFC 0009: Implementation Roadmap, Testing, and Performance

Status: **Canonical**

## Purpose

Define the phased plan for evolving the current Omicron MVP into the architecture described by the RFC set.

The goal is incremental hardening, not a rewrite for its own sake.

## Current Codebase Baseline

See [IMPLEMENTATION-BASELINE.md](IMPLEMENTATION-BASELINE.md) for the current implementation inventory. In short, Omicron is presently a .NET 10 console MVP with:

- `Omicron.Core`: stateful agent loop, simple streaming events, messages/models, provider abstraction, API-shape routing, TOML config, direct file/shell tools;
- `Omicron.CLI`: console menu, model discovery, config UI, chat loop, streaming display, Escape cancellation, tool-result truncation;
- `Omicron.Core.Tests`: xUnit coverage for agent/message/tool/provider behavior.

The MVP is best classified as **Phase Set 0 partially complete**: core agent/provider/tool plumbing exists, but the durable event stream, package split, command/permission/plugin abstractions, text/rendering foundations, persistence/workspace, sandboxing, remoting, and TUI/GUI frontends are not yet implemented.

## Dependency Analysis

See [DEPENDENCY-ANALYSIS.md](DEPENDENCY-ANALYSIS.md) for the full dependency graph between RFCs, parallel-track analysis, and content-migration recommendations. The phases below are derived from that analysis.

## Implementation Phase Sets

The phases are organized into **parallelizable tracks** within each phase set. Cross-track coordination points are noted.

### Phase Set 0: Foundations (parallelizable)

Three independent tracks that can start simultaneously:

**Track 0a — RFC 0001: Core and Event Stream**

Current status:

- Partially implemented: `Omicron.Core`, stateful `Agent`, `Message`/`Model`, simple `Tool`, `IChatProvider`, provider/API-shape routing, streaming UI events, and headless tests.
- Not yet implemented: durable typed event records, stable IDs/sequences, replay-safe session state, command interface, permission interface, plugin registration, and the full identifier set.

Build/stabilize:

- `Omicron.Core`
- Omicron agent event model
- session state
- simple tool interface
- command interface
- plugin registration
- headless test harness
- identifiers for sessions, agents, backend hosts, attachments, tasks, shells, sandboxes, and tool calls

Goal:

> Omicron can run and emit structured events without any UI.

**Track 0b — RFC 0011: Rust/C# Interop and FFI**

Build/spike:

- native interop conventions and ABI versioning
- C ABI + csbindgen proof of concept
- UniFFI + uniffi-bindgen-cs proof of concept
- native UTF-8 slab/buffer prototype
- safe C# wrapper pattern over generated bindings
- CI packaging for Windows/Linux/macOS native libraries
- error, memory ownership, cancellation, and handle lifetime conventions

Goal:

> Omicron can add Rust-backed subsystems without accumulating ad hoc FFI approaches.

**Track 0c — RFC 0003 (Text) + RFC 0013 (Content Foundations) + RFC 0015 (OpenTUI Spike)**

Build/spike:

- `Utf8TextStore` (native-slab-backed)
- grapheme/terminal-cell model
- layout indexes (block, logical line, wrapped line)
- terminal backend interface (raw mode, alternate screen, resize, input)
- frame buffer and cell model
- differential renderer baseline
- OpenTUI C ABI binding spike: native load, hello-world render, input/resize, basic buffer drawing
- **content parsing foundations**: language detection from file extensions/shebangs, code block fence language extraction, basic syntax scope model

Goal:

> Omicron can store, index, and display large UTF-8 text efficiently, identify language for code blocks, and make an evidence-based early decision on whether OpenTUI can serve as the native terminal backend.

### Phase Set 1: Independent Capabilities (parallelizable)

Build the independent middle layers atop the foundations:

**Track 1a — RFC 0002 (UI) + RFC 0013 (Content Block Model)**

Build:

- `ContentBlock` model (including `CodeBlock`, `MarkdownBlock`)
- `UiNode` model
- panel provider interface
- status item provider interface
- notification model
- plugin manifest and capability declaration model
- permission system
- command system (semantic IDs shared across frontends)
- host ABI shape for tools, commands, events, panels, and status items
- explicit backend/session/agent target scoping for commands, plugins, and permissions
- **content block rendering stubs**: block type dispatch for TUI and GUI renderers

Goal:

> Plugins and agents can contribute structured content and panels; content blocks can be rendered by frontends.

**Track 1b — RFC 0006: Persistence and Workspace Foundations**

Build:

- session catalog/history store
- append-only event log
- session snapshot/checkpoint model
- basic host-backed `IWorkspaceFileSystem`
- workspace snapshot manifest format
- workspace transaction/diff model

Goal:

> Omicron can resume, replay, checkpoint, and diff sessions/workspaces before UI complexity grows.

**Track 1c — RFC 0004: Transcript Viewport and App Scrollback**

Build:

- transcript block model
- append-only UTF-8 transcript store (uses `Utf8TextStore` from 0c)
- message/block index
- basic wrapping
- internal scrollback (app-owned)
- viewport state (follow-tail mode, scroll position)
- streaming append with non-yanking behavior
- minimal TUI widget system (rect, stack, split)

Goal:

> Large streaming transcripts are usable with app-owned scrollback.

### Phase Set 2: TUI Integration

**Phase 2a — TUI Shell**

Integrate foundations into a working fullscreen TUI:

- terminal backend + differential renderer or OpenTUI backend selected from 0c spike
- transcript viewport with scrollback (1c)
- input system with key mappings to semantic commands (1a)
- status bar
- basic app layout (transcript + status + input)

**Phase 2b — Rich Content (RFC 0013 Markdown + Syntax Highlighting)**

Add rendering for structured content:

- **incremental markdown parser**: CommonMark + GFM, streaming-aware, safe-commit-point resumption
- markdown AST → content block model bridge
- **syntax highlighting engine**: Tree-sitter integration via Rust FFI (or direct .NET if available)
- syntax theme system: TextMate scope model, theme file loading, dark/light defaults
- highlighted code block rendering in TUI frame buffer
- tool-call panels, diff blocks, tables/trees
- fallback regex-based tokenizer for languages without Tree-sitter grammar

Goal:

> Omicron has a working interactive TUI with incremental markdown parsing and syntax-highlighted code blocks.

### Phase Set 3: Safety and Extensibility (parallelizable)

**Track 3a — RFC 0007: Sandboxing Foundations**

Build:

- `Omicron.Sandboxing` policy/provider interfaces
- explicit local/no-sandbox provider for development
- execution broker with audit events
- workspace transaction/overlay integration
- risk explanation model for approvals
- evaluation spike for Codex sandbox runtime, zerobox, and heel/leash-style providers

Goal:

> Omicron can broker command/code execution consistently before committing to a specific OS sandbox provider.

**Track 3b — RFC 0012 (Edit Harness subset): High-Reliability Edits**

Build/spike:

- hashline edit prototype (stateless line-hash anchors)
- stateful single-token anchor edit prototype (Dirac-style)
- anchor state manager and anchor table
- Myers diff reconciler for anchor preservation
- optimistic write preconditions (observed file hash, snapshot ID, anchor version)
- model-actionable structured error messages
- multi-file batch edit transaction support
- AST/context-curation spike for one or two languages

Goal:

> Omicron applies edits through a high-reliability, token-efficient harness.

**Track 3c — RFC 0010: WASM Plugin Runtime**

Build/spike:

- `Omicron.Plugins.Wasm` host using Wasmtime
- AssemblyScript SDK/prototype bindings
- plugin manifest loading and capability review
- JSON-based v1 host ABI for registration and event callbacks
- remote-backend/multi-agent aware plugin host contexts
- memory/fuel/timeout limits
- plugin-scoped persistence
- sample tool/command/panel plugin compiled to WASM
- feasibility benchmark for C# WASM AOT plugin size/startup/memory

Goal:

> Third-party plugins can run with capability-limited host bindings instead of full in-process authority.

### Phase Set 4: Advanced Agent Features (parallelizable)

**Track 4a — RFC 0005: Embedded Virtual Terminal**

Build:

- PTY/process adapter abstraction
- VT/ANSI parser
- emulated terminal screen and scrollback buffer
- terminal pane model with tabs/splits
- TUI pane renderer backed by normal frame renderer
- AI command suggestion/insert flow with approval gates

Goal:

> Omicron can host manual shell sessions in tmux-like panes.

**Track 4b — RFC 0012 (Sub-Agent Orchestration subset): Parallel Agents**

Build:

- `AgentCoordinator` with active/queued tasks
- sub-agent task lifecycle and event types
- structured output contract (no raw transcript leakage into parent context)
- VFS lock manager (read/write/intent/tree locks)
- workspace views (read-only, transactional overlay, shared write, sandboxed)
- per-agent budgets (tokens, time, tool calls)
- cancellation trees from parent to child tasks
- sub-agent dashboard/debug events

Goal:

> Omicron can run parallel agents safely with workspace isolation.

**Track 4c — RFC 0014: Remoting Protocol Foundations**

Build/spike:

- frontend/backend process split over in-process or local IPC transport
- MessagePack-oriented binary frame protocol
- target addressing for backend/session/agent/task/terminal/operation ids
- channel open/close, priority scheduling, and flow control
- per-agent subscription and attention modes: foreground, visible, background, quiet, hidden
- reconnect by session/agent event cursors
- protocol tests for noisy multi-agent streams and cancellation latency

Goal:

> Omicron can route events and controls for multiple active agents through a transport-neutral frontend/backend boundary.

### Phase Set 5: Alternative Frontend and Polish

**Phase 5a — RFC 0008 + RFC 0014: GUI Frontend and Multi-Agent Dashboard**

Build:

- GUI session view model
- backend/session/agent inventory view models
- virtualized transcript list
- renderers for content blocks
- shared command bindings with explicit agent targets
- plugin panel host
- GUI terminal pane host
- multi-agent dashboard and split-view observation

Goal:

> Same core powers a GUI app.

**Phase 5b — RFC 0014: Remote Backend Transports**

Build/spike:

- SSH bootstrap + localhost-bound remote backend
- SSH local port forwarding transport
- remote install/update/discovery metadata
- detached remote agent reattach
- remote backend health/inventory
- evaluate TCP/WebSocket, QUIC/WebTransport, VPN/mesh, rendezvous, and relay modes

Goal:

> Omicron can run agents on another host and attach/reconnect through a structured protocol instead of shell control.

**Phase 5c — Advanced Features**

Build:

- command palette
- settings UI
- plugin marketplace/local discovery
- WASM plugin package signing/trust UX
- theme system
- session replay viewer
- search
- collapsible tool blocks
- inline diffs
- workspace snapshot browser
- session branching UI
- snapshot compare/restore workflows
- sandbox provider diagnostics/settings
- per-workspace sandbox policy profiles
- browser-side plugin runtime research spike
- sub-agent workflow designer
- remote backend manager
- multi-agent notification center
- anchor/AST edit diagnostics UI
- workspace lock browser

## Example End-to-End Flows

### User submits prompt

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

### Model streams response

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

### Tool call starts

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

### Tool modifies workspace

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

### AI assists with shell command

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

### Sandboxed command/code execution

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

### Parent agent launches parallel sub-agents

```text
parent agent creates scouting/review/implementation tasks
  ↓
AgentCoordinator assigns budgets, models, tools, and workspace views
  ↓
read-only scout agents run in parallel
  ↓
implementation agents write to isolated overlays
  ↓
sub-agents return structured outputs, not raw transcripts
  ↓
parent/coordinator reviews diffs and merges selected overlays
  ↓
workspace snapshot records merged result
```

### Anchor-based edit

```text
agent reads file with hashline or stateful anchors
  ↓
model proposes replacement using start/end anchors
  ↓
edit tool validates anchors and snapshot preconditions
  ↓
workspace lock is acquired
  ↓
replacement is applied in transaction overlay
  ↓
Myers reconciler preserves unchanged anchors and assigns new ones to changed lines
  ↓
updated anchor state and diff are returned
```

Same core flows, different frontend rendering.

## Testing Strategy

Current test command:

```bash
dotnet test Omicron.slnx --nologo
```

As of the baseline review on 2026-05-08 this test suite passes after preserving compatibility between single and multiple assistant tool-call message representations.

### Core Tests

- Omicron agent event ordering
- tool-call lifecycle
- permission flow
- command dispatch
- plugin registration
- session replay
- snapshot creation/resume
- workspace transaction event ordering
- sandbox execution event ordering
- plugin capability enforcement
- sub-agent event ordering and cancellation propagation

### Content Parsing and Syntax Highlighting Tests

- markdown incremental parse correctness on streaming chunks
- safe commit point resumption
- GFM extension handling (tables, task lists, strikethrough)
- code block language tag detection
- Tree-sitter highlight correctness per language
- syntax theme scope matching
- highlight cache invalidation on theme change
- fallback regex tokenizer accuracy
- large code block (>500 lines) highlight performance
- streaming chunk interleaving with markdown blocks
- inline code, emphasis, and link rendering

### Text Tests

- UTF-8 decoding
- invalid UTF-8 handling
- grapheme segmentation
- combining marks
- emoji sequences
- CJK width
- ambiguous-width characters
- tabs
- long lines
- newline variants

### Terminal Renderer Tests

- frame diff correctness
- style reset correctness
- cursor movement minimization
- dirty-region rendering
- resize behavior
- alternate-screen lifecycle

### Virtual Terminal Tests

- VT/ANSI parser correctness
- PTY resize propagation
- screen buffer and scrollback behavior
- alternate-screen child app behavior
- bracketed paste/input encoding
- pane split/focus/resize commands
- AI command insert vs execute safety policy

### Golden Frame Tests

Given semantic UI and terminal size, verify expected styled cell grid.

```text
input:
  transcript blocks + terminal size

expected:
  styled cell grid
```

### Persistence and Workspace Tests

- event stream append/read integrity
- snapshot restore equivalence to full replay
- session branching/forking
- workspace snapshot diff correctness
- transaction commit/rollback
- VFS path normalization and traversal prevention
- host-backed workspace reconciliation after external file changes

### Agent Orchestration and Edit Harness Tests

- parallel sub-agent scheduling and budget enforcement
- structured sub-agent output validation
- VFS lock acquisition/release/deadlock prevention
- optimistic precondition conflict detection
- hashline edit success/stale-anchor behavior
- stateful anchor reconciliation after edits and external file changes
- Myers diff anchor preservation for unchanged lines
- AST context/index correctness for supported languages
- multi-file batch edit atomic commit/rollback

### Sandboxing Tests

- policy serialization and risk explanation
- provider capability detection
- deny-by-default filesystem/network/environment behavior
- workspace overlay mount and diff behavior
- secret/environment scrubbing
- sandbox audit event integrity
- provider unavailable/fallback behavior
- cross-platform provider conformance suite

### WASM Plugin Runtime Tests

- manifest parsing and capability validation
- denied host-call behavior
- tool/command/panel registration through host bindings
- event callback ordering and cancellation
- plugin memory/fuel/timeout enforcement
- plugin-scoped persistence isolation
- Wasmtime instance lifecycle and teardown
- AssemblyScript SDK conformance
- C# AOT plugin size/startup/memory benchmark

### Rust/C# Interop Tests

- native ABI version mismatch handling
- generated binding compile checks
- safe wrapper error conversion
- ownership/free/release correctness
- cancellation/operation handle behavior
- cross-platform native library loading
- C ABI/csbindgen vs UniFFI proof-of-concept comparison

## Benchmark Scenarios

- 100k–1M lines or equivalent token volume
- 20–100 streaming chunks per second
- terminal resize spam
- scroll while streaming
- emoji / CJK / combining marks
- markdown and code blocks
- long unbroken lines
- large tool output bursts
- diff-heavy output
- autocomplete/input editor latency
- high-volume shell output in virtual terminal panes
- OpenTUI C ABI call overhead and buffer throughput if RFC 0015 remains viable
- markdown incremental parse throughput (bytes/sec)
- syntax highlight time per 100-line code block
- highlight cache hit ratio during streaming
- Tree-sitter grammar load time and memory per language
- sandboxed short-lived command overhead
- parallel sub-agent scheduling overhead
- hashline/stateful-anchor edit token cost and failure rate
- AST context curation accuracy and index update cost
- VFS lock contention under parallel agents
- WASM plugin startup and call overhead
- AssemblyScript vs C# AOT plugin size/memory/startup comparison
- C#/Rust FFI call overhead for representative payload sizes
- generated binding build and packaging cost

Metrics:

- managed allocations per frame
- native allocation/free rate
- GC pause time and GC count under streaming load
- CPU time per frame
- worst-case resize time
- scroll latency
- input latency while streaming
- transcript storage memory
- layout cache memory
- dirty rows per frame
- bytes written to stdout per frame
- sandbox startup overhead
- WASM plugin startup overhead
- host binding call overhead
- plugin memory footprint
- FFI call overhead
- native buffer/slice throughput
- native library load time
- snapshot/diff time
- markdown parse time per chunk (mean, p99)
- syntax highlight time per block (mean, p99)
- highlight cache memory per unique block
- theme load and apply time

## Error Handling

TUI terminal session must restore terminal state on crash/cancellation:

- show cursor
- exit raw mode
- exit alternate screen if active
- reset styles
- flush output

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

Plugin/tool failures:

- failed plugin commands produce structured errors
- failed tools emit failed `ToolCallFinished`
- failed UI panels render fallback error nodes

Sandbox/terminal failures:

- unavailable sandbox providers produce clear capability errors
- provider crashes do not corrupt event stream
- overlays rollback on failed/cancelled execution unless committed
- disconnected virtual terminal processes leave recoverable pane/session records

## Completion Criteria

The architecture is considered in shape when:

1. core events are stable and replayable (Phase Set 0a);
2. UTF-8 text store, frame buffer/diff renderer baseline, and OpenTUI C ABI spike pass or fail with documented benchmarks (Phase Set 0c);
3. Rust/C# FFI conventions are proven with at least csbindgen and one Rust-backed PoC (Phase Set 0b);
4. sessions can persist/resume with snapshots and workspace VFS transactions (Phase Set 1b);
5. TUI transcript can stream large sessions with incremental markdown parsing and syntax-highlighted code blocks (Phase Set 2);
6. execution broker can run local/no-sandbox and at least one real provider spike (Phase Set 3a);
7. edit harness provides hashline and stateful-anchor tools with measured reliability (Phase Set 3b);
8. third-party plugin runtime has a Wasmtime/AssemblyScript proof of concept (Phase Set 3c);
9. virtual terminal panes run through emulator buffers with AI command support (Phase Set 4a);
10. sub-agents can run in parallel with VFS locking/overlays and structured outputs (Phase Set 4b);
11. frontend/backend remoting can route multiple agents with priority channels and attention modes (Phase Set 4c);
12. markdown parsing is incremental and streaming-safe, with safe-commit-point resumption (Phase Set 2b);
13. syntax highlighting works for Tier-1 languages with Tree-sitter and caches per block (Phase Set 2b);
14. syntax themes are swappable and affect both TUI and GUI consistently (Phase Set 2b);
15. GUI can consume the same session and multi-agent model without TUI dependencies (Phase Set 5a);
16. remote backends can be bootstrapped over SSH and reached through structured transports (Phase Set 5b);
17. tests and benchmarks cover the hot paths (ongoing).
