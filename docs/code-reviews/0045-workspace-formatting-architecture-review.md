# Code Review 0045: Workspace Read Model and Rendering Architecture Review

Date: 2026-05-08  
Scope: architecture review for separating workspace/VFS backend logic from source-independent workspace read content and consumer-specific rendering.

## Summary

The current workspace stack has the right low-level direction after Plan 3 Phase 3: `IWorkspaceFileSystem` / `HostWorkspaceFileSystem` provides contained path resolution and raw file-system operations. However, file/directory formatting logic is duplicated and partially mixed into adapter/backend-adjacent code.

The key architectural point is **not** merely “move text formatting somewhere else.” A standalone text formatter is not enough if it only serves today’s LLM `read_path` output.

The useful long-term abstraction is a **source-independent structured workspace read model**. That model can be produced from host VFS, transaction overlays, snapshots, remote workspaces, or staged edit views. Then separate renderers can target different consumers:

```text
IWorkspaceFileSystem / transaction overlay / snapshot / remote workspace
        ↓
WorkspaceReadContent / WorkspaceReadModel
        ↓
 ┌─────────────────────────────┬──────────────────────────────┐
 │ LLM context renderer         │ UI/web renderer              │
 │ deterministic text           │ ContentBlock/components      │
 └─────────────────────────────┴──────────────────────────────┘
```

So the desired cleanup is:

- keep VFS raw and source-agnostic;
- introduce a structured workspace read/content model;
- render that model into deterministic LLM text for `read_path` today;
- leave room for future UI/web renderers to render the same model as structured content blocks/components;
- avoid coupling presentation behavior to VFS semantics.

This should be cleaned before Plan 3 Phase 4 workspace transactions/diffs, because transaction work will add more file-operation sources/views and we should avoid growing duplicate formatting or making the host VFS the only source capable of producing readable context.

## Current Problem

Formatting logic exists in multiple places:

```text
Omicron.Core/Workspace/IWorkspace.cs          // HostWorkspace old formatter
Omicron.Core/Workspace/VfsWorkspaceAdapter.cs // new VFS-backed formatter
Omicron.Core/Tools/FileTools.cs               // older standalone read_path implementation
```

Examples of duplicated presentation/context behavior:

- `[FILE] ...` / `[DIR] ...` headers;
- line numbering;
- max output lines / max output bytes;
- truncation messages and continuation hints;
- binary display messages;
- directory display sorting and summaries;
- file size formatting;
- line-count display.

This makes the boundaries blurry:

- VFS should be a raw storage/filesystem abstraction.
- Source-independent workspace read content should represent what was read/listed.
- LLM text rendering should be one renderer over that model.
- UI/web rendering should be a separate future renderer over that same model.
- Tool adapters should orchestrate between raw data source, structured model, and renderer.

## Desired Architecture

Use four conceptual layers:

```text
IWorkspaceFileSystem / HostWorkspaceFileSystem
  Raw workspace operations:
  - resolve contained paths
  - stat
  - read bytes
  - list entries
  - write/delete/move
  - later: transaction staging

WorkspaceReadContent / WorkspaceReadModel
  Source-independent read representation:
  - file content with metadata
  - directory listing with metadata
  - binary file marker
  - truncation/continuation info
  - errors such as not found / escapes root / unreadable
  - later: edit anchors / line hashes / staged-vs-committed markers

WorkspaceReadBuilder / Adapter
  Glue from source to model:
  - resolve path
  - stat path
  - call VFS read/list
  - create WorkspaceReadContent

Renderers
  Consumer-specific output:
  - LLM text renderer for read_path/tool context
  - future UI/web renderer for ContentBlocks/components
```

## Formatting Targets Are Different

The same `WorkspaceReadContent` can serve different consumers.

### LLM context renderer

Optimized for model usefulness:

- deterministic plain text;
- line numbers;
- compact metadata;
- truncation/continuation instructions;
- chunk/offset hints;
- later: line hashes/anchors for edit harnesses;
- minimal visual decoration.

Example:

```text
[FILE] src/Foo.cs
  Size: 2.1 KB | Lines: 74
  Showing: lines 1-40 of 74
---
     1| using System;
     2| ...
```

