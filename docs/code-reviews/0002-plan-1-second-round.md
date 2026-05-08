# Code Review 0002: Plan 1 Second Round

Date: 2026-05-08  
Scope: second round review of latest Plan 1 core architecture groundwork implementation.

## Validation

Build and test status at time of review:

```text
dotnet build Omicron.slnx --nologo
0 warnings, 0 errors

dotnet test Omicron.slnx --nologo
Passed: 68
```

## Summary

Good progress since the first review. The most important first-round issues have been addressed:

- CLI now creates sessions through `host.CreateSession(...)` instead of directly constructing the old `Agent`.
- `AgentSession` now emits yielded events into `IEventSink` via `Emit(...)`.
- Provider state moved to the `Omicron.Core.Sessions` namespace.
- Provider state key creation now has a normalization factory.
- Tests were added for session event-log emission, tool-call event emission, and reset clearing provider state.
- File and shell tools are now at least registered as `ToolDefinition`s so the `AgentSession` tool pipeline can invoke them.

This is a meaningful improvement and gets the runtime path much closer to Plan 1.

There are still a few architectural issues to resolve before Plan 1 should be considered solid enough for Plan 2.

## High-Priority Findings

### 1. `SessionStartedEvent` is emitted on every prompt/continue, not when the session is actually created

File: `Omicron.Core/Sessions/AgentSession.cs`  
Relevant lines: `PromptAsync()` emits `SessionStartedEvent`; `ContinueAsync()` also emits `SessionStartedEvent`.

Current behavior:

```csharp
yield return Emit(new SessionStartedEvent(...));
```

inside every prompt/continue call.

This means one logical session can contain many `SessionStartedEvent`s. For replay/persistence, `SessionStartedEvent` should represent the lifecycle start of the session, not the start of every model turn.

Recommendation:

Use separate events:

- `SessionStartedEvent` once when the session is created or first opened;
- `TurnStartedEvent` / `PromptStartedEvent` for each user prompt/model turn;
- optionally `ModelRequestStartedEvent` for each provider call inside a tool loop.

Minimum short-term fix:

- rename current repeated event to `TurnStartedEvent`; or
- add a boolean guard so `SessionStartedEvent` is emitted only once per `AgentSession`.

This matters for Plan 2 because provider turn state and replay need clear turn boundaries.

---

### 2. Permission decisions are not emitted as events

File: `Omicron.Core/Sessions/AgentSession.cs`  
Relevant area: permission request before tool invocation.

`AgentSession` calls:

```csharp
var permResult = await _permissions.RequestAsync(permRequest, ct);
```

but never emits `PermissionRequestedEvent`.

Plan 1 explicitly wants durable-shaped events for permission seams. If the event log is the authoritative session history, permission requests/decisions must be observable.

Recommendation:

Emit a permission event for every permission check, including allow-all decisions:

```csharp
yield return Emit(new PermissionRequestedEvent(
    EventId.New(), NextSequence(), DateTimeOffset.UtcNow, Id,
    permRequest.Action,
    permResult.Allowed));
```

Longer term this should include target, description, risk, and chosen option, but the current event type is enough for Plan 1.

---

### 3. File and shell tools still bypass `IWorkspace` and `IExecutionBroker`

File: `Omicron.CLI/Program.cs`  
Relevant lines: `RegisterFileAndShellTools(host)` wraps `FileTools.Create(...)` and `ShellTools.Create(...)`.

Current behavior:

```csharp
var fileTool = FileTools.Create(host.Workspace.RootPath);
host.Tools.Register(WrapTool(fileTool));

var shellTool = ShellTools.Create(host.Workspace.RootPath);
host.Tools.Register(WrapTool(shellTool));
```

This is better than direct `agent.AddTool(...)`, because tools now flow through `ToolRegistry` and `AgentSession`. However, the file/shell internals still call the old direct implementations and bypass:

- `IWorkspace.ReadPathAsync(...)`;
- `IExecutionBroker.ExecuteAsync(...)`;
- workspace/execution-specific events;
- future sandbox/workspace transaction seams.

The generic `tool.execute` permission check does run, but the key Plan 1 seam is still not exercised for file/shell internals.

Recommendation:

Next step should be to move file/shell wrappers into a built-in extension that depends on host services, and implement them using:

- `IWorkspace` for `read_path`;
- `IExecutionBroker` for `shell`.

If preserving existing behavior is more important right now, extract the rich `FileTools` and `ShellTools` logic into reusable service classes, then have both tool definitions and workspace/execution implementations call those services.

---

### 4. File/shell tool registration is still owned by the CLI

File: `Omicron.CLI/Program.cs`  
Relevant lines: `RegisterFileAndShellTools(host)`.

Plan 1's direction is for the CLI to consume the host, not own capability registration. The CLI now still decides that file and shell tools exist.

Recommendation:

Move file/shell registration into core host setup or a built-in extension, for example:

```csharp
host.LoadBuiltinExtensions();
```

should register all default built-in tools, or accept options controlling which built-ins are enabled.

This also prepares the later C#/WASM extension path: built-in C# tools should use the same contribution path as future plugins.

## Medium-Priority Findings

### 5. `ProviderStateKey.Create(...)` is only conventionally enforced

File: `Omicron.Core/Sessions/ProviderState.cs`

The factory lowercases provider names, which is good:

```csharp
providerName.ToLowerInvariant()
```

However, the primary record struct constructor is still public:

```csharp
public readonly record struct ProviderStateKey(...)
```

So callers can still bypass normalization:

```csharp
new ProviderStateKey(session, agent, "OpenAI", model, apiType)
```

The XML comment says "Always use this factory", but that is not enforced.

