# Code Review 0033: Plan 3 Phase 2 Fix Verification

Date: 2026-05-08  
Scope: verify fixes after `docs/code-reviews/0032-plan-3-phase-2-review.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 231

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

The focused Phase 2 repair pass fixed the main blocker from review 0032:

- `ToolInvocationStartedEvent` now projects an assistant message containing `ToolCalls`.
- `ToolInvocationCompletedEvent` appends the corresponding tool result.
- A real `AgentSession` + `FakeProvider` tool-call integration projection test was added.
- `SessionId` fallback from any event is implemented.
- `pendingToolName` was removed.
- `ProviderStateUpdatedEvent` now carries `SessionAffinityKey` and `ProviderMetadata`, and the projector restores those fields.

This is a meaningful improvement. Phase 2 is close, but I would still treat it as **not fully complete** until the remaining transcript-fidelity issues below are explicitly fixed or documented as MVP limitations.

## Remaining Findings

### 1. Provider state replay still loses `StoragePolicy`

Files:

- `Omicron.Core/Events/OmicronEvent.cs`
- `Omicron.Core/Sessions/ProviderStateManager.cs`
- `Omicron.Core/Sessions/SessionProjection.cs`

Review 0032 called out these lost fields from `ProviderTurnState`:

- `SessionAffinityKey`
- `ProviderMetadata`
- non-default `StoragePolicy`

The first two were fixed. `StoragePolicy` is still not emitted in `ProviderStateUpdatedEvent`, so projected provider state always gets the default:

```csharp
ProviderStoragePolicy.AllowProviderStateNoStore
```

Impact:

- replayed provider state may not match the runtime provider state for OpenRouter/stateless or other storage-policy-sensitive models;
- future resume/fork logic could make the wrong stateful/stateless decision if it relies on projected state.

Recommendation:

Add `ProviderStoragePolicy StoragePolicy` to `ProviderStateUpdatedEvent`, have `ProviderStateManager.Set(...)` pass `normalizedState.StoragePolicy`, and set it on the projected `ProviderTurnState`.

Add a test asserting a non-default storage policy round-trips through projection.

---

### 2. Tool-call assistant text/reasoning is still lost

Files:

- `Omicron.Core/Sessions/AgentSession.cs`
- `Omicron.Core/Sessions/SessionProjection.cs`

Runtime `AgentSession` preserves text/reasoning from a provider response that contains tool calls:

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

But the persisted event stream does not include that assistant tool-call response text/reasoning. The projector therefore creates:

```csharp
Text = ""
```

Impact:

- replay projection is not transcript-equivalent for models that emit text before/with tool calls;
- UI history/debug replay can lose visible assistant text;
- future provider replay may lose reasoning/text content associated with tool-use turns.

Recommendation:

Either:

1. add a dedicated assistant tool-call event that includes response text/reasoning and all tool calls for that provider turn; or
2. extend `ToolInvocationStartedEvent` with optional `AssistantText` / `AssistantReasoning` if staying event-minimal.

A dedicated event is cleaner because it can represent all tool calls from one assistant turn as a group.

---

### 3. Multiple tool calls from one assistant turn project as multiple assistant messages

Files:

- `Omicron.Core/Sessions/AgentSession.cs`
- `Omicron.Core/Sessions/SessionProjection.cs`

Runtime behavior:

- one assistant message can contain `ToolCalls = [call1, call2, ...]`.

Projection behavior:

- each `ToolInvocationStartedEvent` appends a separate assistant message with one tool call.

Impact:

- replayed transcript shape differs from runtime transcript for multi-tool-call turns;
- some provider wire formats expect a single assistant message containing all tool calls before the tool results.

Recommendation:

The best fix overlaps with finding #2: emit a single assistant tool-call message event before tool execution, containing:

- assistant text;
- reasoning;
- full normalized `ToolCallContent` list;
- timestamp/session identity.

Then projection can reconstruct the exact assistant tool-call message.

If deferring this, document that Phase 2 projection reconstructs an MVP-compatible transcript, not an exact runtime transcript for multi-tool-call assistant turns.

---

### 4. Token usage for tool-call provider responses is not persisted

File:

- `Omicron.Core/Sessions/AgentSession.cs`

The new test notes:

```csharp
// Only the final response's usage is recorded in events (intermediate tool-call usage is not captured)
```

This means `SessionProjection.TotalUsage` is incomplete for conversations with tool-use provider turns.

Impact:

- projected total usage can undercount tokens;
- future session history/cost accounting from replay will be inaccurate.

Recommendation:

Emit an event for provider response completion even when the stop reason is tool use, or include usage in the proposed assistant tool-call event.

## Status Against Review 0032 Findings

| Finding | Status |
| --- | --- |
| Real tool-call replay missing assistant tool-call message | Partially fixed. Single-tool replay now reconstructs an assistant tool-call message, but text/reasoning and multi-tool grouping are not exact. |
| Provider-state projection loses fields | Partially fixed. `SessionAffinityKey` and `ProviderMetadata` preserved; `StoragePolicy` still lost. |
| SessionId default when range omits start | Fixed. |
| Projector assumes ordered input | Documented in XML comments; acceptable. |
| `pendingToolName` unused | Fixed. |

## Recommendation

Before marking Plan 3 Phase 2 complete, choose one path:

1. **Exact replay path:** add an assistant tool-call turn event and include storage policy in provider-state events. This gives future resume/provider replay a stronger foundation.
2. **MVP projection path:** explicitly document Phase 2 limitations: tool-use projection is display/debug-oriented, not guaranteed transcript-identical for assistant text/reasoning, multi-tool turns, storage policy, or tool-use token accounting.

Given Omicron's RFC goals around replay-safe session state and provider abstraction, I recommend the exact replay path now while the event model is still small.
