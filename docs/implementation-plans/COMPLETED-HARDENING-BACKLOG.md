# Completed Hardening Backlog

Status: Archive of completed items  
Last updated: 2026-05-10

This document records hardening items that have been implemented and reviewed. Items here are no longer active work but are preserved for reference and audit history.

---

## Persistence / Event Store Hardening

### FH-0001: JSONL concurrency is MVP-level

Status: **Completed 2026-05-10**  
Area: `Omicron.Core/Sessions/SessionStore.cs` (`JsonlSessionStore`)  
Priority: Medium.

Completion note:

- Added per-session `SemaphoreSlim` locking via `ConcurrentDictionary<SessionId, SemaphoreSlim>`.
- `AppendEventsAsync` acquires the lock per session before file access.
- Added `JsonlConcurrencyTests` with 10-task × 100-event stress test.

Source reviews:

- `docs/code-reviews/0030-plan-3-phase-1-completion-review.md`
- `docs/code-reviews/0031-plan-3-progress-review.md`
- `docs/code-reviews/0074-plan-3.6-comprehensive-review.md`

---

### FH-0002: `PersistentEventSink` diagnostic counters are not atomic

Status: **Completed 2026-05-08**  
Area: `Omicron.Core/Events/PersistentEventSink.cs`  
Priority: Low.

Completion note:

- `PersistedCount` and `FailureCount` now use `Volatile.Read`-backed getters.
- Updates use `Interlocked.Increment` / `Interlocked.Add`.

Source reviews:

- `docs/code-reviews/0030-plan-3-phase-1-completion-review.md`
- `docs/code-reviews/0031-plan-3-progress-review.md`

---

### FH-0003: JSONL event deserialization switch requires manual maintenance

Status: **Completed 2026-05-10**  
Area: `Omicron.Core/Sessions/SessionStore.cs` (`JsonlSessionStore.DeserializeEvent`)  
Priority: Medium.

Completion note:

- Created `OmicronEventRegistry` with static registration of all concrete event types.
- Replaced manual switch in `JsonlSessionStore.DeserializeEvent` with `OmicronEventRegistry.GetType(typeName)`.
- Added `EventRegistryTests` verifying all types are registered and round-trip correctly.

Source reviews:

- `docs/code-reviews/0030-plan-3-phase-1-completion-review.md`
- `docs/code-reviews/0031-plan-3-progress-review.md`
- `docs/code-reviews/0074-plan-3.6-comprehensive-review.md`

---

### FH-0004: Non-session services manually construct `EventEnvelope`

Status: **Completed 2026-05-08**  
Area: `Omicron.Core/Events/EventEnvelope.cs`, non-session event producers such as `ProviderStateManager`  
Priority: Low.

Completion note:

- Added `EventEnvelope.ForSession(SessionId)`.
- Updated `ProviderStateManager` to use the helper for provider-state events.

Source reviews:

- `docs/code-reviews/0030-plan-3-phase-1-completion-review.md`
- `docs/code-reviews/0031-plan-3-progress-review.md`

---

### FH-0005: Move VFS host integration test to a better test file

Status: **Completed 2026-05-08**  
Area: `Omicron.Core.Tests/WorkspaceVfsTests.cs`, workspace/VFS tests  
Priority: Low.

Completion note:

- Moved `OmicronHost_WorkspaceIsVfsBacked_AndReadsFile` to `WorkspaceVfsTests.cs`.
- Removed it from `PersistenceIntegrationTests.cs`.

Source reviews:

- `docs/code-reviews/0030-plan-3-phase-1-completion-review.md`
- `docs/code-reviews/0031-plan-3-progress-review.md`

---

## Workspace Transaction Hardening

### FH-0007: Workspace transaction commit is non-atomic

Status: **Completed 2026-05-10 (implementation); tests pending**  
Area: `Omicron.Core/Workspace/WorkspaceTransaction.cs`  
Priority: Medium.

Completion note:

- Added operation journaling: `CommittedOperation` and `CommittedAction` track each successful host mutation.
- On commit failure, rollback iterates the journal in reverse and attempts to undo each operation:
  - Write → delete
  - Move → move back
  - Delete → no-op (documented best-effort limitation)
- `_committed` reset to `false` on failure.
- `TransactionRolledBackEvent` emitted on rollback.
- ⚠️ No tests verify the commit failure / rollback path. Tracked as FH-0023 in active backlog.

Source reviews:

- `docs/code-reviews/0060-workspace-transactions-final-verification.md`
- `docs/code-reviews/0074-plan-3.6-comprehensive-review.md`

---

## Session Resume / Fork Hardening

### FH-0010: Session prefix matching should detect ambiguity

Status: **Completed 2026-05-10**  
Area: `Omicron.CLI/SlashCommandDispatcher.cs`  
Priority: Low.

Completion note:

- Both `/resume` and `/fork` now detect multiple prefix matches and print all candidates with index, short ID, model, and provider.
- Single match proceeds; zero matches prints clear error; index-based selection still takes precedence.

Source reviews:

- `docs/code-reviews/0062-session-resume-fork-ux-review.md`
- `docs/code-reviews/0074-plan-3.6-comprehensive-review.md`

---

