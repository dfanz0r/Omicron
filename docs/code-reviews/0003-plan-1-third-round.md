# Code Review 0003: Plan 1 Third Round

Date: 2026-05-08  
Scope: third round review of latest Plan 1 core architecture groundwork implementation.

## Validation

Build and test status at time of review:

```text
dotnet test Omicron.slnx --nologo
Passed: 69

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

Good progress again. Several second-round items were addressed:

- `SessionStartedEvent` is now gated by `_sessionStarted` and a new `TurnStartedEvent` was added.
- `SessionErrorEvent` was added and CLI renders it separately from tool completions.
- Permission checks now emit `PermissionRequestedEvent`.
- File and shell tools moved out of CLI registration and into built-in extensions.
- `read_path` now routes through `IWorkspace`.
- `shell` now routes through `IExecutionBroker`.
- `LocalExecutionBroker` is now a real implementation rather than a stub.
- `HostWorkspace` has been expanded to preserve much more of the old `FileTools` behavior.
- `InMemoryEventSink.NextSequence()` was removed.
- Provider-state store normalizes keys internally, so callers bypassing `ProviderStateKey.Create(...)` are still handled.

This is moving Plan 1 from “interfaces exist” toward “the active runtime path uses the interfaces,” which is the right direction.

There are still some important Plan 1 gaps before this should be considered a stable foundation for Plan 2.

## High-Priority Findings

### 1. Provider state events are defined but never emitted

Files:

- `Omicron.Core/Events/OmicronEvent.cs`
- `Omicron.Core/Sessions/ProviderState.cs`
- `Omicron.Core/Sessions/AgentSession.cs`

Events exist:

```csharp
ProviderStateUpdatedEvent
ProviderStateClearedEvent
```

But `InMemoryProviderConversationStateStore` has no event sink and does not emit them. `AgentSession.Reset()` clears provider state and emits `SessionResetEvent`, but does not emit `ProviderStateClearedEvent` for cleared keys.

For Plan 2, provider turn state is one of the most important pieces of replay/debug state. If `previous_response_id` or `conversation_id` changes, that must be visible in the event log.

Recommendation:

For Plan 1, either:

1. remove/defer `ProviderStateUpdatedEvent` and `ProviderStateClearedEvent` until Plan 2 wires them correctly; or
2. add an event-aware provider state service that emits update/clear events.

Preferred direction:

- keep the store as a persistence primitive;
- add a `ProviderStateManager` or session-owned wrapper that emits events when it mutates provider state;
- make Plan 2 providers use that manager instead of directly using the raw store.

Also consider adding a `GetSessionStates(SessionId)` method so reset can emit one clear event per removed key.

---

### 2. `ContinueAsync()` can emit `TurnStartedEvent` without ever emitting `SessionStartedEvent`

File: `Omicron.Core/Sessions/AgentSession.cs`

`PromptAsync()` has the `_sessionStarted` guard, but `ContinueAsync()` does not. Since `ContinueAsync()` is public, callers can do:

```csharp
await foreach (var evt in session.ContinueAsync()) { ... }
```

and produce a session event stream with a turn but no session start.

Recommendation:

Extract a helper:

```csharp
private OmicronEvent? EnsureSessionStarted()
```

or similar, and use it from both `PromptAsync()` and `ContinueAsync()`.

If `ContinueAsync()` requires an existing prompt, enforce that with a clear exception and test it.

---

### 3. Reset currently causes the same `SessionId` to emit `SessionStartedEvent` again

File: `Omicron.Core/Sessions/AgentSession.cs`

`Reset()` does:

```csharp
_sessionStarted = false;
```

so the next prompt emits another `SessionStartedEvent` using the same `SessionId`.

That is ambiguous for replay: is this a new session, or the same session with cleared messages?

Recommendation:

For Plan 1, define one policy clearly:

- **Same session policy:** `SessionStartedEvent` occurs once per `SessionId`; reset emits only `SessionResetEvent`, then future prompts emit `TurnStartedEvent`.
- **New session policy:** reset creates a new `SessionId`/`AgentId` or returns a new `AgentSession`.

I recommend same-session policy for now: do not reset `_sessionStarted`. Let `SessionResetEvent` mark the state-clearing point.

This matters for Plan 2 because provider state reset/fork behavior must be unambiguous.

---

### 4. Model discovery/catalog is still owned by `Program.cs`

File: `Omicron.CLI/Program.cs`

Plan 1 says orchestration should move out of the CLI. The CLI now uses `OmicronHost` and `AgentSession`, but model discovery remains a large local function in `Program.cs`:

- fallback model seeding;
- OpenCode discovery;
- OpenRouter discovery;
- free-model tracking;
- provider resolution;
- model display shaping.

This is still product orchestration, not frontend-only code.

Recommendation:

Introduce an `IModelCatalog` / `ModelCatalogService` in core, as described in Plan 1. CLI should ask for models and render/select them, not know discovery internals.

This is especially important before Plan 2 because API classification belongs in model discovery/catalog, not the CLI.

---

### 5. `OmicronHost.Providers` exposes concrete `ProviderFactory`, not an interface/registry seam

File: `Omicron.Core/OmicronHost.cs`

Plan 1 names a provider registry boundary. Current host exposes:

```csharp
public ProviderFactory Providers { get; }
```

This is better than direct CLI construction, but it is still a concrete factory rather than an abstraction.

Recommendation:

Introduce an interface such as:

```csharp
public interface IProviderRegistry
{
    IEnumerable<string> ProviderNames { get; }
    bool TryGetProvider(string name, out IChatProvider provider);
    IChatProvider GetProvider(string name);
    void Register(string name, IChatProvider provider);
}
```

Then have `ProviderFactory : IProviderRegistry` or replace it with `ProviderRegistry`.

This will help when extensions later register model providers.

## Medium-Priority Findings

### 6. `IExecutionBroker` executes commands but does not emit execution events

Files:

- `Omicron.Core/Execution/IExecutionBroker.cs`
- `Omicron.Core/Events/OmicronEvent.cs`

`ExecutionStartedEvent` and `ExecutionCompletedEvent` exist, but `LocalExecutionBroker` has no event sink/session context, so shell executions are not reflected as execution events.

Currently shell execution is visible only as generic tool start/completion events.

Recommendation:

For Plan 1, either:

- add session-aware execution context to `ExecutionRequest`, e.g. `SessionId?`, `AgentId?`, `ToolCallId?`; and/or
- emit execution events from the `BuiltinExecutionToolsExtension` around `_execution.ExecuteAsync(...)` if it can receive an event sink; or
- defer execution events and remove/comment them as future Plan 3 safety work.

Given Plan 1's event-log goal, prefer making shell tool execution emit execution events soon.

---

### 7. `IWorkspace` reads do not emit workspace/read events

Files:

- `Omicron.Core/Workspace/IWorkspace.cs`
- `Omicron.Core/Extensions/BuiltinWorkspaceToolsExtension.cs`

There are no workspace read events yet. The RFCs eventually need workspace auditability. Plan 1 may not need full workspace event coverage, but if file reads are tool-visible and session-relevant, we should decide whether reads are part of the durable event log.

Recommendation:

At minimum, document this as deferred. If adding now, introduce simple events:

```csharp
WorkspacePathReadEvent
WorkspacePathReadCompletedEvent
```

But avoid overbuilding VFS events before RFC 0006 work.

---

### 8. `ToolInvocationContext` does not expose workspace/execution/permission/event services

Files:

- `Omicron.Core/Tools/ToolRegistry.cs`
- `Omicron.Core/Extensions/BuiltinWorkspaceToolsExtension.cs`
- `Omicron.Core/Extensions/BuiltinExecutionToolsExtension.cs`

Built-in workspace/execution extensions currently capture `IWorkspace` and `IExecutionBroker` in constructors. That works for built-ins but is less ideal for future plugin/WASM alignment. Future extensions may need host-mediated service access at invocation time rather than captured concrete services.

Recommendation:

Consider extending `ToolInvocationContext` with a small host service context, for example:

```csharp
IWorkspace Workspace
IExecutionBroker Execution
IPermissionService Permissions
IEventSink Events
```

or a narrower `IHostServices`/`IExtensionServices` interface.

This can wait until after Plan 1 if we want to avoid over-abstraction, but it is worth deciding before the extension API becomes stable.

---

### 9. Shell command quoting/escaping is fragile across shells

File: `Omicron.Core/Execution/IExecutionBroker.cs`

Current execution builds arguments as:

```csharp
Arguments = $"{shellArgs} \"{EscapeCommand(request.Command)}\""
```

with:

```csharp
command.Replace("\\", "\\\\").Replace("\"", "\\\"")
```

This may work for some simple cases but is fragile across `cmd`, PowerShell, bash, and fish. Escaping rules differ substantially.

Recommendation:

Short term: add tests for commands containing quotes, backslashes, semicolons, and PowerShell-style strings.

Longer term: preserve or adapt the most robust parts of the old `ShellTools` implementation, or use shell-specific argument escaping.

---

### 10. `LocalExecutionBroker.FindExe()` has a suspicious Program Files path

File: `Omicron.Core/Execution/IExecutionBroker.cs`

This line appears incorrect:

```csharp
paths.Add(Path.Combine(Environment.SpecialFolder.ProgramFiles.ToString(), "PowerShell", "7", "pwsh.exe"));
```

`Environment.SpecialFolder.ProgramFiles.ToString()` returns the enum name, not the actual folder path.

There is a correct call immediately after:

```csharp
Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
```

Recommendation: remove the incorrect path entry.

---

### 11. `HostWorkspace.Truncated` detection is brittle

File: `Omicron.Core/Workspace/IWorkspace.cs`

Current truncation flag for files is inferred with:

```csharp
Truncated: fileContent.Contains("[Output truncated")
```

This misses continuation cases where output was limited by explicit `limit` but not by the hard output cap, and it couples metadata to display text.

Recommendation:

Return structured read metadata internally from `ReadFile`, e.g. `(content, truncated)`, rather than searching rendered output.

This is not urgent for Plan 1 but should be cleaned before persistence/workspace tests depend on it.

## Lower-Priority Cleanup

### 12. Architecture test formatting remains uneven

File: `Omicron.Core.Tests/ArchitectureTests.cs`

The added session tests still have inconsistent indentation/bracing near the end of the class. It compiles, but it is getting harder to review.

Recommendation: run formatting/cleanup soon before more tests are added.

---

### 13. `AgentSession` still carries unused `_workspace` and `_execution` fields

File: `Omicron.Core/Sessions/AgentSession.cs`

Since workspace/execution are now captured by built-in extensions, these fields are currently unused in `AgentSession`.

Recommendation:

Either:

- remove them from `AgentSession`; or
- intentionally expose them through tool invocation context / host services.

Avoid keeping unused dependencies in the session object because it blurs ownership.

---

### 14. Provider tool-call start/delta events are still ignored

File: `Omicron.Core/Sessions/AgentSession.cs`

The stream switch still ignores `StreamEventType.ToolCallStart` and `StreamEventType.ToolCallDelta`.

This is acceptable for current MVP, but Plan 2's Responses parser will need typed function-call argument streaming. Keep this as a known handoff item.

## Plan 1 Alignment Check

### Now aligned or mostly aligned

- CLI uses `OmicronHost` and `AgentSession`.
- Built-in tools are registered through extensions.
- `read_path` routes through `IWorkspace`.
- `shell` routes through `IExecutionBroker`.
- Tool calls go through `ToolRegistry` and `ToolInvocationContext`.
- Permission decisions emit events.
- Non-tool errors have a dedicated session error event.
- Session start vs turn start semantics are improved.
- Provider state namespace and case normalization issues are mostly resolved.
- Event sink contains yielded session events.

### Still incomplete for Plan 1

- Model catalog/discovery is still in the CLI.
- Provider registry is still exposed as concrete `ProviderFactory`.
- Provider state update/clear events are not wired.
- Execution events are not emitted for shell commands.
- Session reset/start semantics need a final policy decision.
- Tool invocation context may need host-service access before extension APIs harden.

## Recommended Next Fixes

Suggested order:

1. Decide/fix reset semantics: do not re-emit `SessionStartedEvent` for the same `SessionId`, or create a new session on reset.
2. Make `ContinueAsync()` either ensure session start or throw if no prior prompt exists.
3. Add model catalog service and move model discovery/classification out of `Program.cs`.
4. Introduce `IProviderRegistry` and have the host expose the interface instead of concrete `ProviderFactory`.
5. Wire provider-state update/clear events through a provider state manager or explicitly defer those events to Plan 2.
6. Decide whether execution events should be emitted in Plan 1; if yes, add session-aware execution event emission around shell tool execution.
7. Remove unused `AgentSession` dependencies or expose host services through `ToolInvocationContext`.
8. Add shell quoting tests and remove the bad Program Files path.
9. Clean up architecture test formatting.

Overall, this is now substantially closer to Plan 1's intent. The main remaining architectural risk before Plan 2 is that model/API classification still lives in the CLI rather than a core model catalog, and provider-state events exist but are not connected to mutations.
