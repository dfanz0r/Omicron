# Code Review 0019: OpenRouter GPT-5.4 Mini Responses Test Option

Date: 2026-05-08

## Purpose

Add `gpt-5.4-mini` as an OpenRouter Responses API test option so the CLI can exercise:

```text
https://openrouter.ai/api/v1/responses
```

## Changes Made

### Fallback catalog seed

Updated `Omicron.Core/Models/ModelCatalogService.cs`:

- added fallback model key `or:openai/gpt-5.4-mini`;
- model ID: `openai/gpt-5.4-mini`;
- provider: `openrouter`;
- API type: `OpenAiResponses`;
- base URL: `https://openrouter.ai/api/v1`;
- not marked free, so it appears in the CLI when an OpenRouter API key is configured.

### OpenRouter prefixed model detection

Updated `Omicron.Core/Providers/CompatibilityDetector.cs`:

- Responses model detection now handles provider-prefixed IDs such as `openai/gpt-5.4-mini` by checking the suffix after the final `/`.

### Tests added

Updated `Omicron.Core.Tests/CompatibilityDetectorTests.cs`:

- `openrouter` + `openai/gpt-5.4-mini` resolves to `OpenAiResponses`.

Updated `Omicron.Core.Tests/ModelCatalogTests.cs`:

- fallback catalog includes `or:openai/gpt-5.4-mini`;
- it is classified as `OpenAiResponses`;
- it supports previous response IDs;
- it is not marked free.

### Docs updated

Updated `docs/rfcs/IMPLEMENTATION-BASELINE.md` with the new OpenRouter Responses test model.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 184

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Usage Note

Configure an OpenRouter API key, then select:

```text
OpenAI: GPT-5.4 Mini (OR)
```

This should route through the OpenRouter `/responses` endpoint.
