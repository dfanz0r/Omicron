# Code Review 0089: Plan 0005 — Semantic Content and Command Primitives

**Status:** Accepted — 0 findings, 2 observations  
**Date:** 2026-05-10  
**Build:** 0 errors, 532 tests (+27), 0 failures

---

## What Was Built

| File | Purpose |
|------|---------|
| `Omicron.Core/Content/ContentBlock.cs` | 7 sealed record types: `PlainText`, `Markdown`, `Code`, `Diff`, `FilePreview`, `Error`, `ToolCall` |
| `Omicron.Core/Content/ContentBlockTextRenderer.cs` | `Render(block)` / `RenderAll(blocks)` — simple text fallback |
| `Omicron.Core/Workspace/WorkspaceLlmTextRenderer.cs` | `ToContentBlocks(WorkspaceReadContent)` — dual-output path |
| `Omicron.Core.Tests/ContentBlockTests.cs` | 27 tests |

## Verification

```text
dotnet build → 0 errors, 0 warnings
dotnet test  → 532 passing (+27), 0 failed
```

## Test Coverage (27 new)

- **Phase 1 (9):** Creation + equality for all 7 block types + base type assignability
- **Phase 2 (12):** Text renderer for each block type, `RenderAll` single/multi/empty
- **Phase 3 (6):** File → preview, binary → preview, directory → preview, error → ErrorBlock, truncated → warning block, truncated with offset

## Observations

### 1. `FormatSize` duplicated between `ContentBlockTextRenderer` and `WorkspaceLlmTextRenderer`

**Severity:** None (acceptable). Both classes need the helper and neither can access the other's private version. Extract to a shared utility later if a third copy appears.

### 2. `ToDirectoryBlock` uses `FilePreviewContentBlock` for directories

**Severity:** None (acceptable for MVP). Directories get `Size = 0`, `LineCount = entries.Count`, `IsBinary = false`. A dedicated `DirectoryPreviewContentBlock` would be cleaner but the plan says keep it small. Revisit when TUI rendering needs to distinguish.

## Verdict

Clean, minimal, matches the plan. All 7 block types defined, text renderer covers all branches, one dual-output path wired. No blocking issues.
