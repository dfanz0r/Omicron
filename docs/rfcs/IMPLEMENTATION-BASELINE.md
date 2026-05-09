# Current Implementation Baseline

Last reviewed: 2026-05-08

This document records the codebase state that the RFCs currently target. It is descriptive, not aspirational: when it conflicts with an individual RFC's future-state design, this baseline describes what exists today and the RFC describes the intended direction.

## Solution and Projects

Current solution layout:

```text
Omicron.slnx
Omicron.Core/          net10.0 shared agent/provider/tool/config code
Omicron.CLI/           net10.0 console MVP frontend
Omicron.Core.Tests/    xUnit tests for core/provider behavior
READ_ONLY/             external/reference snapshots, not edited in place
```

The broader package layout in RFC 0001 is still the target architecture. The current codebase has not yet split out `Omicron.UI.Abstractions`, `Omicron.Text`, `Omicron.Rendering.Terminal`, `Omicron.Terminal.Emulation`, `Omicron.Persistence`, `Omicron.Workspace`, `Omicron.Sandboxing`, `Omicron.Plugins`, `Omicron.Plugins.Wasm`, `Omicron.Remoting`, or dedicated frontend projects.

## Composition Root (`OmicronHost`)

`Omicron.Core.OmicronHost` is the central composition root. It wires together:

- `IEventSink` / `PersistentEventSink` over `InMemoryEventSink` — authoritative event stream with global monotonic sequence stamping plus session-store persistence.
- `ISessionStore` — constructor-injected, get-only session catalog/event store; defaults to `InMemorySessionStore` and can be supplied as `JsonlSessionStore`.
- `IToolRegistry` / `ToolRegistry` — tool registration and lookup.
- `ICommandRegistry` / `CommandRegistry` — command registration and lookup.
- `IPermissionService` / `AllowAllPermissionService` — permission gating.
- `IWorkspace` / `HostWorkspace` — file-system access with path containment.
- `IExecutionBroker` / `LocalExecutionBroker` — shell command execution (session-scoped).
- `IProviderRegistry` / `ProviderFactory` — LLM provider registration/lookup.
- `IModelCatalog` / `ModelCatalogService` — model discovery and free-model tracking.
- `IExtensionRegistry` / `ExtensionRegistry` — built-in and third-party extensions.
- `IProviderStateManager` / `ProviderStateManager` — provider turn state (response IDs, conversation IDs).
- `IProviderConversationStateStore` / `InMemoryProviderConversationStateStore` — raw storage primitive (storage-only, no event emission).

Built-in extensions are loaded via `LoadBuiltinExtensions()`:

- `BuiltinToolsExtension` — calculator, get_current_time.
- `BuiltinWorkspaceToolsExtension` — read_path (via `IWorkspace`).
- `BuiltinExecutionToolsExtension` — shell (via `IExecutionBroker` + `IWorkspace`).

## Session Runtime (`AgentSession`)

`Omicron.Core.Sessions.AgentSession` is the only active session/agent runtime. The legacy `Agent` class has been removed.

Key features:
- `PromptAsync(text)` — starts a session (fires `SessionStartedEvent` once per session lifecycle) and runs the LLM loop.
- `ContinueAsync()` — continues the conversation; throws if `_messages.Count == 0` (e.g., after reset).
- `Reset()` — clears messages and provider state; does NOT re-emit `SessionStartedEvent` (same-session policy).
- Uses `SessionEventWriter` for event emission (wraps `IEventSink`).
- Receives `IProviderStateManager` for provider state management.
- Receives `IToolRegistry` and `IPermissionService` for tool execution.
- Emits durable-shaped events: `TurnStartedEvent`, `UserMessageEvent`, `AssistantTextDeltaEvent`, `ToolInvocationStartedEvent`, `ToolInvocationCompletedEvent`, `AssistantResponseCompleteEvent`, `SessionErrorEvent`, `SessionResetEvent`.

## Events

Event contracts in `Omicron.Core.Events`:
- All events inherit from `OmicronEvent` with `Id`, `Sequence`, `Timestamp`, `SessionId`.
- `IEventSink.Emit()` stamps a global monotonic sequence number on every event, replacing any per-producer sequence.
- `IEventSink.EmitBatch()` returns the stamped events.
- `InMemoryEventSink` provides `GetAllEvents()` and `GetSessionEvents(SessionId)`.
- `PersistentEventSink` wraps another sink and synchronously appends stamped events to `ISessionStore` in sequence order. It exposes an `OnError` callback and `PersistedCount` / `FailureCount` diagnostics.

