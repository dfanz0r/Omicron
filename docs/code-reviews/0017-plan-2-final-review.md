# Code Review 0017: Plan 2 Final Review

Date: 2026-05-08  
Scope: verify fixes applied after Code Review 0016 and assess Plan 2 readiness.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 176

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Verification Summary

The two Code Review 0016 issues were fixed in code:

- `AgentSession` provider-state persistence now checks both:
  - `CompatibilityDetector.SupportsStatefulContinuation(Model.ApiType, Model.StoragePolicy)`
  - `Model.GetEffectiveCompatibility().SupportsPreviousResponseId`
- `OpenAiResponsesShape.BuildRequestBody()` now checks `compat.SupportsPreviousResponseId` before using stateful `previous_response_id` mode.
- Responses accumulator index allocation now uses `Keys.Max() + 1` / `0` instead of `Count`, avoiding obvious key reuse after removals.

No blocking findings remain for Plan 2.

## Remaining Non-Blocking Notes

### 1. Missing regression tests for the last two fixes

The code changes are present, but I did not find dedicated tests for:

- `OpenAiResponses` model with `SupportsPreviousResponseId = false` does not send `previous_response_id`;
- `AgentSession` does not persist response IDs when `SupportsPreviousResponseId = false`;
- interleaved Responses tool calls do not overwrite an active accumulator after one earlier call is removed.

These are not blockers because the implementation is straightforward and all existing tests pass, but they would be useful regression tests if another polish pass happens.

### 2. Plan/docs should be marked complete after this review

`docs/implementation-plans/0002-provider-api-abstraction.md` should be updated from `Ready to start` to complete/in review status, and the implementation baseline should be refreshed to mention the new Plan 2 capabilities.

Suggested baseline additions:

- `ProviderCompatibility`, `ProviderStoragePolicy`, and compatibility detector.
- `ProviderTurnState.StoragePolicy` and stateful continuation helpers.
- Canonical conversation seed and tool-call ID mapper.
- OpenAI Responses request builder/parser and provider routing.
- Stateful Responses continuation through `AgentSession` / `ProviderStateManager`.
- OpenRouter remains Chat by default unless overridden later.

## Recommendation

Plan 2 is complete enough to mark implemented after updating the plan/baseline docs.

Future work should focus on:

- real-provider fixture/integration tests for OpenAI Responses tool calling;
- stateless fallback retry on rejected/expired `previous_response_id`;
- provider/model config overrides;
- exposing debug output for selected provider/model/API/storage policy/state mode.
