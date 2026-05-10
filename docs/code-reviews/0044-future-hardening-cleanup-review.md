# Code Review 0044: Future Hardening Cleanup Review

Date: 2026-05-08  
Scope: verify FH-0005, FH-0004, and FH-0002 cleanup work.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 262

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

The code changes for the three selected backlog items are complete:

- FH-0005: VFS host integration test moved to `WorkspaceVfsTests.cs`.
- FH-0004: `EventEnvelope.ForSession(SessionId)` helper added and `ProviderStateManager` now uses it.
- FH-0002: `PersistentEventSink` counters now use `Interlocked` updates and `Volatile.Read` getters.

One documentation cleanup remains: the backlog still lists FH-0002, FH-0004, and FH-0005 as open even though they are now completed.

## Verified Fixes

### FH-0005: VFS test location

Verified `OmicronHost_WorkspaceIsVfsBacked_AndReadsFile` is now in:

```text
Omicron.Core.Tests/WorkspaceVfsTests.cs
```

and no longer present in `PersistenceIntegrationTests.cs`.

### FH-0004: `EventEnvelope.ForSession`

Verified:

```csharp
public static EventEnvelope ForSession(SessionId sessionId) =>
    new(EventId.New(), 0, DateTimeOffset.UtcNow, sessionId);
```

`ProviderStateManager` uses it for provider-state update/clear events.

### FH-0002: atomic persistent sink counters

Verified:

```csharp
public long PersistedCount => Volatile.Read(ref _persistedCount);
private long _persistedCount;

public long FailureCount => Volatile.Read(ref _failureCount);
private long _failureCount;
```

and updates use `Interlocked.Increment` / `Interlocked.Add`.

## Remaining Documentation Finding

### Backlog status is stale

File:

```text
docs/implementation-plans/FUTURE-HARDENING-BACKLOG.md
```

The backlog still shows these items as `Status: Open`:

- FH-0002
- FH-0004
- FH-0005

Recommendation:

Update each to `Status: Completed` and add a short completion note/date. This will keep the backlog useful before Phase 4.

## Recommendation

After marking FH-0002/FH-0004/FH-0005 completed in the backlog, proceed to Plan 3 Phase 4: workspace transactions and diff v1.
