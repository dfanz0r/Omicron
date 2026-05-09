# Code Review 0027: Plan 3 First Review

Date: 2026-05-08  
Scope: review initial work toward `docs/implementation-plans/0003-core-persistence-workspace-foundations.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 202

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

Initial Plan 3 work added useful persistence scaffolding:

- `ISessionStore`
- `SessionRecord`
- `SessionCreateRequest`
- `SessionListQuery`
- `EventSequenceRange`
- `InMemorySessionStore`
- `JsonlSessionStore` sketch
- `OmicronHost.SessionStore`
- `AgentSession` receives a store and attempts to persist emitted events
- session-store unit tests

This is a good start, but the integration currently has critical correctness issues. The largest problem is that `AgentSession` and `ISessionStore` disagree about who owns `SessionId`, so emitted events are not actually persisted under the created session record in the default in-memory path.

Plan 3 Phase 1 should not be considered complete yet.

## High-Priority Findings

### 1. `AgentSession` creates events under one `SessionId`, while `SessionStore` creates a different `SessionId`

Files:

- `Omicron.Core/Sessions/AgentSession.cs`
- `Omicron.Core/Sessions/SessionStore.cs`

`AgentSession` owns its ID:

```csharp
Id = SessionId.New();
```

But `InMemorySessionStore.CreateSessionAsync(...)` creates another ID internally:

```csharp
var sessionId = SessionId.New();
```

Then `AgentSession.Emit(...)` appends events under `AgentSession.Id`:

```csharp
await _sessionStore.AppendEventsAsync(Id, [stamped]);
```

For `InMemorySessionStore`, `_events` only contains the store-created session ID. Appending with the agent session ID falls through silently:

```csharp
if (_events.TryGetValue(sessionId, out var list))
{
    list.AddRange(events);
}
```

Impact:

- session record has ID A;
- runtime events have ID B;
- in-memory store appends no events;
- persisted session catalog cannot replay the session;
- tests do not catch this because there is no AgentSession → SessionStore integration test.

Recommendation:

Make session ID explicit in the create request or let the store create the ID and pass it into `AgentSession`, but do not have both create different IDs.

Minimal fix:

```csharp
public sealed record SessionCreateRequest(
    SessionId? SessionId,
    string ModelId,
    string ProviderName,
    ApiType ApiType,
    ...);
```

Then `AgentSession.InitializeSessionRecordAsync()` passes `Id`, and the store uses the provided ID.

Also make `AppendEventsAsync` fail loudly if the session does not exist, or create the stream intentionally. Silent no-op hides data loss.

Acceptance test to add:

- create `AgentSession` with `InMemorySessionStore`;
- run one prompt;
- read events from `session.Id`;
- assert `SessionStartedEvent`, `UserMessageEvent`, and `AssistantResponseCompleteEvent` exist.

---

### 2. Event persistence is fire-and-forget and can lose/reorder events

File: `Omicron.Core/Sessions/AgentSession.cs`

`Emit(...)` persists with:

```csharp
Task.Run(async () => await _sessionStore.AppendEventsAsync(Id, [stamped]));
```

Problems:

- no ordering guarantee across multiple `Task.Run` calls;
- process/session can exit before writes finish;
- exceptions are swallowed;
- tests cannot deterministically assert persistence;
- JSONL appends from concurrent tasks can race.

The event sink already stamps sequence synchronously. Persistence should preserve that order.

Recommendation:

Do not write to the store directly from `AgentSession.Emit()` with fire-and-forget tasks.

Better options:

1. Implement a `PersistentEventSink : IEventSink` that wraps an inner sink and synchronously/serially appends stamped events to `ISessionStore`.
2. Add a session-scoped event sink/writer that queues persistence in order and can be flushed/disposed.
3. For MVP, persist synchronously after `_writer.Emit(evt)` in `AgentSession` using a blocking call only if acceptable, but prefer #1.

Because `IEventSink.Emit(...)` is synchronous today, a persistent sink may need either sync store methods or a safe serialized background queue with flush semantics.

---

### 3. `UpdateSessionAsync(Action<SessionRecord>)` cannot update immutable records

Files:

- `Omicron.Core/Sessions/SessionStore.cs`
- `Omicron.Core.Tests/SessionStoreTests.cs`

The API is:

```csharp
ValueTask UpdateSessionAsync(SessionId sessionId, Action<SessionRecord> update, ...)
```

But `SessionRecord` is immutable. The test demonstrates the issue:

```csharp
update(record);
_sessions[index] = record;
```

The lambda cannot return the modified record:

```csharp
r = r with { Status = SessionStatus.Archived };
```

This only reassigns the local lambda parameter. The store writes back the unchanged record. The test explicitly does not assert the updated values.

Recommendation:

Change the contract to return the updated record:

```csharp
ValueTask UpdateSessionAsync(
    SessionId sessionId,
    Func<SessionRecord, SessionRecord> update,
    CancellationToken ct = default);
