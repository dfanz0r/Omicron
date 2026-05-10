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

## Review Cadence

Revisit this backlog:

- before implementing remoting or multi-frontend event streaming;
- before adding sub-agents/concurrent sessions;
- before replacing JSONL MVP persistence with a production backend;
- whenever a new `OmicronEvent` subtype is added.
