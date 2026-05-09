# Code Review 0020: OpenRouter Responses Stateless Default

Date: 2026-05-08

## Issue Observed

Testing `openai/gpt-5.4-mini` through OpenRouter `/responses` produced a provider error after a tool call:

```text
No tool call found for function call output with call_id ...
```

The request path was using `previous_response_id` continuation. OpenRouter routed the model to an Azure-backed provider, and the routed backend did not match the `function_call_output` to the prior tool call in provider-managed state.

## Interpretation

OpenRouter's `/responses` endpoint can be useful for testing Responses wire shape, but provider-managed state via `previous_response_id` is not reliable across its routed backends.

For OpenRouter Responses models, the safer default is:

- use `/responses`;
- do **not** use `previous_response_id`;
- do stateless full-context replay, including prior function calls and `function_call_output` items.

## Changes Made

Updated `Omicron.Core/Providers/CompatibilityDetector.cs`:

- OpenRouter known Responses-capable model IDs still classify as `OpenAiResponses`.
- `ResolveCompatibility(OpenAiResponses, "openrouter")` now returns:
  - `SupportsStore = false`;
  - `SupportsPreviousResponseId = false`.
- `ResolveStoragePolicy(OpenAiResponses, "openrouter")` now returns `PreferStateless`.
- Native OpenAI/OpenCode Responses keep `AllowProviderStateNoStore` by default.

Updated tests:

- `CompatibilityDetector_ResolveCompatibility_OpenRouterResponses_IsStatelessByDefault`
- `CompatibilityDetector_ResolveStoragePolicy_OpenRouterResponses_PreferStateless`
- `ModelCatalogService_SeedsOpenRouterGpt54MiniResponsesModel` now asserts:
  - `ApiType.OpenAiResponses`;
  - `SupportsPreviousResponseId == false`;
  - `StoragePolicy == PreferStateless`.

Updated `docs/rfcs/IMPLEMENTATION-BASELINE.md` to document this behavior.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 186

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Expected Behavior Now

`OpenAI: GPT-5.4 Mini (OR)` should still route to:

```text
https://openrouter.ai/api/v1/responses
```

but should not send `previous_response_id`. Tool follow-up turns should use stateless full-context replay instead of relying on OpenRouter provider-managed continuation state.
