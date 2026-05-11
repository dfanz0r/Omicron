# Implementation Plan 0009: Sandboxing Foundations

Status: Proposed
Target: Introduce sandbox policy model, execution broker integration, and audit events so tool execution and edit harness writes are governed by explicit policy.

## Purpose

Omicron's execution broker and edit harness currently operate with implicit host authority. The `shell` tool launches commands directly; the `edit_file_hashline` tool writes through workspace transactions but without policy gates. Before expanding tool authority, adding plugins, or supporting delegated remote agents, Omicron needs a sandboxing layer that:

1. Classifies execution risk.
2. Applies explicit policy (filesystem, network, environment).
3. Records audit events.
4. Supports provider-based sandbox implementations.

This plan builds the **policy and broker integration** (Phase Set 3a). Concrete OS-level sandbox providers (zerobox, Codex runtime, etc.) are deferred to later evaluation spikes.

## Primary RFCs

- RFC 0007 — Sandboxing and Execution Broker
- RFC 0006 — Persistence, Session History, VFS, and Workspace Snapshots
- RFC 0001 — Core Architecture and Event Model
- RFC 0009 — Implementation Roadmap (Phase Set 3a)

## Depends On

- Plans 1–6 (core events, tool registry, execution broker, workspace transactions)
- Plan 4 (hashline edit harness on workspace transactions)
- Plan 3.6 (transaction lifecycle events, audit event patterns)

**Edit harness integration:** This plan explicitly integrates with the edit harness. The `ExecutionBroker` (Phase C) applies sandbox policy to all tool executions, including `edit_file_hashline` and `edit_file_anchors`. Specifically:
- Workspace writes from edit tools use `RequireTransactionOverlay = true` by default, so all edits are staged in a transaction before commit.
- `RiskClassifier.ClassifyEdit` (Phase B) categorizes batch edits by file count, paths touched, and deletions.
- High-risk edits (e.g., deleting files outside workspace, batch edits >10 files) trigger permission prompts.
- Audit events (`SandboxExecutionStartedEvent`, `SandboxExecutionFinishedEvent`) are emitted for edit tool commits, making the edit harness observable in the session log.

## Non-Goals

- No concrete OS-level sandbox provider (zerobox, Codex, heel, Windows Sandbox, containers, micro-VMs)
- No network firewall / iptables manipulation
- No seccomp / pledge / AppArmor policy generation
- No GUI permission dialog (TUI modal overlay only)
- No plugin sandboxing (that is WASM / RFC 0010)
- No remote backend sandbox enforcement (local backend only)
- No automatic sandbox provider selection based on platform detection

## Guiding Principles

1. **Brokered execution is mandatory.** All tools, shell commands, and generated code launch through `IExecutionBroker`.
2. **Deny by default.** Filesystem writes outside workspace, network access, and environment credential access are denied unless explicitly allowed.
3. **Policy is explicit and inspectable.** Users and frontends can read the effective policy before execution.
4. **Audit events are authoritative.** Every brokered execution emits durable events to `IEventSink`.
5. **Workspace overlays first.** Even without a real sandbox provider, writes go through workspace transactions so they can be diffed and rolled back.

---

## Proposed Layout

```text
Omicron.Core/Sandboxing/
  SandboxPolicy.cs
  SandboxPolicyId.cs
  SandboxExecutionRequest.cs
  SandboxExecutionResult.cs
  SandboxFileSystemPolicy.cs
  SandboxNetworkPolicy.cs
  SandboxEnvironmentPolicy.cs
  SandboxResourceLimits.cs
  SandboxInteractivity.cs
  SandboxPolicyRegistry.cs
  ISandboxProvider.cs
  SandboxProviderCapability.cs
  HostDirectProvider.cs
  RiskClassifier.cs
  RiskExplanation.cs
  ExecutionBroker.cs          // replaces/refactors LocalExecutionBroker

Omicron.Core/Sandboxing/Events/
  SandboxExecutionStartedEvent.cs
  SandboxExecutionFinishedEvent.cs
  SandboxPolicyAppliedEvent.cs
  PermissionApprovedEvent.cs
  PermissionRejectedEvent.cs

Omicron.CLI/
  SandboxPrompts.cs           // TUI modal prompts for approval

Omicron.Core.Tests/Sandboxing/
  SandboxPolicyTests.cs
  RiskClassifierTests.cs
  ExecutionBrokerTests.cs
  SandboxEventTests.cs
```

---

## Phase A: Policy Model

### Goals
Define explicit, serializable, user-readable policy records.

### Deliverables

