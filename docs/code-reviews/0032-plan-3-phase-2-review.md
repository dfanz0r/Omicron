# Code Review 0032: Plan 3 Phase 2 Replay Projection Review

Date: 2026-05-08  
Scope: verify Plan 3 Phase 2 session replay projection implementation.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 229

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

Phase 2 has a solid first implementation:

- `SessionProjection`
- `ISessionProjector`
- `SessionProjector`
- 10 projection tests covering simple prompt replay, provider-state update/clear, reset, errors, token usage, JSONL replay, and in-memory store replay.

This is enough to call Phase 2 started and mostly aligned with the Plan 3 shape. However, I would not mark Phase 2 fully complete until the tool-call replay gap below is fixed or explicitly deferred.

## High-Priority Finding

### 1. Real tool-call transcripts are not fully reconstructable from current projection

Files:

- `Omicron.Core/Sessions/SessionProjection.cs`
- `Omicron.Core.Tests/SessionProjectionTests.cs`
- `Omicron.Core/Sessions/AgentSession.cs`

The current projector handles tool events like this:

```csharp
case ToolInvocationStartedEvent tis:
    pendingToolCallId = tis.ToolCallId.Value;
    pendingToolName = tis.ToolName;
    break;

case ToolInvocationCompletedEvent tic:
    if (pendingToolCallId is not null)
    {
        messages.Add(Message.ToolResultMessage(
            pendingToolCallId,
            tic.ToolName,
            tic.Result,
            tic.IsError));
        ...
    }
    break;
```

This reconstructs only the tool result message. It does not reconstruct the assistant tool-call message that caused the tool invocation.

This matters because `AgentSession` does **not** emit an `AssistantResponseCompleteEvent` before tool execution when a model returns tool calls. In real runtime flow, the assistant tool-call message exists only in memory:

```csharp
_messages.Add(new Message
{
    Role = MessageRole.Assistant,
    Text = fullText,
    Reasoning = fullReasoning,
    ToolCalls = [.. normalizedToolCalls],
    Timestamp = DateTime.UtcNow
});
```

The event stream then emits `ToolInvocationStartedEvent` / `ToolInvocationCompletedEvent`, but no assistant message event containing the tool call.

The current test `ToolCallTranscript_ReconstructsToolResults()` uses a synthetic event stream with `AssistantResp("Let me check")` before `ToolStart(...)`, which does not match the actual `AgentSession` event pattern for tool calls.

Impact:

- replay projection cannot reconstruct a provider-compatible transcript after real tool use;
- future resume/replay may miss the assistant tool-call turn required before a tool-result message;
- OpenAI Responses/stateless replay especially needs assistant function-call items paired with tool outputs.

Recommended fix:

- On `ToolInvocationStartedEvent`, append or update an assistant message with a `ToolCallContent` built from:
  - `ToolCallId`
  - `ToolName`
  - `Arguments`
- On `ToolInvocationCompletedEvent`, append `Message.ToolResultMessage(...)` using `tic.ToolCallId.Value`, not only a pending ID.

Add an integration-style projection test using a real `AgentSession` with a fake provider that returns a tool call, then project persisted events and assert the projection includes:

1. user message;
2. assistant message with `ToolCalls` containing the tool call ID/name/args;
3. tool result message;
4. final assistant response.

## Medium-Priority Findings

### 2. Provider-state projection loses some `ProviderTurnState` fields

Files:

- `Omicron.Core/Events/OmicronEvent.cs`
- `Omicron.Core/Sessions/ProviderStateManager.cs`
- `Omicron.Core/Sessions/SessionProjection.cs`

`ProviderStateUpdatedEvent` currently carries:

- `ProviderStateKey`
- `PreviousResponseId`
- `ConversationId`
- `Reason`

`SessionProjector` reconstructs:

```csharp
new ProviderTurnState(psu.Key, psu.PreviousResponseId, psu.ConversationId, null, null)
```

This loses:

- `SessionAffinityKey`
- `ProviderMetadata`
- non-default `StoragePolicy`

Today this may be acceptable because persisted continuation primarily needs `PreviousResponseId` / `ConversationId`, and OpenRouter Responses defaults stateless. But it is not a full provider-state replay.

Recommendation:

Either:

- extend `ProviderStateUpdatedEvent` to include the missing fields; or
- document that Phase 2 projection reconstructs MVP provider state only.

If extending the event, update JSONL serialization tests and projection tests.

---

### 3. `SessionId` remains default if the projected range omits `SessionStartedEvent`

File:

- `Omicron.Core/Sessions/SessionProjection.cs`

The projector only sets `sessionId` from `SessionStartedEvent`. If projecting a range/cursor that starts after session start, the projection returns `default` even though every event has `SessionId`.

Recommendation:

Set `sessionId ??= evt.SessionId` at the start of event iteration, then let `SessionStartedEvent` remain explicit when present.

---

### 4. The projector assumes input events are already ordered

File:

- `Omicron.Core/Sessions/SessionProjection.cs`

The interface comment says ordered event sequence, and `ISessionStore.ReadEventsAsync(...)` returns sequence order, so this is acceptable. But callers can pass arbitrary `IEnumerable<OmicronEvent>`.

Recommendation:

No immediate change required. Consider either:

- documenting this strongly in XML comments; or
- sorting by `Sequence` inside `Project(...)` if projection should be defensive.

## Low-Priority Findings

### 5. `pendingToolName` is assigned but unused

File:

- `Omicron.Core/Sessions/SessionProjection.cs`

`pendingToolName` is set on tool start and cleared on completion, but never read. Remove it or use it when creating assistant tool-call messages.

### 6. Reset semantics are currently “latest epoch only”

File:

- `Omicron.Core/Sessions/SessionProjection.cs`

`SessionResetEvent` clears messages, provider states, errors, and token usage, while leaving `IsReset = true`. This is reasonable for “current active session state after latest reset.” If future UI wants full historical replay across reset boundaries, introduce epochs/checkpoints rather than changing this MVP projection.

## Phase 2 Status

Implemented and good:

- projection records/interfaces exist;
- user/assistant messages replay;
- provider-state update/clear replay works for MVP fields;
- reset clears projected active state;
- errors are captured;
- token usage accumulates;
- JSONL and in-memory replay tests exist.

Needs follow-up before marking Phase 2 complete:

1. reconstruct assistant tool-call messages from real tool-call event streams;
2. add a real `AgentSession` tool-call projection test;
3. decide/document MVP vs full provider-state replay fields.

## Recommendation

Do one focused Phase 2 follow-up pass for real tool-use replay. After that, Phase 2 should be ready to mark complete and Plan 3 can move toward workspace/VFS foundations.
