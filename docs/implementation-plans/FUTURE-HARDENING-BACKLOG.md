# Future Hardening Backlog

Status: Active tracking document  
Last updated: 2026-05-10

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

### FH-0023: Add `WorkspaceTransaction` commit failure recovery test

Status: Open  
Area: `Omicron.Core.Tests/WorkspaceTransactionTests.cs`  
Priority: Medium.

Current state:

- `CommitAsync` has a try/catch that sets `_committed = false` on failure, and attempts rollback via operation journaling (FH-0019 implementation is complete).
- No test verifies this behavior or the partial-commit scenario.

Risk:

- Commit failure semantics are untested.

Potential follow-up:

- Add a test using a mock `HostWorkspaceFileSystem` that throws mid-commit.
- Verify rollback reverses earlier operations.
- Verify `TransactionRolledBackEvent` is emitted.

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

## Review Cadence

Revisit this backlog:

- before implementing remoting or multi-frontend event streaming;
- before adding sub-agents/concurrent sessions;
- before replacing JSONL MVP persistence with a production backend;
- whenever a new `OmicronEvent` subtype is added;
- before entering each new implementation plan (Plan 4, Plan 5, etc.).
