# RFC 0006: Persistence, Session History, VFS, and Workspace Snapshots

Status: **Canonical**

## Purpose

Define durable session history, session snapshots/checkpoints, workspace virtual file system boundaries, and immutable workspace snapshots.

Persistence is frontend-neutral. Persist authoritative data and rebuild derived UI/render caches.

## Current Implementation Snapshot

As of 2026-05-08, only user configuration is persisted: `ConfigManager` reads/writes TOML at `%APPDATA%/Omicron/config.toml` on Windows or `~/.config/omicron/config.toml` on Unix, storing API keys and general settings. Conversation transcripts live only in memory on `Agent`; there is no session catalog, event log, replay, checkpointing, VFS, workspace snapshot, transaction layer, or host reconciliation yet. Current `read_path` and `shell` tools access the host filesystem/processes directly with workspace-root containment rather than through the VFS described here.

## Persisted Data

Authoritative persisted data:

```text
session metadata
agent records/inventory
agent events + content blocks
tool-call records
permission decisions
session snapshots
terminal pane/session records
sub-agent task records and structured outputs
remote backend ids and agent placement metadata where applicable
captured shell command/output events
workspace lock/audit records
workspace snapshot manifests
workspace blobs
settings/plugin config
optional frontend attachment/subscription preferences
```

Derived/cache data:

```text
transcript layout indexes
markdown parse caches
syntax highlighting caches
search indexes
GUI/TUI view state caches
```

Search indexes are derived cache data, not authoritative workspace state. If Omicron adopts a local search/indexing backend such as `dmtrKovalenko/fff`, its index/cache should be treated as rebuildable and invalidated by workspace root, ignore rules, VFS backend identity, and future transaction/snapshot context.

Do not persist terminal frame buffers.

## Session History

A session is durable conversation/runtime history, not a frontend transcript.

```csharp
public sealed record SessionRecord(
    SessionId Id,
    string? Title,
    WorkspaceId? WorkspaceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    SessionSnapshotId? LatestSnapshotId,
    WorkspaceSnapshotId? LatestWorkspaceSnapshotId);

public sealed record PersistedAgentEvent(
    SessionId SessionId,
    AgentId? AgentId,
    long SessionSequence,
    long? AgentSequence,
    AgentEvent Event);
```

Session history must support:

- listing recent sessions by workspace/time/title/tags/model
- reopening a session in TUI or GUI
- replaying a session from event stream
- listing active/archived agents within a session
- replaying or backfilling a specific agent transcript/event stream
- forking a prior point into a new branch
- pruning/archiving without corrupting workspace snapshots

## Session Snapshots

The event stream is authoritative. Snapshots are checkpoints of reconstructed session state at an event sequence.

```csharp
public sealed record SessionSnapshot(
    SessionSnapshotId Id,
    SessionId SessionId,
    long EventSequence,
    WorkspaceSnapshotId? WorkspaceSnapshotId,
    SessionState State,
    DateTimeOffset CreatedAt);
```

Use snapshots for:

- fast session resume
- crash recovery
- replay checkpoints
- branch points before risky operations
- compaction/summarization checkpoints

Policy examples:

```text
create session snapshot when:
  session starts or resumes
  every N events or M minutes
  before/after tool batches that modify workspace state
  before context compaction/summarization
  when user explicitly creates a checkpoint
```

Rule:

> A session snapshot references immutable content, active agent inventory, event cursors, and workspace snapshot IDs. It does not copy terminal UI state, remoting transport state, or frontend frame buffers.

## Workspace Virtual File System

Tools and agent workflows should use a workspace file system abstraction, not direct uncontrolled host FS access.

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

Initial implementation can be host-backed with change tracking. Later implementations can add overlays, remote workspaces, containers, and sandbox mounts.

The VFS boundary enables:

- permission review
- diffs
- rollback
- snapshots
- replay
- future sandbox promotion
- path normalization/traversal prevention
- file/path/symbol locking for concurrent agents
- optimistic precondition checks for stale edits

## Workspace Locking

Multiple active agents, including agents running on a remote backend host, and parallel tools require explicit workspace coordination.

Lock types should include:

```text
read lock
write lock
intent lock
tree/directory lock
optional symbol/AST-node lock
```

A lock should be represented as a lease with an owner, timeout, and cancellation behavior. Failed or cancelled agent tasks must release their leases.

```csharp
public sealed record WorkspaceLockRequest(
    WorkspaceId WorkspaceId,
    AgentTaskId? OwnerTaskId,
    WorkspacePath Path,
    WorkspaceLockKind Kind,
    WorkspaceLockMode Mode,
    TimeSpan? LeaseDuration);
```

Write transactions should also validate optimistic preconditions such as observed file hash, observed snapshot ID, anchor table version, or AST symbol version. See RFC 0012 for the full sub-agent and edit-harness design.

## Workspace Snapshots

A workspace snapshot captures the file tree visible to Omicron at a point in time. Prefer content-addressed immutable storage.

```text
WorkspaceSnapshot
  id
  workspace id
  parent snapshot id(s)
  root tree hash
  created timestamp
  reason/user label

TreeManifest
  path entries
  file metadata
  blob hashes

BlobStore
  hash -> compressed file bytes
```

Workspace snapshots support:

- pre-tool and post-tool checkpoints
- diffs between snapshots
- rollback
- replay with historical workspace state
- branching/experimentation
- future sandbox promotion, where approved changes merge from isolated layer into real workspace

## Workspace Transactions

Workspace mutations occur through transactions.

```csharp
public interface IWorkspaceTransaction : IAsyncDisposable
{
    WorkspaceTransactionId Id { get; }
    IWorkspaceFileSystem Files { get; }

    ValueTask<WorkspaceDiff> GetDiffAsync(CancellationToken ct);
    ValueTask<WorkspaceSnapshotId> CommitAsync(string reason, CancellationToken ct);
    ValueTask RollbackAsync(CancellationToken ct);
}
```

Recommended flow:

```text
tool requests workspace write
  ↓
core opens workspace transaction / overlay
  ↓
tool writes through IWorkspaceFileSystem
  ↓
core computes diff
  ↓
permission policy decides auto-commit vs ask user
  ↓
commit creates WorkspaceTransactionCommitted + workspace snapshot
  ↓
session snapshot references latest workspace snapshot
```

## Host Reconciliation

Host-backed workspaces need change ingestion:

- file watcher events
- external modification detection
- stale snapshot warnings
- user-visible conflict resolution
- explicit rescan/rebase commands

## Design Decisions

1. Event stream is authoritative session history.
2. Session snapshots speed resume/replay but do not replace events.
3. Workspace access goes through VFS.
4. Workspace snapshots are immutable and content-addressed.
5. Mutations happen through transactions/overlays.
6. UI render caches are derived and disposable.
7. Per-agent event cursors and agent inventory are persisted so remote frontends can reconnect, foreground quiet agents, and backfill missed events as described in RFC 0014.
