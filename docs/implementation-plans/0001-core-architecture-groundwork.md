# Implementation Plan 0001: Core Architecture Groundwork

Status: Draft  
Target: upgrade the current C# MVP into a stable architecture that can support the long-term RFCs incrementally.

## Purpose

Before implementing the larger RFC features, Omicron needs a clean internal architecture with stable seams. The immediate goal is **not** to build WASM plugins, remoting, GUI, sandboxing, persistence, or a full TUI yet. The goal is to shape the C# codebase so those capabilities can be added later without rewriting the agent loop.

This plan turns the current MVP into a modular core with:

- stable session and event contracts;
- internal extension/plugin hook points implemented in C# first;
- provider, tool, command, permission, workspace, execution, and UI contribution seams;
- a console frontend that consumes core services rather than owning product logic;
- tests that lock down the new architecture.

## Guiding Principles

1. **Introduce seams before features.** Add interfaces and pipelines now; keep implementations simple.
2. **C# internal extensions first, WASM later.** Built-in C# extensions should use the same host-facing contracts that future WASM plugins will use through an adapter.
3. **Keep the CLI as a thin frontend.** Move model discovery, tool registration, config orchestration, and agent setup out of `Program.cs`.
4. **Keep direct host access behind interfaces.** File and shell tools can remain direct for now, but must sit behind workspace/execution/permission abstractions.
5. **Make events durable-shaped early.** Even before persistence exists, events should have IDs, timestamps, sequence numbers, and typed payloads.
6. **Avoid premature project explosion.** Add projects when they clarify boundaries; avoid adding empty projects with no code.

## Current Starting Point

Current projects:

```text
Omicron.Core
Omicron.CLI
Omicron.Core.Tests
```

Current useful MVP pieces to preserve:

- `Agent` loop and provider streaming model;
- `IChatProvider`, API shapes, provider discovery/factory;
- `Model`, `Message`, tool-call handling, reasoning preservation;
- `Tool` JSON-schema contract;
- `FileTools` and `ShellTools` functionality;
- TOML config;
- CLI model menu/config/chat loop;
- existing tests.

Current architectural issues to address:

- `Omicron.CLI/Program.cs` owns too much orchestration;
- events are UI-oriented and not replay/persistence-ready;
- tools execute directly with no permission, workspace, execution, or audit seam;
- no command system or extension registration pipeline;
- no core service container/host object;
- no session abstraction independent of console chat loop;
- no semantic UI/content block boundary;
- no workspace abstraction, even for basic file reads;
- no execution broker seam for shell commands;
- no separation between extension contribution discovery and agent runtime.

## Proposed Near-Term Project Layout

Phase 1 should keep the layout small:

```text
Omicron.Core
  Agent/
  Commands/
  Config/
  Events/
  Extensions/
  Models/
  Permissions/
  Providers/
  Sessions/
  Tools/
  Workspace/
  Execution/

Omicron.CLI
  Console frontend only

Omicron.Core.Tests
```

Optional after the seams stabilize:

```text
Omicron.UI.Abstractions       semantic UI/content block contracts
Omicron.Extensions.Builtin    built-in C# extensions/tools/commands
Omicron.Persistence           event log/session store implementation
Omicron.Workspace             heavier VFS/snapshot implementation
Omicron.Execution             sandbox/execution broker implementation
```

Do not split projects until namespace boundaries are clear and tested inside `Omicron.Core`.

## Target Core Concepts

### 1. Omicron Host

Introduce a core host/facade responsible for composing services.

Candidate API:

```csharp
public sealed class OmicronHost
{
    public IExtensionRegistry Extensions { get; }
    public IProviderRegistry Providers { get; }
    public ICommandRegistry Commands { get; }
    public IToolRegistry Tools { get; }
    public IPermissionService Permissions { get; }
    public IWorkspace Workspace { get; }
    public IExecutionBroker Execution { get; }
    public ISessionManager Sessions { get; }
}
```

Initial implementation can be simple and in-memory. The important part is that the CLI asks the host for capabilities instead of manually wiring everything in `Program.cs`.

### 2. Internal Extension Pipeline

Create C# extension contracts that future WASM plugins can adapt to.

```csharp
public interface IOmicronExtension
{
    string Id { get; }
    string DisplayName { get; }
    Version Version { get; }
    void Register(IExtensionContext context);
}

public interface IExtensionContext
{
    void RegisterTool(ToolDefinition tool);
    void RegisterCommand(CommandDefinition command);
    void RegisterModelProvider(IChatProvider provider);
    void RegisterPanel(IUiPanelProvider panelProvider);
    void RegisterStatusItem(IStatusItemProvider statusProvider);
}
```

For now:

- built-in file/shell/calculator/time capabilities become C# extensions;
- provider registration can become a provider extension;
- CLI-specific commands can register command handlers.

Later:

- WASM modules load manifests;
- WASM host exposes an adapter implementing `IExtensionContext`;
- WASM contributions enter the same registries as C# extensions.

### 3. Tool Registry and Tool Execution Pipeline

