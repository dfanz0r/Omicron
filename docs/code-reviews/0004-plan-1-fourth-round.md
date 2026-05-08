# Code Review 0004: Plan 1 Fourth Round

Date: 2026-05-08  
Scope: fourth round review after actioning `0003-plan-1-third-round.md`.

## Validation

Build and test status at time of review:

```text
dotnet test Omicron.slnx --nologo
Passed: 69

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

More good progress. Several round-three items were addressed:

- `ModelCatalogService` was introduced and the CLI now consumes `host.ModelCatalog`.
- `IProviderRegistry` was introduced and `ProviderFactory` implements it.
- `OmicronHost.Providers` now exposes `IProviderRegistry` instead of the concrete `ProviderFactory`.
- `ContinueAsync()` now rejects use before the session has started.
- Reset now uses same-session policy and no longer resets `_sessionStarted`.
- `ExecutionRequest` now carries optional `SessionId` and `ToolCallId`.
- `LocalExecutionBroker` emits execution start/completion events when constructed with an event sink.
- The incorrect `Environment.SpecialFolder.ProgramFiles.ToString()` path was removed from `LocalExecutionBroker`.
- `AgentSession` unused workspace/execution fields were removed.

This puts the codebase significantly closer to Plan 1's desired shape.

The main remaining architectural concern is event sequencing/ownership now that event-producing services besides `AgentSession` can write into the same event log. There are also some model catalog correctness and test coverage gaps.

## High-Priority Findings

### 1. Event sequences are no longer coherent once services emit directly to the event sink

Files:

- `Omicron.Core/Sessions/AgentSession.cs`
- `Omicron.Core/Execution/IExecutionBroker.cs`
- `Omicron.Core/Sessions/ProviderState.cs`

`AgentSession` emits events with a per-session `_sequence`:

```csharp
private long _sequence;
private long NextSequence() => Interlocked.Increment(ref _sequence);
```

But `LocalExecutionBroker` now emits events using its own `_sequence`:

```csharp
_eventSink?.Emit(new ExecutionStartedEvent(
    EventId.New(), Interlocked.Increment(ref _sequence), ...));
```

And `InMemoryProviderConversationStateStore` emits provider-state events with sequence `0`:

```csharp
_eventSink?.Emit(new ProviderStateUpdatedEvent(
    EventId.New(), 0, ...));
```

So a single session event log can contain duplicate, zero, or out-of-order sequence numbers.

This undermines the durable/replayable event stream goal from Plan 1.

Recommendation:

Choose one sequence ownership model before Plan 2:

1. **Session-owned event writer.** All services emit through a session-scoped `ISessionEventWriter` that assigns sequence numbers.
2. **Event sink assigns sequence numbers.** Producers provide event payloads, and the sink stamps sequence/order.
3. **Separate global and session sequence fields.** More complex; probably unnecessary now.

Preferred short-term direction: introduce a session event writer/factory that owns sequence assignment and pass it into tool/execution/provider-state contexts. Avoid allowing arbitrary services to stamp their own session event sequence.

---

### 2. Execution completion events are not emitted for early failure paths

File: `Omicron.Core/Execution/IExecutionBroker.cs`

`LocalExecutionBroker` emits `ExecutionStartedEvent` before shell lookup, but returns early without `ExecutionCompletedEvent` for cases like:

- unknown shell;
- shell executable not found;
- process start exception/catch path.

This creates incomplete execution lifecycles in the event log.

Recommendation:

Ensure every started execution produces exactly one completion event, including validation failures and exceptions. Use a single exit path or helper:

```csharp
private ExecutionResult Complete(...)
{
    _eventSink?.Emit(new ExecutionCompletedEvent(...));
    return result;
}
```

Also consider including failure/error in `ExecutionCompletedEvent` or adding a separate execution error field/event.

---

### 3. Provider-state events are emitted by the raw store with sequence `0`

File: `Omicron.Core/Sessions/ProviderState.cs`

It is good that provider-state events are now emitted, but the raw store is probably the wrong layer to emit durable session events because it has no session sequence context.

Current output:

```csharp
new ProviderStateUpdatedEvent(EventId.New(), 0, ...)
```

Recommendation:

Do not emit durable events directly from the raw store unless the store has an event writer capable of assigning correct session sequence numbers.

Better options:

- introduce `ProviderStateManager` that wraps the store and session event writer;
- keep the store dumb and let `AgentSession`/provider pipeline emit provider-state events;
- defer provider-state events to Plan 2 and remove sequence-0 emissions for now.

Sequence `0` should not appear in normal session event logs.

---

### 4. OpenRouter/free model visibility appears broken in `ModelCatalogService`

File: `Omicron.Core/Models/ModelCatalogService.cs`

`AddDiscovered()` marks free models using the catalog key:

```csharp
var key = $"{prefix}:{id}";
...
if (isFree)
    _freeModelKeys.Add(key);