#### A1. `SandboxPolicy`
```csharp
namespace Omicron.Core.Sandboxing;

public sealed record SandboxPolicy(
    SandboxPolicyId Id,
    string DisplayName,
    SandboxFileSystemPolicy FileSystem,
    SandboxNetworkPolicy Network,
    SandboxEnvironmentPolicy Environment,
    SandboxResourceLimits ResourceLimits,
    SandboxInteractivity Interactivity,
    SandboxRiskLevel DefaultRiskLevel);
```

#### A2. `SandboxFileSystemPolicy`
```csharp
namespace Omicron.Core.Sandboxing;

public sealed record SandboxFileSystemPolicy
{
    public bool AllowReadWorkspace { get; init; } = true;
    public bool AllowWriteWorkspace { get; init; } = false;
    public bool AllowReadHome { get; init; } = false;
    public bool AllowWriteHome { get; init; } = false;
    public bool AllowReadSystem { get; init; } = false;
    public bool AllowWriteSystem { get; init; } = false;
    public bool AllowReadCredentials { get; init; } = false;
    public bool RequireTransactionOverlay { get; init; } = true;
    public IReadOnlyList<string> AllowedPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DeniedPaths { get; init; } = Array.Empty<string>();
}
```

- `RequireTransactionOverlay`: when true, all workspace writes go through `IWorkspaceTransaction`.

#### A3. `SandboxNetworkPolicy`
```csharp
namespace Omicron.Core.Sandboxing;

public sealed record SandboxNetworkPolicy
{
    public bool AllowAny { get; init; } = false;
    public bool AllowLocalhost { get; init; } = false;
    public IReadOnlyList<string> AllowedHosts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> AllowedPorts { get; init; } = Array.Empty<int>();
}
```

#### A4. `SandboxEnvironmentPolicy`
```csharp
namespace Omicron.Core.Sandboxing;

public sealed record SandboxEnvironmentPolicy
{
    public bool InheritAll { get; init; } = false;
    public IReadOnlyList<string> AllowedVars { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ScrubbedVars { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> ExtraVars { get; init; } = new Dictionary<string, string>();
}
```

- `ScrubbedVars` removes sensitive variables (e.g., `OPENAI_API_KEY`, `AWS_SECRET_ACCESS_KEY`, `GITHUB_TOKEN`) from the environment before execution.

#### A5. `SandboxResourceLimits`
```csharp
namespace Omicron.Core.Sandboxing;

public sealed record SandboxResourceLimits
{
    public TimeSpan? MaxDuration { get; init; }
    public long? MaxMemoryBytes { get; init; }
    public int? MaxCpuPercent { get; init; }
    public int? MaxFileSizeBytes { get; init; }
    public int? MaxOpenFiles { get; init; }
}
```

#### A6. `SandboxInteractivity`
```csharp
namespace Omicron.Core.Sandboxing;

public enum SandboxInteractivity
{
    NonInteractive,
    Interactive,      // allows PTY / stdin
    InteractiveAi     // AI-assisted command composition
}
```

#### A7. `SandboxPolicyRegistry`
```csharp
namespace Omicron.Core.Sandboxing;

public sealed class SandboxPolicyRegistry
{
    public void Register(SandboxPolicy policy);
    public SandboxPolicy? Get(SandboxPolicyId id);
    public SandboxPolicy GetDefault();
}
```

Built-in policies:
```text
"default"       — read workspace, deny writes, deny network, scrub secrets
"read-only"     — read workspace, deny everything else
"trusted-tool"  — read/write workspace via overlay, deny network, scrub secrets
"shell-interactive" — read workspace, deny writes, deny network, interactive PTY, scrub secrets
"network-allowed" — read workspace, deny writes, allow localhost network, scrub secrets
"unsafe"        — allow everything (explicit opt-in, big red warning)
```

### Tests
- `PolicyRegistry_RegisterAndRetrieve`
- `PolicyRegistry_DefaultPolicy_DeniesNetwork`
- `PolicyRegistry_DefaultPolicy_ScubsSecrets`
- `Policy_Serialize_RoundTripsViaJson`

### Acceptance Criteria
- Policy records are immutable, serializable, and human-readable.
- Default policy denies network, system access, and credential reads.
- `SandboxPolicyRegistry` provides at least 6 built-in policies.

---

## Phase B: Risk Classification

### Goals
Before executing a command or applying an edit, classify the risk and generate a human-readable explanation.

### Deliverables

#### B1. `SandboxRiskLevel`
```csharp
namespace Omicron.Core.Sandboxing;

public enum SandboxRiskLevel
{
    Safe,       // read-only workspace operations
    Low,        // workspace writes via transaction
    Medium,     // shell commands with no network / no system paths
    High,       // shell commands that may mutate files outside workspace
    Critical    // network access, credential access, destructive commands
}
```

