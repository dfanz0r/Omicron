# Code Review 0036: Plan 3 Phase 2 Completion Review

Date: 2026-05-08  
Scope: verify latest fixes after `docs/code-reviews/0035-plan-3-phase-2-third-fix-verification.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 234

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

Plan 3 Phase 2 is complete for the MVP baseline.

The key ambiguity called out in prior reviews has been resolved by changing event emission: `AgentSession` now emits `AssistantResponseCompleteEvent` for tool-call provider responses before emitting individual tool invocation events. That event serves as an explicit assistant-turn boundary and carries text/reasoning/usage for tool-use turns.

`SessionProjector` now has enough information to reconstruct:

- normal user/assistant transcript turns;
- assistant tool-call messages with text/reasoning and grouped tool calls;
- tool-result messages;
- provider state including `StoragePolicy`;
- reset-active-state behavior;
- errors;
- total usage across final and tool-call provider responses.

## Verified Fixes

### 1. Ambiguous tool-call boundaries resolved

File: `Omicron.Core/Sessions/AgentSession.cs`

Tool-call provider responses now emit an `AssistantResponseCompleteEvent` before tool execution begins. This gives projection a boundary between consecutive tool-use iterations.

### 2. Tool-call text/reasoning preserved

File: `Omicron.Core/Sessions/SessionProjection.cs`

The projector accumulates assistant deltas / response-complete text and combines that text/reasoning with subsequent tool calls when building the assistant tool-call message.

### 3. Multiple tool calls in one assistant turn are grouped

File: `Omicron.Core/Sessions/SessionProjection.cs`

The projector buffers tool calls and tool results, then flushes them as one assistant tool-call message followed by the corresponding tool results.

Test coverage includes `MultipleToolCallsFromOneTurn_AreGroupedIntoSingleAssistantMessage`.

### 4. Provider state projection includes `StoragePolicy`

Files:

- `Omicron.Core/Events/OmicronEvent.cs`
- `Omicron.Core/Sessions/ProviderStateManager.cs`
- `Omicron.Core/Sessions/SessionProjection.cs`

`ProviderStateUpdatedEvent` now carries `StoragePolicy`, `ProviderStateManager.Set(...)` emits it, and `SessionProjector` restores it.

Test coverage includes `ProviderState_PreservesStoragePolicy`.

### 5. Token usage for tool-call turns is now included

Because tool-call provider responses emit `AssistantResponseCompleteEvent`, `SessionProjection.TotalUsage` now includes tool-call-turn usage as well as final response usage.

Test coverage was updated in `RealToolCallFlow_ReconstructsFullTranscript`.

### 6. Projected timestamps are event-derived

Projection no longer unconditionally uses `DateTime.UtcNow` for reconstructed messages. Test coverage includes `Projection_UsesEventTimestamps`.

## Remaining Non-Blocking Notes

These do not block Phase 2 completion.

### 1. Add an explicit consecutive tool-use iteration regression test

The ARC boundary design should distinguish:

- one assistant turn with two tool calls; from
- two consecutive assistant tool-use turns with one tool call each.

The current code structure supports this, but adding a direct regression test would lock it down.

Suggested test shape:

```text
ARC(tool turn 1) -> Start/End(call_1) -> ARC(tool turn 2) -> Start/End(call_2) -> ARC(final)
```

Expected projection:

```text
Assistant(ToolCalls=[call_1])
ToolResult(call_1)
Assistant(ToolCalls=[call_2])
ToolResult(call_2)
Assistant(final)
```

### 2. CLI now receives `AssistantResponseCompleteEvent` for tool-call turns

`Omicron.CLI/Program.cs` prints token usage for every `AssistantResponseCompleteEvent`. Since tool-call turns now emit this event, the CLI may display usage before tool execution as well as after the final answer. This may be acceptable, but it is a UX change to check during live CLI testing.

### 3. Tool-call assistant timestamps are improved but still approximate

The projector uses event-derived timestamps. For assistant tool-call messages, the timestamp may be based on the flush-trigger event rather than a dedicated assistant-turn event timestamp. This is acceptable for MVP display/debug replay.

A future exact transcript model could introduce a dedicated assistant tool-call event if more precise timestamps or richer metadata are needed.

## Phase 2 Status

Phase 2 acceptance criteria are met:

- persisted event streams can be projected into session state;
- messages are reconstructed for normal and tool-use transcripts;
- provider state update/clear replay works;
- reset clears active projected state;
- errors are captured;
- token usage accumulates;
- JSONL and in-memory replay paths are tested.

## Recommendation

Mark Plan 3 Phase 2 complete and proceed to Plan 3 Phase 3: host-backed workspace VFS v1.

Before starting Phase 3, update `docs/implementation-plans/0003-core-persistence-workspace-foundations.md` to mark Phases 1 and 2 complete and leave Phase 3+ pending.