### UI/web renderer

Optimized for humans and interaction:

- syntax highlighting;
- collapsible directory trees;
- clickable file paths;
- icons/actions;
- virtualized long output;
- structured diff/code components;
- no need to bake continuation hints into the content string.

Example future shape:

```csharp
new FilePreviewBlock(
    Path: "src/Foo.cs",
    Language: "csharp",
    Lines: [...],
    Actions: [Open, CopyPath, InsertIntoPrompt]);
```

The abstraction should enable this split. It should **not** enshrine today’s LLM text format as the UI format.

## Recommended Implementation Plan

### 1. Add structured workspace read content records

Create something like:

```text
Omicron.Core/Workspace/WorkspaceReadContent.cs
```

Suggested shape:

```csharp
public abstract record WorkspaceReadContent(
    string RequestedPath,
    WorkspacePath? ResolvedPath);

public sealed record WorkspaceFileContent(
    string RequestedPath,
    WorkspacePath ResolvedPath,
    FileStat Stat,
    IReadOnlyList<WorkspaceTextLine> Lines,
    bool Truncated,
    int? NextOffset) : WorkspaceReadContent(RequestedPath, ResolvedPath);

public sealed record WorkspaceBinaryFileContent(
    string RequestedPath,
    WorkspacePath ResolvedPath,
    FileStat Stat) : WorkspaceReadContent(RequestedPath, ResolvedPath);

public sealed record WorkspaceDirectoryContent(
    string RequestedPath,
    WorkspacePath ResolvedPath,
    IReadOnlyList<DirectoryEntry> Entries,
    bool Truncated) : WorkspaceReadContent(RequestedPath, ResolvedPath);

public sealed record WorkspaceReadErrorContent(
    string RequestedPath,
    WorkspaceReadErrorKind Kind,
    string Message) : WorkspaceReadContent(RequestedPath, null);

public sealed record WorkspaceTextLine(
    int Number,
    string Text);

public enum WorkspaceReadErrorKind
{
    EscapesRoot,
    NotFound,
    Unreadable,
    InvalidPath
}
```

Exact names can change. The important part is that this model is not VFS-specific and not tied to plain-text formatting.

### 2. Add a builder/orchestrator from VFS to read content

Create something like:

```text
Omicron.Core/Workspace/WorkspaceReadService.cs
```

Suggested shape:

```csharp
public interface IWorkspaceReadService
{
    ValueTask<WorkspaceReadContent> ReadAsync(
        string path,
        ReadOptions? options = null,
        CancellationToken ct = default);
}

public sealed class WorkspaceReadService : IWorkspaceReadService
{
    private readonly IWorkspaceFileSystem _vfs;

    public async ValueTask<WorkspaceReadContent> ReadAsync(...)
    {
        // resolve
        // stat
        // directory => WorkspaceDirectoryContent
        // binary => WorkspaceBinaryFileContent
        // file => read bytes, split selected lines, set truncation/next offset
        // errors => WorkspaceReadErrorContent
    }
}
```

This service is where source-specific access happens. Later, equivalent services can read from transaction overlays, snapshots, remote workspaces, etc., while still producing the same `WorkspaceReadContent` model.

### 3. Add an LLM text renderer over the read content model

Create one of:

```text
Omicron.Core/Workspace/WorkspaceLlmTextRenderer.cs
Omicron.Core/Tools/ReadPathLlmRenderer.cs
Omicron.Core/Tools/WorkspaceContextRenderer.cs
```

Suggested shape:

```csharp
public interface IWorkspaceReadRenderer<out T>
{
    T Render(WorkspaceReadContent content);
}

public sealed class WorkspaceLlmTextRenderer : IWorkspaceReadRenderer<WorkspaceReadResult>
{
    public WorkspaceReadResult Render(WorkspaceReadContent content)
    {
        return content switch
        {
            WorkspaceFileContent file => RenderFile(file),
            WorkspaceBinaryFileContent binary => RenderBinary(binary),
            WorkspaceDirectoryContent dir => RenderDirectory(dir),
            WorkspaceReadErrorContent error => RenderError(error),
            _ => ...
        };
    }
}
```

