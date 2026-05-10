# Code Review 0059: Workspace Transactions Second Fix Verification

Date: 2026-05-08  
Scope: verification after fixes for `0058-workspace-transactions-fix-verification.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 336
```

Note: `dotnet test` emitted an analyzer warning:

```text
Omicron.Core.Tests/TextDiffEngineTests.cs(307,9): warning xUnit2013:
Do not use Assert.Equal() to check for collection size. Use Assert.Single instead.
```

```text
dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

The transaction implementation is much closer to the intended Phase 4 shape:

- overlay `Files` exists and is used for staged write/delete/move operations;
- move destination stat/read is now represented;
- subdirectory listing normalization was improved;
- moved diffs use `WorkspaceChangeKind.Moved`;
- added binary files are metadata-only;
- staged directory entry line counts are better;
- tests now cover more overlay and conflict behavior.

However, I found one concrete bug in staged write+move behavior, and a few remaining correctness/cleanup issues.

## High-Priority Findings

### 1. `StageWrite` then `StageMove` loses staged content

File:

```text
Omicron.Core/Workspace/WorkspaceTransaction.cs
```

Current conflict logic allows moving a path that already has a staged write:

```csharp
if (_staged.TryGetValue(from.Value, out var fromExisting) && fromExisting.Action != StagedAction.Write)
    throw ...
```

But `StageMove` then overwrites the staged write with `MoveFrom`/`MoveTo` entries that do not carry the written content:

```csharp
_staged[NormalizePath(from.Value)] = new StagedEntry(StagedAction.MoveFrom, default, NormalizePath(to.Value));
_staged[NormalizePath(to.Value)] = new StagedEntry(StagedAction.MoveTo, default, NormalizePath(from.Value));
```

I verified with a temporary probe:

```csharp
var tx = new WorkspaceTransaction(host);
tx.StageWrite(Resolve("a.txt"), "hello"u8.ToArray());
tx.StageMove(Resolve("a.txt"), Resolve("b.txt"));
var content = await tx.Files.ReadFileAsync(Resolve("b.txt"));
```

Expected:

```text
b.txt contains hello in overlay
```

Actual:

```text
b.txt reads empty
```

If committed, this will likely try to move a non-existent host `a.txt`, because the staged new file was never written to host.

Recommendation:

Pick and test one semantic:

1. **Reject move of staged writes** for MVP:

```csharp
if (fromExisting.Action == StagedAction.Write)
    throw new InvalidOperationException("Cannot move a staged write in MVP transaction.");
```

or

2. **Carry staged content through the move**:

- remove the source staged write;
- create a staged write at destination with the same bytes;
- source should remain absent from host and overlay if it did not exist before.

For MVP, rejection is simpler and safer unless moving staged new files is required soon.

Add test:

```text
StageWrite_ThenMove_Throws
```

or

```text
StageWrite_ThenMove_CarriesContentToDestination
```

---

### 2. Conflict checks use unnormalized keys in some branches

File:

```text
Omicron.Core/Workspace/WorkspaceTransaction.cs
```

Staging stores keys with:

```csharp
_staged[NormalizePath(path.Value)] = ...
```

but some conflict checks use raw `path.Value`:

```csharp
_staged.TryGetValue(path.Value, out var existing)
_staged.Remove(path.Value)
_staged.TryGetValue(from.Value, out var fromExisting)
_staged.TryGetValue(to.Value, out var toExisting)
```

On Windows, `WorkspacePath.Value` can contain backslashes depending on how it was produced. This can bypass conflict detection or fail to remove a staged write.

Impact examples:

- `StageDelete` after staged write may not cancel the write if path separators differ;
- move conflict checks can miss existing staged operations;
- behavior differs cross-platform.

Recommendation:

Normalize once at method entry:

```csharp
var key = NormalizePath(path.Value);
```

and use the normalized key for all `_staged` lookups/removes/writes. Same for `from`/`to`.

Add a Windows-style path normalization test if possible by manually constructing `WorkspacePath("sub\\a.txt")` and ensuring conflict behavior is consistent.

---

### 3. Overlay methods still do not validate manually constructed unsafe `WorkspacePath` values at staging time

File:

```text
Omicron.Core/Workspace/WorkspaceTransaction.cs
```

The reported fix says staging resolves through host first, but the overlay mutation methods directly call staging:

```csharp
public ValueTask WriteFileAsync(WorkspacePath path, ReadOnlyMemory<byte> content, ...)
{
    _tx.StageWrite(path, content);
    return ValueTask.CompletedTask;
}
```

`StageWrite/StageDelete/StageMove` do not call host containment validation. Host VFS will reject during commit/diff operation boundaries, but unsafe paths can still be staged.

Recommendation:

Add a private validation helper using host operation-boundary semantics before staging. Since `HostWorkspaceFileSystem.ToAbsolute` is private, options are:

- expose/centralize containment validation on `IWorkspaceFileSystem`/host;
- call a cheap host operation such as `StatAsync(path)` only if it reliably validates containment, but avoid async in sync stage methods;
- make staging APIs async through overlay only and validate there;
- at minimum, add tests proving commit rejects and does not write outside root.

Prefer fail-fast if feasible.

## Medium-Priority Findings

### 4. `ReadDirectoryAsync` uses sync-over-async for moved entries

File:

```text
Omicron.Core/Workspace/WorkspaceTransaction.cs
```

```csharp
var fromStat = _host.StatAsync(fromPath).GetAwaiter().GetResult();
```

This is inside an async method and ignores `ct`. Use:

```csharp
var fromStat = await _host.StatAsync(fromPath, ct);
```

### 5. Move/write destination behavior remains underdefined

`StageWrite` after `MoveTo` is currently allowed and overwrites the move destination entry with a write, leaving the `MoveFrom` entry behind. That likely means source is hidden/deleted in overlay but not moved on commit, while destination is written as new content. This may or may not be desired.

Recommendation:

For MVP, reject writes to `MoveTo` as well unless explicitly implementing “move then modify destination” semantics.

If move-then-modify is desired, `MoveTo` needs to carry content and commit should perform move then write destination content.

### 6. Add non-atomic commit to hardening backlog

The class comment says commit is non-atomic, but the backlog should track this explicitly.

Suggested item:

```text
FH-0007: Workspace transaction commit is non-atomic
```

## Recommendation

Do one small follow-up before marking Phase 4 complete:

1. Fix normalized-key usage in all staging conflict branches.
2. Reject or correctly implement staged write -> move.
3. Reject or correctly implement move -> write destination.
4. Remove sync-over-async in `ReadDirectoryAsync`.
5. Add/adjust tests for the above.
6. Add non-atomic commit hardening item.

After that, the transaction foundation should be ready to mark complete.