#### B2. `RiskClassifier`
```csharp
namespace Omicron.Core.Sandboxing;

public static class RiskClassifier
{
    public static SandboxRiskLevel Classify(SandboxExecutionRequest request);
    public static SandboxRiskLevel ClassifyTool(string toolName, JsonElement args);
    public static SandboxRiskLevel ClassifyEdit(IReadOnlyList<WorkspaceFileDiff> diffs);
}
```

Rules:
- `read_path` → Safe
- `edit_file_hashline` with workspace paths only → Low
- `shell` with `rm`, `del`, `format`, `dd`, `mkfs`, `reg delete` → High or Critical
- `shell` with `curl`, `wget`, `Invoke-WebRequest`, `fetch` → Medium or High (depending on URL)
- `shell` with `git clone`, `npm install`, `pip install` → Medium (network)
- `shell` touching paths outside workspace → High
- `shell` reading `~/.ssh`, `~/.aws`, env vars → Critical
- Any command with `sudo` → Critical

#### B3. `RiskExplanation`
```csharp
namespace Omicron.Core.Sandboxing;

public sealed record RiskExplanation(
    SandboxRiskLevel Level,
    string Summary,
    IReadOnlyList<string> Concerns,
    IReadOnlyList<string> Mitigations);
```

Examples:
```text
Level: Medium
Summary: "This command runs a shell script that may download packages."
Concerns:
  - Network access to external hosts
  - File writes to workspace
Mitigations:
  - Workspace writes go through transaction overlay
  - Network is denied by default; command may fail if it requires internet
```

### Tests
- `Classify_ReadPath_Safe`
- `Classify_EditWorkspace_Low`
- `Classify_ShellRm_High`
- `Classify_ShellCurl_Medium`
- `Classify_ShellSudo_Critical`
- `Classify_ShellOutsideWorkspace_High`
- `Explain_Medium_GeneratesConcernsAndMitigations`

### Acceptance Criteria
- `read_path` is classified Safe.
- `edit_file_hashline` is classified Low.
- `shell` with destructive keywords is classified High or Critical.
- `shell` with network keywords is classified Medium or higher.
- Explanation includes specific concerns and mitigations.

---

## Phase C: Execution Broker Integration

### Goals
Replace/refactor `LocalExecutionBroker` so all execution goes through policy checks, risk classification, and audit events.

### Deliverables

#### C1. `ExecutionBroker`
```csharp
namespace Omicron.Core.Sandboxing;

public sealed class ExecutionBroker : IExecutionBroker
{
    public ExecutionBroker(
        IWorkspaceFileSystem workspace,
        IWorkspaceTransactionManager? transactions,
        IEventSink eventSink,
        IPermissionService permissionService,
        SandboxPolicyRegistry policyRegistry,
        ISandboxProvider? sandboxProvider = null);

    public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken ct);
}
```

Execution flow:
```text
1. Receive ExecutionRequest with SessionId, Command, etc.
2. Determine effective SandboxPolicy:
   a. If request specifies a policy ID, use it.
   b. Else classify risk from command text + args.
   c. Apply default policy as baseline.
3. Classify risk.
4. If risk >= Medium and policy requires approval:
   a. Build RiskExplanation.
   b. Call IPermissionService.RequestAsync.
   c. If rejected, emit PermissionRejectedEvent and return error result.
   d. If approved, emit PermissionApprovedEvent.
5. If policy requires transaction overlay and writes are expected:
   a. Open IWorkspaceTransaction.
   b. Set working directory to transaction overlay.
6. If sandbox provider is available and policy != "unsafe":
   a. Build SandboxExecutionRequest.
   b. Call ISandboxProvider.ExecuteAsync.
   c. Stream stdout/stderr as events.
7. Else (no sandbox provider):
   a. Run process directly via existing shell logic.
   b. Scrub environment per policy.
   c. Apply timeout per ResourceLimits.
   d. Stream stdout/stderr as events.
8. On completion:
   a. Emit SandboxExecutionFinishedEvent.
   b. If transaction was opened and auto-commit is enabled, commit.
   c. Return ExecutionResult.
```

#### C2. `ExecutionRequest` update
```csharp
public sealed record ExecutionRequest(
    SessionId SessionId,
    string Command,
    string? ShellId,
    string? WorkingDirectory,
    int TimeoutSeconds = 120,
    ToolCallId? ToolCallId = null,
    SandboxPolicyId? PolicyId = null,       // NEW
    bool RequiresTransaction = false);       // NEW
```

