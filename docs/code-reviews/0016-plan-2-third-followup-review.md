# Code Review 0016: Plan 2 Third Follow-up Review

Date: 2026-05-08  
Scope: review fixes applied after Code Review 0015.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 176

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

The latest follow-up addressed the prior review items well:

- `OpenAiResponsesShape` no longer stores `_responsesItemIdMap` / `_nextAccIndex` on the shared shape instance.
- Negative provider-state persistence tests were added for Chat, Anthropic, and Responses + `PreferStateless`.
- OpenRouter now defaults to `OpenAiChat` classification, avoiding the OpenRouter Responses shape mismatch.
- `ProviderCompatibility.SupportsPreviousResponseId` was added.
- The comment typo was fixed.

There are no broad architecture blockers remaining, but I found two correctness details worth fixing before marking Plan 2 complete.

## Findings

### 1. `SupportsPreviousResponseId` was added but is not used for stateful gating

Files:

- `Omicron.Core/Providers/ApiShape.cs`
- `Omicron.Core/Sessions/AgentSession.cs`
- `Omicron.Core/Providers/CompatibilityDetector.cs`

`ProviderCompatibility.SupportsPreviousResponseId` now exists, but the actual stateful gates still use only:

```csharp
CompatibilityDetector.SupportsStatefulContinuation(Model.ApiType, Model.StoragePolicy)
```

and in `OpenAiResponsesShape`:

```csharp
CompatibilityDetector.SupportsStatefulContinuation(ApiType, storagePolicy)
```

That helper considers only API type and storage policy. It does not check `model.GetEffectiveCompatibility().SupportsPreviousResponseId`.

Impact:

A model/provider can explicitly set `SupportsPreviousResponseId = false`, but `AgentSession` may still persist response IDs and `OpenAiResponsesShape` may still send `previous_response_id` as long as the model's `ApiType` is `OpenAiResponses` and policy is not `PreferStateless`.

Recommendation:

Gate stateful behavior with both policy and compatibility:

```csharp
var compat = model.GetEffectiveCompatibility();
var supportsStateful = compat.SupportsPreviousResponseId &&
    CompatibilityDetector.SupportsStatefulContinuation(model.ApiType, storagePolicy);
```

Apply this in:

- `AgentSession` before persisting response IDs;
- `OpenAiResponsesShape.BuildRequestBody()` before sending `previous_response_id` and latest-turn-only input.

Add a test where an `OpenAiResponses` model has `Compatibility = ProviderCompatibility.OpenAiResponses with { SupportsPreviousResponseId = false }` and verify:

- request body does not include `previous_response_id`;
- AgentSession does not persist response ID.

---

### 2. Responses accumulator key allocation can reuse an active dictionary key

File: `Omicron.Core/Providers/ApiShape.cs`

`response.output_item.added` assigns the accumulator index using:

```csharp
var accIndex = toolCallAccumulators.Count;
toolCallAccumulators[accIndex] = ...;
```

This works while calls are only added before any are removed. But if one function call completes and is removed while another remains active, `Count` can equal an already-used key.

Example:

1. add item A → key `0`
2. add item B → key `1`
3. done item A → remove key `0`; dictionary count is now `1`
4. add item C → chooses key `1`, overwriting item B

This is plausible in interleaved/multi-tool streams.

Recommendation:

Use the next unused integer key derived from existing keys:

```csharp
var accIndex = toolCallAccumulators.Count == 0
    ? 0
    : toolCallAccumulators.Keys.Max() + 1;
```

or, preferably, change parser state to a string-keyed structure so Responses can use `item_id` directly.

Add a parser unit test that simulates add A, add B, done A, add C, then done B and C, verifying B is not overwritten.

## Recommendation

Do one small final fix pass for these two items. After that, Plan 2 should be ready for final review/completion.
