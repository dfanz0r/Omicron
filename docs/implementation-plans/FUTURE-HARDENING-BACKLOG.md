# Future Hardening Backlog

Status: Active tracking document  
Date: 2026-05-12

This document tracks non-blocking hardening items discovered during implementation reviews. Completed items are archived in `COMPLETED-HARDENING-BACKLOG.md`.

## Workspace / Read Model Hardening

### FH-0006: Workspace read continuation hints should be structured

Status: Open  
Area: `Omicron.Core/Workspace/WorkspaceReadContent.cs`, `WorkspaceLlmTextRenderer`  
Priority: Low before Phase 4; medium before richer edit/read UX.

Current state:

- `WorkspaceReadService` supports chunk-relative `offset` when `chunk` is set.
- `WorkspaceFileContent.NextOffset` is rendered as a simple text hint: `[Use offset=N to continue.]`.
- The hinted offset is currently a valid continuation path when used without `chunk`, but the text does not explicitly say whether to keep or omit `chunk`.

Risk:

- Models or future UIs may combine the hint with the prior `chunk`, causing confusing continuation behavior because offset is chunk-relative when chunk is present.

Potential follow-up:

- Add structured continuation metadata to `WorkspaceFileContent`, such as `ContinuationReadOptions` or an explicit absolute/relative marker.
- Render clearer LLM text, e.g. `[Use offset=N without chunk to continue.]`.
- Allow future UI/web renderers to expose continuation as an action rather than parsing text.

---

## Workspace Transaction Hardening

### FH-0008: Transaction manager currently assumes host-backed workspace filesystem

Status: Open  
Area: `Omicron.Core/OmicronHost.cs`, `Omicron.Core/Workspace/WorkspaceTransactionManager.cs`  
Priority: Low until alternative workspace backends are introduced.

Current state:

- `OmicronHost` constructs `FileSystem` as `HostWorkspaceFileSystem` and then casts it back to wire `WorkspaceTransactionManager`:
  `WorkspaceTransactions = new WorkspaceTransactionManager((HostWorkspaceFileSystem)FileSystem);`
- This is safe today because the host controls construction.

Risk:

- If `FileSystem` later becomes injectable as only `IWorkspaceFileSystem`, the cast will fail or force host-backed assumptions into transaction management.
- Future remote/snapshot/overlay workspaces may need transaction managers that do not depend on `HostWorkspaceFileSystem`.

Potential follow-up:

- Make `WorkspaceTransactionManager` depend on an interface capable of host mutation/transaction backing.
- Add backend-specific transaction manager implementations.
- Validate host wiring without concrete casts when alternative VFS backends exist.

---

### FH-0009: Transaction manager integration test lives in persistence test file

Status: Open  
Area: `Omicron.Core.Tests/PersistenceIntegrationTests.cs`  
Priority: Low.

Current state:

- `OmicronHost_TransactionManager_CreatesWorkingTransaction` verifies host transaction-manager wiring.
- The test currently lives in `PersistenceIntegrationTests.cs`, although it is a host/workspace integration test rather than persistence-specific.

Risk:

- Test organization becomes harder to navigate as host/workspace coverage grows.

Potential follow-up:

- Move the test to `WorkspaceVfsTests.cs`, `WorkspaceTransactionTests.cs`, or a dedicated `OmicronHostIntegrationTests.cs` file.
- Keep coverage unchanged.

---

## Session Resume / Fork Hardening

### FH-0011: Session slash commands synchronously block on async store calls

Status: Open  
Area: `Omicron.CLI/SlashCommandDispatcher.cs`  
Priority: Low for local JSONL MVP; medium before remote stores/TUI.

Current state:

- CLI slash command handlers call async session-store/host methods with `.GetAwaiter().GetResult()`.
- The dispatcher is currently synchronous, so this is simple and works for local stores.

Risk:

- Slow/remote stores could block the UI.
- Future async frontends will need async command dispatch anyway.

Potential follow-up:

- Make slash command dispatch async.
- Thread cancellation through command handlers.
- Keep synchronous wrapper only for simple CLI if needed.

---

### FH-0012: Fork lineage metadata/events are not represented

Status: Open  
Area: `Omicron.Core/OmicronHost.cs`, session events/store metadata  
Priority: Low for MVP materialized fork; medium before branch/history UI.

Current state:

- Forking can create a new session, but there is no explicit lineage metadata or event recording source session id/fork point.

Risk:

- Future session history UI cannot show branch relationships.
- Debugging fork provenance requires external knowledge.

Potential follow-up:

- Add source session id/fork point metadata to `SessionRecord`, or add a dedicated `SessionForkedEvent`.
- Project/display fork lineage in future session UI.

---

## AgentSession Hardening

### FH-0013: Refactor `AgentSession.RunLoopAsync` into smaller private methods

Status: Open  
Area: `Omicron.Core/Sessions/AgentSession.cs`  
Priority: Low.

Current state:

- `RunLoopAsync` is ~250 lines handling streaming, provider state, tool calls, permission checks, and error handling in one method.

Risk:

- Hard to follow and test in isolation.

Potential follow-up:

- Extract private methods: `BuildChatOptions()`, `StreamResponseAsync()`, `HandleToolCallsAsync()`, `BuildAssistantMessage()`.

---

## Tooling Hardening

### FH-0014: Cache `ToolSchema` outputs or document imperative builder

Status: Open  
Area: `Omicron.Core/Tools/Tool.cs`  
Priority: Low.

Current state:

- `ToolSchema.Object()` uses `Utf8JsonWriter` to build JSON from scratch every time. Tool schemas are typically static per tool definition.

Risk:

- Unnecessary allocations for frequently referenced tool schemas.

Potential follow-up:

- Cache `JsonElement` results per tool definition, or document that the imperative builder is MVP-only.

---

### FH-0029: Improve model feedback for invalid tool-call arguments

Status: Open
Area: `Omicron.Core/Extensions/BuiltinExecutionToolsExtension.cs`, `Omicron.Core/Tools/`, provider tool-call handling
Priority: Medium.

Current state:

- The `shell` tool schema marks both `shell` and `command` as required, but models can still emit malformed calls such as an empty `shell` value.
- Current feedback is technically correct but not model-repair-friendly:
  `Error: unknown shell ''.`
- The result wrapper echoes `[shell: ]`, which reinforces the malformed value but does not clearly tell the model how to fix the next call.

Risk:

- Models may repeatedly retry invalid tool calls because the error does not include a concise correction, valid values, or an example repaired call.
- Similar issues will apply to other tools as schemas grow more structured.

Potential follow-up:

- Add a tool-argument validation layer that returns structured, model-facing repair hints before invoking tool implementations.
- For enum-like parameters, include valid values and defaults, e.g. `shell is required; use one of: bash, sh, zsh, fish, pwsh, powershell, cmd. Example: { "shell": "bash", "command": "ls -la" }`.
- Consider making `shell` optional and defaulting it in `LocalExecutionBroker` when omitted, while still warning on empty/invalid values.
- Surface validation failures as structured `ToolResult` content blocks so TUI/GUI/Web frontends can display them separately from command stderr/stdout.
- Add regression tests for malformed tool calls: missing required argument, empty string, wrong type, unknown enum value, invalid cwd, and timeout out of range.

---

## Model Hardening

### FH-0016: Adopt `ConversationTurn` canonical model in `AgentSession` (long-term)

Status: Open  
Area: `Omicron.Core/Models/Conversation.cs`, `Omicron.Core/Sessions/AgentSession.cs`  
Priority: Low for MVP; medium before richer content models.

Current state:

- `ConversationTurn`, `ConversationContent`, and `ConversationConverter` exist but `AgentSession` and all providers use `Message` directly. The canonical model is only used in tests.

Risk:

- The canonical format was designed for cross-provider portability but is not exercised at runtime. Divergence will grow.

Potential follow-up:

- Migrate `AgentSession` internal transcript from `Message` to `ConversationTurn`, or reduce the canonical surface area until it is actually adopted.

---

### FH-0020: Decouple `Model.Provider` from catalog metadata

Status: Open  
Area: `Omicron.Core/Models/Model.cs`, `Omicron.Core/Models/ModelCatalogService.cs`  
Priority: Low.

Current state:

- `Model` carries a mutable `Provider` property. Both `ProviderFactory` and `ModelCatalogService` mutate it, creating dual ownership.

Risk:

- Catalog metadata and runtime provider resolution are conflated.

Potential follow-up:

- Resolve providers at call time via `IProviderRegistry.GetProvider(model.ProviderName)` rather than caching on `Model`.

---

## Test Coverage Gaps

### FH-0024: Add `ConfigManager` save/load round-trip test

Status: Open  
Area: `Omicron.Core.Tests/`  
Priority: Low.

Current state:

- Config persistence via Tomlyn is untested.

Risk:

- A regression in Tomlyn serialization would break user configs.

Potential follow-up:

- Add `ConfigManagerTests` in a temp directory covering save/load round-trip.

---

### FH-0025: Add `OpenCodeProvider` routing tests

