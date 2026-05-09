# Code Review 0034: Plan 3 Phase 2 Second Fix Verification

Date: 2026-05-08  
Scope: verify latest fixes after `docs/code-reviews/0033-plan-3-phase-2-fix-verification.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 231

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

Several review findings were improved:

- `ProviderStateUpdatedEvent` now includes `StoragePolicy`.
- `ProviderStateManager.Set(...)` passes `StoragePolicy` through.
- `SessionProjector` reconstructs `ProviderTurnState.StoragePolicy`.
- `SessionProjector` now accumulates assistant text/reasoning deltas and can attach them to a projected assistant tool-call message.
- `SessionId` fallback and unused `pendingToolName` remain fixed.

However, one important claim is not actually true against the real `AgentSession` event order: multiple tool calls from one assistant turn are still not projected as one assistant message.

## High-Priority Finding

### 1. Multi-tool turns are still not grouped for real `AgentSession` event order

Files:

- `Omicron.Core/Sessions/AgentSession.cs`
- `Omicron.Core/Sessions/SessionProjection.cs`
- `Omicron.Core.Tests/SessionProjectionTests.cs`

The new projector queues tool calls on `ToolInvocationStartedEvent` and flushes on `ToolInvocationCompletedEvent`:

```csharp
case ToolInvocationStartedEvent tis:
    pendingToolCalls.Add(...);
    break;

case ToolInvocationCompletedEvent tic:
    FlushPendingToolCalls();
    messages.Add(Message.ToolResultMessage(...));
    break;
```

This only groups multiple tool calls if all `ToolInvocationStartedEvent`s arrive before the first `ToolInvocationCompletedEvent`.

But real `AgentSession` emits tool events per tool in a start/complete loop:

```text
ToolInvocationStarted(call_1)
ToolInvocationCompleted(call_1)
ToolInvocationStarted(call_2)
ToolInvocationCompleted(call_2)
```

So the projector flushes after `call_1` and creates a second assistant message for `call_2`. Runtime state, however, has one assistant message containing both tool calls.

Impact:

- multi-tool assistant turns are still not transcript-equivalent after replay;
- provider-compatible resume/replay may be incorrect for providers that expect one assistant message with all tool calls before tool results;
- the current tests do not cover real multi-tool `AgentSession` flow.

Recommendation:

Best fix: add a dedicated assistant tool-call turn event emitted once per assistant tool-use response, before individual tool execution events. Example:

```csharp
public sealed record AssistantToolCallsEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId,
    string Text,
    string? ReasoningText,
    IReadOnlyList<ToolCallContent> ToolCalls,
    int InputTokens,
    int OutputTokens) : OmicronEvent(Id, Sequence, Timestamp, SessionId);
```

Then projection can reconstruct the exact assistant tool-call message and usage from this event, while `ToolInvocationStarted/Completed` remain execution audit events.

Alternative: change `AgentSession` event ordering to emit all `ToolInvocationStartedEvent`s before any completions. That still would not preserve assistant text/reasoning/usage as cleanly as a dedicated event.

Add a real integration test with a fake provider returning two tool calls in one response. Assert projection has one assistant message with two `ToolCalls`, followed by two tool result messages.

## Medium-Priority Findings

### 2. StoragePolicy is now projected, but round-trip coverage should be explicit

Files:

- `Omicron.Core/Events/OmicronEvent.cs`
- `Omicron.Core/Sessions/SessionProjection.cs`
- `Omicron.Core.Tests/SessionProjectionTests.cs`

The code now preserves `StoragePolicy`, but the visible `ProviderState_PreservesAllFields` test does not assert a non-default storage policy. Add an assertion using a non-default value such as `PreferStateless` or another available policy.

This is small, but worthwhile because this was a specific review finding.

---

### 3. Projected assistant message timestamps are not replay-deterministic

File:

- `Omicron.Core/Sessions/SessionProjection.cs`

`FlushAssistantText()` and `FlushPendingToolCalls()` use:

```csharp
Timestamp = DateTime.UtcNow
```

Projection should generally be deterministic from the event stream. Re-projecting the same event log later will produce different timestamps for reconstructed assistant text/tool-call messages.

Recommendation:

Track the timestamp of the first/last contributing assistant delta/tool event and use that event timestamp. For completed assistant responses, `AssistantResponseCompleteEvent.Timestamp` is available and should be used.

---

### 4. Tool-use token usage undercount remains accepted, but should be tracked

The user summary marks token undercount as accepted/documented because `AgentSession` does not emit usage for tool-call provider responses.

That is acceptable for MVP if intentional, but it should be tracked in the future hardening backlog or Plan 3 notes because session history/cost accounting from replay will undercount tool-use turns.

Suggested backlog item:

- persist provider response usage for tool-use stops, preferably in the proposed assistant tool-call turn event.

## Status Against Review 0033 Findings

| Finding | Status |
| --- | --- |
| Provider state loses `StoragePolicy` | Code fixed; add explicit non-default test. |
| Tool-call text/reasoning lost | Improved for single-tool turns with deltas before tool completion. |
| Multiple tool calls from one assistant turn | Not fixed for real `AgentSession` event order. |
| Token usage undercount | Accepted as limitation; should be tracked. |

## Recommendation

Do one more small Phase 2 pass before marking complete:

1. Add a dedicated assistant tool-call turn event, or otherwise make real multi-tool turns replay as one assistant message.
2. Add a real multi-tool `AgentSession` projection test.
3. Add explicit non-default `StoragePolicy` projection test.
4. Track tool-use usage undercount as a known future hardening item if not fixing now.

After that, Phase 2 should be ready to close.
