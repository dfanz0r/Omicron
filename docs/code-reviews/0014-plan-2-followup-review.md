# Code Review 0014: Plan 2 Follow-up Review

Date: 2026-05-08  
Scope: review fixes applied after Code Review 0013.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 173

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

The first round of Plan 2 fixes improved the implementation materially:

- `AgentSession` now constructs a `ProviderStateKey`, reads current state, passes provider-state context through `ChatOptions`, and persists response IDs from `Done` events.
- `InMemoryProviderConversationStateStore.Set()` and `ProviderStateManager.Set()` now preserve init-only fields via `with`.
- `ModelCatalogService.MakeModel()` assigns compatibility and storage policy.
- `Model.StoragePolicy` now defaults to `PreferStateless`.
- Responses tool-result replay now emits `function_call_output` items.
- Responses function-call SSE parsing now correlates start/delta/done using accumulated metadata.
- `ConversationConverter` no longer invents empty provider origins.

However, there are still a few important correctness issues before Plan 2 should be considered complete.

## High-Priority Findings

### 1. `AgentSession` persists response IDs for stateless/non-Responses APIs

File: `Omicron.Core/Sessions/AgentSession.cs`

`AgentSession` now persists any non-null `StreamEvent.Done.Delta` as provider state:

```csharp
if (responseId is not null)
{
    var newState = new ProviderTurnState(... responseId ...)
    {
        StoragePolicy = Model.StoragePolicy
    };
    _providerState.Set(newState, reason: "response_completed");
}
```

But `OpenAiChatShape` also sets `Delta` to the chat completion `id` on `Done`. That means Chat Completions and other stateless paths can now create provider continuation state even when `Model.StoragePolicy == PreferStateless` and `ApiType != OpenAiResponses`.

Impact:

- Stateless chat models emit misleading provider-state update events.
- Provider-state store may contain continuation state for APIs that cannot use it.
- Future fallback/debug logic could interpret chat completion IDs as usable `previous_response_id` values.

Recommendation:

Gate persistence with the same stateful-continuation policy used by Responses request building:

```csharp
var storagePolicy = Model.StoragePolicy;
var supportsStateful = CompatibilityDetector.SupportsStatefulContinuation(Model.ApiType, storagePolicy);

if (supportsStateful && responseId is not null)
{
    _providerState.Set(...);
}
```

Also add tests proving:

- Responses model persists response ID.
- OpenAI Chat model with a completion ID does **not** persist provider state.
- `PreferStateless` Responses model does **not** persist provider state.

---

### 2. `ChatOptions` exposes `IProviderStateManager` to providers, but providers should not own session-state mutation

Files:

- `Omicron.Core/Providers/IChatProvider.cs`
- `Omicron.Core/Sessions/AgentSession.cs`

`ChatOptions` now includes:

```csharp
public IProviderStateManager? ProviderStateManager { get; init; }
```

The XML comment says providers should call `Set()` when they receive a completed response ID. But `AgentSession` already persists response IDs from `Done` events.

This creates two competing ownership models:

1. provider mutates provider state directly;
2. session/runtime consumes stream events and mutates provider state.

The second model is cleaner and matches the Plan 1.5 event model: `AgentSession` coordinates runtime state, providers parse/stream wire events.

Impact:

- Future providers may double-write state.
- Providers gain a dependency on session lifecycle concerns.
- It becomes harder to test provider parsers as pure wire adapters.

Recommendation:

Remove `ProviderStateManager` from `ChatOptions`, or at least change the comment to say providers must not mutate it. Prefer passing only immutable request context into providers:

- `ProviderStateKey?`
- `CurrentProviderState?`
- `StoragePolicy?`

Then keep state persistence in `AgentSession` after `Done`.

---

### 3. Responses function-call accumulator uses `string.GetHashCode()` as a dictionary key

File: `Omicron.Core/Providers/ApiShape.cs`

`OpenAiResponsesShape.ParseSseChunk()` maps `item_id` to an `int` key using:

```csharp
var accKey = itemId.GetHashCode();
```

This is fragile:

- hash collisions can merge unrelated tool calls;
- string hash codes are randomized between processes;
- the `Dictionary<int, ToolCallAccumulator>` shape is inherited from Chat, but Responses naturally keys by `item_id` / `call_id` strings.

