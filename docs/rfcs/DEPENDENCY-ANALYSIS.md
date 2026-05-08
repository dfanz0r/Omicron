# RFC Dependency Analysis and Implementation Order

## Purpose

Analyze the full RFC set, map dependencies, identify parallelizable tracks, and propose a re-ordered implementation plan. This document also evaluates whether content should be migrated between RFCs and whether separate implementation-plan documents are needed.

## Dependency Graph

```text
FOUNDATIONS
  RFC 0001 – Core Architecture and Events (canonical)
    └── no internal dependencies

  RFC 0003 – Text Storage, Unicode Layout, Terminal Rendering (canonical)
    └── no Omicron-internal dependencies (relies on runtime libraries)

  RFC 0015 – OpenTUI C ABI Rendering Backend (research)
    └── depends on: 0003 rendering requirements, 0011 native interop conventions

  RFC 0011 – Rust/C# Interop and FFI (research/planned)
    └── no Omicron-internal dependencies (independent technical strategy)

INDEPENDENT LAYER (depends only on foundations)
  RFC 0002 – UI Abstractions, Plugins, Commands, Permissions
    └── depends on: 0001 (core primitives, identifiers, events)

  RFC 0006 – Persistence, VFS, Workspace Snapshots
    └── depends on: 0001 (core events, session model, identifiers)

  RFC 0014 – Remoting Protocol and Remote Backends (protocol subset)
    └── depends on: 0001 (core ids/events), 0006 (event cursors/persistence for reconnect)

  RFC 0004 – Transcript Viewport, Scrollback, Layout, Input
    └── depends on: 0003 (text engine, terminal rendering)

MIDDLE LAYER (depends on independent layer)
  RFC 0007 – Sandboxing and Execution Broker
    └── depends on: 0001 (core events, identifiers), 0006 (VFS, overlays, snapshots)

  RFC 0010 – WASM Plugin Runtime
    └── depends on: 0002 (plugin ABI, capability model), 0011 (FFI patterns for Wasmtime)

  Edit Harness sub-concern of RFC 0012 (can be split)
    └── depends on: 0001 (tool interface), 0006 (VFS transactions), 0003 (text indexing for anchors)

ADVANCED LAYER (depends on middle layer)
  RFC 0005 – Embedded Virtual Terminal and Shell Panes
    └── depends on: 0003+0004 (terminal rendering pipeline), 0006 (persistence, workspace context), 0007 (sandboxing)

  Sub-Agent Orchestration sub-concern of RFC 0012
    └── depends on: 0001 (core events), 0006 (VFS, locking, snapshots), 0007 (sandbox policies), Edit Harness

  RFC 0014 – Remoting Protocol and Remote Backends (remote transport subset)
    └── depends on: protocol subset, 0012 (multi-agent orchestration), 0005 (terminal channels where needed), 0010 (remote-aware plugins where needed)

FRONTEND LAYER
  RFC 0008 – GUI Frontend
    └── depends on: every core subsystem above, with RFC 0014 needed for remote/multi-agent dashboards

ADMINISTRATIVE
  RFC 0009 – Implementation Roadmap, Testing, Performance
    └── meta-RFC describing the plan; must reflect actual dependencies
```

## Problems with Current RFC 0009 Phase Order

The current RFC 0009 ordering (as written) has several issues:

1. **Rust/C# Interop (Phase 4) placed too late.**
   RFC 0011 defines the native memory and FFI strategy. Sandboxing (Phase 5), WASM plugins (Phase 12), and any Rust-backed subsystem depend on it. It should be a foundation alongside Phase 1, or at least Phase 2.

2. **Semantic UI (Phase 2) and Persistence (Phase 3) are correctly early but could run in parallel.**
   They have no mutual dependency. This is a lost parallelism opportunity.

3. **Agent Orchestration and Edit Harness (Phase 10) spans two separable concerns.**
   Hashline/anchor edits could be prototyped earlier (they just need the tool interface from Phase 1 + text from Phase 3 + VFS writes from Phase 3). Sub-agent orchestration requires sandboxing (Phase 5) and VFS locking (which itself depends on Phase 3/VFS).

4. **WASM Plugin Runtime (Phase 12) depends on Phase 2 (plugin API) and Phase 4 (FFI).**
   In the current ordering, Phase 2 is early but Phase 4 is late. If Phase 4 moves earlier, WASM plugins could start much sooner.

