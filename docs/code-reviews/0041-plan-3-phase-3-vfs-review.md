# Code Review 0041: Plan 3 Phase 3 VFS Review

Date: 2026-05-08  
Scope: review Plan 3 Phase 3 Host-Backed Workspace VFS v1 implementation.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 256

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

Phase 3 has a useful first VFS implementation:

- `WorkspacePath`
- `FileStat`
- `DirectoryEntry`
- `IWorkspaceFileSystem`
- `HostWorkspaceFileSystem`
- `OmicronHost.FileSystem`
- 21 focused VFS tests
- Plan 3 document updated to show Phases 1/2 complete and Phase 3 in progress

The implementation is a good start, but Phase 3 should not be marked complete yet. The biggest gap is that existing tools still use the old `IWorkspace`/`HostWorkspace` path, so the acceptance criterion “all file reads go through VFS abstraction” is not yet met. There are also a few path containment and semantic cleanup issues to fix while the VFS is still small.

## High-Priority Findings

### 1. Existing workspace tools do not use `IWorkspaceFileSystem` yet

Files:

- `Omicron.Core/OmicronHost.cs`
- `Omicron.Core/Extensions/BuiltinWorkspaceToolsExtension.cs`
- `Omicron.Core/Tools/FileTools.cs`
- `Omicron.Core/Workspace/IWorkspace.cs`
- `Omicron.Core/Workspace/WorkspaceVfs.cs`

`OmicronHost` now exposes both:

```csharp
public IWorkspace Workspace { get; }
public IWorkspaceFileSystem FileSystem { get; }
```

But `LoadBuiltinExtensions()` still registers workspace tools with the old abstraction:

```csharp
Extensions.Register(new BuiltinWorkspaceToolsExtension(Workspace));
```

So `read_path` still goes through `IWorkspace` / `HostWorkspace`, not the new VFS.

Impact:

- Phase 3 acceptance criterion is not met: “all file reads go through VFS abstraction.”
- The VFS is not exercised by normal agent tool usage.
- The old workspace abstraction remains the active runtime path.

Recommendation:

Pick one bridge strategy:

1. Update `BuiltinWorkspaceToolsExtension` / read tool to consume `IWorkspaceFileSystem`; or
2. Implement an adapter where `HostWorkspace` uses `IWorkspaceFileSystem` under the hood for path resolution/read/list; or
3. Keep `IWorkspace` as the high-level formatted read API but make it wrap `IWorkspaceFileSystem` so VFS is the low-level source of truth.

Option 3 is likely best because the existing `read_path` tool returns formatted line-numbered text, while VFS should stay bytes/metadata-oriented.

---

### 2. `WorkspacePath` can be constructed directly with unsafe values

File:

- `Omicron.Core/Workspace/WorkspaceVfs.cs`

`WorkspacePath` is public:

```csharp
public readonly record struct WorkspacePath(string Value)
```

Any caller can bypass `Resolve(...)`:

```csharp
new WorkspacePath("../../outside.txt")
```

Then `ToAbsolute(...)` simply does:

```csharp
Path.Combine(RootPath, path.Value)
```

A manually constructed unsafe `WorkspacePath` can escape the workspace for read/write/delete/move operations.

Impact:

- The type comment says “guaranteed to be contained,” but the type does not enforce it.
- Security/containment depends on caller discipline.

Recommendation:

Make containment enforced at operation boundaries even if `WorkspacePath` is manually constructed.

Options:

- make `WorkspacePath` constructor private/internal and expose factory only through `IWorkspaceFileSystem.Resolve`; and/or
- change `ToAbsolute(...)` to re-resolve/revalidate `path.Value` and throw/return failure if it escapes.

At minimum, update `ToAbsolute(...)`:

```csharp
private string? TryToAbsolute(WorkspacePath path)
{
    var full = Path.GetFullPath(Path.Combine(RootPath, path.Value));
    return IsContained(full) ? full : null;
}
```

Then tests should directly pass `new WorkspacePath("../outside")` to `ReadFileAsync`, `WriteFileAsync`, `DeleteAsync`, and `MoveAsync` and assert escape is blocked.

---

### 3. `Resolve(...)` traversal detection rejects/handles paths too broadly and inconsistently

