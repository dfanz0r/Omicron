# Implementation Plan 0001: Core Architecture Groundwork

Status: Draft  
Target: upgrade the current C# MVP into a stable architecture that can support the long-term RFCs incrementally.

Related plan: [0002-provider-api-abstraction.md](0002-provider-api-abstraction.md) covers the provider/session architecture for stateful APIs such as OpenAI Responses.

Important boundary: this plan must not create provider/session abstractions that Plan 0002 later has to undo. Plan 0001 should establish minimal stable seams and, where necessary, pull forward small provider-state primitives from Plan 0002 so the groundwork is a stepping stone rather than a dead end.

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
7. **Do not bake in Chat Completions assumptions.** Current `Message`/`Tool` types can remain as compatibility types, but new seams must allow typed item APIs, provider turn state, and stateless/stateful request strategies.

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

## Plan 0001 / Plan 0002 Boundary

Plan 0001 owns the architectural seams needed by all later work:

- composition host;
- registries;
- extension registration;
- tool invocation pipeline;
- session identity;
- durable-shaped event envelope;
- provider/model registry boundary;
- minimal provider turn-state seam;
- CLI extraction.

Plan 0002 owns the provider-specific depth:

- canonical conversation content model;
- provider compatibility matrix;
- OpenAI Responses request/stream implementation;
- stateful/stateless fallback policy;
- cross-API replay transforms;
- tool-call ID normalization rules beyond the minimal seam;
- storage/cache/session-affinity policy.

To avoid duplicate work, Plan 0001 should **pull forward only these pieces from Plan 0002**:

1. provider state identity/scoping types;
2. a minimal provider state store interface;
3. provider/model registry APIs that expose `ApiType` without assuming one provider equals one API;
4. request/session context objects broad enough to carry provider state later.

Plan 0001 should **not** implement the full canonical content model or real Responses API parser. It should only make sure later implementation can add them without changing the CLI or extension/tool contracts.

## Target Core Concepts

### 1. Omicron Host

Introduce a core host/facade responsible for composing services.

Candidate API:

```csharp
public sealed class OmicronHost
{
    public IExtensionRegistry Extensions { get; }
    public IProviderRegistry Providers { get; }
    public IModelCatalog Models { get; }
    public ICommandRegistry Commands { get; }
    public IToolRegistry Tools { get; }
    public IPermissionService Permissions { get; }
    public IWorkspace Workspace { get; }
    public IExecutionBroker Execution { get; }
    public ISessionManager Sessions { get; }
    public IProviderConversationStateStore ProviderState { get; }
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

### 10. Stateful Provider Sessions and API-Agnostic Conversation State

The provider layer must support both stateless transcript-resend APIs and stateful conversation APIs such as OpenAI's Responses API.

Current MVP note: `ApiType.OpenAiResponses` exists, but `OpenAiResponsesShape` currently falls back to the OpenAI Chat Completions request/stream format. That is not sufficient for first-class Responses API support.

Plan 0001 should introduce only the **minimal state seam**, not the full provider abstraction from Plan 0002:

```csharp
public readonly record struct ProviderStateKey(
    SessionId SessionId,
    AgentId AgentId,
    string ProviderName,
    string ModelId,
    ApiType ApiType);

public sealed record ProviderTurnState(
    ProviderStateKey Key,
    string? PreviousResponseId,
    string? ConversationId,
    string? SessionAffinityKey,
    JsonElement? ProviderMetadata);

