# Code Review 0005: Plan 1 Fifth Round

Date: 2026-05-08  
Scope: fifth round review after actioning `0004-plan-1-fourth-round.md`.

## Validation

Build and test status at time of review:

```text
dotnet test Omicron.slnx --nologo
Passed: 75

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

This round addressed several of the largest remaining Plan 1 issues:

- `IEventSink.Emit(...)` now stamps a global monotonic sequence and returns the stamped event.
- `AgentSession` now emits events with `Sequence = 0` and returns the stamped copy from the sink.
- `LocalExecutionBroker` now emits through the sink with `Sequence = 0`, relying on sink stamping.
- `InMemoryProviderConversationStateStore` emits provider-state events through the sink with `Sequence = 0`, relying on sink stamping.
- `ExecutionStartedEvent` and `ExecutionCompletedEvent` now include `ToolCallId?` correlation.
- `ExecutionRequest` carries session/tool-call context into the broker.
- `IModelCatalog` was introduced and `OmicronHost.ModelCatalog` now exposes the interface.
- CLI now uses catalog-key-based free model checks.
- Free model tracking was fixed to include catalog keys and provider/model aliases.
- Tests were added for continue-before-start, reset/session-start behavior, provider-state events, execution events, unknown-shell completion, and free model tracking.

The implementation is now much closer to Plan 1's architecture target. The remaining issues are narrower and mostly concern lifecycle semantics, event correctness details, cross-platform robustness, and cleaning up transitional code.

## High-Priority Findings

### 1. `ContinueAsync()` remains valid after `Reset()` even though conversation history is empty

File: `Omicron.Core/Sessions/AgentSession.cs`

Reset now follows same-session policy and does not reset `_sessionStarted`, which is good for avoiding repeated `SessionStartedEvent`s. However, that means this sequence is allowed:

```csharp
await session.PromptAsync("hello").DrainAsync();
session.Reset();
await session.ContinueAsync().DrainAsync();
```

After reset, `_messages` is empty but `_sessionStarted` remains true, so `ContinueAsync()` emits `TurnStartedEvent("(continuation)")` and calls the provider with an empty transcript.

Recommendation:

Track whether the session has resumable conversation content separately from whether it has started. For example:

```csharp
private bool HasConversation => _messages.Count > 0;
```

Then `ContinueAsync()` should throw if there are no messages:

```csharp
if (!_sessionStarted || _messages.Count == 0)
    throw new InvalidOperationException("No conversation to continue. Call PromptAsync first.");
```

Add a test for `ContinueAsync()` after reset.

---

### 2. Execution completion events use the execution start timestamp

File: `Omicron.Core/Execution/IExecutionBroker.cs`

The local helper emits completion events with `startTime`:

```csharp
_eventSink?.Emit(new ExecutionCompletedEvent(
    EventId.New(), 0, startTime, sessionId, request.Command,
    exitCode, durationMs, timedOut, toolCallId));