This renderer should preserve today’s `read_path` output shape as much as possible.

### 4. Slim `VfsWorkspaceAdapter`

`VfsWorkspaceAdapter` should stop owning read logic and formatting details. It should become glue from `IWorkspace` to read service + renderer:

```csharp
public sealed class VfsWorkspaceAdapter : IWorkspace
{
    private readonly IWorkspaceReadService _readService;
    private readonly IWorkspaceReadRenderer<WorkspaceReadResult> _renderer;

    public async Task<WorkspaceReadResult> ReadPathAsync(string path, ReadOptions? options = null, CancellationToken ct = default)
    {
        var content = await _readService.ReadAsync(path, options, ct);
        return _renderer.Render(content);
    }
}
```

If keeping constructors simple, `VfsWorkspaceAdapter` can construct the default read service and LLM renderer internally for now, but the logical separation should be clear.

### 5. Remove old/duplicate implementations

Assess references, then remove or retire:

```text
Omicron.Core/Tools/FileTools.cs
```

If no code references it, delete it.

For:

```text
Omicron.Core/Workspace/IWorkspace.cs
```

keep the interfaces/data contracts:

- `WorkspaceId`
- `ReadOptions`
- `WorkspaceReadResult`
- `IWorkspace`

Remove the old `HostWorkspace` implementation if `OmicronHost` no longer uses it. The active implementation should be `VfsWorkspaceAdapter : IWorkspace`.

This ensures there is only one path from workspace source to structured read model to LLM text.

### 6. Preserve existing LLM-facing output behavior

The refactor should be behavior-preserving for current tools.

Existing tests should continue passing. Add/adjust tests for:

- `WorkspaceReadService` produces the right content model for files/directories/binary/errors;
- LLM renderer output includes `[FILE] path`;
- line numbering works;
- offset/limit works;
- truncation message appears;
- binary rendering works;
- directory rendering sorts and displays entries;
- not-found and escape-root messages match existing tool expectations;
- `VfsWorkspaceAdapter` combines service + renderer correctly.

### 7. Keep VFS tests raw

`WorkspaceVfsTests` should mostly test raw VFS behavior:

- resolve;
- stat;
- raw read bytes;
- list entries;
- write/delete/move;
- containment enforcement.

Read-content tests should target `WorkspaceReadService`.

LLM text rendering tests should target `WorkspaceLlmTextRenderer`.

Adapter tests should verify only glue behavior:

- `VfsWorkspaceAdapter.ReadPathAsync(...)` returns rendered result by combining read service + renderer;
- `OmicronHost.Workspace` is VFS-backed.

## Acceptance Criteria

This cleanup is complete when:

- `HostWorkspaceFileSystem` contains no `[FILE]`, `[DIR]`, line-number, truncation, or display-formatting code.
- `VfsWorkspaceAdapter` contains minimal/no read-formatting logic and delegates to read service + renderer.
- A structured `WorkspaceReadContent` model exists and is source-independent.
- An LLM text renderer exists for `read_path` output.
- `FileTools.cs` is removed if unused, or clearly marked obsolete if temporarily retained.
- `HostWorkspace` old formatting implementation is removed if unused.
- Existing `read_path` tool output remains functionally equivalent.
- Tests pass with no warnings.

## Risks / Notes

- Be careful not to break `read_path` output shape unexpectedly; providers may have learned the current format during manual testing.
- Do not treat the LLM text renderer as the future UI/web renderer.
- Do not move write/delete/move behavior into the read model or renderer. VFS/transactions own mutations.
- Do not add transaction/diff behavior here; that belongs to Plan 3 Phase 4.
- Do not add audit events here; Phase 4 will decide audit semantics around transactions/diffs.

## Recommendation

Do this cleanup before Plan 3 Phase 4. It will make transaction/diff work cleaner by ensuring:

- VFS remains storage-focused;
- workspace read content is source-independent;
- LLM-context rendering is explicit and testable;
- future UI/web rendering can diverge cleanly through content blocks/components;
- tools consume one canonical read model/rendering path;
- duplicate legacy file-reading logic does not keep expanding.
