# Code Review 0006: Plan 1 Sixth Round

Date: 2026-05-08  
Scope: sixth round review after actioning `0005-plan-1-fifth-round.md`.

## Validation

Build and test status at time of review:

```text
dotnet test Omicron.slnx --nologo
Passed: 77

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

This round addressed nearly all concrete fifth-round findings:

- `ContinueAsync()` now rejects both pre-start and post-reset/empty-history cases.
- `ExecutionCompletedEvent.Timestamp` now uses completion time.
- `LocalExecutionBroker` no longer emits events under synthetic random session IDs; events are suppressed if `SessionId` is not supplied.
- `ExecutionCompletedEvent` now carries `Cancelled` and `Error` fields.
- Execution broker tests are platform-aware (`cmd` on Windows, `sh` on Unix).
- A monotonic event sequence test was added.
- `Agent` is documented as legacy compatibility.
- `ProviderState` store XML docs clearly call out raw-store event emission as MVP-only and earmark a Plan 2 `ProviderStateManager`.

At this point, Plan 1 is broadly in good shape as a foundation. Remaining issues are mostly architectural polish and pre-Plan-2 hardening rather than blockers for continuing.

## Remaining Findings

### 1. Provider-state events are still emitted from the raw store

File: `Omicron.Core/Sessions/ProviderState.cs`

This was documented as MVP-only, which is acceptable for Plan 1. The remaining risk is that Plan 2 may accidentally build on the raw store event behavior instead of introducing a richer provider-state manager.

Recommendation:

Before starting serious Plan 2 provider work, create a small task/checkpoint:

```text
Introduce ProviderStateManager or provider-session state service before first-class Responses implementation.
```

This should own provider-state events and sequence/context enrichment. The raw store should become persistence-only later.

Status: acceptable for Plan 1, explicit Plan 2 handoff item.

---

### 2. Event sequencing is global, not per-session

File: `Omicron.Core/Events/IEventSink.cs`

The sink now stamps a global monotonic sequence. This fixes the previous inconsistency. However, the RFC language often refers to session event sequence/replay. A global sequence is fine as an MVP, but if persisted events are scoped per session later, the distinction should be explicit.

Recommendation:

Document current semantics in the implementation baseline or Plan 1 completion notes:

```text
Current event Sequence is global per event sink, not per session. Session-filtered streams preserve global ordering. Persistence work may introduce per-session sequence if needed.
```

Status: acceptable for Plan 1.

---

### 3. Model catalog discovery remains centralized and provider-specific

File: `Omicron.Core/Models/ModelCatalogService.cs`

The catalog directly knows about `OpenCodeProvider` and `OpenRouterProvider`. This is a reasonable migration from `Program.cs`, but it is not the final extension-oriented architecture.

Recommendation:

Defer to Plan 2 or an early provider follow-up:

```csharp
public interface IModelDiscoveryProvider
{
    Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(CancellationToken ct);
}
```

Provider extensions can eventually contribute discovery/classification rules.

Status: acceptable for Plan 1, Plan 2 handoff item.

---

### 4. CLI still owns provider credential environment-variable mapping

File: `Omicron.CLI/Program.cs`

The mapping from provider name to env var still lives in the CLI. This is not a blocker for Plan 1, but it should eventually move into provider metadata/compatibility descriptors.

Recommendation:

Defer to Plan 2 provider metadata work. Add it to provider abstraction tasks if not already captured.

Status: acceptable for Plan 1.

---

### 5. Shell quoting remains generic and fragile

File: `Omicron.Core/Execution/IExecutionBroker.cs`

Escaping is still generic:

```csharp
command.Replace("\\", "\\\\").Replace("\"", "\\\"")
```

This is a known shell-specific correctness issue. It is probably okay for MVP, but it should be treated as a risk before expanding shell authority or sandboxing.

Recommendation:

Add a future test suite for shell quoting across available shells, or move to shell-specific quoting. This can happen during execution broker/sandbox hardening.

Status: acceptable for Plan 1 if documented as deferred.

---

### 6. Tests still have formatting/readability issues

File: `Omicron.Core.Tests/ArchitectureTests.cs`

The file compiles and tests pass, but indentation/bracing near the later session tests remains uneven. This is not a functional issue, but the file is now large and will be harder to maintain.

Recommendation:

Run formatter or split architecture tests by subsystem soon:

```text
EventTests.cs
ProviderStateTests.cs
AgentSessionTests.cs
ModelCatalogTests.cs
ExecutionBrokerTests.cs
ExtensionRegistryTests.cs
```

Status: cleanup item, not Plan 1 blocker.

---

### 7. Legacy `Agent` remains active code

File: `Omicron.Core/Agent/Agent.cs`

The class is now documented as legacy compatibility, which is good. Longer term, dual agent loops can diverge.

Recommendation:

Before Plan 2 is complete, decide whether to:

- remove `Agent`;
- make it a thin wrapper over `AgentSession`;
- or keep it with tests proving behavior parity.

Status: acceptable for Plan 1.

## Plan 1 Alignment Check

### Aligned

- CLI uses `OmicronHost`, `AgentSession`, and `IModelCatalog`.
- Provider registry and model catalog have interface seams.
- Model discovery moved out of `Program.cs`.
- Built-in tools are loaded through extensions.
- Workspace and execution tools route through core seams.
- Permission, turn, session error, execution, and provider-state event types exist.
- Event sink stamps global monotonic sequence numbers.
- Execution events include `ToolCallId`, cancellation, and error details.
- Execution events are not emitted under synthetic session IDs.
- Reset follows same-session policy.
- `ContinueAsync()` pre-start and post-reset/empty-history behavior is guarded.
- Free model detection bug is addressed.
- `Agent` is documented as legacy.

### Deferred / acceptable for Plan 1

- Provider-state events are raw-store-emitted for now; Plan 2 should replace with manager/service.
- Model discovery is centralized and provider-specific; Plan 2 should make discovery provider-extensible.
- Credential env-var mapping remains in CLI; Plan 2 provider metadata should absorb it.
- Shell escaping and deeper execution broker correctness remain future hardening work.
- Tests need organization/format cleanup.

## Recommendation

Plan 1 appears close enough to call the core groundwork implementation substantially complete, assuming the team accepts the listed deferrals.

Before moving into Plan 2, I recommend doing a short documentation/update pass:

1. Update `docs/rfcs/IMPLEMENTATION-BASELINE.md` with the new architecture state.
2. Update `docs/implementation-plans/0001-core-architecture-groundwork.md` status/checklist to mark completed vs deferred items.
3. Add explicit Plan 2 handoff notes for:
   - provider state manager;
   - provider-extensible model discovery;
   - provider credential metadata;
   - canonical conversation/provider compatibility work.
4. Optionally split/format `ArchitectureTests.cs` to reduce review friction.

No new high-priority code blockers were found in this round.
