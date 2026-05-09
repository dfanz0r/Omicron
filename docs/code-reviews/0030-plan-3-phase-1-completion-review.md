# Code Review 0030: Plan 3 Phase 1 Completion Review

Date: 2026-05-08  
Scope: re-check latest Plan 3 Phase 1 persistence/session-store fixes after review 0029.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 219

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

`git status --short` no longer shows the previous `%TEMP%sessionstore_bak.txt` file.

## Result

The previous review is now stale. The six requested cleanup items from review 0029 are resolved, and Plan 3 Phase 1 can be considered complete for the MVP baseline.

## Verified Fixes

### 1. `OmicronHost.SessionStore` is constructor-injected and get-only

File: `Omicron.Core/OmicronHost.cs`

Verified:

```csharp
public ISessionStore SessionStore { get; }

public OmicronHost(string workspaceRoot, ISessionStore? sessionStore = null)
{
    EventLog = new InMemoryEventSink();
    SessionStore = sessionStore ?? new InMemorySessionStore();
    Events = new PersistentEventSink(EventLog, SessionStore);
    ...
}
```

This fixes the broken store replacement issue. `Events`, `Execution`, and `ProviderStateManager` are now wired from the final store chosen at construction.

### 2. Stray temp file removed

The old untracked temp file no longer appears in `git status --short`.

### 3. JSONL store tests added

File: `Omicron.Core.Tests/JsonlSessionStoreTests.cs`

Verified tests cover:

- create/get session;
- list sessions;
- append/read events;
- reopen/list sessions;
- reopen/read events;
- event count after reopen;
- `SessionEndedEvent` round trip;
- duplicate session ID rejection;
- provider-state event round trip;
- `OmicronHost` with injected `JsonlSessionStore` persists events.

### 4. JSONL duplicate session IDs are rejected

File: `Omicron.Core/Sessions/SessionStore.cs`

Verified duplicate guards exist in both in-memory and JSONL stores:

```text
Session {sessionId} already exists.
```

### 5. JSONL event write now happens before metadata update

File: `Omicron.Core/Sessions/SessionStore.cs`

Verified `JsonlSessionStore.AppendEventsAsync(...)` now:

1. verifies session existence;
2. writes JSONL events;
3. updates `LastActivityAt`;
4. calls `SaveIndex()`.

This is the right ordering for the MVP.

### 6. Unused `AgentSession` store plumbing removed

File: `Omicron.Core/Sessions/AgentSession.cs`

Verified no `_sessionStore` field and no `SetEventSink(...)` remain. Persistence ownership is now clearly sink/host-owned.

## Phase 1 Status

Plan 3 Phase 1 acceptance criteria are met for MVP:

- session start creates a `SessionRecord` via `OmicronHost.CreateSession()`;
- events can be appended/read in sequence order;
- `AgentSession` runs against persistent host sink without provider/tool changes;
- provider-state and execution events go through the authoritative host sink;
- in-memory and JSONL stores have tests;
- JSONL supports process-reopen recovery for records/events.

## Non-Blocking Future Hardening

Not blockers for Phase 1, but worth tracking later:

- `PersistentEventSink` counters are diagnostic and still not fully concurrency-hardened.
- concurrent JSONL appends are not explicitly serialized by session; future durable backends should centralize write ordering/locking.
- JSONL polymorphic event serialization is manual; future event types must update the switch and tests.

## Recommendation

Mark Plan 3 Phase 1 complete and proceed to Plan 3 Phase 2: session replay projection.
