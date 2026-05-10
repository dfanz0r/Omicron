# Code Review 0057: Workspace Transactions Phase 4 Review

Date: 2026-05-08  
Scope: review of initial Plan 3 Phase 4 workspace transaction/diff implementation.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 320

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

Reviewed files:

```text
Omicron.Core/Workspace/WorkspaceDiff.cs
Omicron.Core/Workspace/WorkspaceTransaction.cs
Omicron.Core.Tests/WorkspaceTransactionTests.cs
```

## Summary

The implementation provides a useful first staging/diff/commit skeleton:

- staged writes/deletes/moves are tracked in memory;
- text diffs use the native diff stack (`TextLineSplitter`, `TextDiffEngine`, `UnifiedDiffRenderer`);
- binary modifications are metadata-only;
- commit/rollback/dispose behavior is covered for basic write/delete cases;
- tests pass and cover 11 transaction scenarios.

However, this is not yet the Phase 4 transaction abstraction described in the plan. The main missing piece is an overlay `IWorkspaceFileSystem` view: staged changes are visible in diffs, but not through a transaction VFS. There are also gaps around move semantics, binary added files, path validation timing, and commit atomicity.

## Blocking / High-Priority Findings

### 1. `IWorkspaceTransaction` does not expose transaction overlay `Files`

Files:

```text
Omicron.Core/Workspace/WorkspaceTransaction.cs
```

Plan 3 Phase 4 expected:

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

Current implementation exposes:

```csharp
IWorkspaceFileSystem Host { get; }
```

and staging methods live only on the concrete `WorkspaceTransaction`:

```csharp
StageWrite(...)
StageDelete(...)
StageMove(...)
```

Impact:

- callers using the interface cannot stage changes;
- there is no overlay VFS where reads/stat/list reflect staged state;
- future edit tools cannot be written against `IWorkspaceTransaction.Files` as planned;
- acceptance criteria “transaction read overlay sees staged changes” is not met.

Recommendation:

Add an overlay `IWorkspaceFileSystem` implementation exposed as:

```csharp
public IWorkspaceFileSystem Files { get; }
```

`Files.WriteFileAsync/DeleteAsync/MoveAsync` should stage changes. `Files.ReadFileAsync/StatAsync/ReadDirectoryAsync` should reflect staged writes/deletes/moves without mutating host.

Concrete `StageWrite/StageDelete/StageMove` can remain as convenience helpers, but the interface should support the VFS path.

---

### 2. Move support is not represented in the diff model

Files:

```text
Omicron.Core/Workspace/WorkspaceDiff.cs
Omicron.Core/Workspace/WorkspaceTransaction.cs
```

`WorkspaceChangeKind` has only:

```csharp
Modified,
Added,
Deleted
```

but the transaction supports `StageMove(...)`. Diff currently represents a move as added + deleted entries. The Phase 4 brief called for move support and suggested `Moved` as a change kind.

Impact:

- transaction diff loses move intent;
- future audit/edit UX cannot distinguish rename/move from delete+add;
- `OldPath` is underused for move semantics.

Recommendation:

Add:

```csharp
Moved
```

to `WorkspaceChangeKind`, and represent staged moves as a single `WorkspaceFileDiff` where possible:

```csharp
new WorkspaceFileDiff(
    Path: to,
    Kind: WorkspaceChangeKind.Moved,
    OldPath: from.Value,
    ...)
```

If content also changes after move, either represent as moved+modified metadata or one moved diff with text diff comparing old source to new destination content.

---

### 3. Added binary files are decoded/rendered as text

File:

```text
Omicron.Core/Workspace/WorkspaceTransaction.cs
```

`BuildTextDiff(...)` treats `stat is null` as a new text file:

```csharp
var newText = Encoding.UTF8.GetString(newContent.Span);
var diff = ComputeAddedDiff(newText);
return new WorkspaceFileDiff(path, WorkspaceChangeKind.Added, null, diff, false, false);
```

For added binary files, there is no host `FileStat`, so extension-based binary detection from `HostWorkspaceFileSystem` is not applied.

Impact:

- adding `image.png` or `.zip` can decode arbitrary bytes as UTF-8 and render garbage/replacement chars into diffs;
- LLM-facing diff may include binary-like content.