Replace ad hoc `agent.AddTool(...)` calls from the CLI with a registry and pipeline.

```csharp
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement? Parameters,
    ToolCapability Capability,
    Func<ToolInvocationContext, Task<ToolResult>> InvokeAsync);

public sealed record ToolInvocationContext(
    string ToolCallId,
    IReadOnlyDictionary<string, object?> Arguments,
    SessionId SessionId,
    AgentId AgentId,
    CancellationToken CancellationToken);

public sealed record ToolResult(string Text, bool IsError = false);
```

Initial behavior:

- tools still return text for model compatibility;
- agent resolves tools from `IToolRegistry`;
- tool start/end/error events are emitted centrally;
- future permission/execution/workspace decisions happen in this pipeline.

### 4. Command Registry

Commands are not model tools. They are user/frontend/plugin actions.

```csharp
public sealed record CommandDefinition(
    string Id,
    string Title,
    string? Description,
    CommandScope Scope,
    Func<CommandContext, Task<CommandResult>> ExecuteAsync);
```

Initial commands:

- `session.reset`
- `models.refresh`
- `config.open`
- `config.setApiKey`
- `chat.cancel`

CLI can keep textual commands initially, but route them through `ICommandRegistry`.

### 5. Permission Service

Add the permission seam before enforcing everything.

```csharp
public interface IPermissionService
{
    Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken ct);
}
```

Initial implementation:

- `AllowAllPermissionService` for MVP;
- optional `ConsolePermissionService` for shell/file write/high-risk operations later.

Every tool definition should declare capabilities, even if current policy allows them.

### 6. Workspace Abstraction

Put current file access behind an interface.

```csharp
public interface IWorkspace
{
    WorkspaceId Id { get; }
    string RootPath { get; }
    Task<WorkspaceReadResult> ReadPathAsync(WorkspacePath path, ReadOptions options, CancellationToken ct);
}
```

Initial implementation:

- `HostWorkspace` wrapping the current root-contained `FileTools` logic;
- no snapshots yet;
- no VFS overlays yet.

Later:

- VFS, transactions, snapshots, locks, and remote workspace implementations.

### 7. Execution Broker Seam

Move shell execution behind `IExecutionBroker`.

```csharp
public interface IExecutionBroker
{
    Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken ct);
}
```

Initial implementation:

- `LocalExecutionBroker` wrapping existing `ShellTools` behavior;
- root-contained cwd;
- timeout and output truncation;
- emits execution events;
- uses `IPermissionService`, initially allow-all.

Later:

- sandbox providers;
- workspace overlays;
- audit logs;
- remote backend execution.

### 8. Durable-Shaped Events

Introduce a new core event model without needing persistence immediately.

```csharp
public abstract record OmicronEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId);
```

Initial event families:

- session events;
- user message events;
- assistant stream events;
- tool invocation events;
- permission events;
- command events;
- execution events;
- workspace read/write events.

Bridge strategy:

- keep current `AgentEvent` temporarily for CLI display;
- internally emit `OmicronEvent` through an event sink;
- add adapter from `OmicronEvent` to CLI display events;
- eventually remove or demote old `AgentEvent` to frontend DTO.

### 9. Session Model

Introduce a `Session` or `AgentSession` abstraction that owns conversation state and event sequence.

```csharp
public sealed class AgentSession
{
    public SessionId Id { get; }
    public AgentId AgentId { get; }
    public IReadOnlyList<Message> Messages { get; }
    public IAsyncEnumerable<OmicronEvent> PromptAsync(...);
}
```

The current `Agent` can either become this type or be used internally by it. The key is that session identity, events, tools, model, config, and cancellation are coordinated outside the CLI.

### 10. Semantic UI / Content Blocks Minimal Seed

Do not build rich rendering yet, but define minimal neutral content types.

```csharp
public abstract record ContentBlock;
public sealed record TextBlock(string Text) : ContentBlock;
public sealed record MarkdownBlock(string Markdown) : ContentBlock;
public sealed record ToolCallBlock(string ToolName, string Summary, ToolResult Result) : ContentBlock;
```

Initial use:

- CLI can flatten content blocks to text;
- future TUI/GUI can render them richly;
- tools and extensions can return structured content later.

## Implementation Phases

### Phase A: Stabilize Contracts and Move Orchestration Out of CLI

Goals:

- create core IDs and event base types;
- create `OmicronHost` composition root;
- move provider registration/model discovery service out of `Program.cs`;
- move built-in tool registration out of `Program.cs`;
- keep CLI behavior equivalent.

Deliverables:

- `Events/` ID and event contracts;
- `Sessions/` initial session interfaces;
- `Extensions/` registry interfaces;
- `Providers/ProviderRegistry` or equivalent;
- `Models/ModelCatalogService` for discovery and fallback models;
- tests for host composition and provider/model registry.

Acceptance criteria:

- CLI still runs and selects models;
- tests pass;
- `Program.cs` is materially smaller and mostly frontend flow.

### Phase B: Internal C# Extension System

Goals:

- formalize internal extension registration;
- convert built-in calculator/time/file/shell registration to extensions;
- register providers through the same or adjacent registry path;
- make extension metadata visible to the host.

Deliverables:

- `IOmicronExtension`;
- `IExtensionContext`;
- `ExtensionRegistry`;
- `BuiltinToolsExtension`;
- `BuiltinProvidersExtension` if practical;
- tests proving extensions register tools/commands/providers.

Acceptance criteria:

- no direct built-in tool construction in `Program.cs`;
- an extension can register a tool and have the agent call it;
- extension pipeline has no dependency on CLI.

### Phase C: Tool Pipeline, Permissions, Workspace, Execution Broker Seams

Goals:

- replace direct tool execution with contextual invocation;
- introduce allow-all permission service;
- wrap file reads in workspace abstraction;
- wrap shell execution in execution broker;
- emit durable-shaped events for tool/permission/execution/workspace activity.

Deliverables:

- `ToolDefinition`, `ToolInvocationContext`, `ToolResult`;
- `IToolRegistry`;
- `IPermissionService` + `AllowAllPermissionService`;
- `IWorkspace` + `HostWorkspace`;
- `IExecutionBroker` + `LocalExecutionBroker`;
- adapters for existing provider-facing `Tool` schema if needed.

Acceptance criteria:

- file and shell behavior remains equivalent;
- all file/shell access goes through workspace/execution abstractions;
- tests cover path containment, timeout/truncation, and permission seam invocation.

### Phase D: Session/Event Hardening

Goals:

- make session identity and event ordering explicit;
- introduce event sink and in-memory event log;
- bridge old UI events to new core events;
- prepare for persistence without implementing disk storage yet.

Deliverables:

- `SessionId`, `AgentId`, `ToolCallId`, `EventId` value types;
- `IEventSink` / `InMemoryEventLog`;
- `AgentSession` or refactored `Agent` with sequence-aware event emission;
- event replay projection tests for basic message/tool lifecycle.

Acceptance criteria:

- prompt → assistant response emits deterministic ordered core events;
- prompt → tool call → tool result → final response emits deterministic ordered core events;
- CLI rendering is fed by event adaptation, not direct ad hoc callbacks where practical.

### Phase E: Command and Minimal Content Block Layer

Goals:

- introduce frontend-neutral commands;
- route CLI commands through registry;
- define minimal content blocks for future TUI/GUI/rendering work.

Deliverables:

- `ICommandRegistry`;
- command definitions for reset/config/refresh/cancel where practical;
- minimal `ContentBlock` records;
- output adapter to console text.

Acceptance criteria:

- CLI user commands have command IDs;
- extensions can contribute commands;
- responses/tool outputs can be represented as basic content blocks.

### Phase F: Cleanup and Boundary Enforcement

Goals:

- reduce `Program.cs` to frontend code;
- document extension authoring for internal C# extensions;
- add architecture tests or dependency tests where useful;
- update RFC baseline.

Deliverables:

- updated `IMPLEMENTATION-BASELINE.md`;
- `docs/implementation-plans/0001-core-architecture-groundwork.md` status updated;
- tests for registry/session/event/tool architecture;
- obsolete APIs marked or removed.

Acceptance criteria:

- all tests pass;
- new architecture is documented;
- future work can target persistence, TUI, sandboxing, WASM, and remoting without first rewriting the MVP wiring.

## Suggested First Pull Request

The first code PR should be deliberately small:

1. Add ID value types and base event contracts.
2. Add `IEventSink` and `InMemoryEventSink`.
3. Add `IExtensionRegistry`, `IExtensionContext`, and `IOmicronExtension` with tests.
4. Move calculator/time tool registration into `BuiltinToolsExtension`.
5. Keep `FileTools` and `ShellTools` as-is for the moment.
6. Update CLI to load built-in extensions and register their tools.

This proves the extension seam without touching the riskiest areas first.

## Risks and Mitigations

| Risk | Mitigation |
| --- | --- |
| Over-abstraction before features | Keep initial implementations tiny and directly backed by current code. |
| Breaking provider/tool calling | Preserve provider-facing tool schema and add adapters rather than replacing everything at once. |
| CLI regression | Keep CLI behavior tests/manual smoke tests for model menu, config, chat, tools. |
| WASM design mismatch | Design extension contracts as host-level semantic APIs, not .NET-specific implementation details. Future WASM adapter can translate. |
| Event model churn | Start with durable IDs/sequences/timestamps but keep payloads small and versionable. |
| Project split churn | Use namespaces first, split projects only when real code justifies it. |

## Definition of Done for Groundwork

The groundwork phase is complete when:

- the CLI is a consumer of core services, not the owner of orchestration;
- built-in C# extensions use the same registration path future plugins will use;
- tools execute through a registry and contextual invocation pipeline;
- file and shell capabilities sit behind workspace/execution/permission seams;
- sessions emit durable-shaped ordered events;
- there is an in-memory event log and projection tests;
- command and minimal content block contracts exist;
- all current tests pass and new architecture tests cover the seams;
- RFC baseline documentation is updated to reflect the new state.