```

Completion events should use the actual completion time, not the start time.

Recommendation:

Use `DateTimeOffset.UtcNow` in `Complete(...)` for `ExecutionCompletedEvent`.

This matters for audit/replay timelines and later sandbox execution UX.

---

### 3. Execution broker emits events with a random session when no `SessionId` is supplied

File: `Omicron.Core/Execution/IExecutionBroker.cs`

Current behavior:

```csharp
var sessionId = request.SessionId ?? SessionId.New();
```

If a caller uses the broker outside a session, the shared event log receives execution events under a synthetic random session ID that no frontend/session manager knows about.

Recommendation:

Choose one policy:

1. Require `SessionId` for event emission and do not emit execution events if absent.
2. Require `SessionId` always by making it non-nullable in `ExecutionRequest`.
3. Emit a clearly separate host/global execution event type not scoped to a session.

For Plan 1, I recommend option 1 or 2. Since shell tool calls already pass `ctx.SessionId`, making `SessionId` required may be simplest.

Also update `LocalExecutionBroker_EmitsCompletionOnUnknownShell` to provide a session ID if event emission remains session-scoped.

---

### 4. Provider-state events are emitted from the raw store rather than a session-aware state manager

File: `Omicron.Core/Sessions/ProviderState.cs`

The sequence problem is resolved by sink stamping, but the raw store is still emitting durable events directly. This works technically, but it couples a storage primitive to event production.

This may become awkward in Plan 2 when provider state updates need richer context:

- provider/model display names;
- reason for update;
- previous vs new response ID;
- storage policy;
- whether update came from stateful Responses continuation, fallback, reset, or fork.

Recommendation:

This can remain for Plan 1 if documented as temporary, but Plan 2 should likely introduce a `ProviderStateManager` or provider-session service that wraps the store and emits richer events. The raw store should eventually become a persistence primitive.

Short-term improvement: add XML/docs comment saying event emission from the in-memory store is MVP-only.

## Medium-Priority Findings

### 5. `ExecutionCompletedEvent` does not include cancellation/error details

Files:

- `Omicron.Core/Events/OmicronEvent.cs`
- `Omicron.Core/Execution/IExecutionBroker.cs`

`ExecutionResult` contains:

```csharp
bool Cancelled
string? Error
```

But `ExecutionCompletedEvent` contains only:

```csharp
int ExitCode
long DurationMs
bool TimedOut
ToolCallId? ToolCallId
```

So the event log loses whether the process was cancelled or failed due to broker/process error.

Recommendation:

Add at least:

```csharp
bool Cancelled
string? Error
```

to `ExecutionCompletedEvent`, or add a separate `ExecutionFailedEvent`. The single completed event with result fields is probably enough for Plan 1.

---

### 6. Model catalog interface is good, but discovery is still provider-specific inside the catalog

File: `Omicron.Core/Models/ModelCatalogService.cs`

This is acceptable for Plan 1, but note that the catalog directly knows about:

- `OpenCodeProvider`;
- `OpenRouterProvider`;
- OpenCode base URL routing;
- OpenRouter free-pricing rules.

For Plan 2, API classification/compatibility would scale better if providers or provider extensions could contribute discovery logic.

Recommendation:

Document as a Plan 2/early follow-up item:

```csharp
public interface IModelDiscoveryProvider
{
    Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(CancellationToken ct);
}
```

The current central catalog is a reasonable stepping stone.

---

### 7. CLI still owns provider credential environment-variable mapping

File: `Omicron.CLI/Program.cs`

The CLI still contains:

```csharp
"openai" => "OPENAI_API_KEY"
"anthropic" => "ANTHROPIC_API_KEY"
...
```

This is provider metadata rather than frontend behavior. It is not critical for Plan 1, but provider extensions/custom providers will eventually need a non-CLI way to declare credential hints.

Recommendation:

Defer to Plan 2 or provider metadata work, but keep it on the list.

---

### 8. Shell command escaping remains fragile and under-tested

File: `Omicron.Core/Execution/IExecutionBroker.cs`

Command escaping remains generic:

```csharp
command.Replace("\\", "\\\\").Replace("\"", "\\\"")
```

This is likely fragile across `cmd`, PowerShell, bash, fish, and zsh.

Recommendation:

Add tests for simple quote/backslash cases on whichever shells are available in CI/dev. Longer term, use shell-specific argument escaping.

---

### 9. Execution broker tests are Windows-specific

File: `Omicron.Core.Tests/ArchitectureTests.cs`

`LocalExecutionBroker_EmitsExecutionEvents` uses:

```csharp
new ExecutionRequest("echo hello", "cmd", ...)
```

This passes in the current Windows environment but will fail on Linux/macOS where `cmd` is unavailable.

Recommendation:

Make the test platform-aware:

- use `cmd` on Windows;
- use `sh` or `bash` on Unix;
- skip if no shell exists.

Plan 1 does not need full cross-platform shell conformance yet, but tests should not bake in Windows-only assumptions.

---

### 10. Tests verify event presence but not sequence monotonicity/order

File: `Omicron.Core.Tests/ArchitectureTests.cs`

Now that event sequencing moved into `IEventSink`, add a focused test that emits mixed event sources and verifies:

- no sequence is zero;
- sequences are strictly increasing in `GetAllEvents()` order;
- session-filtered events preserve increasing order.

This would protect the most important fix from this round.

## Lower-Priority Cleanup

### 11. Architecture test formatting remains uneven

File: `Omicron.Core.Tests/ArchitectureTests.cs`

The bottom of the file still has inconsistent indentation/bracing around session tests. It compiles, but readability is poor.

Recommendation: run formatter or clean this up before adding more tests.

---

### 12. Old `ShellTools` still has the bad Program Files path

File: `Omicron.Core/Tools/ShellTools.cs`

The active path has moved to `LocalExecutionBroker`, but `ShellTools` still contains the old bad path:

```csharp
Environment.SpecialFolder.ProgramFiles.ToString()
```

Recommendation:

Either fix the legacy code or mark `ShellTools` obsolete/remove it once behavior parity is confirmed.

---

### 13. Old `Agent` still exists alongside `AgentSession`

File: `Omicron.Core/Agent/Agent.cs`

The CLI now uses `AgentSession`, but tests and code still keep the old `Agent` path. That may be fine temporarily, but it increases the chance of behavior diverging.

Recommendation:

Before Plan 1 is closed, decide whether `Agent` is:

- legacy compatibility;
- an internal implementation detail behind `AgentSession`;
- or slated for removal.

If kept, document it clearly.

## Plan 1 Alignment Check

### Now aligned or mostly aligned

- CLI uses `OmicronHost`, `AgentSession`, and `IModelCatalog`.
- Provider registry and model catalog both have interface seams.
- Model discovery moved out of `Program.cs`.
- Built-in tools are loaded through extensions.
- Workspace and execution tools route through core seams.
- Permission, turn, session error, execution, and provider-state events exist.
- Event sink now stamps global monotonic sequence numbers.
- Execution events include `ToolCallId` correlation.
- Reset follows same-session policy.
- `ContinueAsync()` before first prompt is rejected.
- Free model detection bug was addressed.

### Still incomplete/risky for Plan 1

- `ContinueAsync()` after reset still appears valid despite empty conversation history.
- Execution completion timestamp is wrong.
- Execution events can be emitted under synthetic random session IDs.
- Provider-state event emission is still coupled to the raw store.
- Execution events omit cancellation/error details.
- New sequence behavior lacks monotonicity tests.
- Cross-platform shell test robustness is weak.

## Recommended Next Fixes

Suggested order:

1. Fix `ContinueAsync()` after reset/empty history semantics and add a test.
2. Use completion time for `ExecutionCompletedEvent.Timestamp`.
3. Require or explicitly gate `ExecutionRequest.SessionId` for event emission.
4. Add `Cancelled` and `Error` to `ExecutionCompletedEvent` or add a failure event.
5. Add event sequence monotonicity/order tests across mixed event sources.
6. Make execution broker tests platform-aware.
7. Document or refactor provider-state event emission out of the raw store before Plan 2.
8. Fix or obsolete old `ShellTools`.
9. Clean up architecture test formatting.
10. Decide/document the fate of legacy `Agent`.

Overall, this is a strong iteration. Plan 1 is nearing completion, but the event lifecycle semantics should be tightened before using this foundation for Plan 2's stateful provider work.