File:

- `Omicron.Core/Workspace/WorkspaceVfs.cs`

Current logic checks:

```csharp
if (normalized.Contains(".."))
```

This treats filenames containing `..` as traversal-like even when they are safe, e.g. `notes..txt` or `foo..bar`. It may still resolve inside root, but the branch is misleading and can produce inconsistent behavior.

Recommendation:

Do not special-case `".."` by substring. Always compute full path and check containment:

```csharp
var normalized = rawPath.Replace('\\', '/').TrimStart('/');
var fullPath = Path.GetFullPath(Path.Combine(RootPath, normalized));
return IsContained(fullPath) ? new WorkspacePath(GetRelative(fullPath)) : null;
```

Containment check is the important protection.

## Medium-Priority Findings

### 4. VFS read methods silently return empty content for error cases

File:

- `Omicron.Core/Workspace/WorkspaceVfs.cs`

Examples:

- `ReadFileAsync(...)` returns empty memory for missing file, binary file according to comment, or exceptions.
- `ReadDirectoryAsync(...)` returns empty list for missing path, non-directory, or exceptions.

This makes missing/empty/error indistinguishable for callers.

Recommendation:

For a low-level VFS, prefer explicit semantics:

- `StatAsync(...)` returns null for missing;
- `ReadFileAsync(...)` throws `FileNotFoundException` / `InvalidOperationException` or returns a result type with error info;
- `ReadDirectoryAsync(...)` distinguishes missing vs empty directory.

If keeping silent-empty MVP semantics, document them clearly and ensure high-level tools use `StatAsync(...)` first.

---

### 5. `ReadFileAsync` comment says binary returns empty, but implementation reads binary bytes

File:

- `Omicron.Core/Workspace/WorkspaceVfs.cs`

Comment:

```csharp
/// Read file content. Returns empty memory if file not found or is binary.
```

Implementation reads all bytes for any existing file, including binary.

Recommendation:

Either update the comment to say VFS reads raw bytes, or add an explicit binary check. I recommend updating the comment: low-level VFS should be byte-oriented and not suppress binary content.

---

### 6. `FileStat.Path` should probably be `WorkspacePath`

File:

- `Omicron.Core/Workspace/WorkspaceVfs.cs`

`FileStat` stores `string Path`. Since the VFS has a first-class `WorkspacePath`, use it consistently:

```csharp
public sealed record FileStat(WorkspacePath Path, ...)
```

Same consideration applies if `DirectoryEntry` eventually carries path; today it only carries `Name`, which is acceptable for listing.

---

### 7. VFS operations do not emit audit events yet

Plan 3 Phase 3 says:

> path containment/normalization preserved; read/write/delete/move events emitted through `IEventSink` or returned as operations for later persistence.

Current `HostWorkspaceFileSystem` has no event/audit hook. That may be acceptable as v1 if this is treated as “returned later,” but it should be explicit.

Recommendation:

Either:

- add event sink injection and emit simple workspace operation events; or
- update Plan 3 Phase 3 to explicitly defer VFS audit events to Phase 4 transactions/diffs.

Given Phase 4 transaction/diff is next, deferring audit events is acceptable if documented.

## Low-Priority Findings

### 8. Directory listing order is filesystem-dependent

Tests currently use `Assert.Contains`, so they do not depend on order. For stable UI/replay, consider sorting directories/files by name.

### 9. Line-count implementation reads whole files

`ReadDirectoryAsync(...)` reads every text file fully to count lines. This can be expensive in large directories. Existing `HostWorkspace` has caps/skip lists for context control. Consider dropping line count from low-level VFS or making it optional.

## Recommendation

Do a focused Phase 3 follow-up pass before marking complete:

1. Route existing workspace reads/tools through `IWorkspaceFileSystem` or an adapter.
2. Enforce containment for manually constructed `WorkspacePath` values at every operation boundary.
3. Simplify `Resolve(...)` to always full-path + containment check.
4. Fix `ReadFileAsync` documentation/semantics.
5. Decide/document VFS audit-event deferral vs implementation.

After those are resolved, Phase 3 will be ready to close and Plan 3 can move to Phase 4 workspace transactions/diffs.