#### C3. `ISandboxProvider`
```csharp
namespace Omicron.Core.Sandboxing;

public interface ISandboxProvider
{
    string Id { get; }
    string DisplayName { get; }
    bool IsAvailable { get; }
    IReadOnlyList<SandboxProviderCapability> Capabilities { get; }

    ValueTask<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        IWorkspaceFileSystem workspace,
        CancellationToken ct);
}

public enum SandboxProviderCapability
{
    FileSystemIsolation,
    NetworkIsolation,
    EnvironmentIsolation,
    ResourceLimits,
    PtySupport,
    WorkspaceOverlayMount
}
```

#### C4. `HostDirectProvider`
The default provider when no OS-level sandbox is available. It runs commands directly on the host process through the existing shell infrastructure. It does **not** provide any OS-level isolation, but it still enforces policy at the broker level:
- Denies commands that touch denied paths.
- Scrubs environment variables.
- Applies timeout.
- Logs a warning that execution is direct-host.

The name is intentionally `HostDirectProvider` (not "no sandbox") to avoid implying that sandboxing is somehow disabled or optional at the policy layer. Policy enforcement always happens in the broker; this provider is simply the fallback execution engine when no OS-level provider is installed.

```csharp
namespace Omicron.Core.Sandboxing;

public sealed class HostDirectProvider : ISandboxProvider
{
    public string Id => "host-direct";
    public string DisplayName => "Host Direct (no OS isolation)";
    public bool IsAvailable => true;
    public IReadOnlyList<SandboxProviderCapability> Capabilities => Array.Empty<SandboxProviderCapability>();
}
```

#### C5. `SandboxExecutionRequest`
```csharp
namespace Omicron.Core.Sandboxing;

public sealed record SandboxExecutionRequest(
    SessionId SessionId,
    AgentId? AgentId,
    ToolCallId? ToolCallId,
    string Executable,
    IReadOnlyList<string> Args,
    WorkspacePath? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    SandboxPolicyId PolicyId,
    SandboxPolicy Policy,
    bool RequiresPty);
```

#### C6. `SandboxExecutionResult`
```csharp
namespace Omicron.Core.Sandboxing;

public sealed record SandboxExecutionResult(
    int? ExitCode,
    bool TimedOut,
    bool Cancelled,
    ReadOnlyMemory<byte> Stdout,
    ReadOnlyMemory<byte> Stderr,
    TimeSpan Duration,
    string? Error);
```

### Tests
- `Broker_Execute_ReadPath_NoApprovalNeeded`
- `Broker_Execute_ShellMediumRisk_PromptsApproval`
- `Broker_Execute_ShellHighRisk_ApprovalRequired`
- `Broker_Execute_Rejected_ReturnsError`
- `Broker_Execute_Transaction_CreatesOverlay`
- `Broker_Execute_NoProvider_UsesLocalShell`
- `Broker_Execute_Environment_ScrubsSecrets`
- `Broker_Execute_Timeout_RespectsResourceLimit`
- `Broker_Emit_Events_StartAndFinish`

### Acceptance Criteria
- `ExecutionBroker` classifies risk for every request.
- Medium+ risk requests prompt for permission before execution.
- Rejected requests emit `PermissionRejectedEvent` and return error.
- Approved requests emit `PermissionApprovedEvent` and `SandboxExecutionStartedEvent`.
- Environment variables in `ScrubbedVars` are removed before execution.
- Timeout is enforced even without a sandbox provider.
- Transaction overlay is created when `RequiresTransaction` is true.

---

## Phase D: Audit Events

### Goals
All sandbox activity emits durable events.

### Deliverables

#### D1. `SandboxExecutionStartedEvent`
```csharp
namespace Omicron.Core.Sandboxing.Events;

public sealed record SandboxExecutionStartedEvent(
    EventEnvelope Envelope,
    SandboxExecutionId ExecutionId,
    SandboxPolicyId PolicyId,
    string Executable,
    IReadOnlyList<string> Args,
    WorkspacePath? WorkingDirectory,
    SandboxRiskLevel RiskLevel) : OmicronEvent(Envelope);
```

#### D2. `SandboxExecutionFinishedEvent`
```csharp
namespace Omicron.Core.Sandboxing.Events;

public sealed record SandboxExecutionFinishedEvent(
    EventEnvelope Envelope,
    SandboxExecutionId ExecutionId,
    int? ExitCode,
    bool TimedOut,
    bool Cancelled,
    TimeSpan Duration,
    string? Error) : OmicronEvent(Envelope);
```

