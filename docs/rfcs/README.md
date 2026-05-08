# Omicron RFC Plan

This directory is the canonical planning source for Omicron. The original monolithic design document has been split into focused RFCs so the current MVP can be hardened while leaving a clear path for the larger product vision.

Status terms:

- **Canonical**: accepted as the current architectural direction.
- **Planned**: intended, but details may change during implementation.
- **Research**: requires spikes, license review, platform testing, or benchmarking.

See [IMPLEMENTATION-BASELINE.md](IMPLEMENTATION-BASELINE.md) for the current codebase state these RFCs target. The RFCs describe the intended architecture; the baseline records what is implemented today.

## RFC Index

| RFC | Title | Status | Scope |
| --- | --- | --- | --- |
| [0001](0001-core-architecture-and-events.md) | Core Architecture and Event Model | Canonical | shared core, package boundaries, event stream |
| [0002](0002-ui-plugins-commands-and-permissions.md) | UI Abstractions, Plugins, Commands, and Permissions | Canonical | semantic UI, plugin model, commands, approvals |
| [0003](0003-text-rendering-and-terminal-engine.md) | Text Storage, Unicode Layout, and Terminal Rendering | Canonical | UTF-8 storage, terminal cells, frame diffing |
| [0004](0004-transcript-viewport-scrollback-layout-and-input.md) | Transcript Viewport, Scrollback, Layout, and Input | Canonical | transcript virtualization, app scrollback, input mapping |
| [0005](0005-embedded-virtual-terminal-and-shell-panes.md) | Embedded Virtual Terminal and Shell Panes | Planned | PTY, terminal emulator, tmux-like panes, AI command help |
| [0006](0006-persistence-sessions-and-workspace-snapshots.md) | Persistence, Session History, VFS, and Workspace Snapshots | Canonical | event logs, checkpoints, VFS, immutable snapshots |
| [0007](0007-sandboxing-and-execution-broker.md) | Sandboxing and Execution Broker | Research/Planned | policy broker, providers, Codex/zerobox/heel evaluation |
| [0008](0008-gui-frontend.md) | GUI Frontend | Planned | GUI app model, shared core, virtualized controls |
| [0009](0009-implementation-roadmap-testing-and-performance.md) | Implementation Roadmap, Testing, and Performance | Canonical | phases, tests, benchmarks, validation gates |
| [0010](0010-wasm-plugin-runtime.md) | WebAssembly Plugin Runtime | Research/Planned | Wasmtime, AssemblyScript, capability-sandboxed plugins |
| [0011](0011-rust-csharp-interop-and-ffi.md) | Rust/C# Interop and FFI Strategy | Research/Planned | mixed C#/Rust architecture, FFI, binding generators |
| [0012](0012-agent-orchestration-vfs-locking-and-edit-harness.md) | Agent Orchestration, VFS Locking, and Edit Harness | Research/Planned | sub-agents, parallel agents, VFS locks, hash anchors, AST context |
| [0013](0013-content-parsing-and-syntax-highlighting.md) | Content Parsing, Markdown Rendering, and Syntax Highlighting | Planned | incremental markdown, Tree-sitter, syntax themes, code blocks |
| [0014](0014-remoting-protocol-and-remote-backends.md) | Remoting Protocol and Remote Backends | Research/Planned | frontend/backend split, binary remoting protocol, remote agents, transport bindings |
| [0015](0015-opentui-c-abi-rendering-backend.md) | OpenTUI C ABI Rendering Backend | Research | alternate native TUI backend, C# bindings, OpenTUI Zig core spike |
| [Implementation Baseline](IMPLEMENTATION-BASELINE.md) | Current Implementation Baseline | Reference | current projects, implemented MVP features, gaps, and targeting notes |
| [Traceability](TRACEABILITY.md) | RFC Traceability Matrix | Reference | maps original seed sections to RFCs |

## Architectural North Star

Omicron is an AI coding-agent system with a single UI-agnostic core that can power a high-performance TUI, a GUI, tests, headless automation, and future remote/web frontends.

The core owns:

- session state
- event ordering
- tool lifecycle
- command dispatch
- permissions
- persistence hooks
- workspace/VFS abstractions
- shell/session identifiers
- sandbox policy identifiers and execution events
- semantic UI contributions
- plugin capability grants and host ABI boundaries
- native interop boundaries for Rust-backed subsystems
- sub-agent orchestration, concurrency limits, and workspace coordination
- remote backend/session lifecycle and transport-neutral remoting

Frontends own:

- rendering
- layout
- platform input mechanics
- terminal or GUI control details
- scrolling behavior
- accessibility/platform integrations

Plugins generally contribute capabilities and semantic UI, not terminal cells, ANSI, GUI controls, or platform widgets.

## Primary Decisions

