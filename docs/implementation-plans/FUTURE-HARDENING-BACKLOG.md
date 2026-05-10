# Future Hardening Backlog

Status: Active tracking document  
Last updated: 2026-05-08

This document tracks non-blocking hardening items discovered during implementation reviews. Items here are not immediate blockers for the current implementation phase, but should be revisited before production-grade persistence/remoting/multi-session work.

## Persistence / Event Store Hardening

Source reviews:

- `docs/code-reviews/0030-plan-3-phase-1-completion-review.md`
- `docs/code-reviews/0031-plan-3-progress-review.md`

### FH-0001: JSONL concurrency is MVP-level

Status: Open  
Area: `Omicron.Core/Sessions/SessionStore.cs` (`JsonlSessionStore`)  
Priority: Medium before multi-session/concurrent frontend usage; low for current CLI MVP.

Current state:

- `JsonlSessionStore` is suitable for local MVP durability.
- Event writes are synchronous and preserve order on the emitting call path.
- It is not designed as a robust concurrent writer backend.

Risk:

- Concurrent appends for the same or different sessions could contend/fail depending on timing and file-system behavior.
- Future remoting, multi-agent, or multi-frontend usage will need stronger append ordering and locking guarantees.

Potential follow-up:

- Add per-session append locks or a centralized serialized write queue.
- Consider a single writer loop with flush semantics.
- Add stress tests for concurrent session/event appends.
- Revisit whether JSONL remains sufficient or should be replaced by SQLite/embedded DB for durable production use.

### FH-0002: `PersistentEventSink` diagnostic counters are not atomic

Status: Completed 2026-05-08  
Area: `Omicron.Core/Events/PersistentEventSink.cs`  
Priority: Low.

Completion note:

- `PersistedCount` and `FailureCount` now use `Volatile.Read`-backed getters.
- Updates use `Interlocked.Increment` / `Interlocked.Add`.

Original state:

- `PersistedCount` and `FailureCount` were diagnostic counters.
- Counter increments were not atomic.

Risk:

- Counter values can be inaccurate under concurrent event emission.
- Event persistence correctness is not directly affected.

Potential follow-up:

- Back counters with private `long` fields.
- Use `Interlocked.Increment` / `Interlocked.Add` for updates.
- Expose reads through `Volatile.Read` or equivalent property getters.
- Add a small concurrency test if/when the sink is expected to be used concurrently.

### FH-0003: JSONL event deserialization switch requires manual maintenance

Status: Open  
Area: `Omicron.Core/Sessions/SessionStore.cs` (`JsonlSessionStore.DeserializeEvent`)  
Priority: Medium as event type count grows.

Current state:

- JSONL event round-trip uses a `$type` discriminator and manual switch over concrete `OmicronEvent` types.
- Each new event type must be added to this switch.

Risk:

- New events can be persisted but silently skipped on replay if the switch is not updated.
- Replay projection and resume features may miss newly introduced event types.

Potential follow-up:

- Add a test that enumerates all concrete `OmicronEvent` subclasses and verifies JSONL round-trip coverage.
- Move event serialization/deserialization into an explicit event type registry.
- Consider source generation or a central `EventTypeRegistry` used by both persistence and tests.

### FH-0004: Non-session services manually construct `EventEnvelope`

Status: Completed 2026-05-08  
Area: `Omicron.Core/Events/EventEnvelope.cs`, non-session event producers such as `ProviderStateManager`  
Priority: Low.

Completion note:

- Added `EventEnvelope.ForSession(SessionId)`.
- Updated `ProviderStateManager` to use the helper for provider-state events.

Original state:

- `AgentSession` used `SessionEventWriter.Envelope()` for standard session event metadata.
- `ProviderStateManager` did not have a `SessionEventWriter`, so it constructed envelopes directly:
  `new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, sessionId)`.

Risk:

- If more non-session services emit events directly, envelope creation boilerplate may spread.
- Repeated manual construction increases the chance of inconsistent timestamp/sequence conventions.