Recommendation:

Either:

- normalize inside the store on `Get`, `Set`, and `Clear`; or
- replace the positional record struct with a manually defined readonly struct that normalizes in its constructor/factory; or
- accept this temporarily, but add tests showing store behavior is case-insensitive even when callers use the constructor directly.

For Plan 2, provider-state key correctness matters because response IDs must not split across casing variants.

---

### 6. Error states are represented as fake tool completions

File: `Omicron.Core/Sessions/AgentSession.cs`  
Relevant areas: provider errors and max-iteration limit.

Current behavior emits:

```csharp
new ToolInvocationCompletedEvent(..., new ToolCallId("error"), "system", errMsg, IsError: true)
```

for non-tool errors.

This conflates provider/session errors with tool lifecycle events. It also causes the CLI to render system/provider errors as if a tool completed:

```text
done.
[error text]
```

Recommendation:

Add a dedicated event type such as:

```csharp
public sealed record SessionErrorEvent(..., string Message, string? Code)
```

or:

```csharp
public sealed record AgentErrorEvent(...)
```

Then have the CLI render it separately.

This is important before Plan 2 because provider-state retry/fallback errors need to be distinguishable from tool failures.

---

### 7. Tool-call streaming start/delta events from providers are still ignored

File: `Omicron.Core/Sessions/AgentSession.cs`  
Relevant switch over `StreamEventType`.

Current switch handles:

- `TextDelta`
- `ToolCallEnd`
- `Done`
- `Error`

It ignores:

- `ToolCallStart`
- `ToolCallDelta`

As a result, `ToolInvocationStartedEvent` is emitted only after the provider has fully accumulated the tool call and model streaming has completed for that turn.

This is acceptable for the current MVP, but it is not the eventual streaming lifecycle described by the RFCs. Responses API will have typed function-call argument delta events, so Plan 2 will need this path.

Recommendation:

For Plan 1, add a comment/test documenting this as intentionally deferred. For Plan 2, map provider `ToolCallStart`/`ToolCallDelta` into durable core events or a tool-call-assembly lifecycle.

---

### 8. `IWorkspace` and `IExecutionBroker` are injected into `AgentSession` but unused

File: `Omicron.Core/Sessions/AgentSession.cs`

Fields:

```csharp
private readonly IWorkspace _workspace;
private readonly IExecutionBroker _execution;
```

These are currently not used by `AgentSession`. This is not a functional bug, but it confirms that workspace/execution seams are present but not yet participating in runtime behavior.

Recommendation:

Once file/shell tool registration moves into built-in extensions, either:

- remove unused fields from `AgentSession` if tools get services through extension context; or
- expose them through `ToolInvocationContext` / service provider context if tools are expected to access host services at invocation time.

Avoid keeping unused dependencies indefinitely because they obscure ownership.

## Lower-Priority Cleanup

### 9. `BuiltinToolsExtension` still only registers calculator/time

File: `Omicron.Core/Extensions/BuiltinToolsExtension.cs`

The extension path is now used for calculator/time, while file/shell are registered separately in CLI.

Recommendation:

Split built-ins by capability:

- `BuiltinUtilityToolsExtension` for calculator/time;
- `BuiltinWorkspaceToolsExtension` for `read_path`;
- `BuiltinExecutionToolsExtension` for `shell`.

This will make later permission/capability gating cleaner.

---

### 10. Architecture tests are useful but formatting should be cleaned up

File: `Omicron.Core.Tests/ArchitectureTests.cs`

The new tests are valuable, but the last test block has inconsistent indentation/bracing. It compiles, but it makes future review harder.

Recommendation:

Run formatter or clean up indentation before this grows further.

---

### 11. `InMemoryEventSink.NextSequence()` remains unused

File: `Omicron.Core/Events/IEventSink.cs`

This was noted in the first review and still exists. `AgentSession` owns per-session sequencing.

Recommendation:

Either remove `InMemoryEventSink.NextSequence()` or document it as intentionally unused/reserved. Prefer removing it for now to keep sequence ownership clear.

## Plan 1 Alignment Check

### Now aligned

- CLI uses `AgentSession` through `OmicronHost`.
- Tool calls go through `ToolRegistry` and `ToolInvocationContext`.
- Events are durable-shaped and emitted into `IEventSink`.
- Provider state is session-scoped and reset clears it.
- Provider state is no longer in the events namespace.
- Basic tests cover session event logging and provider-state reset.

### Still incomplete for Plan 1

- Permission events are not logged.
- File/shell internals do not use workspace/execution seams.
- File/shell registration is still CLI-owned.
- Session/turn lifecycle events need clearer semantics.
- Non-tool errors need a dedicated event type.
- Provider turn-state seam exists but is not yet exposed to provider invocation context; that is acceptable for Plan 1 if explicitly left for Plan 2 handoff.

## Recommended Next Fixes

Suggested order:

1. Fix lifecycle semantics: emit `SessionStartedEvent` once or introduce `TurnStartedEvent`.
2. Emit `PermissionRequestedEvent` for every permission decision.
3. Add a dedicated session/agent error event and stop using fake tool completions for provider/max-iteration errors.
4. Move file/shell registration out of CLI into built-in extension(s).
5. Start routing `read_path` through `IWorkspace` and `shell` through `IExecutionBroker`, or explicitly document that behavior-preserving extraction is next.
6. Decide/enforce provider-state key normalization beyond factory convention.
7. Remove or document `InMemoryEventSink.NextSequence()`.
8. Clean up architecture test formatting.

After these fixes, Plan 1 will be much closer to a stable platform for Plan 2's provider API abstraction work.
