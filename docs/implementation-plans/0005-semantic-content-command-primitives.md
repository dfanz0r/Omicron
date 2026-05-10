# Implementation Plan 0005: Semantic Content and Command Primitives

Status: Proposed  
Depends on: Plans 3, 4, 4.1 (existing structured content types)  
Primary RFCs: RFC 0002, RFC 0013

## Purpose

Start extracting frontend-neutral content and command primitives so CLI, future TUI, GUI/web, plugins, and tools can share structured output instead of passing only text strings.

## Goals

- Represent tool/assistant/session/workspace output as structured content.
- Keep rendering separate from content semantics.
- Layer on top of existing structured types (`WorkspaceReadContent`, `ContentProcessorResult`, `WorkspaceDiff`) rather than replacing them.
- Prepare for TUI/GUI/web without rewriting tool results later.

## Current State (Post-Plan-4.1)

Already structured content sources:

- `WorkspaceReadContent` / `WorkspaceFileContent` / `WorkspaceDirectoryContent` / `WorkspaceBinaryFileContent` — workspace read results with line counts, binary detection, directory entries.
- `ContentProcessorResult` — carries `ActualModality`, `MimeType`, `Warning`, `IsTruncated`, `NextOffset` from the format-adaptive read pipeline.
- `WorkspaceDiff` / `UnifiedDiffRenderer` — diff generation from workspace transactions and edit harness.
- `ToolResult` — currently plain `(string Text, bool IsError)` — the primary target for structured upgrade.

## Content Model

Add core records (layer on top of existing types):

```csharp
public abstract record ContentBlock;
public sealed record PlainTextContentBlock(string Text) : ContentBlock;
public sealed record MarkdownContentBlock(string Markdown) : ContentBlock;
public sealed record CodeContentBlock(string Code, string? Language, string? Path) : ContentBlock;
public sealed record DiffContentBlock(string UnifiedDiff, string? Path) : ContentBlock;
public sealed record FilePreviewContentBlock(... ) : ContentBlock;
public sealed record ToolCallContentBlock(... ) : ContentBlock;
public sealed record ErrorContentBlock(string Message, string? Details) : ContentBlock;
```

Keep this small and stable. Avoid UI-specific concerns like colors, layout, icons, or terminal width.

## Command Model

Add semantic command references/results:

```csharp
public sealed record CommandRef(string Id, JsonElement? Args);
public sealed record SemanticCommandResult(
    bool Success,
    IReadOnlyList<ContentBlock> Content,
    string? Error = null);
```

Slash commands can remain CLI-local initially but should eventually map to semantic command IDs.

## Integration Points

- Tool results can carry content blocks in addition to `ToolResult.Text`.
- `ContentProcessorResult` (from Plan 4.1) can be wrapped as `CodeContentBlock`, `FilePreviewContentBlock`, or `HexDumpContentBlock`.
- `WorkspaceReadContent` (from Plan 3) already provides `WorkspaceFileContent` / `WorkspaceDirectoryContent` / `WorkspaceBinaryFileContent` — map to `FilePreviewContentBlock` and `CodeContentBlock`.
- `WorkspaceDiff` / `UnifiedDiffRenderer` (from Plan 3.1/4) already provide structured diffs — map to `DiffContentBlock`.
- CLI renderer converts content blocks to text; future TUI/GUI renders them natively.

## Acceptance Criteria

- Content block records exist in core with no terminal/UI dependencies.
- A simple text renderer converts content blocks to CLI text.
- At least one existing output path can produce both text and content blocks, preferably diff or workspace read output.
- Existing CLI behavior remains unchanged.
- Tests cover content block creation and CLI text rendering.
- Build/test pass with no warnings.

## Non-Goals

- No full TUI renderer.
- No syntax highlighting engine.
- No markdown parser.
- No plugin manifest system.
- No visual layout primitives.