public interface IProviderConversationStateStore
{
    ProviderTurnState? Get(ProviderStateKey key);
    void Set(ProviderTurnState state);
    void Clear(ProviderStateKey key);
    void ClearSession(SessionId sessionId);
}
```

Design requirements for Plan 0001:

- the core session owns logical conversation history regardless of provider API style;
- provider state is scoped by session + agent + provider + model + API type;
- reset clears provider state through `ClearSession`;
- the CLI never directly reads/writes `previous_response_id` or other provider continuation details;
- provider registries/model catalogs continue to expose `ApiType` per model;
- request/session context objects can carry provider state later without changing public CLI-facing code.

Deferred to Plan 0002:

- full canonical conversation content model;
- storage/cache policy;
- real Responses request bodies and streaming parser;
- function-call output item conversion;
- cross-API replay/fallback transforms;
- provider compatibility descriptors.

OpenAI Responses-specific requirements:

- send requests to `/v1/responses`, not `/chat/completions`;
- use `input` items rather than `messages`;
- include `previous_response_id` on subsequent turns when stateful mode is enabled;
- capture `response.completed` IDs and store them as provider turn state;
- parse typed streaming events such as `response.output_text.delta`, `response.function_call_arguments.delta`, `response.output_item.done`, `response.completed`, `response.failed`, and `error`;
- accumulate function-call arguments until the function-call item is complete;
- return tool results as `function_call_output` input items with the matching `call_id`;
- keep a stateless fallback mode that can rebuild input from Omicron's local transcript if provider-side state is unavailable or intentionally disabled.

This should be part of the provider/session groundwork, not a one-off patch inside the CLI. Plan 0001 provides the seam; [Implementation Plan 0002](0002-provider-api-abstraction.md) fills it with the detailed provider API abstraction informed by the stateful-vs-stateless API report.

### 11. Semantic UI / Content Blocks Minimal Seed

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
- introduce provider turn-state contracts so stateful APIs such as OpenAI Responses can be supported cleanly;
- keep CLI behavior equivalent.

Deliverables:

- `Events/` ID and event contracts;
- `Sessions/` initial session interfaces;
- `Extensions/` registry interfaces;
- `Providers/ProviderRegistry` or equivalent;
- `Models/ModelCatalogService` for discovery and fallback models;
- minimal `ProviderStateKey`, `ProviderTurnState`, and `IProviderConversationStateStore`;
- request/session context types that carry model `ApiType` and can later carry provider state;
- tests for host composition, provider/model registry, and provider-state scoping.

Acceptance criteria:

- CLI still runs and selects models;
- tests pass;
- `Program.cs` is materially smaller and mostly frontend flow;
- no new core abstraction assumes Chat Completions is the only request shape;
- provider state is not manipulated directly by the CLI.

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
- add provider-state events for response IDs/conversation IDs/tool output correlation IDs;
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

### Phase E: Provider Abstraction Handoff Checkpoint

Goals:

- verify that the groundwork from this plan is ready for Plan 0002;
- avoid starting a real Responses API implementation until provider state, model classification, and session contexts are in place;
- document any remaining blockers before Plan 0002 begins.

Deliverables:

- checklist showing where provider state is stored, cleared, and exposed to providers;
- tests proving reset clears provider state;
- model catalog exposes `ApiType` per model;
- provider invocation path can receive a context object rather than only a raw `IReadOnlyList<Message>` where practical;
- documented migration notes for `OpenAiResponsesShape` replacement in Plan 0002.

Acceptance criteria:

- implementing Plan 0002 does not require another CLI orchestration rewrite;
- implementing Plan 0002 does not require replacing the extension/tool pipeline;
- implementing real Responses support is isolated to provider/session abstractions, not frontend code.

### Phase F: Command and Minimal Content Block Layer

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

### Phase G: Cleanup and Boundary Enforcement

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
3. Add minimal provider state primitives: `ProviderStateKey`, `ProviderTurnState`, `IProviderConversationStateStore`, in-memory implementation.
4. Add `IExtensionRegistry`, `IExtensionContext`, and `IOmicronExtension` with tests.
5. Move calculator/time tool registration into `BuiltinToolsExtension`.
6. Keep `FileTools` and `ShellTools` as-is for the moment.
7. Update CLI to load built-in extensions and register their tools.

Do **not** implement real OpenAI Responses in this first PR. The goal is to create the seam Plan 0002 will use.

This proves the extension seam without touching the riskiest areas first.

## Risks and Mitigations

| Risk | Mitigation |
| --- | --- |
| Over-abstraction before features | Keep initial implementations tiny and directly backed by current code. |
| Breaking provider/tool calling | Preserve provider-facing tool schema and add adapters rather than replacing everything at once. |
| CLI regression | Keep CLI behavior tests/manual smoke tests for model menu, config, chat, tools. |
| WASM design mismatch | Design extension contracts as host-level semantic APIs, not .NET-specific implementation details. Future WASM adapter can translate. |
| Event model churn | Start with durable IDs/sequences/timestamps but keep payloads small and versionable. |
| Duplicating Plan 0002 work badly | Pull forward only minimal provider-state seams; defer canonical transforms and real Responses parsing to Plan 0002. |
| Project split churn | Use namespaces first, split projects only when real code justifies it. |

## Definition of Done for Groundwork

The groundwork phase is complete when:

- the CLI is a consumer of core services, not the owner of orchestration;
- built-in C# extensions use the same registration path future plugins will use;
- tools execute through a registry and contextual invocation pipeline;
- file and shell capabilities sit behind workspace/execution/permission seams;
- sessions emit durable-shaped ordered events;
- there is an in-memory event log and projection tests;
- provider turn state is session-scoped and cleared on reset;
- command and minimal content block contracts exist;
- all current tests pass and new architecture tests cover the seams;
- RFC baseline documentation is updated to reflect the new state.