Potential follow-up:

- Add a static helper such as `EventEnvelope.Create(SessionId sessionId)` or `EventEnvelope.ForSession(SessionId sessionId)`.
- Update non-session producers to use the helper.
- Keep `SessionEventWriter.Envelope()` for session-scoped runtime code.

### FH-0005: Move VFS host integration test to a better test file

Status: Completed 2026-05-08  
Area: `Omicron.Core.Tests/WorkspaceVfsTests.cs`, workspace/VFS tests  
Priority: Low.

Completion note:

- Moved `OmicronHost_WorkspaceIsVfsBacked_AndReadsFile` to `WorkspaceVfsTests.cs`.
- Removed it from `PersistenceIntegrationTests.cs`.

Original state:

- `OmicronHost_WorkspaceIsVfsBacked_AndReadsFile` verified that `OmicronHost.Workspace` is a `VfsWorkspaceAdapter` and preserves formatted reads through the VFS-backed path.
- The test lived in `PersistenceIntegrationTests.cs` even though it was a workspace/host integration test, not a persistence test.

Risk:

- Test organization becomes harder to navigate as subsystem coverage grows.

Potential follow-up:

- Move `OmicronHost_WorkspaceIsVfsBacked_AndReadsFile` to `WorkspaceVfsTests.cs`, `OmicronHostIntegrationTests.cs`, or another workspace/host-focused test file.
- Keep the assertion coverage unchanged.

## Workspace / Read Model Hardening

Source reviews:

- `docs/code-reviews/0049-workspace-read-model-final-verification.md`

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

## Workspace Transaction Hardening

Source reviews:

- `docs/code-reviews/0060-workspace-transactions-final-verification.md`

### FH-0007: Workspace transaction commit is non-atomic

Status: Open  
Area: `Omicron.Core/Workspace/WorkspaceTransaction.cs`  
Priority: Medium before high-reliability edit tools; low for current transaction MVP.

Current state:

- `WorkspaceTransaction.CommitAsync()` applies staged host mutations sequentially.
- If one operation fails midway, earlier operations may already have changed the host workspace.
- The transaction resets `_committed` on failure so callers can retry or roll back staged state, but it cannot automatically undo host mutations already applied.

Risk:

- Future edit tools may assume transaction commit is atomic when it is currently best-effort.
- Partial commit failures can leave the host workspace in a mixed state.

Potential follow-up:

- Preflight validate all staged operations before applying.
- Apply writes through temp files/backups and rename where feasible.
- Journal host mutations and attempt rollback on failure.
- Emit transaction audit events only after fully successful commits.

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

## Session Resume / Fork Hardening

Source reviews:

- `docs/code-reviews/0062-session-resume-fork-ux-review.md`

### FH-0010: Session prefix matching should detect ambiguity

Status: Open  
Area: `Omicron.CLI/SlashCommandDispatcher.cs`  
Priority: Low for MVP; medium as saved session count grows.

Current state:

- `/resume <prefix>` and `/fork <prefix>` use the first `SessionId` prefix match.
- If multiple sessions share a prefix, the CLI silently selects whichever appears first in the listed session order.

Risk:

- User may resume/fork the wrong session when using short prefixes.

Potential follow-up:

- Detect multiple prefix matches and print all candidates.
- Require a longer prefix or index when ambiguous.

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

## Plan 3 Comprehensive Review Hardening

Source reviews:

- `docs/code-reviews/0065-plan-3-comprehensive-pre-plan-4-review.md`
- `docs/code-reviews/0067-plan-3-second-fix-verification.md`

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

### FH-0015: Add `EmitAsync`/`EmitBatchAsync` to `IEventSink` to eliminate sync-over-async

Status: Open
Area: `Omicron.Core/Events/IEventSink.cs`, `PersistentEventSink.cs`
Priority: Medium before async UI or background workers.

Current state:
- `IEventSink` is sync-only. `PersistentEventSink` calls `_store.AppendEventsAsync(...).GetAwaiter().GetResult()`.

