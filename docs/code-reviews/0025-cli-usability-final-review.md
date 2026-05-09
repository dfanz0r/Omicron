# Code Review 0025: CLI Usability Final Review

Date: 2026-05-08

## Scope

Reviewed recent CLI usability changes:

- slash-command dispatcher;
- direct startup into chat without initial model picker;
- `/models` and `/model <n|key>` model switching;
- tab completion;
- bare `/` crash fix;
- OpenRouter Responses test model and stateless defaults.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 187

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Findings and Fixes Applied During Review

### 1. Environment API keys were not considered by visible model filtering

`Program.cs` startup model selection and slash-command model completion/listing originally filtered visible models using saved config keys only:

```csharp
cfg.ApiKeys.ContainsKey(provider)
```

But `ResolveApiKey(...)` can also resolve environment variables such as `OPENROUTER_API_KEY`. This meant users with env-var keys could still have models hidden from startup selection and `/models`.

Fixed:

- `Program.cs` visible model selection now treats a model as visible if it is free or `ResolveApiKey(model)` is non-null.
- `SlashCommandDispatcher` now checks saved config keys, free model status, or provider env-vars when listing/completing models.

## Remaining Notes

No blocking findings remain.

Potential future polish:

- Add `/config` and `/refresh` now that the initial model menu is gone.
- Add CLI-specific tests or move parser-only slash-command logic to a testable library.
- Add completion for display names in addition to catalog keys.
- Consider making `/exit` and `/quit` both explicit in help as app exits, since there is no longer a picker layer.

## Manual Test Checklist

Recommended manual checks:

```text
/
/help
<Tab>
/st<Tab>
/models
/model 1
/model or:openai/gpt-5.4-mini
/status
/provider-state
/events 30
/reset
/exit
```

For OpenRouter Responses:

```text
/model or:openai/gpt-5.4-mini
/status        # should show OpenAiResponses + PreferStateless + previous_response_id false
```
