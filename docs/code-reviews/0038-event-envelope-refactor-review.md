# Code Review 0038: Event Envelope Refactor Review

Date: 2026-05-08  
Scope: review event metadata/payload refactor introducing `EventEnvelope`, `ProviderStateSnapshot`, and `TokenUsage`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 235

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

The refactor is directionally correct and improves the event model substantially.

Positive changes:

- shared event metadata is now bundled in `EventEnvelope`;
- `OmicronEvent` exposes convenience properties (`Id`, `Sequence`, `Timestamp`, `SessionId`) while storing one metadata object;
- `ProviderStateUpdatedEvent` no longer has a long positional tail and uses `ProviderStateSnapshot`;
- `AssistantResponseCompleteEvent` uses `TokenUsage`;
- event sink sequence stamping cleanly updates `evt.Envelope.Sequence`;
- tests and build pass.

This makes the event surface easier to evolve before adding workspace/VFS events.

## Findings

### 1. Stray temporary refactor scripts are untracked

`git status --short` shows these untracked files:

```text
?? _fix2.ps1
?? _fix_all.ps1
?? _fix_bulk.ps1
?? _fix_events.ps1
?? _fix_final.ps1
?? _fix_simple.ps1
?? _fix_tests.ps1
?? fix_tests.py
```

These look like implementation scratch scripts and should be deleted unless intentionally added to the repo.

### 2. Event creation is still verbose at call sites

The envelope refactor fixed the long event constructor tails, but call sites now repeat:

```csharp
new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, Id)
```

This is still a lot of boilerplate, especially in `AgentSession`.

Recommendation:

Add a helper on `SessionEventWriter`, for example:

```csharp
public EventEnvelope Envelope() =>
    new(EventId.New(), 0, DateTimeOffset.UtcNow, SessionId);
```

Then event creation becomes:

```csharp
new UserMessageEvent(_writer.Envelope(), text)
```

This keeps event metadata creation consistent and avoids future copy/paste errors.

### 3. XML comments still reference the old sequence-passing shape

Files:

- `Omicron.Core/Events/IEventSink.cs`
- `Omicron.Core/Events/SessionEventWriter.cs`

The comments say producers pass `sequence=0`. That is still conceptually true, but now it should explicitly say `Envelope.Sequence = 0`.

This is minor but worth cleaning while the refactor is fresh.

### 4. Consider a token usage conversion helper

`AssistantResponseCompleteEvent` now accepts `TokenUsage`, while provider results still expose `UsageInfo`.

Current code manually constructs:

```csharp
new TokenUsage(usage?.InputTokens ?? 0, usage?.OutputTokens ?? 0)
```

Recommendation:

Add either:

```csharp
public static TokenUsage From(UsageInfo? usage)
```

or a small private helper in `AgentSession`. This reduces repeated null-handling and keeps conversion behavior consistent.

### 5. JSONL event type maintenance remains a known hardening item

The refactor changed JSON shape but did not change the manual `$type` switch. Tests pass, so this is fine for MVP. Keep `FH-0003` in `docs/implementation-plans/FUTURE-HARDENING-BACKLOG.md` as the tracking item for future event type registry/source generation.

## Recommendation

The event envelope refactor is acceptable and should stay.

Before moving on, do a tiny cleanup pass:

1. delete the temporary `_fix*.ps1` / `fix_tests.py` files;
2. add a `SessionEventWriter.Envelope()` helper or equivalent;
3. update XML comments to refer to `Envelope.Sequence = 0`;
4. optionally add a `TokenUsage.From(UsageInfo?)` helper.

No architectural blocker found.