Impact:

A rare collision could corrupt tool-call arguments or names. More importantly, the parser contract is awkward for Responses and will get harder to extend.

Recommendation:

Introduce a Responses-specific stream parser state, or extend accumulator support to string keys. Minimal option:

```csharp
public sealed class ToolCallAccumulator
{
    public string? Id { get; set; }
    public string? ItemId { get; set; }
    public string Name { get; set; } = "";
    public StringBuilder Args { get; } = new();
}
```

and let `IApiShape` own parser state through a richer parser object rather than a shared `Dictionary<int,...>`.

If keeping the current interface temporarily, maintain a private deterministic mapping from item IDs to assigned integer slots, not `GetHashCode()`.

---

### 4. Responses stateless replay shape for assistant function calls still looks wire-incompatible

File: `Omicron.Core/Providers/ApiShape.cs`

The tool result was improved to top-level:

```json
{ "type": "function_call_output", "call_id": "...", "output": "..." }
```

But assistant tool calls are still emitted as a nested content item inside an assistant role message:

```json
{
  "role": "assistant",
  "content": [
    {
      "type": "function_call",
      "id": "...",
      "name": "...",
      "arguments": "..."
    }
  ]
}
```

For Responses-style item replay, function calls generally need to be represented as top-level output/function-call items with `call_id`, not nested inside assistant message content. The current representation may still fail for stateless full-history replay with tools.

Recommendation:

Use real Responses request fixtures and adjust `BuildInputItems(...)` so assistant function calls and function-call outputs are represented exactly as the API expects. Add tests that assert the complete full-context tool replay payload, not only the tool-result item.

## Medium-Priority Findings

### 5. Compatibility detection is still only partially centralized

File: `Omicron.Core/Models/ModelCatalogService.cs`

`MakeModel()` now assigns compatibility and storage policy, which is good. But API-type classification is still mostly done before `MakeModel()` and is still split across:

- hardcoded fallback models;
- `OpenCodeProvider.ResolveApiType(...)`;
- forced `OpenAiChat` for OpenCode Go;
- forced `OpenAiChat` for OpenRouter.

This may be intentional for OpenRouter/OpenCode Go, but it means `CompatibilityDetector.ResolveApiType(...)` is not the central classification authority yet.

Recommendation:

Either centralize API-type classification in `CompatibilityDetector`, or document which providers intentionally bypass it and why.

---

### 6. `OpenAiResponsesShape` sends unsupported optional fields without checking compatibility

File: `Omicron.Core/Providers/ApiShape.cs`

`BuildRequestBody()` always sends:

- `store`
- `reasoning_effort` when set

without consulting `model.GetEffectiveCompatibility()`.

For strict OpenAI this may be fine, but Plan 2 explicitly introduced compatibility descriptors to prevent this kind of provider-specific assumption.

Recommendation:

Use compatibility fields such as `SupportsStore` and `SupportsReasoningEffort` when building requests. For example:

```csharp
var compat = model.GetEffectiveCompatibility();
if (compat.SupportsStore)
    body["store"] = storagePolicy == ProviderStoragePolicy.AllowProviderStoredState;
```

If a field is intentionally always emitted for OpenAI only, assert that in tests.

---

### 7. `OpenAiProvider` still duplicates `ShapeBasedProvider`

File: `Omicron.Core/Providers/OpenAiProvider.cs`

This was not fixed. It is not a correctness blocker, but stateful request behavior now exists in two provider loops (`OpenAiProvider` and `ShapeBasedProvider` subclasses). Any future parser/request-context changes must be applied consistently.

Recommendation:

Refactor `OpenAiProvider` to inherit `ShapeBasedProvider` before adding more provider context/fallback behavior.

## Recommendation

The follow-up fixes moved Plan 2 closer to completion, but I would not mark it complete yet.

Required before completion:

1. Gate provider-state persistence to stateful Responses-compatible requests only.
2. Remove or de-authorize `ProviderStateManager` from provider-facing `ChatOptions`.
3. Replace Responses tool-call accumulator `GetHashCode()` usage.
4. Verify/fix full Responses stateless replay payloads for function calls using fixture-shaped tests.

After those are fixed, Plan 2 will be much closer to a reliable stateful/stateless Responses implementation.