5. **Virtual Terminal (Phase 11) depends on text rendering (Phases 6-9), persistence (Phase 3), and sandboxing (Phase 5).**
   Current order mostly respects this, but the gap between Phase 3 (persistence) and Phase 11 is large. The dependency on sandboxing for sandboxed panes is real but the basic virtual terminal can work without sandboxing.

6. **No recognition of parallel tracks.**
   The plan reads as a strict sequence, but several tracks are genuinely independent.

## Proposed Re-Ordered Implementation Plan

### Principle

- **Foundations first.** Core, text engine, FFI, and persistence are the bedrock.
- **Parallel where possible.** UI abstractions, text rendering, and persistence tracks run concurrently.
- **Edit harness earlier.** Hashline/anchors improve tool reliability immediately and are separable from full sub-agent orchestration.
- **Stronger isolation before multi-agent.** Sandboxing and VFS locking precede parallel agents.
- **GUI stays last.** It depends on everything.

### Phase Set 0: Foundations (parallelizable)

| Sub-Phase | RFC(s) | Dependencies | Key Deliverables |
|-----------|--------|-------------|------------------|
| 0a | 0001 | None | Stable event model, session model, tool interface, command interface, plugin registration, identifiers for sessions/tasks/shells/sandboxes |
| 0b | 0011 | None (independent strategy) | Native ABI conventions, csbindgen PoC, UniFFI PoC, native UTF-8 slab prototype, safe C# wrapper pattern, CI packaging, ABI version handshake |
| 0c | 0003 + 0015 | None for custom baseline; 0015 also uses 0011 conventions | Utf8TextStore, grapheme/terminal-cell model, layout indexes, terminal backend interface, frame buffer, differential renderer baseline, OpenTUI C ABI spike |

**Rationale for 0b (FFI):** Every Rust-backed subsystem (Wasmtime host, sandbox providers, PTY adapters, diff engines) will need the FFI conventions established first. Doing this after three other phases risks rework.

**Rationale for 0c (Text):** The text store and renderer are prerequisites for transcript viewport (Phase 2), virtual terminal (Phase 4), and rich content. Starting early lets the renderer mature alongside other systems.

**Parallelism:** 0a, 0b, and 0c have zero mutual dependencies. They can be worked on simultaneously by different engineers.

### Phase Set 1: Independent Capabilities (parallelizable)

| Sub-Phase | RFC(s) | Dependencies | Key Deliverables |
|-----------|--------|-------------|------------------|
| 1a | 0002 | 0a | Semantic UI model (UiNode, ContentBlock), panel provider, command system, permission model, plugin manifest/capability model |
| 1b | 0006 | 0a | Session catalog, append-only event log, session snapshots, IWorkspaceFileSystem host-backed impl, workspace diff model, workspace transactions/overlays |
| 1c | 0004 | 0c | Transcript viewport, app-owned scrollback, viewport state/follow-tail, streaming append, block model, layout cache, minimal TUI widget system |

**Parallelism:** 1a, 1b, and 1c are mutually independent. They depend only on different foundations from Phase 0.

### Phase Set 2: TUI Integration

| Sub-Phase | RFC(s) | Dependencies | Key Deliverables |
|-----------|--------|-------------|------------------|
| 2a | 0009 (TUI shell) | 0a, 0c, 1c | Fullscreen TUI shell, terminal backend + diff renderer + transcript viewport integrated, input system, status bar, basic layouts |
| 2b | 0009 (rich content) | 1a | Markdown/code/diff/table rendering, syntax highlighting abstraction, tool-call panels, collapsible blocks |

**Note:** 2a+b produce the first usable interactive TUI. This is the first point where "Omicron looks like a coding agent."

### Phase Set 3: Safety and Extensibility

| Sub-Phase | RFC(s) | Dependencies | Key Deliverables |
|-----------|--------|-------------|------------------|
| 3a | 0007 | 0a, 1b | Sandbox policy/provider interfaces, local/no-sandbox provider, execution broker, audit events, risk explanation model, integration with workspace overlays |
| 3b | Edit Harness (0012 subset) | 0a, 0c, 1b, 2b | Hashline edit tool, stateful single-token anchor editor (state manager + reconciler + Myers diff), anchor table/VFS integration, optimistic write preconditions, model-actionable error messages, multi-file batch edits |
| 3c | 0010 | 0a, 1a, 0b | Wasmtime host, plugin manifest validation, capability-based host bindings (JSON v1), AssemblyScript SDK prototype, plugin lifecycle and resource limits, sample tool/command plugins |

