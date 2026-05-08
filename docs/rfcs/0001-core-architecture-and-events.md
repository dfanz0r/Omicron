# RFC 0001: Core Architecture and Event Model

Status: **Canonical**

## Purpose

Define the shared Omicron core architecture. The core must be usable from the TUI, GUI, tests, headless automation, and future remote frontends without depending on any frontend rendering technology.

## Core Principle

> The Omicron core owns state, events, tools, permissions, persistence hooks, and semantic UI contributions. Frontends own rendering, layout, input mechanics, scrolling, and platform-specific interaction.

## Current Implementation Snapshot

As of 2026-05-08, the repository contains a compact MVP rather than the full target package split:

```text
Omicron.Core        agent loop, messages/models, providers/API shapes, tools, TOML config
Omicron.CLI         inline console frontend, model menu/discovery, config UI, chat loop
Omicron.Core.Tests  xUnit coverage for core/provider behavior
```

Implemented core events are currently `AgentEventType.Start`, `TextDelta`, `ToolCallStart`, `ToolCallEnd`, `Response`, and `Error` with a simple mutable `AgentEvent` class. This is sufficient for the console MVP, but it is not yet the typed append-only, replayable event stream specified below. Direct `read_path` and `shell` tools exist today and are workspace-root-contained/truncated, but they do not yet pass through the planned VFS, permission system, execution broker, sandbox policy, or audit events.

The broader layout below remains the target architecture. See [IMPLEMENTATION-BASELINE.md](IMPLEMENTATION-BASELINE.md) for the complete current-code inventory.

## Package Boundaries

Recommended target solution layout:

```text
Omicron.Core
Omicron.UI.Abstractions
Omicron.Plugins
Omicron.Plugins.Wasm
Omicron.Remoting
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

### Package Responsibilities

```text
Omicron.Core
  conversation/session model
  agent loop
  tool calls
  model adapters
  permission system
  command system
  plugin host contracts
  shell/session identifiers
  sandbox policy identifiers
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

Omicron.Plugins
  built-in/trusted plugin host model
  plugin manifests/capabilities
  logical plugin API contracts
  slash commands
  tools
  status providers
  semantic UI panels

Omicron.Plugins.Wasm
  Wasmtime host
  WASM module lifecycle
  AssemblyScript/C#/other SDK ABI support
  capability-enforced host bindings
  plugin resource limits

Omicron.Remoting
  frontend/backend protocol boundary
  transport-neutral connection and channel abstractions
  remote backend lifecycle contracts
  target addressing for sessions, agents, tasks, terminals, and operations
  attachment/subscription state DTOs

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

Omicron.Tests
  unit tests
  golden frame tests
  replay tests
  persistence/workspace tests
  sandbox/plugin conformance tests
  performance benchmarks
```

### Omicron.Core

Owns:

- conversation/session model
- agent loop
- tool call lifecycle
- model adapter contracts
- permission requests
- command dispatch
- plugin host contracts
- event stream contracts
- shell session and pane identifiers
- agent, sub-agent task, attachment, and remote backend identifiers
- sub-agent task identifiers and orchestration events
- sandbox policy and execution event identifiers

Must not own:

- terminal frame buffers
- ANSI output
- GUI controls
- PTY implementation
- VT parser implementation
- sandbox provider implementation
- workspace blob-storage implementation details

### Dependency Rules

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

Omicron.Plugins.Wasm
  depends on Omicron.Core primitives, Omicron.UI.Abstractions contracts, Wasmtime, and plugin ABI types

Omicron.Remoting
  depends on Omicron.Core primitives and transport/runtime libraries

Omicron.Workspace
  depends on low-level runtime libraries and persistence primitives

Omicron.Frontend.Tui
  depends on Omicron.Core + Omicron.UI.Abstractions + Omicron.Text + Omicron.Rendering.Terminal + Omicron.Terminal.Emulation + Omicron.Sandboxing

Omicron.Frontend.Gui
  depends on Omicron.Core + Omicron.UI.Abstractions + Omicron.Terminal.Emulation + Omicron.Sandboxing + GUI framework
```

