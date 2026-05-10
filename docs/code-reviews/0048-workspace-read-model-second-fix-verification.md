# Code Review 0048: Workspace Read Model Second Fix Verification

Date: 2026-05-08  
Scope: verification after second follow-up for `WorkspaceReadService` / `WorkspaceLlmTextRenderer` edge cases.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 276

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

Most remaining implementation issues from `0047` are fixed:

- chunk past EOF now returns empty file content;
- directory entries are sorted before truncation;
- directory model now carries `TotalFileCount` and `TotalDirCount`;
- renderer uses total directory counts in the header;
- negative `Limit` no longer creates invalid ranges;
- `ParseLines` is now `internal`, making focused unit testing easier.

One important mismatch remains between the implemented `Chunk + Offset` semantics and the public tool schema.

## Blocking Finding

### 1. `Chunk + Offset` semantics still conflict with `read_path` schema

Files:

- `Omicron.Core/Workspace/WorkspaceReadService.cs`
- `Omicron.Core/Extensions/BuiltinWorkspaceToolsExtension.cs`

`WorkspaceReadService` now documents and implements offset as an **absolute 1-based line number**, clamped to the chunk start when chunk is set:

```csharp
// Offset is 1-based line number (absolute, not relative to chunk)
int startLine = options?.Offset is > 0
    ? Math.Max(options.Offset.Value - 1, chunkStartLine)
    : chunkStartLine;
```

But the public `read_path` tool schema still says:

```text
When set, offset is relative to the start of this chunk.
```

That is a direct contract mismatch. The user/model will call the tool according to the schema, not the code comment.

Impact:

- LLM may request `chunk=1, offset=10` expecting line 10 relative to chunk 1;
- implementation treats `offset=10` as absolute line 10, then clamps it to the chunk start;
- result starts at the chunk start, not chunk-relative offset 10;
- tool behavior is surprising and hard to reason about.

Recommendation:

Pick one contract and make code/schema/tests agree.

Option A — keep current implementation:

- update `BuiltinWorkspaceToolsExtension` schema to say offset is absolute and clamped to chunk start when `chunk` is set;
- add a regression test for `chunk=1, offset=10` showing the clamp behavior;
- consider whether the truncation hint `[Use offset=N to continue.]` should mention whether to keep/drop chunk.

Option B — match current schema:

- implement offset as chunk-relative when chunk is present:

```csharp
var relativeOffset = options?.Offset is > 0 ? options.Offset.Value - 1 : 0;
startLine = chunkStartLine + relativeOffset;
```

- add a regression test for `chunk=1, offset=10`.

Given the tool schema is the public contract, Option B is probably less surprising unless there is a strong reason to make offsets absolute.

## Medium-Priority Findings

### 2. Chunk test does not prove chunk 1 works on an in-range large file

File:

- `Omicron.Core.Tests/WorkspaceReadServiceTests.cs`

The current chunk test writes 2,000 short lines. That is likely under 50 KB, so `Chunk = 1` is past EOF and returns empty. The assertion allows either empty or first line > 1:

```csharp
Assert.True(f1.Lines.Count == 0 || f1.Lines[0].Number > 1, ...);
```

This verifies past-EOF behavior, but it does not verify that chunk 1 works when the file actually spans chunk 1.

Recommendation:

Add a separate test with a file definitely larger than `DefaultChunkSizeBytes`, for example 10,000 long lines or one line repeated with enough bytes, and assert:

```csharp
Assert.NotEmpty(f1.Lines);
Assert.True(f1.Lines[0].Number > 1);
```

Keep the far-past-EOF test separate.

### 3. Directory sort should use ordinal comparison

File:

- `Omicron.Core/Workspace/WorkspaceReadService.cs`

Current sort:

```csharp
.ThenBy(e => e.Name)
```

For deterministic tool output across cultures/environments, prefer:

```csharp
.ThenBy(e => e.Name, StringComparer.Ordinal)
```

This is minor, but the renderer output is LLM context and should be stable.

## Recommendation

The architecture and most behavior are now solid. Before marking the cleanup complete, fix the public contract mismatch around `chunk + offset`, strengthen the chunk test with an actually >50 KB file, and switch directory sorting to ordinal comparison.