```

Then test that `Status` and `Label` actually changed.

---

### 4. `JsonlSessionStore.LoadIndex()` is a no-op

File: `Omicron.Core/Sessions/SessionStore.cs`

`LoadIndex()` deserializes `SessionRecord`s but does not populate `_memory`:

```csharp
foreach (var record in records)
{
    lock (_lock)
    {
        // We'll just store them in a separate list for lookup
    }
}
```

There is no separate list. Reopening a `JsonlSessionStore` will not restore the session catalog. Also, existing event files are not loaded into memory for `GetEventCountAsync(...)`, which currently delegates to `_memory`.

Impact:

- file-backed store is not actually durable for session listing/get;
- `GetEventCountAsync` after restart returns 0;
- only direct `ReadEventsAsync` from a known session ID can work, but there may be no way to discover that ID from the store.

Recommendation:

Either:

- remove `JsonlSessionStore` until it works; or
- implement proper load behavior.

A simple approach:

- add internal `AddSessionRecord(SessionRecord record)` to `InMemorySessionStore`; or
- make `JsonlSessionStore` own its own session dictionary instead of wrapping `InMemorySessionStore`.

Add tests:

- create JSONL store in temp dir;
- create session and append events;
- dispose/reopen;
- list sessions includes record;
- read events returns original events;
- event count matches.

---

### 5. Session store tests cover only isolated store behavior, not runtime integration

File:

- `Omicron.Core.Tests/SessionStoreTests.cs`

The tests validate `InMemorySessionStore` when caller manually uses the returned store-created session ID. They do not validate how `AgentSession` uses the store.

Recommendation:

Add tests for:

- `AgentSession` creates a session record with `session.Id`;
- session events are persisted under `session.Id`;
- event order is preserved;
- reset event is persisted;
- provider-state events are persisted or explicitly documented as sink-only if not routed through the persistent sink.

## Medium-Priority Findings

### 6. `SessionRecord.LastActivityAt` is never updated on event append

File: `Omicron.Core/Sessions/SessionStore.cs`

`SessionRecord.Create(...)` sets `LastActivityAt`, but appending events does not update it.

Recommendation:

Update `LastActivityAt` during `AppendEventsAsync` or through the persistent event sink.

---

### 7. JSONL event serialization is manual and incomplete-risky

File: `Omicron.Core/Sessions/SessionStore.cs`

`DeserializeEvent(...)` has a manual switch over current event types. This is acceptable as an MVP, but each new event type requires updating this switch.

Recommendation:

For now, add a test ensuring every known concrete `OmicronEvent` type can round-trip through JSONL. Later consider a generated registry or explicit event-type registry.

---

### 8. `ISessionStore` location may be temporary, but namespace should be intentional

File:

- `Omicron.Core/Sessions/SessionStore.cs`

Keeping store interfaces in `Omicron.Core.Sessions` is acceptable for the MVP. Longer term, RFC 0006 expects a persistence/workspace package boundary.

Recommendation:

Document that this is a temporary core-local abstraction to be split to `Omicron.Persistence` later.

## Plan 3 Alignment

### Phase 1 partially started

Implemented:

- session records;
- in-memory store;
- JSONL store sketch;
- host exposes `SessionStore`;
- agent session attempts to persist events.

Not yet acceptable:

- runtime events are not reliably persisted;
- session ID ownership is broken;
- JSONL durability is incomplete;
- no replay projection yet.

### Phase 2+ not started

No session projection, VFS, transactions, semantic content, or edit harness work yet. That is fine; Phase 1 should be stabilized first.

## Recommendation

Do a focused Plan 3 Phase 1 repair pass before adding more persistence/workspace features:

1. Fix session ID ownership.
2. Replace fire-and-forget persistence with ordered persistent event sink or flushable session writer.
3. Fix `UpdateSessionAsync` contract.
4. Either finish or remove/defer `JsonlSessionStore`.
5. Add AgentSession/session-store integration tests.

Once Phase 1 is reliable, proceed to Phase 2 session replay projection.