## Event System Hardening

### FH-0015: Add `EmitAsync`/`EmitBatchAsync` to `IEventSink` to eliminate sync-over-async

Status: **Completed 2026-05-10**  
Area: `Omicron.Core/Events/IEventSink.cs`, `PersistentEventSink.cs`  
Priority: Medium.

Completion note:

- Added `EmitAsync` and `EmitBatchAsync` to `IEventSink` as default interface methods.
- `PersistentEventSink` overrides both async methods to call `_store.AppendEventsAsync` directly — no sync-over-async in the async path.
- `InMemoryEventSink` overrides both with trivial pass-throughs.
- Sync variants remain as convenience wrappers (documented hazard).

Source reviews:

- `docs/code-reviews/0065-plan-3-comprehensive-pre-plan-4-review.md`
- `docs/code-reviews/0074-plan-3.6-comprehensive-review.md`

---

## Session Config Hardening

### FH-0017: Make `AgentSession` config immutable after construction

Status: **Completed 2026-05-10**  
Area: `Omicron.Core/Sessions/AgentSession.cs`  
Priority: Medium.

Completion note:

- Created `SessionConfig` sealed record.
- All `AgentSession` config properties are now read-only, delegating to `_config`.
- Constructor and `FromProjection` accept `SessionConfig`.
- `OmicronHost.CreateSession`, `ResumeSessionAsync`, `ForkSessionAsync` build config at construction time.
- CLI and all tests updated.

Source reviews:

- `docs/code-reviews/0065-plan-3-comprehensive-pre-plan-4-review.md`
- `docs/code-reviews/0074-plan-3.6-comprehensive-review.md`

---

## Transaction Lifecycle Events

### FH-0018: Add transaction lifecycle events (`TransactionStartedEvent`, etc.)

Status: **Completed 2026-05-10 (implementation); tests pending**  
Area: `Omicron.Core/Workspace/WorkspaceTransaction.cs`, `Omicron.Core/Events/OmicronEvent.cs`  
Priority: High for Plan 4.

Completion note:

- Added 4 event types: `TransactionStartedEvent`, `TransactionStagedEvent`, `TransactionCommittedEvent`, `TransactionRolledBackEvent`.
- `WorkspaceTransaction` accepts optional `IEventSink`; emits events lazily on first stage, per stage operation, on commit, and on rollback/dispose/failure.
- `WorkspaceTransactionManager` and `OmicronHost` wired to pass the event sink through.
- ⚠️ All transaction events use `SessionId.Empty` — they are not scoped to a session and will not appear in `GetSessionEvents` queries. Documented design limitation.
- ⚠️ No tests verify transaction event emission. See review 0074.

Source reviews:

- `docs/code-reviews/0065-plan-3-comprehensive-pre-plan-4-review.md`
- `docs/code-reviews/0074-plan-3.6-comprehensive-review.md`

---

### FH-0019: Make `WorkspaceTransaction.CommitAsync` atomic or add rollback tracking

Status: **Completed 2026-05-10 (implementation); tests pending**  
Area: `Omicron.Core/Workspace/WorkspaceTransaction.cs`  
Priority: High for Plan 4.

Completion note:

- Added operation journaling: `CommittedOperation` and `CommittedAction` track each successful host mutation.
- On commit failure, rollback iterates the journal in reverse and attempts to undo each operation:
  - Write → delete
  - Move → move back
  - Delete → no-op (documented best-effort limitation)
- `_committed` reset to `false` on failure.
- `TransactionRolledBackEvent` emitted on rollback.
- ⚠️ No tests verify the commit failure / rollback path. Tracked as FH-0023 in active backlog.

Source reviews:

- `docs/code-reviews/0065-plan-3-comprehensive-pre-plan-4-review.md`
- `docs/code-reviews/0074-plan-3.6-comprehensive-review.md`

---

## CLI Hardening

### FH-0021: Add `LineEditor` tests

Status: **Completed 2026-05-10**  
Area: `Omicron.CLI/LineEditor.cs`  
Priority: Medium.

Completion note:

- Extracted `LineEditorEngine` (pure logic, no console I/O) and `LineEditorState` (mutable buffer/cursor state).
- `LineEditor.ReadLine()` is a thin wrapper over the engine.
- Added 19 tests covering typing, navigation, editing, submission, escape, tab completion, paste mode, and mid-buffer insertion.

Source reviews:

- `docs/code-reviews/0065-plan-3-comprehensive-pre-plan-4-review.md`
- `docs/code-reviews/0074-plan-3.6-comprehensive-review.md`

---

## JSONL Concurrency Hardening

### FH-0022: Add `JsonlSessionStore` concurrent access stress test

Status: **Completed 2026-05-10**  
Area: `Omicron.Core.Tests/JsonlConcurrencyTests.cs`  
Priority: Medium.

Completion note:

- Added `JsonlConcurrencyTests` with 10 tasks × 100 events appending concurrently to the same session.
- Verifies total count (1000) and that all events deserialize successfully.

Source reviews:

- `docs/code-reviews/0065-plan-3-comprehensive-pre-plan-4-review.md`
- `docs/code-reviews/0074-plan-3.6-comprehensive-review.md`