Core may define identifiers, events, requests, plugin capability records, remote target/attachment DTOs, and policy records. Provider mechanics, Wasmtime hosting, plugin ABI marshaling, remoting transport implementations, Rust/C# FFI bindings, and sandbox provider implementations belong outside core. See RFC 0011 for native interop strategy and RFC 0014 for remoting protocol details.

## Event-Driven Model

The target core is append-oriented. State changes are emitted as ordered events. Frontends consume the same stream and render it differently.

Current MVP note: `Agent.PromptAsync` and `Agent.ContinueAsync` already stream lifecycle events via `IAsyncEnumerable<AgentEvent>`, but the event objects are coarse UI events rather than durable session events. The next hardening step is to preserve this streaming shape while introducing stable IDs, timestamps/sequences, typed payloads, and replay-safe event records.

Benefits:

- streaming UI updates
- deterministic replay
- persistence
- debugging
- tests
- multi-frontend observation
- headless execution with later visualization
- session forking and time travel
- audit trail for tools, shell commands, and sandbox execution

## Event Shape

Representative event contracts:

```csharp
public abstract record AgentEvent(DateTimeOffset Timestamp);

public readonly record struct AgentId(Guid Value);
public readonly record struct BackendId(Guid Value);
public readonly record struct AttachmentId(Guid Value);

public sealed record SessionStarted(
    SessionId SessionId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record UserMessageAdded(
    MessageId MessageId,
    ReadOnlyMemory<byte> Utf8Content,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record AssistantMessageStarted(
    MessageId MessageId,
    AgentId? AgentId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record AssistantDelta(
    MessageId MessageId,
    AgentId? AgentId,
    ReadOnlyMemory<byte> Utf8Delta,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record AssistantMessageCompleted(
    MessageId MessageId,
    AgentId? AgentId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record ToolCallStarted(
    ToolCallId ToolCallId,
    AgentId? AgentId,
    string ToolName,
    JsonElement Args,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record ToolCallOutputDelta(
    ToolCallId ToolCallId,
    AgentId? AgentId,
    ReadOnlyMemory<byte> Utf8Delta,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record ToolCallFinished(
    ToolCallId ToolCallId,
    AgentId? AgentId,
    ToolResult Result,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record PermissionRequested(
    PermissionRequest Request,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record NotificationRaised(
    UiNotification Notification,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);
```

Persistence/workspace events:

```csharp
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
```

Virtual terminal events:

```csharp
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
```

Sandbox events:

```csharp
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

public sealed record AgentTaskStarted(
    AgentTaskId TaskId,
    SessionId ParentSessionId,
    AgentId AgentId,
    BackendId? BackendId,
    string Role,
    WorkspaceSnapshotId? InputWorkspaceSnapshotId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record AgentTaskOutputProduced(
    AgentTaskId TaskId,
    JsonElement StructuredOutput,
    WorkspaceSnapshotId? OutputWorkspaceSnapshotId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record AgentTaskFinished(
    AgentTaskId TaskId,
    AgentTaskResultKind ResultKind,
    WorkspaceSnapshotId? OutputWorkspaceSnapshotId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);
```

## Session State

Session state is reconstructed from events plus optional snapshots. The core should expose a session model that can be observed by frontends but should not expose frontend state as authoritative data.

Session state includes:

- session metadata
- conversation messages
- model stream state
- tool calls
- permission requests
- notifications
- shell session references
- active agent records
- frontend attachment/subscription summaries where persistence policy keeps them
- sub-agent task records
- sandbox execution records
- latest workspace snapshot reference

Session state excludes:

- terminal frame buffers
- GUI control trees
- current pixel/cell layout
- transient platform input state
- ephemeral remoting transport connection state

Foreground/background/quiet state for a remote agent is frontend attachment state, not agent execution state. Core may define the DTOs and events needed to communicate attention changes, but authoritative session history remains the agent/session event stream. See RFC 0014.

## Non-Goals

This RFC does not define:

- terminal rendering internals;
- GUI widgets;
- workspace blob layout;
- sandbox provider mechanics;
- model provider implementation details.

Those are covered in later RFCs.