### Event Delivery Model

Events flow through two channels:

1. **Async stream** (`AgentSession.PromptAsync()` / `ContinueAsync()`) — yields session-scoped events for live UI rendering: turns, user messages, assistant deltas, tool invocations, errors.
   `Reset()` is synchronous and emits `SessionResetEvent` only to the sink, not the async stream.
2. **IEventSink** (shared authoritative event log) — receives all session events plus nested service events from `LocalExecutionBroker` (execution start/complete) and `ProviderStateManager` (state updated/cleared). In the default host, this is a `PersistentEventSink`, so events are also appended to the configured `ISessionStore`.

Nested service events are **not** yielded from the async stream. Frontends that need the complete event audit log should read from `IEventSink.GetSessionEvents()` / `GetAllEvents()` where available, or from `ISessionStore.ReadEventsAsync(...)` for persisted session history. The async stream is a convenient live UI feed; the sink/session store path is the authoritative durability stream.

Event hierarchy:
- `SessionStartedEvent`, `SessionResetEvent`, `SessionErrorEvent`
- `TurnStartedEvent`
- `UserMessageEvent`
- `AssistantTextDeltaEvent`, `AssistantResponseCompleteEvent`
- `ToolInvocationStartedEvent`, `ToolInvocationCompletedEvent`
- `PermissionRequestedEvent`
- `ExecutionStartedEvent`, `ExecutionCompletedEvent` (with `ExitCode`, `DurationMs`, `TimedOut`, `ToolCallId`, `Cancelled`, `Error`)
- `ProviderStateUpdatedEvent`, `ProviderStateClearedEvent`

## Provider State

- `ProviderStateKey` — scoped to session + agent + provider + model + API type; provider name is case-normalized.
- `ProviderTurnState` — carries `PreviousResponseId`, `ConversationId`, `SessionAffinityKey`, `ProviderMetadata`.
- `IProviderConversationStateStore` / `InMemoryProviderConversationStateStore` — storage-only (no event emission). `Clear()` returns `bool` indicating whether state was actually removed. `ClearSession()` returns the keys that were removed for that session.
- `IProviderStateManager` / `ProviderStateManager` — wraps store + `IEventSink`; emits `ProviderStateUpdatedEvent` / `ProviderStateClearedEvent` on mutations. Events include an optional `Reason` string. Keys are normalized before emission. Clear events are only emitted when state was actually removed.

## Implemented Providers and API Shapes

Implemented provider classes:

- `OpenAiProvider`
- `AnthropicProvider`
- `OpenCodeProvider` for OpenCode Zen and Go model discovery/use
- `OpenRouterProvider`
- `ProviderFactory` (implements `IProviderRegistry`)

Implemented/declared API shapes:

- `OpenAiChat`
- `AnthropicMessages`
- `OpenAiResponses` has a first-class `/responses` request builder/parser for OpenAI-compatible providers that register the shape, including OpenAI, OpenCode, and OpenRouter.
- `GoogleGenAi` is declared in `ApiType` but not a complete first-class provider path yet.

Provider support includes SSE parsing, tool-call accumulation, usage data where available, and reasoning-text preservation/echo behavior needed by reasoning models such as DeepSeek-style APIs.

Provider compatibility/state support includes:

- `ProviderCompatibility` and `ProviderStoragePolicy` for model-level capabilities and storage behavior.
- `CompatibilityDetector` for stateful/stateless support decisions.
- `ToolCallIdMapper` and session-level duplicate tool-call ID normalization for replay/provider edge cases.
- OpenRouter Responses routing defaults to stateless full-context replay (`PreferStateless`, no `previous_response_id`) unless provider-managed continuation is known reliable.

## Canonical Conversation Seed

`Omicron.Core.Models.Conversation` implements the current MVP canonical conversation seed:

- `ConversationTurn`, `MessageId`, `ConversationContent`, `ProviderOrigin`.
- content item records for text, images, reasoning, tool calls, and tool results.
- `ConversationConverter` converts between the existing flat `Message` model and canonical turns.

