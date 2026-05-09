# Code Review 0018: OpenRouter Responses Integration

Date: 2026-05-08  
Scope: integrate and verify `https://openrouter.ai/api/v1/responses` support.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 182

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Changes Made

### OpenRouter provider supports Responses shape

Updated `Omicron.Core/Providers/OpenRouterProvider.cs`:

- registered `OpenAiResponsesShape` alongside `OpenAiChatShape`;
- updated provider docs to state that Chat remains default, while Responses-capable models use `/responses`.

### OpenRouter model classification supports Responses-capable families

Updated `Omicron.Core/Providers/CompatibilityDetector.cs`:

- OpenRouter still defaults to `OpenAiChat` for broad compatibility;
- known Responses-capable model IDs/families now classify as `OpenAiResponses`;
- `/responses` base URL still forces `OpenAiResponses`.

### Tests added

Updated `Omicron.Core.Tests/CompatibilityDetectorTests.cs`:

- `openrouter` + `gpt-5.5` => `OpenAiResponses`;
- `openrouter` + `o3-mini` => `OpenAiResponses`;
- `openrouter` + `gpt-4o` remains `OpenAiChat`.

Updated `Omicron.Core.Tests/ProviderTests.cs`:

- OpenRouter Responses model routes to `https://openrouter.ai/api/v1/responses`;
- OpenRouter Chat model routes to `https://openrouter.ai/api/v1/chat/completions`.

### Docs updated

Updated `docs/rfcs/IMPLEMENTATION-BASELINE.md`:

- OpenAI Responses is now documented as first-class for providers registering the shape;
- OpenRouter Responses endpoint routing is documented for known Responses-capable model families.

## Review Notes

This keeps the safe OpenRouter behavior from previous reviews:

- generic OpenRouter models remain Chat by default;
- only known Responses-capable families route to `/responses`;
- OpenRouter pricing/free detection remains unchanged and conservative.

No blocking issues found in this integration pass.