Risk:
- Can deadlock in contexts with a synchronization context (UI thread, ASP.NET).

Potential follow-up:
- Add `EmitAsync`/`EmitBatchAsync` overloads to `IEventSink`. Keep sync variants for convenience but document the hazard.

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

### FH-0017: Make `AgentSession` config immutable after construction

Status: Open
Area: `Omicron.Core/Sessions/AgentSession.cs`
Priority: Medium.

Current state:
- `Model`, `SystemPrompt`, `ApiKey`, `MaxTokens`, `Temperature`, `ReasoningEffort`, `MaxIterations` are all settable after construction.

Risk:
- Config drift between `SessionRecord` and runtime object. Snapshots for edit history may capture inconsistent state.

Potential follow-up:
- Make config properties `init`-only or move into a `SessionConfig` record passed at construction.

### FH-0018: Add transaction lifecycle events (`TransactionStartedEvent`, etc.)

Status: Open
Area: `Omicron.Core/Workspace/WorkspaceTransaction.cs`, `Omicron.Core/Events/OmicronEvent.cs`
Priority: High for Plan 4.

Current state:
- `WorkspaceTransaction` does not emit events for start, stage, commit, or rollback.

Risk:
- Edit harness cannot observe transaction state for UI feedback, undo, or audit logs.

Potential follow-up:
- Add `TransactionStartedEvent`, `TransactionStagedEvent`, `TransactionCommittedEvent`, `TransactionRolledBackEvent` to the event model, and emit them from `WorkspaceTransaction`.

### FH-0019: Make `WorkspaceTransaction.CommitAsync` atomic or add rollback tracking

Status: Open
Area: `Omicron.Core/Workspace/WorkspaceTransaction.cs`
Priority: High for Plan 4.

Current state:
- `CommitAsync` applies staged changes one by one. If a later operation throws, earlier operations remain applied and `_committed` is reset to `false`.

Risk:
- Partial commit leaves the host workspace in a mixed state with no recovery path.

Potential follow-up:
- Two-phase commit via temp files + rename, or track succeeded operations and auto-rollback on failure. Document MVP best-effort semantics if simpler.

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

### FH-0021: Add `LineEditor` tests

Status: Open
Area: `Omicron.CLI/LineEditor.cs`
Priority: Medium.

Current state:
- `LineEditor` has zero test coverage despite handling complex logic (paste detection, tab completion, multiline input, escape handling).

Risk:
- UX bugs directly affect the primary input path. No regression protection.

Potential follow-up:
- Add `LineEditorTests` using a mocked console input stream or by extracting logic into a testable state machine.

### FH-0022: Add `JsonlSessionStore` concurrent access stress test

Status: Open
Area: `Omicron.Core.Tests/JsonlSessionStoreTests.cs`
Priority: Medium.

Current state:
- The store uses `lock` internally, but no test validates concurrent `AppendEventsAsync` from multiple threads.

Risk:
- Concurrent session writes could have latent ordering or corruption bugs.

Potential follow-up:
- Add a concurrent stress test appending events from multiple threads and verifying sequence integrity.

### FH-0023: Add `WorkspaceTransaction` commit failure recovery test

Status: Open
Area: `Omicron.Core.Tests/WorkspaceTransactionTests.cs`
Priority: Medium.

Current state:
- `CommitAsync` has a try/catch that sets `_committed = false` on failure, but no test verifies this behavior or the partial-commit scenario.

Risk:
- Commit failure semantics are untested.

Potential follow-up:
- Add a test using a mock `HostWorkspaceFileSystem` that throws mid-commit.

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

## Review Cadence

Revisit this backlog:

- before implementing remoting or multi-frontend event streaming;
- before adding sub-agents/concurrent sessions;
- before replacing JSONL MVP persistence with a production backend;
- whenever a new `OmicronEvent` subtype is added;
- before entering each new implementation plan (Plan 4, Plan 5, etc.).
