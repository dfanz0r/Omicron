# RFC 0012: Agent Orchestration, VFS Locking, and Edit Harness

Status: **Research/Planned**

## Purpose

Define Omicron's architecture for sub-agents, multiple concurrently active agents, VFS-level concurrency control, and high-reliability edit tools.

This RFC incorporates ideas from:

- Can Bölük, “I Improved 15 LLMs at Coding in One Afternoon. Only the Harness Changed.”
  - https://blog.can.ac/2026/02/12/the-harness-problem/
- Dirac, “Hash anchors + Myers diff + single-token anchors: 60% cheaper AI code edits”
  - https://dirac.run/posts/hash-anchors-myers-diff-single-token

The key lesson is that model quality is not the only bottleneck. The harness — tool schemas, edit protocols, error messages, state management, concurrency behavior, and workspace interface — can dramatically change success rate, cost, and reliability.

## Goals

- Support sub-agents as first-class Omicron tasks.
- Support multiple active agents running concurrently, including multiple agents per backend host.
- Support scheduling/delegating agents to remote backends where appropriate.
- Prevent parallel agents/tools from corrupting workspace state.
- Provide VFS-level file/path locking and transactional isolation.
- Provide efficient, reliable edit mechanisms beyond exact string replacement.
- Support hash/anchor-based editing and evaluate Dirac-style single-token anchors.
- Support AST/context-curation features where they improve reliability and token efficiency.
- Preserve session history, provenance, snapshots, and auditability across sub-agent work.

## Non-Goals

- Let agents directly mutate the host filesystem without VFS/transaction mediation.
- Treat sub-agent transcripts as raw context dumps for the parent agent.
- Require a single edit format for every model and every task.
- Require AST support for every language in the first implementation.

## Harness Principle

> Omicron should optimize the interface between model intent and workspace change as aggressively as it optimizes model selection.

The edit harness should make correct edits easy for models and incorrect edits easy to reject before corruption occurs.

## Sub-Agent Model

A sub-agent is a child execution context launched by a parent session or coordinator.

```csharp
public sealed record AgentTaskId(Guid Value);

public sealed record AgentTaskSpec(
    AgentTaskId Id,
    SessionId ParentSessionId,
    AgentId? AgentId,
    BackendId? PreferredBackendId,
    string Role,
    string Prompt,
    ModelRef? Model,
    ToolPolicy ToolPolicy,
    WorkspaceViewSpec WorkspaceView,
    AgentTaskOutputContract OutputContract,
    AgentResourceBudget Budget);
```

`AgentTaskId` identifies the scheduled unit of work. `AgentId` identifies the running agent actor once created. `PreferredBackendId` is optional placement guidance for remote/distributed execution; the coordinator may choose a different backend based on capability, load, policy, or availability.

Sub-agents may be used for:

- code search/scouting;
- planning;
- implementation on isolated branches/overlays;
- review;
- test execution;
- documentation;
- parallel hypothesis exploration;
- long-running background analysis.

### Local Search Engine Candidate

For code search/scouting tools, Omicron should evaluate `dmtrKovalenko/fff` as a possible local file search/indexing backend.

Potential use cases:

- fuzzy file/path search for agent scouting;
- grep/plain/regex/fuzzy content search;
- repeated workspace queries against an indexed/watched cache;
- local search tools exposed to primary agents and sub-agents.

Important architectural constraint: search results must flow through Omicron's workspace/search abstraction and respect workspace containment, future transaction overlays, ignore rules, and permissions. Tool schemas should not depend directly on fff-specific API shapes.

This candidate ties into RFC 0011's Rust/C# interop research because fff provides both a Rust core and C ABI option.

## Multiple Parallel Active Agents

Omicron should support more than one active agent loop at a time. This requires a coordinator/scheduler.

```text
AgentCoordinator
  active tasks
  queued tasks
  model/provider concurrency limits
  workspace lock manager
  sandbox execution broker
  event fan-out
  remote backend placement
  foreground/background/quiet subscription hints
  cancellation/budget enforcement
```

Concurrency controls:

- max active agents globally;
- max active agents per backend host;
- max active agents per workspace;
- max model requests per provider/model;
- max tool executions per workspace;
- max sandbox executions;
- per-agent token/time/tool budgets;
- cancellation trees from parent to child tasks.

Sub-agent events should be part of the main session history without forcing the full sub-agent transcript into parent model context. Remote transport delivery, foregrounding, quieting, and per-agent subscriptions are defined in RFC 0014.

Representative events:

```csharp
public sealed record AgentTaskStarted(
    AgentTaskId TaskId,
    SessionId ParentSessionId,
    AgentId AgentId,
    BackendId? BackendId,
    string Role,
    WorkspaceSnapshotId? InputWorkspaceSnapshotId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record AgentTaskOutputProduced(
    AgentTaskId TaskId,
    JsonElement StructuredOutput,
    WorkspaceSnapshotId? OutputWorkspaceSnapshotId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);

public sealed record AgentTaskFinished(
    AgentTaskId TaskId,
    AgentTaskResultKind ResultKind,
    WorkspaceSnapshotId? OutputWorkspaceSnapshotId,
    DateTimeOffset Timestamp) : AgentEvent(Timestamp);
```

## Structured Sub-Agent Output

Parent agents should consume compact structured outputs, not raw child transcripts by default.

Examples:

```json
{
  "summary": "Found auth middleware and token validation path.",
  "findings": [
    { "path": "src/Auth.cs", "symbol": "ValidateToken", "confidence": 0.92 }
  ],
  "recommended_next_steps": ["Patch expiry validation", "Add regression test"]
}
```

This prevents the “raw sub-agent output leak” problem where parent context is polluted with large JSONL or verbose child traces.

Sub-agent transcript storage remains available for replay, debugging, UI expansion, and remote foreground/backfill when a user chooses to inspect a background agent.

## Workspace Views for Agents

Each agent task should run against an explicit workspace view:

```text
read-only view:
  can inspect files and indexes but cannot write

transactional overlay view:
  writes go into a private overlay; merge requires commit/review

shared write view:
  writes to shared transaction with locks; use sparingly

sandboxed view:
  mounted into execution sandbox with policy badges
```

Default for parallel implementation agents should be isolated overlays. Merge after diff/review.

## Relationship to Remote Backends

RFC 0014 defines the remoting protocol that exposes agents running on backend hosts. This RFC defines orchestration and workspace safety semantics. Together they imply:

- a backend host may run multiple active agents concurrently;
- each agent/task must be addressable independently over the remoting protocol;
- quiet/background observation modes must not stop agent execution;
- remote sub-agents should return structured outputs to parent agents by default;
- full transcripts remain durable and can be backfilled when foregrounded;
- cancellation and budget enforcement must work across backend boundaries;
- workspace locks and overlays are enforced by the backend that owns the workspace.

The coordinator should treat remote placement as a scheduling dimension, not a separate agent kind.

## VFS Locking

A custom VFS enables file/path locking and conflict management beyond what host filesystems provide.

Lock types:

```text
read lock:
  multiple readers allowed

write lock:
  exclusive mutation of a file/path

intent lock:
  declares planned mutation before reading/planning completes

tree lock:
  protects directory-level operations such as rename/delete/move

symbol lock:
  optional higher-level lock for AST symbol/node ownership
```

Representative API:

```csharp
public interface IWorkspaceLockManager
{
    ValueTask<WorkspaceLockLease> AcquireAsync(
        WorkspaceLockRequest request,
        CancellationToken ct);

    ValueTask<IReadOnlyList<WorkspaceLockInfo>> ListLocksAsync(
        WorkspaceId workspaceId,
        CancellationToken ct);
}

public sealed record WorkspaceLockRequest(
    WorkspaceId WorkspaceId,
    AgentTaskId? OwnerTaskId,
    WorkspacePath Path,
    WorkspaceLockKind Kind,
    WorkspaceLockMode Mode,
    TimeSpan? LeaseDuration);
```

Locking policy:

- edits acquire write locks for affected files;
- directory moves/deletes acquire tree locks;
- read-only tools normally do not require locks but can record observed snapshot IDs;
- long-running locks use leases and heartbeat renewal;
- failed/cancelled agent tasks release leases;
- deadlock prevention uses deterministic path ordering and timeouts.

## Optimistic Concurrency

Locks are not enough. Every write should also validate against the observed workspace snapshot or anchor state.

Recommended write preconditions:

```text
path exists/does-not-exist expectation
observed file content hash
observed workspace snapshot id
observed anchor version
optional AST/symbol version
```

If preconditions fail, the tool returns a structured conflict error and suggests re-read/rebase rather than blindly applying stale edits.

## Hashline / Anchor-Based Edits

Traditional edit tools often require the model to reproduce exact old text. This is fragile and token-expensive.

Hashline-style editing tags read lines with short content hashes:

```text
1:a3|public int Add(int x, int y) {
2:f1|    return x + y;
3:0e|}
```

The model edits by referencing anchors/ranges:

```json
{
  "path": "Calculator.cs",
  "start_anchor": "2:f1",
  "end_anchor": "2:f1",
  "replacement": "    checked { return x + y; }"
}
```

Benefits:

- model does not reproduce old text or whitespace;
- tool can validate that the file has not changed since read;
- output token cost tends toward replacement size rather than search+replacement size;
- stale edits are rejected before corruption.

## Dirac-Style Stateful Single-Token Anchors

Dirac improves on basic hashline anchors by using stateful single-token word anchors and a reconciler.