This is not yet the sole runtime transcript model; `AgentSession` still keeps `List<Message>` internally and providers bridge from that representation. The canonical records exist to avoid baking provider-specific wire formats into the long-term model.

## Implemented Tools

Registered through built-in extensions:

- `calculator` — basic arithmetic expression evaluation.
- `get_current_time` — returns current UTC/local time.
- `read_path` — file reads with line numbers, offset/limit, binary detection, directory listings, truncation.
- `shell` — command execution via `IExecutionBroker`, workspace-confined `cwd`, timeout clamping, output truncation.

All tools sit behind their respective abstractions (workspace, execution, permission, tool registry).

## Implemented CLI MVP

Implemented in `Omicron.CLI`:
- Consumes `OmicronHost`, `AgentSession`, `IModelCatalog`, `IProviderRegistry`.
- Model discovery on startup and manual `refresh`.
- Fallback built-in models for OpenAI and Anthropic.
- Model menu showing keyed/free providers and last-used model first.
- TOML config UI for API keys, system prompt, max tokens, temperature, display width/line limits, and max iterations.
- Basic line editor (`LineEditor`) for chat input.
- Chat loop starts directly with the last-used visible model (or first visible model) instead of an initial model-selection menu.
- Slash-command chat UX with `/help`, `/model`, `/models`, `/model <n|key>`, `/status`, `/tools`, `/events`, `/provider-state`, `/clear-state`, `/reset`, `/exit`, and `/quit`.
- Chat loop with streaming output, Escape-to-cancel polling, tool-call display, and usage display.
- Output truncation helper for long tool results.
- Event stream dispatcher reads from `session.PromptAsync()`. Per the current event-delivery model, only yielded session events reach the live renderer:
  `UserMessageEvent`, `AssistantTextDeltaEvent`, `ToolInvocationStartedEvent`, `ToolInvocationCompletedEvent`, `SessionStartedEvent`, `PermissionRequestedEvent`, `SessionErrorEvent`.
  Switch cases for `ExecutionStartedEvent`, `ExecutionCompletedEvent`, `ProviderStateUpdatedEvent`, `ProviderStateClearedEvent`, and `SessionResetEvent` exist but are not reached in the current flow because these events are emitted directly to the sink (not yielded from `PromptAsync()`).

## Execution Broker

- `LocalExecutionBroker` implements `IExecutionBroker`.
- `ExecutionRequest.SessionId` is required (first positional parameter).
- Execution events are always emitted for brokered execution; no synthetic/random session IDs.
- Completion events use `DateTimeOffset.UtcNow` (not start time).
- Events include `ToolCallId`, `Cancelled`, `Error` fields.
- Events are emitted for all exit paths (normal, timeout, cancellation, unknown shell).

## Model Catalog

- `IModelCatalog` / `ModelCatalogService` handles fallback seeding and provider-specific discovery.
- Free models tracked by catalog keys.
- OpenRouter discovery marks models free only when parsed prompt and completion prices are both zero; missing/unparseable pricing is treated as not-free.
- OpenRouter defaults broad model compatibility to Chat, but known Responses-capable model families route to `OpenAiResponses` and use `https://openrouter.ai/api/v1/responses`.
- The fallback catalog includes `or:openai/gpt-5.4-mini` as a non-free OpenRouter Responses test model; it appears in the CLI when an OpenRouter API key is configured.
- OpenRouter Responses models default to stateless full-context requests (`PreferStateless`, no `previous_response_id`) because routed backends may reject `function_call_output` continuation against provider-managed state.
- Provider-specific discovery logic is centralized in the catalog; a dedicated `IModelDiscoveryProvider` abstraction remains deferred.

## Persistence

Implemented in Plan 3 Phase 1 (see `docs/implementation-plans/0003-core-persistence-workspace-foundations.md`):

- `ISessionStore` interface — `CreateSessionAsync`, `ListSessionsAsync`, `GetSessionAsync`, `UpdateSessionAsync`, `AppendEventsAsync`, `ReadEventsAsync`, `GetEventCountAsync`.
- `SessionRecord` — persisted session metadata (id, model, provider, API type, timestamps, status).
- `InMemorySessionStore` — thread-safe in-memory implementation for tests/default.
- `JsonlSessionStore` — file-backed store using per-session `.jsonl` files + `sessions.json` index with `$type` discriminator for polymorphic event serialization.
- `PersistentEventSink : IEventSink` — wraps any sink and synchronously persists stamped events to `ISessionStore` in sequence order. Exposes `OnError` callback and `PersistedCount`/`FailureCount` counters.
- `OmicronHost` constructor-injects `ISessionStore` (get-only). The host-level `Events` sink is a `PersistentEventSink` that all producers (`AgentSession`, `ProviderStateManager`, `LocalExecutionBroker`) use, so session, provider-state, and execution events are all durable.

