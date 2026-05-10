# Code Review 0063: Session Resume/Fork Fix Verification

Date: 2026-05-08  
Scope: verification after fixes for `0062-session-resume-fork-ux-review.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 346

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

Reviewed files:

```text
Omicron.Core/Sessions/AgentSession.cs
Omicron.Core/OmicronHost.cs
Omicron.CLI/Program.cs
Omicron.CLI/SlashCommandDispatcher.cs
Omicron.Core.Tests/SessionResumeTests.cs
```

## Summary

Several previous blockers were materially improved:

- CLI loop was restructured so resume/fork can re-enter `ChatLoop` with the returned session.
- `ForkSessionAsync` now creates a session record and materializes replay events for the fork.
- Provider state is re-keyed to the hydrated session's `AgentId`.
- Fork system prompt fallback now uses the source record.
- `/persist` is implemented as read-only diagnostics.
- `/fork --model <n>` is supported via visible model index.

However, there are still correctness issues before Plan 0003.5 should be marked complete.

## Blocking Findings

### 1. Fork replay events bypass `IEventSink`, so sequence stamping/EventLog are wrong

File:

```text
Omicron.Core/OmicronHost.cs
```

`ForkSessionAsync(...)` creates replay events with sequence `0` and appends directly to `ISessionStore`:

```csharp
replayEvents.Add(new SessionStartedEvent(
    new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, session.Id),
    session.AgentId, model.Id, model.ProviderName));
...
await SessionStore.AppendEventsAsync(session.Id, replayEvents, ct);
```

This bypasses the host-level `PersistentEventSink`, which is supposed to own sequence stamping and mirror events to the live `EventLog`.

Impact:

- fork replay events are stored with sequence `0`;
- `EventSequenceRange` behavior can be wrong for forked sessions;
- live `EventLog` will not contain the replay events;
- this violates the project decision that event sink owns sequence stamping.

Recommendation:

Create the fork session record first, then emit replay events through `Events.Emit(...)` / `EmitBatch(...)` so they are stamped and appended by `PersistentEventSink`.

Add tests:

```text
ForkSession_ReplayEvents_AreSequenceStamped
ForkSession_ReplayEvents_AreInEventLog
```

---

### 2. Fork materialization drops tool-call/tool-result transcript data

File:

```text
Omicron.Core/OmicronHost.cs
```

Fork materialization only handles:

```csharp
MessageRole.User -> UserMessageEvent
MessageRole.Assistant with Text -> AssistantResponseCompleteEvent
```

It ignores:

- assistant messages with `ToolCalls` as structured tool-call messages;
- `MessageRole.ToolResult` messages;
- tool invocation started/completed events;
- assistant tool-call IDs/names/arguments.

Impact:

- forked sessions with tool-use history are not durable as full transcript copies;
- resuming a fork of a tool-use session can lose tool context;
- provider replay may receive an incomplete/invalid transcript.

Recommendation:

Materialize enough event data from projected messages to preserve tool-use transcript semantics:

- assistant with tool calls should emit `AssistantResponseCompleteEvent` plus `ToolInvocationStartedEvent` for each call if needed by projector;
- tool result messages should emit `ToolInvocationCompletedEvent` with original tool call id/name/result/error;
- or add a dedicated transcript snapshot/fork event and teach projection to consume it.

For MVP, emitting the existing tool invocation events from projected messages is likely sufficient.

Add test:

```text
ForkSession_ToolTranscript_IsDurablyResumable
```

---

### 3. CLI session switch does not update model key or apply runtime config to resumed/forked sessions

File:

```text
Omicron.CLI/Program.cs
```

The new inner loop keeps `resumedSession`/`resumedModel`, which fixes the discarded-session bug. But when switching sessions:

```csharp
if (loopResult.NewSession is not null && loopResult.NewModel is not null)
{
    resumedSession = loopResult.NewSession;
    resumedModel = loopResult.NewModel;
    continue;
}
```

It does not:

- update `currentModelKey` to the target model key;
- save `cfg.LastModel` for fork target model;
- apply `cfg.DefaultMaxTokens`, `cfg.DefaultTemperature`, or `cfg.MaxIterations` to the resumed/forked session.

`ForkSessionAsync`/`ResumeSessionAsync` receives `apiKey`, so API key is mostly handled. But model-key state and runtime generation settings drift.

Impact:

- after `/fork --model <different>`, `/status` has the right model object but context `ModelKey` remains stale;
- `/model` command can compare against the wrong key;
- subsequent model switching/last-model behavior can be confusing;
- resumed/forked sessions may not use configured max tokens/temperature/max iterations.

Recommendation:

Extend `ChatCommandResult`/`ChatLoopResult` session switch path to carry `NewModelKey`, and in `Program.cs` update:

```csharp
currentModelKey = loopResult.NewModelKey ?? ResolveCatalogKey(loopResult.NewModel)
cfg.LastModel = currentModelKey
configManager.Save()
ApplySessionConfig(resumedSession, cfg, apiKey)
```

Add testable helper if possible.

---

## Medium-Priority Findings

### 4. `AgentSession.FromProjection` has duplicate XML summary comments

File:

```text
Omicron.Core/Sessions/AgentSession.cs
```

There are two adjacent `<summary>` blocks before `FromProjection`:

```csharp
/// <summary>
/// Create a session from a projection (hydrate transcript for resume/fork).
/// Messages are pre-populated; provider state is restored when safe.
/// </summary>
/// <summary>
/// Create a session from a projection, optionally reusing a session ID (for resume).
/// </summary>
```

Collapse into one summary.

### 5. `/fork --model` help text still only says `<key>` in places

File:

```text
Omicron.CLI/SlashCommandDispatcher.cs
```

Plan says `/fork <id|n> [--model <model-key|n>]`, and implementation supports index now, but help/usage still says:

```text
/fork <id|n>        Fork a session [--model <key>]
Usage: /fork <session-id-prefix|index> [--model <model-key>]
```

Update help and usage text to `[--model <model-key|n>]` consistently.

## Recommendation

Do not mark Plan 0003.5 complete yet.

Fix the remaining blockers:

1. Emit fork replay events through `IEventSink` so sequences/EventLog are correct.
2. Preserve tool-call/tool-result transcript semantics during fork materialization.
3. Update CLI session-switch path to carry/update model key and apply runtime config.

After those fixes, rerun validation and update the implementation plan/baseline.
