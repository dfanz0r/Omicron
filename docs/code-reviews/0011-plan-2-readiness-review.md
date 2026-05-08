# Code Review 0011: Plan 2 Readiness Review

Date: 2026-05-08  
Scope: final check before starting `docs/implementation-plans/0002-provider-api-abstraction.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 76

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Result

Plan 1.5 cleanup is complete and Plan 2 is ready to start.

Confirmed in code:

- `ProviderStateManager.Clear()` now uses a local normalized key and no longer calls `Normalize()` twice.
- `InMemoryProviderConversationStateStore.ClearSession()` returns only successfully removed keys.
- Provider-state clear-session behavior has direct tests.
- Baseline documentation reflects sink-only reset behavior and current provider-state store contracts.

## Plan 2 Documentation Updates Made

Updated `docs/implementation-plans/0002-provider-api-abstraction.md` so it matches the current Plan 1.5 baseline:

- Status changed to `Ready to start`.
- Inputs now include `IMPLEMENTATION-BASELINE.md` and Plan 0001.5.
- Prerequisite text now names `AgentSession`, `IProviderStateManager`, session-scoped execution, and authoritative `IEventSink`.
- Phase A now validates the Plan 1.5 seams instead of the older raw-store-only seam.
- Phase C now says to extend existing provider-state events only as needed because update/clear events and `Reason` already exist.
- Responses completion state is now explicitly stored through `ProviderStateManager`.
- Debuggability now points at the authoritative event/debug path.
- `ProviderTurnState` sample now reflects the current Plan 1.5 shape and marks `ProviderStoragePolicy` as an additive Plan 2 field.

Also marked `docs/implementation-plans/0001.5-pre-provider-cleanup.md` as `Complete`.

## Remaining Findings

No required changes before Plan 2.

## Recommendation

Start Plan 2 with Phase A, but treat it as a quick verification/scaffolding phase rather than a large implementation step. The main implementation work can then proceed into:

1. `ProviderCompatibility` + `ProviderStoragePolicy`;
2. provider request context/state plumbing;
3. canonical conversation adapter seed;
4. real OpenAI Responses request/stream support;
5. stateful/stateless fallback tests.
