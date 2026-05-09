# Code Review 0028: Plan 3 Fix Verification

Date: 2026-05-08  
Scope: verify fixes after `docs/code-reviews/0027-plan-3-first-review.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 202

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

Most of the explicit findings from review 0027 were addressed in code:

- `SessionCreateRequest` can now carry the caller-owned `SessionId`.
- `AgentSession.InitializeSessionRecordAsync()` passes `SessionId: Id`.
- `AppendEventsAsync` now throws when appending to an unknown session instead of silently dropping events.
- `UpdateSessionAsync` now uses `Func<SessionRecord, SessionRecord>` and tests assert the updated fields.
- `JsonlSessionStore` no longer wraps `InMemorySessionStore` and now restores session records from `sessions.json`.
- fire-and-forget persistence was removed from `AgentSession.Emit()`.
- `PersistentEventSink` was added and `OmicronHost.CreateSession()` wraps the session's sink with it.

This is a meaningful improvement. However, Plan 3 Phase 1 should still not be marked complete yet. There are remaining correctness gaps and missing integration tests.

## Remaining High-Priority Findings

### 1. Runtime integration tests are still missing

The review explicitly requested `AgentSession` + `ISessionStore` tests. The test count stayed at 202 and the new tests still only cover direct store usage.

Missing tests should verify at least:

- `OmicronHost.CreateSession()` creates a `SessionRecord` using `session.Id`.
- `PromptAsync()` persists emitted events under `session.Id`.
- persisted event order matches stamped sequence order.
- `Reset()` persists `SessionResetEvent`.
- JSONL store can be disposed/reopened and still list/read the session/events.

Without these, the most important Plan 3 path remains unprotected.

---

### 2. Provider-state and execution events are still not persisted by the session store

Files:

- `Omicron.Core/OmicronHost.cs`
- `Omicron.Core/Sessions/AgentSession.cs`
- `Omicron.Core/Events/PersistentEventSink.cs`

`OmicronHost.CreateSession()` replaces only the `AgentSession` writer sink with a session-scoped `PersistentEventSink`:

```csharp
var persistentSink = new PersistentEventSink(Events, SessionStore, session.Id);
session.SetEventSink(persistentSink);
```

But services that emit nested/audit events still use the original `EventLog`:

```csharp
Execution = new LocalExecutionBroker(EventLog);
ProviderStateManager = new ProviderStateManager(ProviderState, EventLog);
```

Impact:

- `ProviderStateUpdatedEvent` / `ProviderStateClearedEvent` are not persisted to `ISessionStore`.
- `ExecutionStartedEvent` / `ExecutionCompletedEvent` are not persisted to `ISessionStore`.
- Phase 2 replay projection cannot reconstruct provider state from the persisted event log, even though Plan 3 specifically calls for provider-state replay.
- The documented event model says `IEventSink` is the authoritative audit/durability stream, but the store currently receives only events emitted through the `AgentSession` writer.

Recommendation:

Either:

1. make the host-level authoritative sink persistent and session-aware, so all producers use the same durable sink; or
2. explicitly document that Phase 1 only persists top-level session events, then add a separate task to route provider-state/execution events through persistence before Phase 2.

Given the RFC direction, option 1 is preferred.

---

### 3. `PersistentEventSink` swallows persistence failures, including unknown-session failures

File:

- `Omicron.Core/Events/PersistentEventSink.cs`

The store now throws on unknown sessions, but `PersistentEventSink` catches and drops all exceptions:

```csharp
catch
{
    // Persistence failure is non-fatal for runtime
}
```

This preserves runtime availability, but it also hides exactly the class of persistence bugs the previous review identified. At minimum, failures should be observable in tests/debug logs or captured in a health/error property.

Recommendation:

For MVP:

- expose an optional `Action<Exception, OmicronEvent>` error callback; or
- emit a non-persisted diagnostic event to the inner sink; or
- in test mode, allow exceptions to propagate.

---

### 4. `JsonlSessionStore.AppendEventsAsync()` updates `LastActivityAt` in memory but does not save the index

File:

- `Omicron.Core/Sessions/SessionStore.cs`

`AppendEventsAsync()` updates the session record:

```csharp
_sessions[index] = _sessions[index] with { LastActivityAt = DateTimeOffset.UtcNow };
```

But it does not call `SaveIndex()` afterward. The updated `LastActivityAt` is lost after restart unless another metadata mutation or dispose happens later.

Recommendation:

Call `SaveIndex()` after updating `LastActivityAt`, ideally after the event append succeeds. If avoiding per-event index writes for performance, document that tradeoff and add an explicit flush/checkpoint mechanism.

---

### 5. JSONL serializer does not handle all current event types

File:

- `Omicron.Core/Sessions/SessionStore.cs`

`DeserializeEvent(...)` has no case for `SessionEndedEvent`, even though it is a concrete `OmicronEvent` type.

Impact:

- if `SessionEndedEvent` is persisted, JSONL replay silently drops it.

Recommendation:

Add `SessionEndedEvent` to the switch and add a round-trip test that enumerates every concrete `OmicronEvent` type.

## Medium-Priority Findings

### 6. `AgentSession` direct constructor is now a persistence footgun

File:

- `Omicron.Core/Sessions/AgentSession.cs`

`AgentSession` accepts `ISessionStore`, and initializes a session record in that store, but event persistence only happens if the event sink has also been wrapped by `PersistentEventSink`.

Direct callers can reasonably assume that passing a store means events are persisted. They are not.

Recommendation:

Either:

- remove `sessionStore` from the public constructor and require a persistent sink to be passed; or
- make the constructor wrap the sink itself when a store is provided; or
- clearly document that persistence is host-wired and direct construction is test-only/advanced.

---

### 7. Duplicate session creation is not guarded

File:

- `Omicron.Core/Sessions/SessionStore.cs`

If `CreateSessionAsync()` is called twice with the same provided `SessionId`, `InMemorySessionStore` adds duplicate records and resets the event list:

```csharp
_sessions.Add(record);
_events[sessionId] = new List<OmicronEvent>();
```

Recommendation:

Reject duplicate session IDs or make create idempotent. Rejecting duplicates is simpler and safer.

---

### 8. Stray untracked file exists

```text
?? %TEMP%sessionstore_bak.txt
```

This looks accidental and should be removed unless it is intentionally part of the repo.

## Status Against Review 0027 Findings

| Finding | Status |
| --- | --- |
| Session ID mismatch | Mostly fixed; still needs integration test. |
| Fire-and-forget persistence | Fixed for `AgentSession`-emitted events. Nested service events still bypass persistence. |
| `UpdateSessionAsync` contract | Fixed. |
| `JsonlSessionStore.LoadIndex()` no-op | Mostly fixed; needs reopen tests and `LastActivityAt` persistence fix. |
| Missing integration tests | Not fixed yet. |

## Recommendation

Before proceeding to Plan 3 Phase 2 replay projection:

1. Add `AgentSession`/`OmicronHost`/`ISessionStore` integration tests.
2. Decide whether the host-level `Events` sink should become persistent so provider-state and execution events are durable too.
3. Fix JSONL `LastActivityAt` save behavior.
4. Add `SessionEndedEvent` JSONL deserialization.
5. Remove `%TEMP%sessionstore_bak.txt`.

After those are done, Plan 3 Phase 1 will be close enough to start replay projection work.