Status: Open  
Area: `Omicron.Core.Tests/`  
Priority: Low.

Current state:

- `ResolveApiType`, `ResolveBaseUrl`, and `ResponsesModelIds` are not directly tested.

Risk:

- Routing regressions for Anthropic vs OpenAI vs Responses models would go undetected.

Potential follow-up:

- Add `OpenCodeProviderTests` covering model ID → ApiType resolution.

---

### FH-0026: Add `ProviderStateManager.ClearSession` event emission test

Status: Open  
Area: `Omicron.Core.Tests/ProviderStateTests.cs`  
Priority: Low.

Current state:

- `ClearSession` is tested on the raw store but not on `ProviderStateManager` (which emits events).

Risk:

- Event emission count/reason for bulk clears is untested.

Potential follow-up:

- Add a test verifying that `ProviderStateManager.ClearSession` emits the correct number of `ProviderStateClearedEvent`s.

---

### FH-0027: Azure OpenAI Chat Completions requires `api-version` query parameter

Status: Open  
Area: `Omicron.Core/Providers/OpenAiProvider.cs`, `Omicron.Core/Providers/IChatProvider.cs` (`ShapeBasedProvider`), `Omicron.Core/Providers/ApiShape.cs`  
Priority: Medium for Azure OpenAI users. Low until Azure is officially supported.

Current state:

- `OpenAiProvider` (and `ShapeBasedProvider`) constructs the endpoint as `{baseUrl.TrimEnd('/')}/chat/completions`.
- Azure OpenAI endpoints require the `?api-version=YYYY-MM-DD-preview` query parameter (e.g. `?api-version=2025-01-01-preview`).
- Without it, Azure returns HTTP 404.
- The OpenAI SDK's `AzureOpenAI` client handles this transparently, but `OpenAiProvider` uses raw `HttpClient`.
- The Responses API shape (`OpenAiResponsesShape`) may also need the same treatment if Azure later supports it.
- `OpenCodeProvider` and `OpenRouterProvider` inherit the same URL construction from `ShapeBasedProvider` and would be affected if pointed at Azure proxies.

Risk:

- Users targeting Azure OpenAI deployments (e.g. `cognitiveservices model-router`) cannot use the `OpenAiChat` ApiType even though the request/response body is otherwise identical.
- Work-arounds like appending `?api-version=…` to `model.BaseUrl` are fragile because `ShapeBasedProvider` strips the trailing slash, which could break query-string handling if not done carefully.

Potential follow-up:

- Introduce a provider-level or model-level `ApiVersion` property.
- In `ShapeBasedProvider.StreamAsync`, append `?api-version={apiVersion}` when the provider/model is flagged as Azure.
- Alternatively, introduce an `AzureOpenAiProvider` that wraps the `Azure.AI.OpenAI` SDK (similar to how `azure-openai-responses` works) for the Chat Completions path.
- Consider a more general `QueryParameters` dictionary on `Model` or `ChatOptions` so other hosted/proxied endpoints can inject required parameters.

---

### FH-0028: Custom model source via config and tunable API model parameters

Status: Open  
Area: `Omicron.Core/Models/`, `Omicron.Core/Config/`, `Omicron.CLI/Program.cs`  
Priority: Medium.

Current state:

- Models are discovered automatically from provider catalog endpoints (e.g. OpenAI `/v1/models`).
- There is no way to add a custom model entry via config (e.g. a self-hosted endpoint, a model-router deployment, or a provider not in the catalog).
- Model parameters (`temperature`, `max_tokens`, `reasoning_effort`) are set globally or per-session but cannot be overridden per-model in config.

Risk:

- Users targeting custom endpoints (Azure model-router, local vLLM, Ollama, etc.) cannot add them without modifying the provider catalog.
- Tuning parameters per-model (e.g. low temperature for code, high for creative writing) requires CLI workarounds.

Potential follow-up:

- Add a `Models` section to `AgentConfig` for user-defined model entries with `id`, `name`, `providerName`, `baseUrl`, `apiKey`, and `apiType`.
- Allow per-model overrides in config: `temperature`, `maxTokens`, `reasoningEffort`, `storagePolicy`.
- Merge user-defined models into the catalog at startup.
- Expose parameters via `/model` or `/config` slash commands for runtime tuning.

---

## Review Cadence

Revisit this backlog:

- before implementing remoting or multi-frontend event streaming;
- before adding sub-agents/concurrent sessions;
- before replacing JSONL MVP persistence with a production backend;
- whenever a new `OmicronEvent` subtype is added;
- before entering each new implementation plan (Plan 4, Plan 5, etc.).