```

But `IsFreeModel()` checks:

```csharp
_freeModelKeys.Contains(m.Id) ||
_freeModelKeys.Contains($"{m.ProviderName}:{m.Id}")
```

For OpenRouter, the catalog key is `or:{id}`, while `ProviderName` is `openrouter`, so `IsFreeModel()` will not recognize `or:{id}` as free. Free OpenRouter models may be hidden unless the user has an OpenRouter API key.

Recommendation:

Store a canonical free identity, e.g.:

```csharp
_freeModelKeys.Add(id);
_freeModelKeys.Add($"{provider}:{id}");
```

or change `IsFreeModel` to accept the catalog key:

```csharp
bool IsFreeModel(string catalogKey, Model model)
```

Then update CLI calls accordingly.

This should have a test because free model visibility affects onboarding.

## Medium-Priority Findings

### 5. Model catalog is a concrete host property, not an interface seam

Files:

- `Omicron.Core/OmicronHost.cs`
- `Omicron.Core/Models/ModelCatalogService.cs`

`IProviderRegistry` was added, which is good. The model catalog is still exposed as concrete `ModelCatalogService`:

```csharp
public ModelCatalogService ModelCatalog { get; }
```

Plan 1 called for an `IModelCatalog` or equivalent. This is less urgent than provider registry, but the catalog will become important in Plan 2 because API classification and compatibility overrides should live there.

Recommendation:

Introduce an interface before the catalog grows further:

```csharp
public interface IModelCatalog
{
    IReadOnlyDictionary<string, Model> Models { get; }
    IReadOnlySet<string> FreeModelKeys { get; }
    Task<int> DiscoverAsync(bool quiet = false);
    bool IsFreeModel(Model model);
    void ResolveProviders();
}
```

or revise with `IsFreeModel(string key, Model model)` as above.

---

### 6. Provider-specific discovery logic lives in core model catalog

File: `Omicron.Core/Models/ModelCatalogService.cs`

The model catalog now directly knows about:

- `OpenCodeProvider`;
- `OpenRouterProvider`;
- OpenCode base URL rules;
- OpenRouter pricing/free logic.

This is better than living in `Program.cs`, but it still means the central catalog must be edited for each provider's discovery behavior.

Recommendation:

For Plan 1 this is acceptable as a migration step. Before Plan 2 gets deep, consider a provider discovery interface:

```csharp
public interface IModelDiscoveryProvider
{
    Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(CancellationToken ct);
}
```

Providers or provider extensions can implement it, and the catalog just aggregates.

This will align better with extension-based provider registration and per-model API classification.

---

### 7. Execution events do not include `ToolCallId`

Files:

- `Omicron.Core/Execution/IExecutionBroker.cs`
- `Omicron.Core/Events/OmicronEvent.cs`

`ExecutionRequest` now carries `ToolCallId`, but `ExecutionStartedEvent` and `ExecutionCompletedEvent` do not include it. That loses the correlation between:

- model tool call;
- shell execution;
- execution audit event;
- tool result.

Recommendation:

Add `ToolCallId?` to execution events, or add a more general `CorrelationId`/`OperationId`.

This is important for future sandbox/audit UI.

---

### 8. Execution event sequence is broker-global, not session-local

File: `Omicron.Core/Execution/IExecutionBroker.cs`

Even if sequence conflicts are fixed later, note that the current `_sequence` in `LocalExecutionBroker` is broker-global. If two sessions execute shell commands, execution events for each session will receive values from the same broker counter, not the session counter.

Recommendation: same as finding #1 — use session-scoped event writer or sink-owned sequencing.

---

### 9. CLI still owns API key environment variable mapping

File: `Omicron.CLI/Program.cs`

The CLI still maps provider names to env vars:

```csharp
"openai" => "OPENAI_API_KEY"
"anthropic" => "ANTHROPIC_API_KEY"
...
```

This is frontend orchestration that will matter for provider extensions and custom providers.

Recommendation:

Not urgent for Plan 1, but provider metadata should eventually expose credential environment hints/defaults. For now, document as deferred.

---

### 10. Tests do not cover the newest fixes

File: `Omicron.Core.Tests/ArchitectureTests.cs`

Test count increased to 69, but there do not appear to be tests for:

- `ContinueAsync()` before first prompt throws;
- reset does not re-emit `SessionStartedEvent` for the same session;
- provider-state events are emitted;
- execution events are emitted;
- execution completion emits on failure;
- free model detection with catalog prefix vs provider name;
- model catalog discovery/provider resolution behavior.

Recommendation:

Add focused tests for the above before considering Plan 1 complete. The event lifecycle tests are especially important.

## Lower-Priority Cleanup

### 11. Architecture test formatting remains uneven

File: `Omicron.Core.Tests/ArchitectureTests.cs`

The bottom session tests still have inconsistent indentation/bracing. This has been noted before. It compiles, but the file is becoming hard to review.

Recommendation: run formatter or manually clean up the file soon.

---

### 12. Old `ShellTools` still contains the bad Program Files path

File: `Omicron.Core/Tools/ShellTools.cs`

The active `LocalExecutionBroker` fixed the bad path, but the old `ShellTools` still contains:

```csharp
Environment.SpecialFolder.ProgramFiles.ToString()
```

If `ShellTools` is now legacy, consider marking it obsolete or removing it once behavior parity is sufficient. If it remains usable, fix the path there too.

---

### 13. Shell quoting still needs tests

File: `Omicron.Core/Execution/IExecutionBroker.cs`

The command escaping remains shell-generic:

```csharp
command.Replace("\\", "\\\\").Replace("\"", "\\\"")
```

This is probably fragile across `cmd`, PowerShell, bash, and fish.

Recommendation: add tests for quotes/backslashes/special characters per available shell, or defer with an explicit issue before expanding shell authority.

## Plan 1 Alignment Check

### Now aligned or mostly aligned

- CLI uses `OmicronHost`, `AgentSession`, and `ModelCatalogService`.
- Provider registry has an interface seam.
- Model discovery has moved out of `Program.cs`.
- Built-in tools are loaded through extensions.
- Workspace and execution tools route through core seams.
- Permission, session error, turn start, execution, and provider-state event types exist.
- Reset now follows same-session policy.
- `ContinueAsync()` no longer silently starts an event stream without session start.

### Still incomplete/risky for Plan 1

- Event sequence ownership is inconsistent across session, execution broker, and provider-state store.
- Provider-state events are sequence `0` and emitted from the raw store.
- Execution event lifecycle is incomplete on early failures.
- Free-model detection in the model catalog likely has a key mismatch bug.
- New lifecycle behavior lacks tests.
- Model catalog is concrete and provider-discovery logic is centralized rather than provider-extensible.

## Recommended Next Fixes

Suggested order:

1. Fix event sequence ownership before adding more event-producing services.
2. Stop emitting sequence-0 provider-state events; use a session event writer/manager or defer those events.
3. Ensure every `ExecutionStartedEvent` has exactly one matching completion event.
4. Fix free-model detection in `ModelCatalogService` and add a test.
5. Add tests for `ContinueAsync()` pre-start and reset/session-start lifecycle.
6. Add tests for execution events and provider-state events if they remain in Plan 1.
7. Add `ToolCallId?` correlation to execution events.
8. Introduce `IModelCatalog` or document why concrete catalog is acceptable until Plan 2.
9. Clean up architecture test formatting.

Overall, the implementation is converging well on Plan 1. The remaining high-impact item is making the event log's ordering and lifecycle semantics trustworthy now that multiple services can emit into it.
