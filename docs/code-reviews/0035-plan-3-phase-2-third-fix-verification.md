# Code Review 0035: Plan 3 Phase 2 Third Fix Verification

Date: 2026-05-08  
Scope: verify latest fixes after `docs/code-reviews/0034-plan-3-phase-2-second-fix-verification.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 234

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

The latest pass fixed several concrete issues:

- `StoragePolicy` is now part of `ProviderStateUpdatedEvent` and projection.
- A non-default `StoragePolicy` projection test was added.
- Projection now uses event-derived timestamps instead of unconditional `DateTime.UtcNow` for reconstructed messages.
- Tool-call projection now buffers tool starts/results and can group interleaved start/end pairs from a single assistant turn.
- A multi-tool grouping test was added.

However, the current tool-call grouping heuristic still cannot distinguish **multiple tool calls in one assistant turn** from **multiple consecutive assistant tool-use turns**. That is an important replay-fidelity gap for real agent loops.

## High-Priority Finding

### 1. Consecutive tool-use turns with no intervening assistant text are incorrectly merged

Files:

- `Omicron.Core/Sessions/SessionProjection.cs`
- `Omicron.Core.Tests/SessionProjectionTests.cs`
- `Omicron.Core/Sessions/AgentSession.cs`

The current projector flushes a tool-call run only on a subsequent non-tool event:

- `AssistantTextDeltaEvent`
- `AssistantResponseCompleteEvent`
- `UserMessageEvent`
- final end-of-stream flush

This fixes the case where one provider response contains multiple tool calls and `AgentSession` emits interleaved events:

```text
Start(call_1)
End(call_1)
Start(call_2)
End(call_2)
AssistantResponseComplete(...)
```

But the exact same event shape can also occur when the model performs **two consecutive tool-use iterations**, each with one tool call, and the second tool-use response has no assistant text delta before its tool call:

```text
UserMessage
Start(call_1)   // assistant turn 1 requested a tool
End(call_1)
Start(call_2)   // assistant turn 2 requested another tool after seeing result 1
End(call_2)
AssistantResponseComplete(final)
```

Runtime transcript shape for this case is:

```text
User
Assistant(ToolCalls=[call_1])
ToolResult(call_1)
Assistant(ToolCalls=[call_2])
ToolResult(call_2)
Assistant(final)
```

Current projection produces:

```text
User
Assistant(ToolCalls=[call_1, call_2])
ToolResult(call_1)
ToolResult(call_2)
Assistant(final)
```

Impact:

- replayed transcript can differ from real runtime transcript for repeated tool-use loops;
- this can affect provider replay/resume because tool results may be associated with the wrong assistant tool-call turn;
- this is not hypothetical: repeated tool-use loops were already encountered during OpenRouter/Responses debugging.

The current test only validates the grouping case; it does not validate consecutive tool-use iterations.

Recommendation:

Add an event that marks the assistant tool-call turn boundary. The cleanest option remains a dedicated event emitted once per provider tool-use response, before executing tools, for example:

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

Then projection can reconstruct exact assistant tool-call messages from `AssistantToolCallsEvent`, and `ToolInvocationStartedEvent` / `ToolInvocationCompletedEvent` can remain execution audit events.

At minimum, add a failing/passing test for real `AgentSession` with a fake provider that returns:

1. first response: one tool call;
2. second response: another tool call;
3. third response: final answer.

Projection should produce two separate assistant tool-call messages, not one merged message.

## Medium-Priority Findings

### 2. Tool-call usage undercount is said to be tracked, but is not yet in the hardening backlog

File:

- `docs/implementation-plans/FUTURE-HARDENING-BACKLOG.md`

The latest summary says token usage undercount is accepted and tracked, but the backlog currently only contains:

- FH-0001 JSONL concurrency
- FH-0002 non-atomic diagnostic counters
- FH-0003 JSONL deserialization switch maintenance

There is no item for tool-call provider response usage not being emitted/persisted.

Recommendation:

Add a backlog item such as:

```text
FH-0004: Tool-use provider response usage is not persisted
```

Include that `AgentSession` currently captures usage for final `AssistantResponseCompleteEvent` but does not emit usage for provider responses that stop with tool use.

---

### 3. Tool-call assistant message timestamps are event-derived but not necessarily source-turn timestamps

File:

- `Omicron.Core/Sessions/SessionProjection.cs`

Using `lastTimestamp` is an improvement over unconditional `DateTime.UtcNow`, but `FlushToolCallRun()` uses the timestamp of whichever event triggered the flush. For tool-call assistant messages, that can be the final assistant response event rather than the original tool-call start/provider-turn event.

This is lower priority than transcript shape, but a dedicated assistant tool-call event would also solve this by carrying the exact timestamp for the assistant tool-call turn.

## Status Against Review 0034 Findings

| Finding | Status |
| --- | --- |
| Multiple tool calls not grouped | Fixed for one assistant turn with multiple interleaved tool events. Still incorrect for consecutive tool-use turns with no intervening non-tool event. |
| StoragePolicy test | Fixed. |
| Timestamps use `DateTime.UtcNow` | Mostly fixed; timestamps are event-derived, though tool-call assistant timestamp can be the flush trigger timestamp. |
| Token usage undercount tracked | Not yet tracked in backlog. |

## Recommendation

Do one final Phase 2 fix before marking complete:

1. Add an explicit assistant tool-call turn event, or another explicit turn-boundary signal, so replay can distinguish same-turn multi-tool calls from consecutive tool-use turns.
2. Add tests for both cases:
   - one provider response with two tool calls should project one assistant tool-call message;
   - two consecutive provider tool-use responses should project two assistant tool-call messages.
3. Add FH-0004 for tool-use usage undercount if not fixing usage emission now.

This is the same core reason prior reviews recommended an explicit assistant tool-call turn event: inference from `ToolInvocationStarted/Completed` events alone is ambiguous.
