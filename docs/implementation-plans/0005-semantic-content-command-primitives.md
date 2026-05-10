# Implementation Plan 0005: Semantic Content and Command Primitives

Status: Proposed  
Depends on: Plan 3 foundations  
Primary RFCs: RFC 0002, RFC 0013

## Purpose

Start extracting frontend-neutral content and command primitives so CLI, future TUI, GUI/web, plugins, and tools can share structured output instead of passing only text strings.

## Goals

- Represent tool/assistant/session/workspace output as structured content.
- Keep rendering separate from content semantics.
- Prepare for TUI/GUI/web without rewriting tool results later.

## Content Model

Add core records:

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

- Tool results can optionally carry content blocks in addition to text.
- Workspace reads can produce `FilePreviewContentBlock` or `CodeContentBlock` later from `WorkspaceReadContent`.
- Diffs can produce `DiffContentBlock` from transaction/edit harness results.
- CLI renderer converts content blocks to text.

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