**Parallelism:** 3a, 3b, 3c are largely independent. 3b (edit harness) does not require sandboxing—it needs VFS transactions (1b) and text indexes (0c). 3c (WASM) needs FFI conventions (0b) and plugin APIs (1a) but not sandboxing or VFS.

### Phase Set 4: Advanced Agent Features

| Sub-Phase | RFC(s) | Dependencies | Key Deliverables |
|-----------|--------|-------------|------------------|
| 4a | 0005 | 0c, 1c, 2a, 1b | PTY adapter, VT/ANSI parser, emulated screen buffer + scrollback, terminal pane model (tabs/splits), TUI pane renderer, AI command suggest/insert/approve flows |
| 4b | Sub-Agent Orchestration (0012 remainder) | 0a, 1b, 3a, 3b | AgentCoordinator, task lifecycle, structured output contract, VFS lock manager, workspace views (read-only/overlay/shared/sandboxed), cancellation trees, per-agent budgets |
| 4c | Remoting Protocol Foundations (0014 subset) | 0a, 1b, 4b preferred | Frontend/backend split, MessagePack-oriented framing, target addressing, channels, per-agent attention/subscription modes, reconnect cursors |
| 4d | 0010 (advanced) | 3c, 4b, 4c | WASM plugins can register sub-agent/remote-aware tools, richer plugin panel rendering, binary encoding for hot paths, plugin-scoped persistence |

**Parallelism:** 4a (virtual terminal) and 4b (sub-agents) can be worked on in parallel. 4c can start as a local protocol spike after core/persistence primitives exist, but its full multi-agent semantics should align with 4b. 4d depends on plugin runtime foundations plus 4b/4c semantics.

### Phase Set 5: Alternative Frontend and Polish

| Sub-Phase | RFC(s) | Dependencies | Key Deliverables |
|-----------|--------|-------------|------------------|
| 5a | 0008 + 0014 | Everything above | GUI app shell, backend/session/agent dashboard, virtualized transcript list, content block renderers, command bindings, plugin panel host, GUI terminal pane host |
| 5b | 0014 remote transports | 4c | SSH bootstrap + tunnel, detached remote backend sessions, remote inventory/health, direct TCP/WebSocket/QUIC/relay evaluation |
| 5c | 0009 (advanced features) | Everything above | Command palette, settings, theme system, session replay viewer, workspace snapshot browser, session branching, sandbox diagnostics, plugin marketplace/discovery, sub-agent workflow designer, remote backend manager, anchor/AST edit diagnostics |

## Parallel Tracks Summary

```text
Track A: Core + Persistence + Sandboxing
  0001 → 0006 → 0007 → 4b (sub-agent orchestration)

Track B: Text + Rendering + Viewport → TUI Shell
  0003 + 0015 spike → 0004 → 2a (TUI integration) → 0005 (virtual terminal)

Track C: UI Abstractions + Plugins + WASM
  0001 → 0002 → 0010 (WASM plugins) → remote-aware plugin capabilities from 0014

Track D: FFI + Rust Subsystems
  0011 → all Rust-backed components in tracks A, B, C

Track E: Edit Harness (cross-cutting, starts after 0001)
  0001 → 3b (hashline/anchors) → merges into 4b (sub-agents using edits)

Track F: Remoting + Remote Backends
  0001 + 0006 → 0014 protocol subset → 0012 multi-agent integration → SSH/QUIC/relay transport work
```

Key insight: Track D (FFI) underpins multiple other tracks. It should not be delayed to Phase 4.

## Content Migration Analysis

### Do these RFCs need content reshuffling?

