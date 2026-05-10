# Code Review 0046: Workspace Read Model / Renderer Refactor Review

Date: 2026-05-08  
Scope: review implementation following `docs/code-reviews/0045-workspace-formatting-architecture-review.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 262

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

The refactor landed the intended high-level shape:

- `WorkspaceReadContent` structured model exists.
- `WorkspaceReadService` builds structured content from `IWorkspaceFileSystem`.
- `WorkspaceLlmTextRenderer` renders structured content to current `read_path`-style text.
- `VfsWorkspaceAdapter` is now thin glue.
- old `HostWorkspace` formatting implementation was removed from `IWorkspace.cs`.
- `FileTools.cs` was removed.

This is the right architectural direction. However, there are some correctness and compatibility problems that should be fixed before considering this cleanup complete.

## High-Priority Findings

### 1. `ReadOptions.Chunk` behavior was dropped

Files:

- `Omicron.Core/Workspace/WorkspaceReadService.cs`
- `Omicron.Core/Workspace/IWorkspace.cs`
- `Omicron.Core/Extensions/BuiltinWorkspaceToolsExtension.cs`

`ReadOptions` still exposes:

```csharp
public int? Chunk { get; init; }
```

The `read_path` tool still advertises chunk support:

```text
0-based chunk index for byte-based access. Each chunk is ~50 KB.
```

But `WorkspaceReadService.ParseLines(...)` ignores `options.Chunk` entirely and only handles `Offset` / `Limit`.

Impact:

- advertised tool behavior no longer works;
- large-file continuation via chunk index regressed;
- this violates “Existing `read_path` tool output remains functionally equivalent.”

Recommendation:

Port the previous chunk behavior into `WorkspaceReadService` or remove/update the tool schema and docs if chunk is intentionally dropped. Since compatibility was an acceptance criterion, restore chunk support.

Add tests for:

- `ReadPathAsync(..., Chunk = 1)` starts later than chunk 0 for a large file;
- `Offset` is relative to chunk when both are set.

---

### 2. File total line count is now wrong for offset/limited reads

File:

- `Omicron.Core/Workspace/WorkspaceLlmTextRenderer.cs`

`RenderFile(...)` computes total lines from the last returned line:

```csharp
var totalLines = file.Lines.Count > 0
    ? file.Lines[^1].Number
    : 0;
```

If the caller reads `offset=50&limit=10`, this reports total lines as 59, not the actual file line count. The previous formatter displayed total file lines.

Impact:

- misleading LLM context;
- continuation hints can be wrong/confusing;
- output no longer matches previous behavior.

Recommendation:

Add total line count to the structured model:

```csharp
public int TotalLines { get; init; }
```

on `WorkspaceFileContent`, set it from the full file line count in `WorkspaceReadService`, and use it in renderer.

Add tests for offset/limit showing:

```text
Lines: <actual total>
Showing: lines 50-59 of <actual total>
```

---

### 3. Directory truncation is wired to the wrong constant and not enforced in the model

Files:

- `Omicron.Core/Workspace/WorkspaceReadService.cs`
- `Omicron.Core/Workspace/WorkspaceLlmTextRenderer.cs`

For directory content:

```csharp
Truncated = entries.Count >= MaxOutputLines
```

But directory truncation should use `MaxDirEntries` (previously 200), not `MaxOutputLines` (2000). Also the service does not actually truncate entries; the renderer iterates all entries and only prints a truncation line if `dir.Truncated || dir.Entries.Count >= 200`.

Impact:

- large directory output can exceed the intended entry cap;
- `Truncated` field is semantically inconsistent;
- source model does not represent the displayed subset.

Recommendation:

Add a directory max constant to read service/model path and truncate before model creation:

```csharp
var allEntries = await _vfs.ReadDirectoryAsync(...);
var shown = allEntries.Take(MaxDirEntries).ToList();
Truncated = allEntries.Count > MaxDirEntries;
```

Use the same constant in renderer or expose truncation info in the model.

---

### 4. Error rendering for escaped paths lost the actual root path

Files:

- `Omicron.Core/Workspace/WorkspaceReadService.cs`
- `Omicron.Core/Workspace/WorkspaceLlmTextRenderer.cs`

`WorkspaceReadService` creates message:

```csharp
Path escapes workspace root: 'x' resolves outside 'root'
```

Then renderer wraps it as:

```text
Error: Path escapes workspace root: ...
  Root: (workspace root)
```

This is awkward and less useful than the previous output, which included the actual root path directly.

Recommendation:

Either:

- include the root in `WorkspaceReadErrorContent` as structured metadata; or
- do not append the placeholder `Root: (workspace root)` in renderer if the message already contains root.

---

## Medium-Priority Findings

### 5. `VfsWorkspaceAdapter.RootPath` / `ResolvePath` depend on casting concrete service

File:

- `Omicron.Core/Workspace/VfsWorkspaceAdapter.cs`

Current code:

```csharp
public string RootPath => (_readService as WorkspaceReadService)?.GetRootPath() ?? "";
public string? ResolvePath(string raw) => (_readService as WorkspaceReadService)?.Resolve(raw)?.Value;
```

This undermines the interface abstraction. If a different `IWorkspaceReadService` is injected later, `RootPath` becomes `""` and `ResolvePath` returns null.

Recommendation:

Either:

- extend `IWorkspaceReadService` with `RootPath` and `Resolve(...)`; or
- keep `_vfs` in the adapter for `RootPath` / `ResolvePath`; or
- inject explicit delegates/properties.

For now, simplest:

```csharp
private readonly IWorkspaceFileSystem _vfs;
public string RootPath => _vfs.RootPath;
public string? ResolvePath(string raw) => _vfs.Resolve(raw)?.Value;
```

---

### 6. `WorkspaceFileContent` stores all selected lines, not bytes or encoding state

This is acceptable for LLM context, but be clear that `WorkspaceReadContent` currently models a **text-preview read**, not arbitrary raw file content. Raw bytes remain in VFS. Future UI/web renderers that need full content or syntax parsing may need an expanded model.

No action required now beyond naming/comments.

### 7. Test coverage for the new model/renderer appears thin

Tests still pass, but I did not see direct tests for:

- `WorkspaceReadService` offset/limit/chunk behavior;
- `WorkspaceLlmTextRenderer` file total line count;
- directory truncation;
- error rendering shape.

Recommendation:

Add focused tests for the new source-independent model and renderer. This is especially important because the refactor was meant to preserve output behavior.

## Recommendation

Do a focused follow-up pass before marking this cleanup complete:

1. Restore `ReadOptions.Chunk` behavior or remove it from schema/docs.
2. Add `TotalLines` to `WorkspaceFileContent` and use it in the renderer.
3. Correct directory truncation semantics.
4. Fix escaped-root error rendering.
5. Remove concrete casts from `VfsWorkspaceAdapter`.
6. Add direct tests for `WorkspaceReadService` and `WorkspaceLlmTextRenderer`.

The architecture is now pointing in the right direction, but the behavior-preservation details need tightening.