1. **Shared core, separate frontends.** `Omicron.Core` must not depend on TUI, GUI, terminal renderer, or sandbox provider implementation details.
2. **Append-only event stream.** Events are authoritative history for session replay, persistence, debugging, and tests.
3. **Semantic UI boundary.** Plugins produce `UiNode`, `ContentBlock`, commands, panels, and status items.
4. **UTF-8 transcript storage.** Large transcripts use append-only UTF-8 chunks plus indexes, not one giant string.
5. **Custom high-throughput terminal rendering.** The TUI renders visible cells into frame buffers and diffs frames.
6. **App-owned scrollback.** Fullscreen TUI mode owns transcript history and viewport behavior.
7. **Embedded terminal panes use an emulator.** Child shell output flows through PTY + VT parser + screen buffer before rendering.
8. **Persistence is frontend-neutral.** Persist events, sessions, snapshots, workspace manifests, and settings, not frame buffers.
9. **Workspace access goes through a VFS.** Tools and executions use Omicron workspace abstractions for diffs, rollback, snapshots, and future sandboxing.
10. **Sandboxing is provider-based.** Omicron defines broker/policy/provider interfaces first, then evaluates concrete implementations.
11. **Third-party plugins should be WASM capability-sandboxed.** Wasmtime is the preferred server-side runtime; AssemblyScript is the preferred first authoring target.
12. **Rust/C# interop must be deliberate and narrow.** Prefer coarse-grained FFI boundaries with explicit ownership, ABI versioning, and generated bindings where they reduce maintenance.
13. **High-volume text should live in native UTF-8 buffers.** Use native/off-heap slabs and handle/slice-based access to reduce GC pressure and enable copy-minimized C#/Rust/WASM pipelines.
14. **Sub-agents and parallel agents are first-class.** They require a coordinator, budgets, cancellation trees, isolated workspace overlays, and structured outputs.
15. **The edit harness is a core capability.** Omicron should evaluate hashline/stateful anchors, Myers reconciliation, AST context, and model-actionable errors to reduce token cost and edit failures.
16. **Content parsing and syntax highlighting are core platform services.** Markdown and code blocks in transcripts require incremental parsing, Tree-sitter-based highlighting, and theming that both TUI and GUI consume uniformly.
17. **Remote backends use a transport-neutral binary protocol.** Frontends and backends communicate through a stable remoting boundary with MessagePack-oriented payloads, multiplexed channels, priority-aware flow control, and pluggable transports.
18. **Plugins are remote/multi-agent aware but transport-blind.** Plugin commands, panels, and event subscriptions carry explicit backend/session/agent target scope; plugins use host-mediated remote APIs rather than opening remoting transports directly.
19. **Remote provider credentials require explicit grants.** Remote backends do not silently receive linked provider secrets; direct provider access uses backend-local credentials, a model gateway, or scoped delegated grants from an unlocked encrypted vault.

## Reference Source Material

The repository contains a `READ_ONLY/` directory with external/reference code snapshots. These include:

- Codex CLI (Rust sandbox, exec-server, apply-patch)
- Zed Editor (Rust agent, terminal emulator, streaming diff, tools)
- Dirac (TypeScript hash-anchor edits, Myers diff reconciler)
- Pi mono (TypeScript extension system, agent session)
- oh-my-pi (TypeScript+Rust mixed agent with Rust shell builtins)
- OpenCode (TypeScript agent platform with ACP, tools, MCP)
- ConsoleEx AgentStudio (.NET TUI agent application)
- XenoAtom.Terminal / .Terminal.UI (.NET terminal backends and TUI controls)
- Termina (.NET TUI framework with frame buffer diff rendering)
- open-claude-code (JS Claude Code architecture with WASM layout engine)
- Gemini CLI (command/skill definitions, evaluation framework)
- pi-opencode-provider (provider bridge pattern)
- open-docs (curated documentation from all major agent tools)

See [REFERENCE-JOURNAL.md](REFERENCE-JOURNAL.md) for the complete annotated index, including which RFC each reference informs and when in the roadmap to study it deeply.

Use `READ_ONLY/` material for:

- studying architecture and API patterns;
- comparing implementation strategies;
- pulling ideas into Omicron's own libraries during refactors;
- validating behavior while designing Omicron-native equivalents.

When adapting reference code:

- check license compatibility first;
- prefer reimplementation or carefully attributed adaptation over blind copying;
- keep Omicron package names, architecture, and RFC boundaries authoritative;
- document any substantial imported design/code in the relevant RFC or implementation notes.

## Relationship to the MVP

The current MVP is a .NET 10 console agent made of `Omicron.Core`, `Omicron.CLI`, and `Omicron.Core.Tests`. It already has a stateful agent loop, provider/API-shape separation, TOML config, model discovery, direct file and shell tools, streaming output, and xUnit coverage. It does **not** yet have the package split, durable event log, VFS, permissions, plugin system, renderer, persistence, sandbox, remoting, or rich TUI/GUI frontends described by the target RFC architecture. See [IMPLEMENTATION-BASELINE.md](IMPLEMENTATION-BASELINE.md) for details.

The current MVP should be evolved toward these RFCs incrementally. Do not stop all work to rebuild everything at once. The intended path is:

1. stabilize core event/session contracts, text engine, and FFI conventions as parallel foundations (Phase Set 0);
2. introduce semantic UI, persistence/workspace, and transcript viewport in parallel (Phase Set 1);
3. integrate the TUI shell with rich content rendering (Phase Set 2);
4. introduce sandboxing, high-reliability edit harness, and WASM plugin hosting in parallel (Phase Set 3);
5. add virtual terminal panes and sub-agent orchestration in parallel (Phase Set 4);
6. add GUI after all core subsystems are stable (Phase Set 5a);
7. content parsing and syntax highlighting span Phase Sets 0c (text), 1a (content blocks), 2b (rich content), and 3b (edit harness AST) — see RFC 0013 for the full architecture.
8. remoting begins with protocol and local split spikes, then SSH tunnel remote backends, detached sessions, delegated sub-agents, and advanced transports — see RFC 0014 for the full architecture.
9. TUI rendering keeps two viable tracks until benchmarks decide: custom renderer baseline (RFC 0003/0004) and OpenTUI C ABI backend candidate (RFC 0015).

See [DEPENDENCY-ANALYSIS.md](DEPENDENCY-ANALYSIS.md) for the full dependency graph and [RFC 0009](0009-implementation-roadmap-testing-and-performance.md) for the detailed phase breakdown.

## Superseded Source Document

The original seed document has been archived at:

```text
docs/archive/agent_tui_gui_framework_design.md
```

These RFCs are now the organized plan and should receive future design edits first.
