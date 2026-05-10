# Implementation Plan 0003: Core Persistence, Workspace, and RFC Foundations

Status: Phase 1 (Durable Event Log and Session Catalog) complete. Phase 2 (Session Replay Projection) complete. Phase 3 (Host-Backed Workspace VFS v1) in progress.  
Primary RFCs: RFC 0001, RFC 0002, RFC 0006, RFC 0012  
Secondary RFCs unblocked: RFC 0007, RFC 0010, RFC 0014

## Purpose

Plan 1/1.5 stabilized the core runtime shape. Plan 2 added provider API abstraction and Responses support. Plan 2.5 improved CLI usability.

The next core work should target the RFC foundations that unlock the largest number of future capabilities without committing to the full TUI/GUI stack yet:

1. durable session/event persistence;
2. host-backed workspace VFS with diffs/transactions;
3. semantic content/command primitives that future TUI, GUI, plugins, and slash commands can share;
4. a first high-reliability edit harness on top of the VFS.

This plan is intentionally core-first. It avoids starting the renderer/TUI before session history, workspace state, and command/content abstractions are stable.

## Current State Summary

Implemented foundations:

- `OmicronHost` composition root.
- `AgentSession` active runtime.
- typed `OmicronEvent` records and `IEventSink` with global sequence stamping.
- provider registry/model catalog/API shape routing.
- OpenAI/OpenRouter Responses support, including stateless OpenRouter default.
- provider state manager.
- tool registry and built-in tools.
- workspace abstraction exists, but is still minimal and not a VFS/transaction layer.
- execution broker exists, but sandbox policy model is not yet implemented.
- CLI slash commands exist, but are CLI-local rather than semantic shared commands.

Important gaps against RFCs:

- no VFS transactions, diffs, manifests, or rollback (Phase 3 target);
- no edit harness beyond direct file tools;
- no session snapshots/checkpoints;
- no semantic `ContentBlock` / `UiNode` model;
- no plugin manifest/capability model;
- no command result/event model shared by CLI/TUI/GUI/plugins;
- no sandbox policy provider;
- no remoting event cursor story.

## Recommended Next Direction

### Why not TUI/renderer next?

The RFCs allow text/rendering work to run in parallel, but the current product will benefit more from durable core/workspace semantics first. A TUI without durable sessions, workspace transactions, and replay-safe command/content events would likely need rework.

### Why persistence/workspace first?

RFC 0006 is a dependency for:

- session resume/replay;
- workspace snapshots/diffs;
- sandbox overlays (RFC 0007);
- edit harness preconditions (RFC 0012);
- sub-agent workspace views/locks (RFC 0012);
- remoting reconnect cursors (RFC 0014).

This is the best next core multiplier.

## Phase 0: Documentation/Baseline Reconciliation

Before implementation, update docs so they match the current code.

Deliverables:

- update `docs/rfcs/IMPLEMENTATION-BASELINE.md` for:
  - Plan 2 completed provider compatibility/storage policy/Responses support;
  - canonical conversation seed and tool-call ID mapper;
  - OpenRouter `gpt-5.4-mini` test model;
  - OpenRouter Responses stateless default;
  - CLI slash commands, tab completion, no initial picker;
  - duplicate tool-call ID normalization;
  - current test count.
- mark `docs/implementation-plans/0002-provider-api-abstraction.md` complete or implemented.
- mark `docs/implementation-plans/0002.5-cli-slash-commands.md` in-progress/complete depending on `/config` and `/refresh` status.

Acceptance criteria:

- baseline no longer says first-class Responses/canonical conversation are not implemented.
- baseline accurately distinguishes MVP canonical conversation seed from full typed conversation runtime.

## Phase 1: Durable Event Log and Session Catalog

RFCs: 0001, 0006, 0014 prerequisite.

### Goals

Make `IEventSink` persistable and introduce session records without changing `AgentSession` behavior too much.

### Deliverables

Add a persistence abstraction, likely in `Omicron.Core` first, then split later:

```csharp
public interface ISessionStore
{
    ValueTask<SessionRecord> CreateSessionAsync(SessionCreateRequest request, CancellationToken ct);
    ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(SessionListQuery query, CancellationToken ct);
    ValueTask<SessionRecord?> GetSessionAsync(SessionId sessionId, CancellationToken ct);
    ValueTask AppendEventsAsync(SessionId sessionId, IReadOnlyList<OmicronEvent> events, CancellationToken ct);
    IAsyncEnumerable<OmicronEvent> ReadEventsAsync(SessionId sessionId, EventSequenceRange range, CancellationToken ct);
}
```