#### D3. `SandboxPolicyAppliedEvent`
```csharp
public sealed record SandboxPolicyAppliedEvent(
    EventEnvelope Envelope,
    SandboxPolicyId PolicyId,
    SandboxRiskLevel RiskLevel,
    string Reason) : OmicronEvent(Envelope);
```

#### D4. `PermissionApprovedEvent`
```csharp
public sealed record PermissionApprovedEvent(
    EventEnvelope Envelope,
    PermissionRequestId RequestId,
    string Title) : OmicronEvent(Envelope);
```

#### D5. `PermissionRejectedEvent`
```csharp
public sealed record PermissionRejectedEvent(
    EventEnvelope Envelope,
    PermissionRequestId RequestId,
    string Title) : OmicronEvent(Envelope);
```

Register all new event types in `OmicronEventRegistry`.

### Tests
- `Event_SandboxStarted_RoundTripsThroughJsonlStore`
- `Event_SandboxFinished_RoundTripsThroughJsonlStore`
- `Event_PolicyApplied_RoundTrips`

### Acceptance Criteria
- All sandbox events round-trip through `JsonlSessionStore`.
- `OmicronEventRegistry` includes all new types.
- Event registry completeness test passes.

---

## Phase E: Policy UX

### Goals
Expose risk classification and approval flow in the CLI/TUI.

### Deliverables

#### E1. `SandboxPrompts`
```csharp
namespace Omicron.CLI;

public static class SandboxPrompts
{
    public static bool PromptForApproval(RiskExplanation explanation);
}
```

Console mode:
```text
⚠️  Medium risk command requested
   Command: npm install
   Concerns:
     - Network access to external hosts
     - File writes to workspace
   Mitigations:
     - Workspace writes go through transaction overlay
   Approve? (y/n/details):
```

TUI mode (Plan 7.1):
- Render a modal overlay at center screen with risk level color:
  - Safe/Low: green
  - Medium: yellow
  - High: orange
  - Critical: red
- Show command text, concerns, mitigations.
- Keybindings: `y` approve, `n` reject, `d` show details (full policy JSON).

#### E2. CLI `/status` integration
Update `/status` to show:
```text
Sandbox: local-none (no isolation)
Policy: default
Risk: Medium
Last command: npm install (approved)
```

#### E3. Config integration
Add sandbox settings to `AgentConfig`:
```toml
[sandbox]
default_policy = "default"
auto_approve_safe = true
auto_approve_low = false
auto_approve_medium = false
auto_approve_high = false
scrub_env = ["OPENAI_API_KEY", "AWS_SECRET_ACCESS_KEY", "GITHUB_TOKEN"]
```

### Tests
- `Prompt_Safe_AutoApprovesWhenConfigured`
- `Prompt_Medium_WaitsForUserInput`
- `Prompt_Critical_RequiresExplicitApproval`

### Acceptance Criteria
- Safe operations auto-approve when configured.
- Medium+ operations pause for user approval in console mode.
- TUI mode shows a colored risk modal.
- Rejected commands do not execute.
- Config controls auto-approval thresholds.

---

## Performance Targets

| Scenario | Target |
|----------|--------|
| Risk classification of a command | <1 ms |
| Permission prompt display | <50 ms |
| Environment scrubbing | <1 ms |
| Event emission | <1 ms |

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Risk classifier has false negatives | Start conservative; classify unknown commands as Medium; whitelist safe commands explicitly. |
| Users disable sandboxing out of frustration | Keep defaults strict but allow config overrides; log when policy is relaxed. |
| `HostDirectProvider` name must not imply safety | Name it "Host Direct (no OS isolation)"; warn in docs and UI that policy enforcement lives in the broker. |
| Transaction overlay conflicts with sandbox provider mounts | Design overlay to be composable; provider mounts on top of overlay or replaces it. |
| Scrubbed env var list is incomplete | Ship a good default list; allow user config extensions; never promise perfect scrubbing. |
| Audit events grow session log quickly | Events are small; compression/rotation is a future persistence concern, not a sandbox concern. |

---

## Definition of Done

- `SandboxPolicy` model covers filesystem, network, environment, resource limits, and interactivity.
- `SandboxPolicyRegistry` provides built-in policies.
- `RiskClassifier` categorizes tool calls and shell commands.
- `ExecutionBroker` enforces policy, prompts for approval, scrubs environment, and applies timeouts.
- `ISandboxProvider` abstraction exists with `HostDirectProvider` fallback.
- All brokered executions emit `SandboxExecutionStartedEvent` and `SandboxExecutionFinishedEvent`.
- CLI/TUI shows risk explanations and approval prompts.
- Config supports auto-approval thresholds and scrubbed variable lists.
- All tests pass with 0 warnings.
