# RFC 0007: Sandboxing and Execution Broker

Status: **Research/Planned**

## Purpose

Define Omicron's sandboxing direction for AI-driven tools, generated code, shell commands, and optionally virtual terminal panes.

This RFC covers sandboxing processes/commands/code launched by Omicron. It is separate from plugin sandboxing. Third-party plugin sandboxing is covered by RFC 0010 and should use WebAssembly/Wasmtime where practical.

Sandboxing is an execution provider layer that plugs into Omicron's permission, VFS, workspace transaction, session-history, and remote-backend systems.

## Current Implementation Snapshot

As of 2026-05-08, sandboxing and brokered execution are not implemented. The CLI registers a direct `shell` tool from `ShellTools.Create(workspaceRoot)`. That tool detects available host shells, confines `cwd` resolution under the workspace root, clamps timeouts, and truncates output, but it still launches commands directly on the host process environment without permission prompts, policy decisions, workspace overlays, sandbox providers, or audit events. Treat this as an MVP implementation gap to close before increasing tool authority.

## Core Principle

> Tools, shell commands, and generated code launch through an Omicron execution broker. The broker applies policy, creates workspace overlays/snapshots, invokes a sandbox provider when required, records audit events, and returns structured output.

The first boundary is brokered execution + VFS + permissions + snapshots. Concrete sandbox providers can evolve under that boundary. In remote mode, the backend that owns the workspace/process must enforce sandbox policy; frontends and plugins cannot bypass it over the remoting protocol.

Plugins that want to run commands must call this execution broker. A plugin runtime must not grant direct process spawning even if the plugin itself is WASM-sandboxed.

## Candidate Systems

Existing systems should be evaluated before building a custom runtime.

```text
OpenAI Codex sandbox runtime / windows-sandbox-rs entry point
  https://github.com/openai/codex/tree/main/codex-rs/windows-sandbox-rs/src
  Role: reference implementation and low-level sandboxing source material.
  The linked path is a Windows-focused entry point, but Codex's sandboxing work should be evaluated as a broader runtime/design.

zerobox
  https://github.com/afshinm/zerobox
  Role: more refined/generalized version of Codex sandboxing ideas; candidate cross-platform provider or provider scaffold.
  Public README describes file, network, environment, and credential controls.

heel / leash candidate
  https://github.com/lexoliu/heel
  Role: native OS sandboxing reference/provider for LLM-generated code.
  Verify repository name, API, platform support, and license during evaluation.
```

Evaluation criteria:

- license compatibility
- supported platforms and OS versions
- Rust/.NET interop complexity
- deployment/update model
- filesystem policy expressiveness
- network controls
- environment and credential scrubbing
- PTY support for interactive commands/panes
- performance for short-lived tool calls
- auditability/debuggability
- provider unavailable/failure behavior

## Provider Abstraction

Omicron defines provider-neutral contracts.

```csharp
public sealed record SandboxPolicy(
    SandboxPolicyId Id,
    SandboxFileSystemPolicy FileSystem,
    SandboxNetworkPolicy Network,
    SandboxEnvironmentPolicy Environment,
    SandboxResourceLimits ResourceLimits,
    SandboxInteractivity Interactivity);

public sealed record SandboxExecutionRequest(
    BackendId? BackendId,
    SessionId? SessionId,
    AgentId? AgentId,
    string Executable,
    IReadOnlyList<string> Args,
    WorkspacePath? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    SandboxPolicyId PolicyId,
    WorkspaceSnapshotId? InputWorkspaceSnapshotId,
    bool RequiresPty);

public interface ISandboxProvider
{
    string Id { get; }
    bool IsAvailable { get; }
    IReadOnlyList<SandboxCapability> Capabilities { get; }

    ValueTask<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        IWorkspaceFileSystem workspace,
        CancellationToken ct);
}
```

Provider examples:

```text
Omicron.Sandboxing.None
  no isolation; explicit unsafe/local mode only

Omicron.Sandboxing.CodexRuntime
  adapter/fork/reference provider based on broader OpenAI Codex sandbox runtime design

Omicron.Sandboxing.WindowsNative
  separate Windows-specific provider if needed

Omicron.Sandboxing.ZeroBox
  adapter around zerobox if licensing/API/deployment fit; likely more polished than raw Codex code

Omicron.Sandboxing.Heel
  adapter or fork/reference implementation after evaluation

Omicron.Sandboxing.ContainerOrMicroVm
  future provider for stronger isolation where available
```

## Default Policy Posture

Deny by default.

```text
filesystem:
  read workspace: allowed by default
  write workspace: through overlay/transaction only
  read home/profile: denied by default
  read credentials/cloud config/ssh keys: denied by default
  write outside workspace: denied by default

network:
  denied by default for generated code/tool execution
  allow only by explicit policy or user approval

environment:
  minimal allowlist
  scrub secrets by default

process:
  resource limits where provider supports them
  child process behavior controlled by provider

interactive:
  non-interactive by default for tools/code
  PTY allowed only for terminal-pane workflows or explicit command execution
```

## Execution Flow

```text
execution request
  ↓
permission/risk policy
  ↓
workspace snapshot before execution
  ↓
workspace transaction/overlay mounted into sandbox
  ↓
sandbox provider launches process
  ↓
stdout/stderr/PTY output captured as events
  ↓
workspace diff computed
  ↓
commit, rollback, or ask user
  ↓
workspace snapshot after execution if committed
```

## Virtual Terminal Modes

```text
normal pane:
  host shell with clear unsafe/local indication

sandboxed pane:
  shell runs under sandbox provider with visible policy badges
```

Provider support for PTYs and interactive processes will vary. UI must expose limitations clearly.

## Audit Events

Sandbox execution must produce ordered, persisted events.

```csharp
public sealed record SandboxExecutionStarted(...);
public sealed record SandboxExecutionFinished(...);
```

Additional implementation events may record:

- selected provider
- effective policy
- capability warnings
- approval decision
- stdout/stderr chunks
- owning backend/session/agent ids
- resource limit termination
- denied access summaries
- produced workspace diff

## Adoption Strategy

```text
1. Define Omicron policy/provider interfaces.
2. Implement explicit no-sandbox/local provider for development.
3. Implement process launch broker and audit events.
4. Integrate workspace overlays/snapshots.
5. Spike candidate providers:
   - Codex sandbox runtime, with windows-sandbox-rs as one entry point
   - zerobox as refined/generalized Codex-derived candidate
   - heel/leash-style native sandboxing as additional reference
6. Choose per-platform defaults based on security, reliability, license, and deployment cost.
7. Add stronger providers later without changing tools.
```

## Design Decisions

1. Sandboxing is provider-based.
2. Brokered execution is mandatory for AI/tool execution.
3. Policies are explicit and inspectable.
4. Filesystem writes go through workspace overlays.
5. Provider selection is platform/capability dependent.
6. Existing sandbox projects are evaluated before custom runtime work.
7. Remote backends enforce sandbox policy locally and expose results/events through RFC 0014 rather than letting frontends execute on their behalf.