Initial implementation:

- `InMemorySessionStore` for tests;
- optional JSONL file-backed store for MVP durability.

Event sink options:

- wrap `IEventSink` with persistent append behavior; or
- introduce `PersistentEventSink : IEventSink` that writes to session store after stamping.

Add records:

```csharp
public sealed record SessionRecord(...);
public sealed record SessionCreateRequest(...);
public sealed record SessionListQuery(...);
public readonly record struct EventSequenceRange(long? FromExclusive, long? ToInclusive);
```

### Acceptance Criteria

- session start creates/records `SessionRecord` metadata;
- events can be appended and read back in sequence order;
- `AgentSession` can run against persistent event sink without code changes to providers/tools;
- tests prove replay read order and sequence stability;
- no UI rendering state is persisted.

## Phase 2: Session Replay Projection

RFCs: 0001, 0006.

### Goals

Allow reconstructed session state from events. This is necessary before resume/checkpoints become meaningful.

### Deliverables

Add a projection type:

```csharp
public sealed class SessionProjection
{
    public SessionId SessionId { get; }
    public IReadOnlyList<Message> Messages { get; }
    public IReadOnlyDictionary<ProviderStateKey, ProviderTurnState> ProviderStates { get; }
    public bool IsReset { get; }
}

public interface ISessionProjector
{
    SessionProjection Project(IEnumerable<OmicronEvent> events);
}
```

Initial projection should handle:

- session started/reset/error;
- user messages;
- assistant complete events;
- tool invocation start/complete enough to reconstruct tool result summaries;
- provider state updated/cleared.

Note: today not every `Message` detail is represented perfectly in events. If necessary, add missing event fields rather than relying on in-memory `Message` lists.

### Acceptance Criteria

- replaying event log reconstructs enough transcript/provider state for display/debug;
- reset clears projected messages/provider state;
- provider state clear/update projection matches `ProviderStateManager` behavior;
- tests cover a tool-call transcript and reset.

## Phase 3: Host-Backed Workspace VFS v1

RFCs: 0006, 0012 prerequisite.

### Goals

Move from basic workspace file tools toward VFS semantics without immediately implementing overlays/blob manifests.

### Deliverables

Introduce or evolve `IWorkspace` into an RFC-shaped file system interface:

```csharp
public interface IWorkspaceFileSystem
{
    ValueTask<FileStat?> StatAsync(WorkspacePath path, CancellationToken ct);
    ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(WorkspacePath path, CancellationToken ct);
    ValueTask<ReadOnlyMemory<byte>> ReadFileAsync(WorkspacePath path, CancellationToken ct);
    ValueTask WriteFileAsync(WorkspacePath path, ReadOnlyMemory<byte> content, CancellationToken ct);
    ValueTask DeleteAsync(WorkspacePath path, CancellationToken ct);
    ValueTask MoveAsync(WorkspacePath from, WorkspacePath to, CancellationToken ct);
}
```

Add primitives:

```csharp
public readonly record struct WorkspacePath(string Value);
public sealed record FileStat(...);
public sealed record DirectoryEntry(...);
public sealed record WorkspaceDiff(...);
```

Initial implementation:

- `HostWorkspaceFileSystem` backed by current workspace root;
- path containment/normalization preserved;
- read/write/delete/move events emitted through `IEventSink` or returned as operations for later persistence.

Update tools:

- `read_path` should use `IWorkspaceFileSystem` or adapter;
- add write/edit tools only after transaction/precondition support exists.

### Acceptance Criteria

- all file reads go through VFS abstraction;
- traversal attacks remain blocked;
- tests cover stat/read/list/path containment;
- event or audit hooks for file operations are deferred to Phase 4 (transactions/diffs) to avoid audit model churn before transaction semantics are stable.

## Phase 4: Workspace Transactions and Diff v1

RFCs: 0006, 0012, 0007 prerequisite.

### Goals

Before edit tools mutate files, introduce transaction/diff semantics.

### Deliverables

```csharp
public interface IWorkspaceTransaction : IAsyncDisposable
{
    WorkspaceTransactionId Id { get; }
    IWorkspaceFileSystem Files { get; }
    ValueTask<WorkspaceDiff> GetDiffAsync(CancellationToken ct);
    ValueTask CommitAsync(string reason, CancellationToken ct);
    ValueTask RollbackAsync(CancellationToken ct);
}
```

Initial implementation can be simple:

- transaction captures original file bytes for touched files;
- writes are staged in memory or temp files;
- `GetDiffAsync` returns text diff for changed text files;
- commit writes to host FS;
- rollback discards staged changes.

No content-addressed blob store required yet.

### Acceptance Criteria

- write/delete/move can be staged;
- diff is inspectable before commit;
- rollback leaves workspace unchanged;
- commit emits audit events;
- tests cover commit/rollback/text diff.

## Phase 5: Semantic Content and Command Primitives

RFCs: 0002, 0013, future TUI/GUI/plugins.

### Goals

Start extracting frontend-neutral output/command semantics from CLI-only code.

### Deliverables

Add core/shared records:

```csharp
public abstract record ContentBlock;
public sealed record PlainTextContentBlock(...);
public sealed record MarkdownContentBlock(...);
public sealed record CodeContentBlock(...);
public sealed record DiffContentBlock(...);
public sealed record ToolCallContentBlock(...);

public sealed record CommandRef(string Id, JsonElement? Args);
public sealed record CommandResult(...);
```

Bridge existing events/tool results into content blocks for display.

Slash commands should eventually map to command IDs, but do not need a full plugin system yet.

### Acceptance Criteria

- assistant/tool outputs can be represented as content blocks;
- CLI can still render plain text from content blocks;
- no terminal/ANSI dependencies in content model;
- commands have stable semantic IDs independent of slash text.

## Phase 6: First Edit Harness Tool

RFCs: 0012, depends on Phase 3/4.

### Goals

Introduce a safer edit tool than exact text replacement or ad hoc file writes.

### Recommended first tool

Hashline/stateless line-anchor edit:

- tool reads file with line hashes;
- model submits patch anchored by line number + hash;
- harness validates hashes before applying;
- on mismatch, returns model-actionable error with nearby context/hashes.

Candidate tool:

```text
edit_file_hashline
```

Input:

```json
{
  "path": "Omicron.Core/Foo.cs",
  "edits": [
    {
      "start_line": 42,
      "start_hash": "abc123",
      "old_text": "...",
      "new_text": "..."
    }
  ]
}
```

Acceptance criteria:

- rejects stale line/hash;
- returns structured error with current line/hash/context;
- applies multiple non-overlapping edits transactionally;
- integrates with workspace transaction/diff;
- tests cover success, stale hash, overlapping edits, rollback.

## Parallel Track Recommendation: Text/Rendering Spike

In parallel with Plan 3, a separate Plan 4 should start RFC 0003/0013 foundations:

- `Utf8TextStore` managed implementation first;
- grapheme/cell width abstraction;
- markdown/code block language detection;
- simple content block renderer-independent tests.

Do not block persistence/workspace on native FFI. Native slabs and Rust FFI can be introduced after managed interfaces stabilize.

## Future Hardening Backlog

Non-blocking hardening items discovered during Plan 3 reviews are tracked in:

```text
docs/implementation-plans/FUTURE-HARDENING-BACKLOG.md
```

Current persistence/event-store items:

- FH-0001: JSONL concurrency is MVP-level.
- FH-0002: `PersistentEventSink` diagnostic counters are not atomic.
- FH-0003: JSONL event deserialization switch requires manual maintenance when new event types are added.

These do not block Phase 2 replay projection, but should be revisited before remoting, multi-frontend usage, sub-agents, or production-grade persistence.

## What Not To Do Next

Avoid these as immediate next steps:

- full TUI renderer before persistence/workspace/content blocks;
- WASM plugins before command/capability model stabilizes;
- sub-agents before VFS transactions/locks;
- sandbox provider selection before VFS transaction/audit hooks;
- GUI before TUI/core protocols mature.

## Proposed Next Implementation Plans

Create these next:

```text
0003-core-persistence-workspace-foundations.md   (this plan)
0004-text-content-foundations.md                 (RFC 0003/0013 managed spike)
0005-edit-harness-hashline.md                    (after VFS transaction v1)
0006-semantic-ui-command-foundations.md          (if split from this plan)
```

## Definition of Done for Plan 3

Plan 3 is done when:

- sessions/events can be persisted and replayed enough for transcript/provider-state reconstruction;
- host-backed VFS exists and tools use it for file access;
- workspace transactions can stage, diff, commit, and rollback file changes;
- semantic content/command primitives exist for future TUI/GUI/plugin use;
- a first hashline edit tool works transactionally;
- tests cover persistence, replay, VFS containment, transaction rollback, and edit stale-anchor failures;
- CLI still works against the new abstractions.