Core pieces to evaluate:

```text
Anchor:
  single-token label assigned by backend, not necessarily line number/hash

Delimiter:
  separates anchor from line content, e.g. §

Validator:
  validates model-proposed start/end anchors against current state/full lines

State Manager:
  tracks file -> line -> anchor mapping for task/session

Reconciler:
  uses Myers diff after edits or external file changes
  preserves anchors for unchanged lines
  assigns new anchors only to changed lines
```

Rationale from Dirac's design:

- line-number based anchors are invalidated by edits near the top of a file;
- stable stateful anchors survive unrelated line shifts;
- a finite list of single-token anchors reduces token overhead;
- Myers diff can preserve anchor identity for unchanged lines;
- updated anchors can be returned to the model after each edit.

Omicron should prototype both:

```text
stateless hashline:
  simpler, content-hash based, easy first implementation

stateful single-token anchors:
  lower token overhead and better multi-edit sessions, requires anchor state manager
```

## Anchor State and VFS Integration

Anchor state belongs with workspace/session state, not inside a frontend.

```text
WorkspaceSnapshot
  file content hash
  anchor table version
  AST index version

AnchorTable
  path
  file content hash
  line entries: anchor -> byte range / line hash / text hash
  used anchor set
  generation
```

Anchor reconciliation triggers:

- successful Omicron edit;
- external file watcher change;
- transaction rebase;
- workspace snapshot restore;
- sub-agent overlay merge.

The edit tool should return updated anchor information after applying changes.

## Edit Tool Portfolio

Omicron should not assume one edit protocol is universally best.

Candidate edit tools:

```text
exact_replace:
  simple, good for tiny precise edits

hashline_edit:
  stateless line hash anchors

stateful_anchor_edit:
  Dirac-style single-token anchors + state manager + reconciler

ast_edit:
  structural edits over supported languages/symbols

full_file_write:
  acceptable for small files or generated files

multi_file_batch:
  coordinated edits across files in one transaction
```

The harness can choose tools based on:

- model behavior;
- file size;
- edit size;
- language support;
- available AST index;
- token budget;
- conflict risk;
- whether multiple agents are active.

## AST and Context Curation

Dirac-style systems also point toward AST-aware context fetching and editing.

Potential features:

- parse files with Tree-sitter, Roslyn, or language-specific parsers;
- build symbol indexes;
- fetch minimal relevant definitions/references into context;
- provide symbol-level anchors;
- support structural edits such as rename symbol, replace method body, add import, add test case;
- detect conflicts at symbol/node level;
- improve sub-agent scouting by returning structured symbol findings.

AST support should be incremental and language-scoped. For C#, Roslyn is likely preferred. For broad multi-language coverage, Tree-sitter or Rust-backed parsers may be useful.

## Multi-File Batched Edits

Complex tasks often need coordinated edits across files.

A batch edit should run inside one workspace transaction:

```text
read anchor/AST state
  ↓
acquire locks in deterministic order
  ↓
validate preconditions
  ↓
apply edits to overlay
  ↓
run formatters/tests if requested
  ↓
compute workspace diff
  ↓
commit or rollback as one unit
```

This is important for sub-agents because many parallel tasks will otherwise produce incompatible partial edits.

## Error Messages as Harness Design

Edit failures should be model-actionable.

Bad:

```text
Edit failed.
```

Good:

```text
Anchor stale: Calculator.cs anchor `velvet` previously referred to line hash abc123, but current file hash is def456.
Re-read Calculator.cs or request anchor reconciliation before retrying.
```

Structured errors should include:

- failure kind;
- path;
- expected/current hashes;
- conflicting lock owner if any;
- suggested next action;
- compact updated anchors when available.

## Relationship to Sandbox and Plugins

- Sub-agents use the same execution broker and sandbox policies as normal tools.
- WASM plugins can register sub-agent tools or edit tools, but they cannot bypass VFS locks or execution broker.
- Parallel agents should not get ambient write access to host files.
- All writes flow through workspace transactions and lock/precondition checks.

## Design Decisions

1. Sub-agents are first-class task/session entities.
2. Multiple active agents require a coordinator, budgets, cancellation trees, and concurrency limits.
3. Parent agents consume structured sub-agent outputs by default, not raw transcripts.
4. Parallel implementation agents should use isolated workspace overlays by default.
5. VFS locking is mandatory for shared writes and useful for conflict diagnostics.
6. Writes validate both locks and optimistic preconditions.
7. Omicron should prototype hashline and Dirac-style stateful single-token anchor edits.
8. Myers diff reconciliation should preserve anchors for unchanged lines.
9. AST/context-curation features should be incremental, language-scoped, and measured empirically.
10. The edit harness is a high-leverage system component, not a minor tool detail.
