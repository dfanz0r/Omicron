# Code Review 0012: OpenRouter Free Model Pricing

Date: 2026-05-08

## Issue

The CLI was listing all discovered OpenRouter models as `free`. This made paid OpenRouter endpoints visible to users without an API key.

## Root Cause

`OpenRouterProvider.FetchModelsAsync()` attempted to parse `pricing.prompt` and `pricing.completion`, but the parsed values were never assigned to `promptCost` / `completionCost`.

Because the values stayed null and were later defaulted to `0`, every OpenRouter model appeared zero-cost.

Additionally, missing or unparseable pricing also defaulted to `0`, which is unsafe because unknown pricing should not imply free usage.

## Fix

Updated `Omicron.Core/Providers/OpenRouterProvider.cs`:

- parse OpenRouter price strings using `CultureInfo.InvariantCulture` and `NumberStyles.Float`;
- support numeric JSON prices as well as string prices;
- assign parsed values to `PromptCost` / `CompletionCost`;
- treat missing or unparseable pricing as `double.PositiveInfinity`, so unknown pricing is **not** marked free.

Updated `Omicron.Core.Tests/ModelCatalogTests.cs`:

- added a fake HTTP handler for OpenRouter model discovery;
- added a test proving only models with both prompt and completion price `0` are marked free;
- added a test proving scientific-notation pricing is parsed into entries.

Updated `docs/rfcs/IMPLEMENTATION-BASELINE.md` to document OpenRouter free-model behavior.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 78

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Remaining Notes

No Plan 2 changes are required for this fix. Plan 2 provider compatibility work can later make pricing/free-tier metadata more formal, but the current catalog behavior is now safe for the CLI.
