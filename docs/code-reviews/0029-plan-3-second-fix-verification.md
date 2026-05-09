# Code Review 0029: Plan 3 Second Fix Verification

Date: 2026-05-08  
Scope: verify latest Plan 3 Phase 1 persistence fixes after review 0028.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 209

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

The second repair pass is a substantial improvement:

- 7 persistence integration tests were added.
- `OmicronHost.Events` is now a host-level `PersistentEventSink` over `EventLog`.
- `ProviderStateManager` and `LocalExecutionBroker` are now wired to the persistent host sink.
- `AgentSession` no longer creates the session record itself.
- `OmicronHost.CreateSession()` creates the session record before returning the session.
- `PersistentEventSink` now exposes `OnError`, `PersistedCount`, and `FailureCount`.
- `SessionEndedEvent` was added to JSONL deserialization.
- In-memory duplicate session IDs are rejected.

The main audit/durability architecture is now much closer to Plan 3 Phase 1. There are still a few important issues before I would call Phase 1 fully done.

## Remaining High-Priority Findings

### 1. Replacing `OmicronHost.SessionStore` after construction breaks persistence

File:

- `Omicron.Core/OmicronHost.cs`

`OmicronHost` constructs `Events` once using the initial in-memory store:

```csharp
SessionStore = new InMemorySessionStore();
Events = new PersistentEventSink(EventLog, SessionStore);
```

But `SessionStore` remains publicly settable:

```csharp
public ISessionStore SessionStore { get; set; }
```

If a caller does what the comments imply is supported:

```csharp
var host = new OmicronHost(root);
host.SessionStore = new JsonlSessionStore(path);
var session = host.CreateSession(model);
```

then `CreateSession()` creates the record in the new `JsonlSessionStore`, but `Events` still persists to the old `InMemorySessionStore` captured by `PersistentEventSink`. The old store does not know the new session ID, so persistence failures are counted and events are not written to the intended store.

Impact:

- file-backed store replacement is broken;
- CLI/future host configuration cannot safely switch stores after construction;
- integration tests only cover the default store, so this is not caught.

Recommendation:

Prefer constructor injection:

```csharp
public OmicronHost(string workspaceRoot, ISessionStore? sessionStore = null)
```

Then make `SessionStore` get-only. Build `Events`, `Execution`, and `ProviderStateManager` from that final store. Alternatively add a dedicated method that rewires all dependent services, but constructor injection is simpler and safer.

Add test:

- construct host with `JsonlSessionStore` or a supplied `InMemorySessionStore`;
- run prompt;
- assert events are persisted to the supplied store.

---

### 2. The stray `%TEMP%sessionstore_bak.txt` file still exists

`git status --short` still shows:

```text
?? %TEMP%sessionstore_bak.txt
```

The previous report said this was removed, but it remains untracked. It should be deleted unless intentionally added.

---

### 3. `JsonlSessionStore` still lacks direct tests

Files:

- `Omicron.Core/Sessions/SessionStore.cs`
- `Omicron.Core.Tests/SessionStoreTests.cs`
- `Omicron.Core.Tests/PersistenceIntegrationTests.cs`

The integration tests use `InMemorySessionStore`. `SessionStoreTests.cs` also only covers `InMemorySessionStore`.

Important JSONL behavior remains untested:

- create/list/get session;
- append/read events;
- dispose/reopen and list session;
- dispose/reopen and read events;
- `GetEventCountAsync()` after reopen;
- `LastActivityAt` persistence;
- `SessionEndedEvent` round-trip;
- duplicate session ID rejection once implemented for JSONL.

Recommendation:

Add a `JsonlSessionStoreTests` class using a temp directory.

## Medium-Priority Findings

### 4. `JsonlSessionStore.CreateSessionAsync()` does not reject duplicate session IDs

File:

- `Omicron.Core/Sessions/SessionStore.cs`

`InMemorySessionStore` now rejects duplicates, but `JsonlSessionStore` does not:

```csharp
_sessions.Add(record);
```

This can create duplicate records in `sessions.json` for the same session ID.

Recommendation:

Apply the same duplicate-session guard to `JsonlSessionStore`.

---

### 5. `JsonlSessionStore.AppendEventsAsync()` saves `LastActivityAt` before the event write succeeds

File:

- `Omicron.Core/Sessions/SessionStore.cs`

Current flow:

1. update `LastActivityAt` in memory;
2. `SaveIndex()`;
3. append events to JSONL file.

If the file append fails, metadata claims activity occurred even though the event did not persist.

Recommendation:

Write events first, then update `LastActivityAt` and save the index. If strict atomicity is deferred, document the compromise.

---

### 6. `AgentSession` still contains unused store plumbing

File:

- `Omicron.Core/Sessions/AgentSession.cs`

`AgentSession` still has:

```csharp
private readonly ISessionStore _sessionStore;
...
_sessionStore = new InMemorySessionStore(); // unused; kept for future store-based features
```

This field is unused and reintroduces ambiguity after intentionally removing store ownership from `AgentSession`.

Also `SetEventSink(...)` remains with a stale comment saying it is used by `OmicronHost`, but `OmicronHost` no longer calls it.

Recommendation:

Remove `_sessionStore` and remove or repurpose `SetEventSink(...)` unless there is a concrete caller. Persistence should stay sink-owned.

---

### 7. `PersistentEventSink` counters are not thread-safe

File:

- `Omicron.Core/Events/PersistentEventSink.cs`

`PersistedCount++` and `FailureCount++` are not atomic. If concurrent producers emit events, counters can be inaccurate.

Recommendation:

Use `Interlocked.Increment` / `Interlocked.Add` and expose reads via `Volatile.Read` or simple properties backed by `long` fields.

This is lower risk because counters are diagnostic, not correctness-critical.

## Status Against Review 0028 Findings

| Finding | Status |
| --- | --- |
| Integration tests missing | Fixed for default in-memory host path. JSONL path still untested. |
| Provider-state/execution events not persisted | Fixed for default host wiring. |
| PersistentEventSink swallows failures | Improved with callback/counters. |
| Jsonl LastActivityAt not saved | Improved, but save happens before event write succeeds. |
| JSONL missing SessionEndedEvent | Fixed in deserialization switch. |
| Direct constructor persistence footgun | Mostly fixed; remove unused `_sessionStore` to avoid ambiguity. |
| Duplicate session creation | Fixed for in-memory only; JSONL still needs guard. |
| Stray `%TEMP%` file | Not fixed; still present. |

## Recommendation

Do one small cleanup pass before moving to Phase 2 replay projection:

1. Make `SessionStore` constructor-injected/get-only on `OmicronHost`, or otherwise rewire `PersistentEventSink` when the store changes.
2. Delete `%TEMP%sessionstore_bak.txt`.
3. Add `JsonlSessionStore` reopen/round-trip tests.
4. Add duplicate-ID guard to `JsonlSessionStore`.
5. Remove unused `_sessionStore` and stale `SetEventSink` from `AgentSession`.

After that, Plan 3 Phase 1 will be in good shape to support session replay projection.
