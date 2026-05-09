# Code Review 0015: Plan 2 Second Follow-up Review

Date: 2026-05-08  
Scope: review fixes applied after Code Review 0014.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 173

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

The Code Review 0014 action items are mostly fixed:

- Provider-state persistence is now gated by `CompatibilityDetector.SupportsStatefulContinuation(...)`.
- `ChatOptions` no longer exposes `IProviderStateManager`; providers receive immutable state context only.
- Responses function-call parsing no longer uses `string.GetHashCode()`.
- Responses assistant tool calls are now emitted as top-level `function_call` items.
- `ModelCatalogService` now routes discovery API-type classification through `CompatibilityDetector`.
- `OpenAiResponsesShape` checks compatibility before sending `store` and `reasoning_effort`.
- `OpenAiProvider` now inherits `ShapeBasedProvider`.

This is a solid improvement. Remaining issues are narrower, but a few still matter before Plan 2 is called complete.

## High-Priority Findings

### 1. Responses parser keeps per-stream state inside the shape instance

File: `Omicron.Core/Providers/ApiShape.cs`

`OpenAiResponsesShape` now has mutable fields:

```csharp
private readonly Dictionary<string, int> _responsesItemIdMap = new();
private int _nextAccIndex;
```

The parser resets these fields when `toolCallAccumulators.Count == 0`:

```csharp
if (toolCallAccumulators.Count == 0)
{
    _responsesItemIdMap.Clear();
    _nextAccIndex = 0;
}
```

This is fragile because `OpenAiResponsesShape` instances are shared on provider instances. Concurrent streams through the same provider/shape can race and corrupt each other's item mappings. Even without concurrency, the reset condition can fire after one tool call completes and the accumulator dictionary becomes empty while the same response stream continues.

This contradicts the `IApiShape` documentation that shapes are stateless and per-stream state lives in the accumulator argument.

Recommendation:

Move all per-stream parser state out of the shape instance. Options:

1. Change the parser interface to create a per-stream parser object, e.g. `IStreamEventParser CreateStreamParser(...)`.
2. Extend the accumulator dictionary/state object to support string item IDs without shape-level fields.
3. Use a request-local parser state object inside `ShapeBasedProvider.StreamAsync()` and pass it to shape-specific parser APIs.

Do not keep mutable stream state on singleton/shared shape instances.

---

### 2. State persistence gating lacks negative tests

Files:

- `Omicron.Core/Sessions/AgentSession.cs`
- `Omicron.Core.Tests/ProviderStateTests.cs`

The positive test exists:

```text
AgentSession_PersistsResponseIdOnDone
```

But there are no tests proving that response IDs are **not** persisted for:

- `ApiType.OpenAiChat` with a chat completion ID;
- `ApiType.AnthropicMessages`;
- `ApiType.OpenAiResponses` with `ProviderStoragePolicy.PreferStateless`.

Given that this was a regression found in review, these negative tests should be added.

Recommendation:

Add tests that feed a fake provider response with `ResponseId` and assert `providerStateManager.Get(key)` is null and no `ProviderStateUpdatedEvent` is emitted for non-stateful cases.

---

### 3. OpenRouter classification can now produce `OpenAiResponses`, but `OpenRouterProvider` only registers Chat shape

Files:

- `Omicron.Core/Models/ModelCatalogService.cs`
- `Omicron.Core/Providers/CompatibilityDetector.cs`
- `Omicron.Core/Providers/OpenRouterProvider.cs`

`ModelCatalogService` now calls:

```csharp
var apiType = CompatibilityDetector.ResolveApiType("openrouter", e.Id);
```

`CompatibilityDetector.ResolveApiType(...)` treats provider `openrouter` like OpenAI and returns `OpenAiResponses` for GPT-5/o-series model IDs.

But `OpenRouterProvider` registers only:

```csharp
[ApiType.OpenAiChat] = new OpenAiChatShape()
```

Impact:

Discovered OpenRouter GPT-5/o-series models can become unusable at runtime with:

```text
No ApiShape registered for OpenAiResponses in provider 'OpenRouter'
```

Recommendation:

Either:

- register `OpenAiResponsesShape` in `OpenRouterProvider` if OpenRouter supports the Responses endpoint for those models; or
- intentionally force OpenRouter discovered models to `OpenAiChat` until Responses support is confirmed; or
- make `CompatibilityDetector.ResolveApiType("openrouter", ...)` return Chat by default and use config overrides for Responses.

Given the earlier OpenRouter issue and broad OpenRouter compatibility variability, defaulting OpenRouter to Chat is safer unless verified.

## Medium-Priority Findings

### 4. `OpenAiResponsesShape` may send `previous_response_id` even when compatibility says store/state is unsupported

File: `Omicron.Core/Providers/ApiShape.cs`

`BuildRequestBody()` checks `CompatibilityDetector.SupportsStatefulContinuation(ApiType, storagePolicy)`, but that helper only considers API type and storage policy. It does not consider provider compatibility, such as `compat.SupportsStore` or a future explicit `SupportsPreviousResponseId` flag.

For non-OpenAI Responses-compatible providers, `CompatibilityDetector.ResolveCompatibility(OpenAiResponses, provider)` may set `SupportsStore = false`, but stateful continuation can still be used if policy is `AllowProviderStateNoStore`.

This may be okay if `previous_response_id` works with `store:false`, but it should be explicit. Plan 2 should distinguish:

- supports `store` parameter;
- supports `previous_response_id` continuation;
- supports `store:false` + continuation.

Recommendation:

Add an explicit compatibility field before relying on Responses-compatible third-party providers:

```csharp
public bool SupportsPreviousResponseId { get; init; }
```

or document that `OpenAiResponses` implies previous-response support and tests cover the selected providers.

---

### 5. `OpenAiResponsesShape` comment says text emitted when no text

File: `Omicron.Core/Providers/ApiShape.cs`

Minor comment bug:

```csharp
// Emit text as a separate assistant message item only if there's no text
if (!string.IsNullOrEmpty(msg.Text))
```

Should say `only if there is text`.

## Recommendation

Do another small fix pass before calling Plan 2 complete:

1. Remove shape-instance parser state from `OpenAiResponsesShape`.
2. Add negative tests for provider-state persistence gating.
3. Fix OpenRouter API-type/shape mismatch.
4. Decide/document whether `previous_response_id` support is implied by `OpenAiResponses` or represented explicitly in `ProviderCompatibility`.

After those are resolved, Plan 2 will be close to ready for a final completion review.
