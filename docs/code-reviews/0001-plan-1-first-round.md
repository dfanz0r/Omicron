# Code Review 0001: Plan 1 First Round

Date: 2026-05-08  
Scope: first round review of Plan 1 core architecture groundwork implementation.

## Validation

Build and test status at time of review:

```text
dotnet build Omicron.slnx --nologo
0 warnings, 0 errors

dotnet test Omicron.slnx --nologo
Passed: 64
```

## Summary

The direction is good. The implementation has introduced several important Plan 1 seams:

- `OmicronHost` composition root;
- extension registry;
- tool registry;
- command registry;
- permission service seam;
- workspace seam;
- execution broker seam;
- provider state seam;
- durable-shaped event records;
- initial architecture tests.

However, the new architecture is not yet the active runtime path. The CLI still runs the old `Agent`, and `AgentSession` does not fully write yielded events into the event sink. Those should be addressed before moving deeper into Plan 2/provider abstraction work.

## High-Priority Findings

### 1. `AgentSession` events are mostly yielded but not emitted to `IEventSink`

File: `Omicron.Core/Sessions/AgentSession.cs`

`PromptAsync()` emits `UserMessageEvent` to `_eventSink`, but most other events are only yielded:

- `SessionStartedEvent`
- `AssistantTextDeltaEvent`
- `ToolInvocationStartedEvent`
- `ToolInvocationCompletedEvent`
- `AssistantResponseCompleteEvent`

This means `host.EventLog` does not contain the real session history.

This breaks the Plan 1 goal of durable-shaped events and replay groundwork.

Recommendation:

Add a helper like:

```csharp
private OmicronEvent Emit(OmicronEvent evt)
{
    _eventSink.Emit(evt);
    return evt;
}
```

Then always use:

```csharp
yield return Emit(new AssistantTextDeltaEvent(...));
```

Also consider yielding `UserMessageEvent` too, not just emitting it.

---

### 2. CLI still uses old `Agent`, not `AgentSession`

File: `Omicron.CLI/Program.cs`

Current flow:

```csharp
var agent = new Agent(...);
RegisterTools(agent, host);
await ChatLoop(agent);
```

This bypasses:

- `AgentSession`;
- session IDs;
- provider state store;
- event sink/log;
- permission service;
- tool invocation context with stable session/agent IDs.

So most Plan 1 architecture exists but is not exercised by the actual MVP frontend.

Recommendation:

Either:

1. switch CLI chat to `host.CreateSession(...)`; or
2. explicitly mark `AgentSession` as not yet wired and make the next PR wire it.

Preferred next step: wire CLI to `AgentSession` soon so the new architecture cannot drift from real behavior.

---

### 3. CLI adapter creates fake session/agent IDs per tool invocation

File: `Omicron.CLI/Program.cs`

In `RegisterTools()`:

```csharp
new ToolInvocationContext(
    new ToolCallId(id),
    args ?? new Dictionary<string, object?>(),
    SessionId.New(), AgentId.New(), CancellationToken.None);
```

This creates a brand-new `SessionId` and `AgentId` for every tool call. That defeats event, tool, and provider/session correlation.

Recommendation:

If the old `Agent` remains temporarily, this adapter should at minimum receive stable IDs per chat session. Better: remove this adapter by using `AgentSession`.

---

### 4. `IProviderConversationStateStore` is defined in the wrong namespace

File: `Omicron.Core/Sessions/ProviderState.cs`

The file is under:

```text
Omicron.Core/Sessions/ProviderState.cs
```

but declares:

```csharp
namespace Omicron.Core.Events;
```

Provider turn state is session/provider infrastructure, not event infrastructure. This will become confusing when Plan 2 expands it.

Recommendation:

Move it to one of:

```csharp
namespace Omicron.Core.Sessions;
```

or:

```csharp
namespace Omicron.Core.Providers;
```

Preferred for now: `Omicron.Core.Sessions`, because provider state is session-scoped.

## Medium-Priority Findings

### 5. `HostWorkspace` is a regression from existing `FileTools`

File: `Omicron.Core/Workspace/IWorkspace.cs`

`HostWorkspace.ReadPathAsync()` currently:

- ignores `ReadOptions.Offset`;
- ignores `ReadOptions.Limit`;
- ignores `ReadOptions.Chunk`;
- returns only `"Directory: {path}"` for directories;
- does not preserve the rich truncation/listing behavior from `FileTools`.

This is acceptable as a placeholder, but if tools start using `IWorkspace`, behavior will regress.

Recommendation:

Either wrap existing `FileTools` logic or extract shared file-read/list logic into reusable workspace service.

---

### 6. `LocalExecutionBroker` is a stub

File: `Omicron.Core/Execution/IExecutionBroker.cs`

Current implementation returns:

```text
Execute: {request.Command}
```

without executing anything.

That is acceptable as a seam, but it should not be used by the shell tool yet. If wired prematurely, shell behavior will silently break.

Recommendation:

Leave unused for now or explicitly name it `StubExecutionBroker` until implemented.

---

### 7. `ProviderStateKey` string fields are case-sensitive

File: `Omicron.Core/Sessions/ProviderState.cs`

Because `ProviderStateKey` is a record struct, `ProviderName` comparisons are case-sensitive. Elsewhere provider names are generally case-insensitive.

This could produce duplicate state entries for:

```text
OpenAI
openai
OPENAI
```

Recommendation:

Normalize provider names when constructing keys or introduce a key factory.

---

### 8. `InMemoryEventSink.NextSequence()` is unused and possibly misleading

File: `Omicron.Core/Events/IEventSink.cs`

`InMemoryEventSink` has:

```csharp
private long _nextSequence;
public long NextSequence() => Interlocked.Increment(ref _nextSequence);
```

But `AgentSession` uses its own `_sequence`.

That is not necessarily wrong if sequences are session-local, but then the sink-level sequence generator should probably be removed or clarified.

Recommendation:

Decide one policy:

- event sequences are per-session: keep sequencing in `AgentSession`;
- event sequences are global-log order: move sequencing to event sink/event factory.

For RFC replay, per-session sequence is probably fine, but document it.

## Lower-Priority Cleanup

### 9. `BuiltinToolsExtension` stores `_workspaceRoot` but does not use it

File: `Omicron.Core/Extensions/BuiltinToolsExtension.cs`

```csharp
private readonly string _workspaceRoot;
```

Unused currently. Either remove it or keep only once file/shell tools move into this extension.

---

### 10. Extension registry has no duplicate/partial failure policy

File: `Omicron.Core/Extensions/ExtensionRegistry.cs`

Currently duplicate extension IDs are allowed, and if an extension throws halfway through registration, partial tool/command contributions may remain.

Not urgent, but before third-party plugins/WASM, we will want:

- duplicate extension ID checks;
- registration transaction/rollback or clear failure semantics.

## Recommended Next Fixes

1. Fix `AgentSession` so every yielded core event is also emitted to `IEventSink`.
2. Move provider state types to `Omicron.Core.Sessions`.
3. Update CLI to use `host.CreateSession(...)` instead of `new Agent(...)`.
4. Adapt `ReadAgentOutput` to consume `OmicronEvent`.
5. Remove fake `SessionId.New()` / `AgentId.New()` from CLI tool adapter.
6. Add tests proving `AgentSession` event log contains user, assistant delta, tool start/end, and final response events.
7. Add a test proving `AgentSession.Reset()` clears provider state.

Once those are done, Plan 1 will be much more solid as the foundation for Plan 2.