| Concern | Current Home | Suggested Change |
|---------|-------------|------------------|
| VFS Locking | Mentioned in 0006 and fully defined in 0012 | **Stay as-is.** RFC 0006 mentions locking as a VFS feature; 0012 fully defines it. Cross-references are sufficient. |
| Workspace Overlays/Transactions | 0006 defines the mechanism; 0012 extends for sub-agents | **Stay as-is.** Clean layering: 0006 defines primitives, 0012 defines sub-agent workspace views. |
| Native Buffer Architecture | 0011 | **Minor addition only:** Add a note in RFC 0003 that the Utf8TextStore should be backed by the native slab model from 0011. This already exists as a brief mention but could be stronger. |
| Edit Tools | 0012 (edit harness section) could partially move to 0002 (tool interface) | **Keep in 0012.** The edit harness is an agent-level concern, not a generic tool interface. 0002 defines the base tool interface; 0012 defines the specific edit protocol. |
| Plugin Capability Model | 0002 defines capability model; 0010 defines WASM-specific capabilities | **Update with RFC 0014 cross-references.** Plugins need explicit backend/session/agent target scope and must not own remoting transports directly. |
| Event Contracts | 0001 defines core events; 0006 defines persistence events; 0012 defines sub-agent events | **Stay as-is.** Events are defined in the RFC that introduces the concept. |
| Sub-Agent Events | Currently in 0001 (event catalog) and 0012 (orchestration) | **Minor:** 0001 should list the event types (it already does); 0012 provides full semantics (it already does). This is correct as-is. |
| Memory/Performance Benchmarks | Currently scattered across RFCs | **Consolidate** into RFC 0009. Per-RFC benchmark mentions are fine for context; 0009 should be the canonical performance document. |

**Verdict: No major content migration is needed.** The current RFC boundaries are well-chosen. Cross-references are appropriate. The main issue is implementation ordering, not content ownership.

## Should We Create Implementation Plans?

### Yes, separate from RFCs.

The RFCs are **strategic design documents**. They answer "what" and "why."

Implementation plans would be **tactical execution documents**. They answer "who", "how", "when", and "in what concrete sequence of code changes."

Recommended structure:

```text
docs/rfcs/
  README.md              ← RFC index and strategic overview
  0001-*.md .. 0012-*.md ← Strategic design (existing)
  DEPENDENCY-ANALYSIS.md ← This document

docs/plans/
  README.md              ← Implementation plan index
  plan-001-core.md       ← Phase Set 0a: Core and Events (concrete)
  plan-002-text.md       ← Phase Set 0c: Text Engine (concrete)
  plan-003-ffi.md        ← Phase Set 0b: Rust/C# Interop (concrete)
  plan-004-persistence.md← Phase Set 1b: Persistence/Workspace (concrete)
  plan-005-tui.md        ← Phase Set 2: TUI Integration (concrete)
  plan-006-edit-harness.md ← Phase Set 3b: Edit Harness (concrete)
  ... as needed
```

### What an implementation plan contains

Each plan document should contain:

```text
Title
RFC(s) it implements
Phase dependency (which plans must come before)
Engineer/stakeholder notes (who)

Concrete deliverables:
  - list of types/interfaces to create or modify
  - sequence of code changes (not pseudocode, but logical order)
  - test expectations
  - acceptance criteria

Milestones:
  - day-level or week-level milestone definitions
  - review gates
  - integration checkpoints

Migration path:
  - if the MVP has existing code doing something similar, what to keep, change, or replace

Risks:
  - known unknowns
  - decisions deferred to this phase
```

### Governance

- **RFCs change by consensus** and represent long-term architectural direction.
- **Plans change by iteration** as implementation reveals new information.
- A plan may reference an RFC change if the plan reveals an RFC gap.
- Plan documents are not required before every code change. They are for coordination across the significant phases.

## Updated RFC 0009 Sections

RFC 0009 needs updating to reflect this analysis. See proposed edits below.

## Revised Completion Criteria

The architecture is considered in shape when:

1. core events are stable and replayable (Phase 0a);
2. UTF-8 text store, frame buffer, and diff renderer pass benchmarks (Phase 0c);
3. Rust/C# FFI conventions are proven with at least csbindgen and one Rust-backed PoC (Phase 0b);
4. sessions can persist/resume with snapshots and workspace VFS transactions (Phase 1b);
5. TUI transcript can stream large sessions without full redraws (Phase 2);
6. edit harness provides hashline and stateful-anchor tools with measured reliability (Phase 3b);
7. execution broker can run local/no-sandbox and at least one real provider spike (Phase 3a);
8. third-party plugin runtime has a Wasmtime/AssemblyScript proof of concept (Phase 3c);
9. virtual terminal panes run through an emulator buffer with AI command support (Phase 4a);
10. sub-agents can run in parallel with VFS locking/overlays and structured outputs (Phase 4b);
11. GUI can consume the same session model without TUI dependencies (Phase 5a);
12. tests and benchmarks cover the hot paths (ongoing).
