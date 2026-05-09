# Code Review 0013: Plan 2 First Review

Date: 2026-05-08  
Scope: review claimed completion of Plan 2 provider abstraction / OpenAI Responses work.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 170

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

The test suite is healthy, but the implementation is not yet complete against Plan 2's runtime goals. A lot of useful scaffolding landed, but several pieces are only directly testable as isolated helpers and are not wired into the live agent/provider path.

## Summary

Good progress:

- `ProviderCompatibility`, compatibility enums, and `ProviderStoragePolicy` exist.
- `CompatibilityDetector` exists.
- `Model` has compatibility/storage policy fields and `GetEffectiveCompatibility()`.
- `ProviderTurnState` was expanded with storage policy helpers.
- Canonical conversation records and conversion helpers exist.
- `ToolCallIdMapper` exists.
- `OpenAiResponsesShape` exists and has request/SSE parser tests.
- `ShapeBasedProvider` and `OpenAiProvider` route `OpenAiResponses` to `/responses`.

However, the core Plan 2 claim — stateful Responses support through the actual runtime — is not yet true. `AgentSession` still calls `provider.StreamAsync(...)` with only `Model`, `Message` history, tools, and `ChatOptions`. No provider-state key/current state is passed in, `previous_response_id` is never used in the live path, and completed response IDs are not persisted through `ProviderStateManager`.

## High-Priority Findings

### 1. Stateful Responses is not wired into `AgentSession` / provider runtime

Files:

- `Omicron.Core/Sessions/AgentSession.cs`
- `Omicron.Core/Providers/IChatProvider.cs`
- `Omicron.Core/Providers/OpenAiProvider.cs`
- `Omicron.Core/Providers/ApiShape.cs`

`OpenAiResponsesShape.BuildStatefulRequestBody(...)` exists, but the live runtime never calls it.

Current runtime path:

```csharp
provider.StreamAsync(Model, _messages, SystemPrompt, tools, options)
```

`ChatOptions` does not carry a `ProviderStateKey`, `ProviderTurnState`, `PreviousResponseId`, `ProviderStoragePolicy`, or state manager. `OpenAiProvider.StreamAsync()` and `ShapeBasedProvider.StreamAsync()` always call:

```csharp
shape.BuildRequestBody(...)
```

not `BuildStatefulRequestBody(...)`.

Also, `AgentSession` receives `StreamEventType.Done`, reads usage, but ignores `evt.Delta` / response ID:

```csharp
case StreamEventType.Done:
    stopReason = evt.StopReason ?? StopReason.Stop;
    usage = evt.Usage;
    break;
```

So completed Responses IDs are not persisted.

Impact:

- No actual second-turn request can include `previous_response_id`.
- `ProviderStateManager` is not used for Responses continuation.
- Reset clears provider state, but no state is ever written in the first place.
- Stateful/stateless fallback cannot be implemented on top of the current live path.

Recommendation:

Introduce a provider request context or extend the current call path so the provider can receive:

- `SessionId`, `AgentId`;
- `ProviderStateKey`;
- current `ProviderTurnState?`;
- `ProviderStoragePolicy`;
- mode: stateful/stateless/fallback;
- an event/state sink or callback to persist response IDs through `ProviderStateManager`.

Then update `AgentSession` to:

1. construct `ProviderStateKey` for the selected model;
2. read current state from `ProviderStateManager`;
3. choose stateful vs stateless based on `ApiType`, storage policy, and current state;
4. pass that context to the provider;
5. persist `Done` response IDs via `ProviderStateManager.Set(...)`.

### 2. `ProviderTurnState.StoragePolicy` is lost when storing state

File: `Omicron.Core/Sessions/ProviderState.cs`

`ProviderTurnState` now has an init-only `StoragePolicy` property, but `InMemoryProviderConversationStateStore.Set()` reconstructs the state manually and does not copy it:

```csharp
var normalizedState = new ProviderTurnState(normalizedKey,
    state.PreviousResponseId, state.ConversationId,
    state.SessionAffinityKey, state.ProviderMetadata);
_states[normalizedKey] = normalizedState;
```

This resets `StoragePolicy` to the default `AllowProviderStateNoStore` for every stored state, even if the caller supplied `PreferStateless` or `AllowProviderStoredState`.

Recommendation:

Use record `with` so additive fields are preserved:

```csharp
var normalizedState = state with { Key = normalizedKey };
```

Add a test that setting a `ProviderTurnState` with `StoragePolicy = AllowProviderStoredState` survives store roundtrip and manager `Set()`.

### 3. Compatibility detection is mostly dormant

Files:

- `Omicron.Core/Providers/CompatibilityDetector.cs`
- `Omicron.Core/Models/ModelCatalogService.cs`
- `Omicron.Core/Models/Model.cs`

`CompatibilityDetector` exists, but `ModelCatalogService` still hardcodes API type selection in multiple places:

- OpenCode Zen uses `OpenCodeProvider.ResolveApiType(...)` directly;
- OpenCode Go forces `OpenAiChat`;
- OpenRouter forces `OpenAiChat`;
- fallback OpenAI models are hardcoded as Chat;
- `MakeModel(...)` does not assign `Compatibility` or provider-specific `StoragePolicy` from `CompatibilityDetector`.

`Model.GetEffectiveCompatibility()` can return default compatibility, but the model catalog does not currently classify discovered models through the new detector or attach compatibility overrides.

Impact:

Plan 2's “model discovery can mark OpenAI/OpenCode/OpenRouter models with intended API family and compatibility” acceptance criterion is not fully met.

Recommendation:

Centralize catalog classification through `CompatibilityDetector` or a catalog service method:

```csharp
var apiType = CompatibilityDetector.ResolveApiType(provider, id, baseUrl);
var compat = CompatibilityDetector.ResolveCompatibility(apiType, provider);
var policy = CompatibilityDetector.ResolveStoragePolicy(apiType, provider);
```

Then set `Model.Compatibility` and `Model.StoragePolicy` during discovery/fallback seeding.

### 4. Responses request shape for tool results/function-call replay appears wire-incompatible

File:

- `Omicron.Core/Providers/ApiShape.cs` (`OpenAiResponsesShape.BuildInputItems`)

The current stateless replay shape emits tool results as:

```json
{
  "role": "user",
  "content": [
    {
      "type": "tool_result",
      "tool_call_id": "...",
      "content": "..."
    }
  ]
}
```

For OpenAI Responses, function-call outputs are normally represented as `function_call_output` items keyed by `call_id`, not chat-style user `tool_result` content. Function-call replay likely also needs to preserve the distinction between Responses item ID and `call_id`.

Impact:

Stateless full-context fallback with tools may fail against the real Responses API, even if text-only requests work.

Recommendation:

Add tests from real Responses API fixtures for:

- assistant function-call output items;
- tool/function-call output items;
- mixed text + function calls;
- second-turn stateless reconstruction after tool execution.

Then adjust `BuildInputItems(...)` to emit provider-correct Responses input item types.

### 5. Responses SSE parser does not correlate tool-call start/delta/done state

File:

- `Omicron.Core/Providers/ApiShape.cs` (`OpenAiResponsesShape.ParseSseChunk`)

The parser emits `ToolCallStart` on `response.output_item.added`, emits argument deltas, and emits `ToolCallEnd` from `response.function_call_arguments.done`. But it does not store item/call/name information in `toolCallAccumulators`.

Current `function_call_arguments.done` parsing assumes `name` and `call_id` are present on that event:

```csharp
var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : "";
var callId = root.TryGetProperty("call_id", out var callIdEl) ? callIdEl.GetString() : itemId;
```

Realistic streams may provide `name` / `call_id` on `response.output_item.added` and only `item_id` + `arguments` on the done event. In that case the resulting `ToolCallContent` may lose the function name or use `item_id` instead of `call_id`.

Recommendation:

Use accumulators keyed by output index or item ID for Responses as well. Store `item_id`, `call_id`, `name`, and argument chunks from start/delta/done events.

## Medium-Priority Findings

### 6. `Model.StoragePolicy` default is stateful for every model

File:

- `Omicron.Core/Models/Model.cs`

`Model.StoragePolicy` defaults to:

```csharp
ProviderStoragePolicy.AllowProviderStateNoStore
```

But `CompatibilityDetector.ResolveStoragePolicy(...)` says non-Responses APIs should default to `PreferStateless`.

This is mostly harmless while storage policy is unused, but it is misleading and may create incorrect defaults when runtime state plumbing is added.

Recommendation:

Either:

- default `Model.StoragePolicy` to `PreferStateless`; or
- ensure all model construction goes through compatibility detection and explicitly sets the policy.

### 7. `OpenAiProvider` duplicates `ShapeBasedProvider` logic

File:

- `Omicron.Core/Providers/OpenAiProvider.cs`

`OpenAiProvider` now has its own copy of the HTTP/SSE loop even though `ShapeBasedProvider` already provides the same shape-routing abstraction and now supports `/responses` routing.

Recommendation:

Make `OpenAiProvider : ShapeBasedProvider` like OpenRouter/OpenCode unless there is a specific reason to keep a duplicate path. This reduces future drift as request context/stateful behavior is added.

### 8. Canonical conversation conversion invents empty provider origin metadata

File:

- `Omicron.Core/Models/Conversation.cs`

`ConversationConverter.ToCanonical(...)` creates this for all assistant messages:

```csharp
new ProviderOrigin("", ApiType.OpenAiChat, "", null)
```

This can make unknown origin look like known OpenAI Chat origin.

Recommendation:

Use `null` until real provider origin is available, or include origin only when supplied by provider/session metadata.

### 9. `ProviderCompatibility` exists but is not used in request builders

Files:

- `Omicron.Core/Providers/ApiShape.cs`
- `Omicron.Core/Models/Model.cs`

Fields such as `MaxTokensField`, `SupportsReasoningEffort`, `SupportsStore`, `RejectsEmptyContent`, `RequiresToolResultName`, and `ToolCallIdFormat` are not currently applied by request builders.

This is acceptable as scaffolding, but Plan 2 should not be marked complete until at least the fields needed by OpenAI Responses and existing Chat/Anthropic compatibility are actually used or explicitly deferred.

## Suggested Next Pass

Before marking Plan 2 complete, do a focused integration pass:

1. Preserve `ProviderTurnState.StoragePolicy` in the store.
2. Add provider runtime context/state plumbing.
3. Wire `AgentSession` → `ProviderStateManager` → provider request → response ID persistence.
4. Make `OpenAiResponsesShape.BuildStatefulRequestBody(...)` reachable in the live path.
5. Centralize model classification/storage policy assignment through `CompatibilityDetector`.
6. Replace Responses tool replay/parser tests with or add real fixture-shaped tests.
7. Consider refactoring `OpenAiProvider` to inherit `ShapeBasedProvider`.

## Recommendation

Do not treat Plan 2 as complete yet. The scaffolding is valuable and the tests are passing, but first-class stateful OpenAI Responses support is not integrated into the actual agent runtime.

Plan 2 should continue with the integration items above before moving on to later RFC work.
