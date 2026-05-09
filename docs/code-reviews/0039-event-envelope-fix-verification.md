# Code Review 0039: Event Envelope Fix Verification

Date: 2026-05-08  
Scope: verify fixes after `docs/code-reviews/0038-event-envelope-refactor-review.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 235

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

Only part of the requested cleanup appears complete.

Completed:

- temporary `_fix*.ps1` / `fix_tests.py` files are gone from `git status`.
- `IEventSink` XML comments now correctly refer to `Envelope.Sequence = 0`.

Still open:

- `SessionEventWriter` still has stale XML comments and no envelope helper.
- event construction call sites still repeat `new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, Id)`.
- no `TokenUsage.From(...)` helper or equivalent shared conversion was added.

The code still builds/tests, so these are cleanup issues rather than functional blockers.

## Findings

### 1. `SessionEventWriter` comments still reference the old shape

File: `Omicron.Core/Events/SessionEventWriter.cs`

Current comments still say:

```csharp
Consumers still supply EventId.New() and DateTimeOffset.UtcNow
...
producers pass sequence=0
```

This should be updated to refer to `EventEnvelope` / `Envelope.Sequence = 0`.

### 2. No `SessionEventWriter.Envelope()` helper was added

Files:

- `Omicron.Core/Events/SessionEventWriter.cs`
- `Omicron.Core/Sessions/AgentSession.cs`
- `Omicron.Core/Sessions/ProviderStateManager.cs`

Call sites still repeat:

```csharp
new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, Id)
```

Recommended helper:

```csharp
public EventEnvelope Envelope() =>
    new(EventId.New(), 0, DateTimeOffset.UtcNow, SessionId);
```

For non-session-writer producers like `ProviderStateManager`, add a small static helper if desired:

```csharp
public static EventEnvelope ForSession(SessionId sessionId) =>
    new(EventId.New(), 0, DateTimeOffset.UtcNow, sessionId);
```

### 3. No `TokenUsage` conversion helper was added

File: `Omicron.Core/Events/EventEnvelope.cs`

`TokenUsage` remains a simple record. `AgentSession` still manually does:

```csharp
new TokenUsage(usage?.InputTokens ?? 0, usage?.OutputTokens ?? 0)
```

Optional but still recommended:

```csharp
public static TokenUsage From(UsageInfo? usage) =>
    new(usage?.InputTokens ?? 0, usage?.OutputTokens ?? 0);
```

or a private helper in `AgentSession` if avoiding a dependency from events to models.

## Recommendation

Do a tiny follow-up cleanup pass:

1. update `SessionEventWriter` XML comments;
2. add and use `SessionEventWriter.Envelope()` at least in `AgentSession`;
3. optionally add `TokenUsage` conversion helper.

No functional blocker found.
