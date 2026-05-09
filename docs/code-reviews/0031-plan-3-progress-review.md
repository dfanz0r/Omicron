# Code Review 0031: Plan 3 Progress Review

Date: 2026-05-08  
Scope: review work completed so far toward `docs/implementation-plans/0003-core-persistence-workspace-foundations.md`, after Phase 1 completion.

## Validation

Latest verified locally:

```text
dotnet test Omicron.slnx --nologo
Passed: 219

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Executive Summary

Plan 3 Phase 1 is complete for the MVP baseline.

The implementation now has a coherent durable event/session foundation:

- `ISessionStore` session catalog/event-log abstraction.
- `InMemorySessionStore` and `JsonlSessionStore` implementations.
- host-level `PersistentEventSink` that persists stamped events.
- `OmicronHost` constructor injection for the session store.
- all event producers wired through the same authoritative persistent sink.
- integration coverage for session creation, prompt lifecycle persistence, provider-state events, execution events, JSONL reopen, JSONL round trips, event counts, duplicates, and host+JSONL integration.

This is enough to proceed to Plan 3 Phase 2: session replay projection.

## What Is Strong

### 1. Persistence ownership is now correctly centralized

`AgentSession` no longer owns persistence directly. It emits through `IEventSink`, and the host decides whether that sink is durable.

This matches the earlier architectural decision:

- async stream = live UI subset;
- `IEventSink` = authoritative audit/durability stream.

### 2. Host wiring is now correct

`OmicronHost` now takes an optional `ISessionStore` at construction and exposes it as get-only. The selected store is captured by the host-level `PersistentEventSink`, and nested producers use that sink:

- `AgentSession`
- `ProviderStateManager`
- `LocalExecutionBroker`

This fixes the prior split-brain issue where session records could be written to one store while events were persisted to another.

### 3. JSONL implementation is MVP-usable

The JSONL store now supports:

- session metadata index;
- per-session `.jsonl` event streams;
- reopen/list/read behavior;
- event count after reopen;
- duplicate session ID rejection;
- polymorphic event round trips for important current event types.

This is sufficient as an MVP durable backend.

### 4. Tests cover the important runtime path

The added tests now cover the actual intended usage path instead of only isolated store calls:

- host creates a session record;
- `PromptAsync()` events persist;
- provider-state and execution events persist;
- JSONL can be injected into the host and reopened.

This materially reduces regression risk before replay projection work.

## Documentation Status

### RFC baseline is now current

File: `docs/rfcs/IMPLEMENTATION-BASELINE.md`

The stale baseline items called out by this review have been fixed directly. The baseline now includes:

- `PersistentEventSink` and constructor-injected `ISessionStore` in `OmicronHost` composition;
- the durable event/session-store delivery path;
- Plan 3 Phase 1 persistence details (`SessionRecord`, `InMemorySessionStore`, `JsonlSessionStore`);
- provider compatibility/storage-policy and OpenRouter Responses stateless-default notes;
- the canonical conversation seed;
- the current 219-test count;
- a narrowed “Not Yet Implemented” persistence gap: replay projection, snapshots/checkpoints, and resume.

### Remaining doc cleanup: Plan 3 status

File: `docs/implementation-plans/0003-core-persistence-workspace-foundations.md`

The Plan 3 document still says `Status: Draft`.

Recommended update:

- mark Phase 1 complete;
- leave Phase 2+ pending;
- adjust “Important gaps” so persistent event log/session catalog are no longer listed as completely missing.

## Remaining Non-Blocking Issues

These do not block Phase 2 and are now tracked in `docs/implementation-plans/FUTURE-HARDENING-BACKLOG.md`.

### 1. JSONL concurrency is MVP-level

Tracking item: FH-0001.

`JsonlSessionStore` is fine for local MVP durability, but it is not a robust concurrent writer backend. Concurrent event appends could contend/fail depending on file sharing and timing.

This is acceptable for now because:

- persistence is synchronous through `PersistentEventSink` on the emitting call path;
- current CLI usage is mostly single-session/single-threaded;
- failures are observable through `OnError`/`FailureCount`.

Future durable backends should centralize append locking/queuing and provide stronger append guarantees.

### 2. `PersistentEventSink` diagnostics are not concurrency-hardened

Tracking item: FH-0002.

`PersistedCount` / `FailureCount` are useful diagnostics, but increments are not atomic. This is not event-log correctness-critical, but can be made robust later with `Interlocked`.

### 3. Manual event-type JSONL switch requires maintenance

Tracking item: FH-0003.

Each new `OmicronEvent` subtype must be added to the JSONL deserialization switch and tests. That is acceptable for now, but Phase 2/3 should keep this visible.

## Phase 2 Readiness

The codebase is ready for session replay projection.

Recommended Phase 2 implementation shape:

```csharp
public sealed class SessionProjection
{
    public SessionId SessionId { get; init; }
    public IReadOnlyList<Message> Messages { get; init; }
    public IReadOnlyDictionary<ProviderStateKey, ProviderTurnState> ProviderStates { get; init; }
    public bool IsReset { get; init; }
    public IReadOnlyList<SessionErrorEvent> Errors { get; init; }
}

public interface ISessionProjector
{
    SessionProjection Project(IEnumerable<OmicronEvent> events);
}
```

Initial projection should handle:

- `SessionStartedEvent`
- `UserMessageEvent`
- `AssistantResponseCompleteEvent`
- `ToolInvocationStartedEvent`
- `ToolInvocationCompletedEvent`
- `ProviderStateUpdatedEvent`
- `ProviderStateClearedEvent`
- `SessionResetEvent`
- `SessionErrorEvent`

Tests should include:

- simple prompt replay;
- tool-call transcript replay;
- provider-state update/clear replay;
- reset clears projected messages/provider states;
- projection from JSONL-reopened events.

## Recommendation

Proceed to Plan 3 Phase 2.

Before or alongside Phase 2, update the Plan 3 document status so it reflects completed Phase 1 and pending Phase 2+ work. The RFC implementation baseline has already been updated.
