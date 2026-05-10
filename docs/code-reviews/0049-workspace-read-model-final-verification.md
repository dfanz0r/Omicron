# Code Review 0049: Workspace Read Model Final Verification

Date: 2026-05-08  
Scope: final verification after fixes from `0048-workspace-read-model-second-fix-verification.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 276

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

The previous blocking mismatch is fixed:

- `read_path` schema says offset is relative to chunk when `chunk` is set.
- `WorkspaceReadService.ParseLines(...)` now implements that behavior.
- chunk test now uses a large enough file to exercise a real second chunk.
- directory sorting now uses `StringComparer.Ordinal`.

The workspace read model / renderer cleanup now satisfies the architectural goals from review `0045`:

- VFS remains raw/backend-focused.
- workspace read content is structured and source-independent.
- LLM rendering is a separate renderer over that model.
- `VfsWorkspaceAdapter` is thin orchestration.
- legacy duplicate formatting paths have been removed.

## Remaining Note: Continuation Hint Semantics

One subtle continuation UX issue remains, but it is not blocking the architecture cleanup.

`WorkspaceReadService` now treats offset as chunk-relative when `chunk` is set:

```csharp
int offsetLine = options?.Offset is > 0 ? options.Offset.Value - 1 : 0;
int startLine = chunkStartLine + offsetLine;
```

But `WorkspaceFileContent.NextOffset` is still rendered as:

```text
[Use offset=N to continue.]
```

`NextOffset` is currently an absolute line number because it is calculated from `startLine + result.Count + 1`. This is fine if the follow-up call uses only `offset=N` and omits `chunk`. It can be confusing if a model repeats the same `chunk` and uses the suggested `offset=N`, because offset is chunk-relative when chunk is present.

Recommended future improvement:

- make continuation metadata explicit in the model, e.g. `ContinuationReadOptions` or `NextOffsetIsAbsolute`;
- or render a clearer hint such as:

```text
[Use offset=N without chunk to continue.]
```

This is a UX/contract polish item rather than a blocker, because the current hint still provides a valid continuation path when used by itself.

## Recommendation

Mark the workspace read model / renderer cleanup as complete for Plan 3 pre-Phase-4 purposes.

Optional follow-up before/within Phase 4:

- improve structured continuation metadata so chunk/offset continuation hints are unambiguous;
- consider carrying total directory entry count directly in addition to file/dir counts if UI renderers need it.