Recommendation:

Expose or duplicate a binary detection helper for staged paths/content. Minimum MVP:

- detect common binary extensions for added files;
- optionally scan bytes for NUL/control-heavy content;
- return metadata-only diff for added binary files.

Add test:

```text
AddedBinaryFile_ShowsBinaryMarker_AndNoTextDiff
```

---

### 4. Path containment is delayed until diff/commit and not covered by transaction tests

File:

```text
Omicron.Core/Workspace/WorkspaceTransaction.cs
```

`StageWrite/StageDelete/StageMove` store `WorkspacePath.Value` directly. Manually constructed unsafe paths are not rejected until `_host.StatAsync`, `_host.WriteFileAsync`, etc. are called.

This may still be safe because host VFS validates at operation boundaries, but transaction overlay state can hold unsafe paths temporarily.

Recommendation:

Validate staged paths immediately using host boundary methods or add a private validation helper that calls host containment logic. At minimum, add tests:

- `StageWrite_Traversal_ThrowsOrCommitRejectsWithoutWritingOutsideRoot`
- `StageMove_TraversalDestination_ThrowsOrCommitRejectsWithoutWritingOutsideRoot`

Prefer fail-fast on staging for a cleaner API.

---

### 5. Commit marks transaction committed before operations complete

File:

```text
Omicron.Core/Workspace/WorkspaceTransaction.cs
```

Current code:

```csharp
ThrowIfDisposed();
ThrowIfCommitted();
_committed = true;

foreach (...)
{
    await _host.WriteFileAsync(...)
    ...
}

_staged.Clear();
```

If an operation fails midway, the transaction is marked committed and cannot be retried/rolled back, while some host changes may have been applied.

Impact:

- partial commit can leave host mutated and transaction unusable;
- not atomic, even at MVP level.

Recommendation:

For MVP, at least set `_committed = true` only after all operations succeed. Longer term, commit should use a safer apply plan with backup/rollback or transaction journal.

Add a future hardening item if full atomic host commit is deferred.

## Medium-Priority Findings

### 6. Diff paths are hardcoded in rendered text

File:

```text
Omicron.Core/Workspace/WorkspaceTransaction.cs
```

Examples:

```csharp
UnifiedDiffRenderer.Render("old", "new", ...)
UnifiedDiffRenderer.Render("/dev/null", "path", ...)
```

This loses actual file path context in `TextDiff`.

Recommendation:

Use real paths:

```csharp
UnifiedDiffRenderer.Render(path.Value, path.Value, ...)
UnifiedDiffRenderer.Render("/dev/null", path.Value, ...)
```

### 7. `CommitAsync` does not accept a reason and no audit events are emitted

The plan mentioned commit reason/audit events. Given the earlier decision not to invent random session IDs, deferring audit is acceptable, but the plan/baseline should explicitly note this if not implemented now.

Recommended for now:

- keep audit deferred;
- optionally add `CommitAsync(string? reason = null, ...)` later when transaction manager/session context exists.

### 8. Tests do not cover staged move behavior

`StageMove` exists but has no tests.

Add tests for:

- move visible in diff;
- commit move applies host rename;
- rollback move leaves host unchanged;
- move destination parent directory behavior;
- move binary file metadata-only diff.

### 9. No transaction manager/host integration yet

Transactions are directly constructed:

```csharp
new WorkspaceTransaction(_host)
```

This is fine for first implementation, but the next integration step should expose a manager/factory from `OmicronHost` rather than requiring callers to know concrete `HostWorkspaceFileSystem`.

## Recommendation

Do a focused follow-up before marking Phase 4 complete:

1. Add transaction overlay `Files : IWorkspaceFileSystem` and route staging through it.
2. Represent moves explicitly with `WorkspaceChangeKind.Moved` or document delete+add as MVP behavior.
3. Fix added binary file handling.
4. Use real paths in unified diff headers.
5. Move `_committed = true` until after successful apply.
6. Add move, overlay read/stat/list, binary-added, and traversal tests.

The current implementation is a good staging/diff skeleton, but the overlay VFS gap is significant for the intended architecture.