The event sink stamps a global monotonic sequence number on every event before persistence, ensuring ordering is preserved in the event store.

Persistence failure is non-fatal for runtime — errors are reported through the `OnError` callback for logging/debugging.

## Not Yet Implemented

The following RFC capabilities remain planned/research unless otherwise noted:

- Session replay projection, snapshots/checkpoints, and resume (Plan 3 Phase 2+).
- Workspace VFS, overlays, transactions, diff manifests, and host reconciliation.
- Full typed-item canonical conversation runtime replacing the flat `Message` transcript.
- Plugin model, semantic UI abstractions, panels/status providers, WASM runtime.
- Text store, grapheme/cell layout, frame buffers, differential renderer, app-owned fullscreen scrollback.
- Embedded PTY/virtual terminal panes.
- Sandboxing/execution broker/audit policy model.
- High-reliability edit harness, anchors, AST context curation, multi-file transactions.
- Remoting protocol/backends/attachment model.
- GUI frontend.
- Rust/C# FFI layer and OpenTUI native backend spike.
- Markdown parsing, syntax highlighting, theme system.
- Provider-contributed model discovery (`IModelDiscoveryProvider`).
- Credential env-var metadata (currently in CLI code).
- Shell-specific argument escaping.

## Current Tests

Tests are split into focused files by subsystem:

- `AgentSessionTests.cs` — AgentSession lifecycle and event emission.
- `EventSinkTests.cs` — EventId, event types, InMemoryEventSink behavior.
- `ProviderStateTests.cs` — ProviderStateKey, store, manager, event emission.
- `ExecutionBrokerTests.cs` — LocalExecutionBroker event emission (platform-aware).
- `ToolRegistryTests.cs` — ToolRegistry, CommandRegistry, PermissionService, Workspace.
- `ExtensionRegistryTests.cs` — ExtensionRegistry, OmicronHost composition.
- `ModelCatalogTests.cs` — ModelCatalogService free model tracking.
- `AgentTests.cs` (now `CoreModelTests`) — Message, ToolSchema, Model, LlmResult, UsageInfo.
- `ProviderTests.cs` — provider parsing/shape behavior.
- `ProviderCompatibilityTests.cs` — ProviderCompatibility, storage policy, model compatibility.
- `CompatibilityDetectorTests.cs` — API type/compatibility/storage-policy detection.
- `ToolCallIdMapperTests.cs` — tool call ID normalization.
- `ConversationConverterTests.cs` — canonical conversation round-trip.
- `OpenAiResponsesShapeTests.cs` — Responses API request/parser tests.
- `SessionStoreTests.cs` — InMemorySessionStore CRUD, events, range queries, concurrency.
- `JsonlSessionStoreTests.cs` — JSONL file-backed store reopen/round-trip/count/duplicate/integration.
- `PersistenceIntegrationTests.cs` — OmicronHost + ISessionStore end-to-end, provider-state/execution event persistence, error callback, persist counts.

`dotnet test Omicron.slnx --nologo` passes with **219 tests**, 0 warnings, 0 errors.

## Current Handoff Points

The next core phase should build on:

- `AgentSession` as the only active runtime path.
- host-level `PersistentEventSink` + `ISessionStore` as the durable event/session foundation.
- `IModelCatalog` for model/API classification.
- `IProviderRegistry` for provider lookup/registration.
- `ProviderStateManager` for `previous_response_id`, conversation IDs, session affinity, and state-clearing behavior.
- `IEventSink` / `SessionEventWriter` for session, provider-state, execution, and audit events.
- canonical conversation records/converters as the seed for future typed transcript work.
- `ToolRegistry` and `ToolInvocationContext` for tool/function-call round trips.

Immediate next target: Plan 3 Phase 2 session replay projection over persisted `OmicronEvent` streams.
