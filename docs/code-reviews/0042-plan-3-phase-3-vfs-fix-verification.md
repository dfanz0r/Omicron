# Code Review 0042: Plan 3 Phase 3 VFS Fix Verification

Date: 2026-05-08  
Scope: verify fixes after `docs/code-reviews/0041-plan-3-phase-3-vfs-review.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 257

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

The Phase 3 follow-up fixed the major VFS review findings:

- `VfsWorkspaceAdapter` now adapts `IWorkspaceFileSystem` to the existing `IWorkspace` rich read interface.
- `OmicronHost.Workspace` is now VFS-backed, so existing tools such as `read_path` route through the VFS layer without tool API churn.
- VFS operations revalidate containment at the boundary, including manually constructed `WorkspacePath` values.
- `Resolve(...)` now uses full-path + containment checks rather than substring scanning for `..`.
- `ReadFileAsync` documentation now matches raw-byte behavior.
- VFS audit events are explicitly documented as deferred.
- Tests cover manually constructed traversal paths.

Phase 3 is close to complete, but I recommend one small cleanup/test pass before closing it.

## Findings

### 1. Phase 3 plan text still says audit events are part of the initial implementation

File:

- `docs/implementation-plans/0003-core-persistence-workspace-foundations.md`

The code now explicitly defers VFS audit events in `IWorkspaceFileSystem` XML comments. That is acceptable, but the Plan 3 Phase 3 text still says:

```text
path containment/normalization preserved;
read/write/delete/move events emitted through IEventSink or returned as operations for later persistence.
```

And the acceptance criterion still says:

```text
event or audit hooks exist for file operations.
```

Recommendation:

Update the Plan 3 doc to say VFS audit events are deferred to Phase 4 transaction/diff/audit work. Otherwise the plan and code disagree.

---

### 2. Adapter path should have an explicit integration test

Files:

- `Omicron.Core/OmicronHost.cs`
- `Omicron.Core/Workspace/VfsWorkspaceAdapter.cs`
- `Omicron.Core.Tests/WorkspaceVfsTests.cs`

The host now wires:

```csharp
FileSystem = new HostWorkspaceFileSystem(workspaceRoot);
Workspace = new VfsWorkspaceAdapter(FileSystem);
```

This is good, but there should be a test proving `OmicronHost.Workspace.ReadPathAsync(...)` uses the VFS-backed adapter and preserves formatted `read_path` behavior.

Suggested test:

- create temp workspace with a text file;
- create `OmicronHost(tempRoot)`;
- call `host.Workspace.ReadPathAsync("file.txt")`;
- assert returned content includes `[FILE] file.txt` and line-numbered content;
- assert `host.Workspace` is `VfsWorkspaceAdapter` if acceptable.

This locks down the active runtime path.

---

### 3. `ReadDirectoryAsync(...)` swallows containment exceptions despite review intent

File:

- `Omicron.Core/Workspace/WorkspaceVfs.cs`

`ReadDirectoryAsync(...)` calls `ToAbsolute(path)` outside the try block, so containment exceptions currently propagate. Good.

But the method has a broad `catch` after that for all directory enumeration errors. This is fine for I/O failures, but please ensure tests cover manual traversal for directory read too, not just stat/delete/read file.

Suggested test:

```csharp
await Assert.ThrowsAsync<InvalidOperationException>(
    () => _vfs.ReadDirectoryAsync(new WorkspacePath("../../outside")).AsTask());
```

---

### 4. Move with unsafe destination should be tested

File:

- `Omicron.Core/Workspace/WorkspaceVfs.cs`

`MoveAsync(...)` validates both `from` and `to` via `ToAbsolute(...)`. Add a test for unsafe destination path:

```csharp
await Assert.ThrowsAsync<InvalidOperationException>(
    () => _vfs.MoveAsync(Resolve("safe.txt"), new WorkspacePath("../../outside.txt")).AsTask());
```

## Status Against Review 0041 Findings

| Finding | Status |
| --- | --- |
| Tools still use old `IWorkspace` | Fixed via `VfsWorkspaceAdapter`; add integration test. |
| `WorkspacePath` unsafe construction | Fixed at operation boundaries; add directory/move destination tests. |
| `Resolve` broad `..` check | Fixed. |
| Read methods silently return empty | Improved/documented; containment propagates. |
| `ReadFileAsync` binary comment mismatch | Fixed. |
| VFS audit events | Deferred in code comments; update plan doc to match. |

## Recommendation

Do one final Phase 3 polish pass:

1. update Plan 3 Phase 3 text to explicitly defer VFS audit events to Phase 4;
2. add host/adapter integration test;
3. add traversal tests for `ReadDirectoryAsync` and unsafe `MoveAsync` destination.

After that, Phase 3 can be marked complete.
