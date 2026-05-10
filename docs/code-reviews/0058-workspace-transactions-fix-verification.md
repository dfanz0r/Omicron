# Code Review 0058: Workspace Transactions Fix Verification

Date: 2026-05-08  
Scope: verification after fixes for `0057-workspace-transactions-phase-4-review.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 328

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

The implementation now covers the main Phase 4 shape much better:

- `IWorkspaceTransaction.Files` exposes an overlay `IWorkspaceFileSystem`.
- Staged writes/deletes/moves can be performed through the overlay VFS.
- Overlay reads/stat reflect some staged state.
- `WorkspaceChangeKind.Moved` exists.
- Added binary files are detected by extension/content heuristic.
- Diff headers now use real paths.
- Move commit and basic move diff are tested.

This is a substantial improvement. There are still correctness gaps in overlay semantics and commit ordering that should be fixed before marking Phase 4 complete.

## High-Priority Findings

### 1. Overlay read/stat do not handle `MoveTo`

File:

```text
Omicron.Core/Workspace/WorkspaceTransaction.cs
```

`TransactionFileSystem.StatAsync(...)` handles `Write`, `Delete`, and `MoveFrom`, but not `MoveTo`:

```csharp
if (_tx.TryGetStaged(path, out var entry))
{
    if (entry.Action == Delete || entry.Action == MoveFrom) return null;
    if (entry.Action == Write) ...
}
return await _host.StatAsync(path, ct);
```

`ReadFileAsync(...)` also only handles staged `Write`:

```csharp
if (_tx.TryGetStaged(path, out var entry) && entry.Action == Write)
    return entry.Content;
return _host.ReadFileAsync(path, ct);
```

For a staged move `source.txt -> dest.txt`, overlay behavior should be:

- `Stat(source.txt)` => null;
- `ReadFile(source.txt)` => empty/not found;
- `Stat(dest.txt)` => source stat under destination path;
- `ReadFile(dest.txt)` => source bytes.

Current behavior likely gives:

- source hidden via `MoveFrom`;
- destination falls through to host and appears missing.

Impact:

- transaction overlay does not faithfully represent staged move state;
- future edit/read tools using `tx.Files` will not see moved destination content before commit.

Recommendation:

Handle `MoveTo` in `StatAsync` and `ReadFileAsync` by reading from `entry.MoveFromPath` on host and projecting the path/metadata to destination.

Add tests:

```text
Overlay_StagedMove_SourceHidden_DestinationVisible
Overlay_ReadFile_StagedMoveDestination_ReturnsSourceContent
```

---

### 2. Overlay directory listing removes nested paths incorrectly

File:

```text
Omicron.Core/Workspace/WorkspaceTransaction.cs`
```

Directory removal logic constructs staged lookup paths from entry names only:

```csharp
var wsPath = new WorkspacePath(e.Name.TrimEnd('/'));
return _tx.TryGetStaged(wsPath, out var entry) && ...;
```

This works for root listings, but for a subdirectory listing like `src/`, entry `a.txt` is checked as `a.txt` instead of `src/a.txt`.

Impact:

- staged deletes/moves in subdirectories remain visible in overlay directory listings;
- staged writes may be added correctly using `Path.GetDirectoryName(p)`, but removals are path-relative wrong.

Recommendation:

When removing from a listing, combine current directory path with entry name:

```csharp
var fullRelative = string.IsNullOrEmpty(path.Value)
    ? e.Name.TrimEnd('/')
    : $"{path.Value.TrimEnd('/')}/{e.Name.TrimEnd('/')}";
```

Add tests for staged delete/move inside a subdirectory.

---

### 3. Commit applies `MoveFrom` and `MoveTo` based on dictionary order, risking stale writes/deletes around moves

File:

```text
Omicron.Core/Workspace/WorkspaceTransaction.cs
```

`StageMove` stores two entries:

```csharp
_staged[from] = MoveFrom(to)
_staged[to] = MoveTo(from)
```

`CommitAsync` iterates dictionary order and applies writes/deletes/moves in one pass. It skips `MoveFrom`, applies `MoveTo`.

This works for simple move-only cases, but order and overwrite semantics can become wrong when combined with writes/deletes, for example:

- stage move A -> B, then write B;
- write A, then move A -> B;
- move A -> B, delete B;
- move A -> B where B already exists.

Current staging model can overwrite entries in ways that lose intent.

Recommendation for MVP:

Define and test semantics for combinations, or explicitly restrict/throw for ambiguous combinations. Prefer a final-state model or operation list rather than two dictionary entries for moves if this grows.

At minimum add tests for:

```text
StageMove_ThenWriteDestination_CommitsWrittenDestination
StageWrite_ThenMove_CommitsMovedWrittenContent
StageMove_ToExistingDestination_DefinesBehavior
```

---

### 4. Commit still is not atomic and retry semantics can duplicate partial operations

The previous fix resets `_committed = false` on failure, allowing retry/rollback. That is better, but partial host changes may already have occurred. Retrying can fail differently or duplicate operations.

This is acceptable for MVP only if documented as non-atomic. Add to future hardening backlog.

## Medium-Priority Findings

### 5. `TransactionFileSystem` casts `tx.Host` back to `HostWorkspaceFileSystem`

File:

```text
Omicron.Core/Workspace/WorkspaceTransaction.cs
```

```csharp
_host = (HostWorkspaceFileSystem)tx.Host;
```

`WorkspaceTransaction` constructor already requires `HostWorkspaceFileSystem`, so this is not currently unsafe, but it undermines the interface shape. If transactions later support another `IWorkspaceFileSystem`, this breaks.

Recommendation:

Store/pass the concrete host directly to `TransactionFileSystem`, or change transaction constructor to accept `IWorkspaceFileSystem` and avoid host-specific assumptions. For now, this is non-blocking.

### 6. Overlay directory entries for staged text files use `LineCount = 1`

New staged text files are listed with line count `1` regardless of content:

```csharp
result.Add(new DirectoryEntry(name, false, entry.Content.Length, isBinary ? null : 1));
```

This is minor. Better to compute line count via `TextLineSplitter` if cheap.

### 7. Public staging API became internal only

`StageWrite/StageDelete/StageMove` are now `internal`, but tests still call them because tests have internals access. External callers will need to stage through `tx.Files`. This is okay, but future docs/examples should use `tx.Files` to avoid relying on internals.

## Recommendation

Do one more focused transaction pass before marking Phase 4 complete:

1. Make overlay `MoveTo` visible in `StatAsync`/`ReadFileAsync`.
2. Fix subdirectory delete/move filtering in `ReadDirectoryAsync`.
3. Add move overlay tests.
4. Define/test move+write/move+delete conflict semantics, or reject ambiguous combinations.
5. Add non-atomic commit hardening note to backlog if full atomicity is deferred.

After that, Phase 4 core transaction/diff foundations should be ready.
