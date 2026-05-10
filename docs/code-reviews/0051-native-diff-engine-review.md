# Code Review 0051: Native Diff Engine Implementation Review

Date: 2026-05-08  
Scope: review of native diff engine implementation under `Omicron.Core/Diff`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 297

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

Reviewed files:

```text
Omicron.Core/Diff/TextDiffModel.cs
Omicron.Core/Diff/TextDiffEngine.cs
Omicron.Core/Diff/TextDiffHunkBuilder.cs
Omicron.Core/Diff/UnifiedDiffRenderer.cs
Omicron.Core/Diff/TextLineSplitter.cs
Omicron.Core.Tests/TextDiffEngineTests.cs
Omicron.Core.Tests/UnifiedDiffRendererTests.cs
```

## Summary

The implementation establishes the intended structured diff stack:

- `TextDiffResult` / `TextDiffEdit` model;
- hunk model;
- unified renderer;
- line splitter;
- focused engine and renderer tests.

However, the current engine is **not the planned full Myers implementation**. It is an O(N*M) dynamic-programming LCS implementation with a full two-dimensional `int[,]` table. That is a major divergence from the spec and from the intended performance profile for workspace transaction previews over large files.

There are also several currently unused options and likely correctness issues in hunk construction for multi-line replacements.

## High-Priority Findings

### 1. Engine claims Myers O(ND), but implementation is O(N*M) DP

File:

```text
Omicron.Core/Diff/TextDiffEngine.cs
```

The class comment says:

```csharp
/// Line-based diff engine using Myers' O(ND) algorithm with clean trace-based path reconstruction.
```

But the implementation builds a full LCS dynamic-programming table:

```csharp
int[,] dp = new int[N + 1, M + 1];
for (int i = 1; i <= N; i++)
{
    for (int j = 1; j <= M; j++)
    {
        ...
    }
}
```

This is O(N*M) time and O(N*M) memory, not Myers O(ND). For 5,000 x 5,000 lines, this allocates roughly 100 MB just for the table. Larger real files can allocate hundreds of MB or more.

Impact:

- violates `0003.1-omicron-native-diff-engine.md` algorithm target;
- can become a transaction-preview memory/time problem;
- comment/API documentation is misleading;
- `MaxLineCount` option is currently unused, so there is no guardrail.

Recommendation:

Replace the DP engine with the planned trace-based Myers algorithm, or explicitly downgrade the spec and add strong max-size guardrails. Given the stated goal is a full diff implementation, implement trace-based Myers.

At minimum before Phase 4 integration:

- fix the comment if DP remains temporarily;
- enforce `TextDiffOptions.MaxLineCount`;
- add stress tests around large inputs and guard behavior.

---

### 2. `TextDiffOptions.MaxLineCount` is unused

File:

```text
Omicron.Core/Diff/TextDiffEngine.cs
```

`TextDiffOptions` exposes:

```csharp
public int MaxLineCount { get; init; }
```

but `DiffLines(...)` never checks it.

Impact:

- callers may think there is a size safety limit when there is not;
- especially risky with the current O(N*M) DP table.

Recommendation:

Define behavior and test it. Options:

1. return a special truncated/too-large result;
2. throw a clear exception;
3. fall back to a cheap coarse diff.

For workspace transaction use, a structured “too large to diff fully” result may be preferable, but current `TextDiffResult` has no field for that. Short-term: throw `InvalidOperationException` or `ArgumentException` with clear message, then handle at workspace-diff layer.

---

### 3. `OptimizeForReadability` is unused

Files:

```text
Omicron.Core/Diff/TextDiffModel.cs
Omicron.Core/Diff/TextDiffEngine.cs
```

The option exists and defaults to true:

```csharp
public bool OptimizeForReadability { get; init; } = true;
```

but the engine never reads it.

Impact:

- public/internal option is misleading;
- tests set it to false, but this has no effect;
- repeated-line readability behavior from the spec is not implemented.

Recommendation:

Either implement the readability optimization or remove/defer the option until implemented. Since repeated-line diff readability is important for code, prefer implementing it and adding targeted tests with repeated braces/blank lines/repeated tokens.

---

### 4. Hunk builder mishandles multi-line replacements

File:

```text
Omicron.Core/Diff/TextDiffHunkBuilder.cs
```

For replacements where both old and new spans have more than one line, this branch increments `hei` after emitting only one removed and one added line:

```csharp
if (inEdit && inNewEdit)
{
    hunkLines.Add(new TextDiffLine(TextDiffLineKind.Removed, li + 1, null, oldLines[li]));
    li++;
    hunkLines.Add(new TextDiffLine(TextDiffLineKind.Added, null, lj + 1, newLines[lj]));
    lj++;
    hei++;
}
```

For an edit like:

```csharp
TextDiffEdit(OldStart: 10, OldCount: 2, NewStart: 10, NewCount: 3)
```

this emits only the first removed/added pair as part of the edit, then advances past the edit. Remaining replacement lines can be treated as context or skipped incorrectly.

Impact:

- rendered diffs for multi-line replacements may be wrong;
- current tests only cover simple single-line replacements and broad contains checks.

Recommendation:

For replacement edits, emit **all removed lines first**, then **all added lines**, then advance `hei`:

```csharp
var e = hunkEdits[hei];
while (li < e.OldStart + e.OldCount) emit removed oldLines[li++];
while (lj < e.NewStart + e.NewCount) emit added newLines[lj++];
hei++;
```

Add tests for:

- replace 2 old lines with 3 new lines;
- replace 3 old lines with 1 new line;
- replacement at beginning/end of file.

---

### 5. Tests are too weak to validate exact edit scripts/rendering

Files:

```text
Omicron.Core.Tests/TextDiffEngineTests.cs
Omicron.Core.Tests/UnifiedDiffRendererTests.cs
```

Several tests only check broad conditions, e.g.:

```csharp
Assert.True(result.Edits.Count >= 1);
Assert.Contains("@@", result);
```

The current tests do not catch:

- multi-line replacement rendering bug;
- unused options;
- exact hunk boundaries;
- repeated-line ambiguity/readability behavior;
- whitespace/case option behavior;
- max line count behavior;
- large-file guard behavior.

Recommendation:

Add exact assertions for critical cases:

- exact `TextDiffEdit` arrays;
- exact rendered diff text for small examples;
- multi-line replacement;
- repeated-line cases from `READ_ONLY/Diff/TestCases.cs` as behavioral inspiration;
- whitespace/case options;
- `MaxLineCount` behavior.

## Medium-Priority Findings

### 6. Whitespace normalization still allocates heavily and uses string keys only

File:

```text
Omicron.Core/Diff/TextDiffEngine.cs
```

`LineNormalizer.GetCode(...)` allocates when options are enabled:

- `line.Trim()` may allocate;
- `StringBuilder` + `ToString()` for whitespace collapse;
- `ToUpperInvariant()` for case folding.

This is acceptable for correctness MVP, but it does not meet the more performance-forward spirit of the spec for normalized comparisons.

Recommendation:

Not a blocker if options remain rarely used, but add a future optimization note or implement a span-based normalization helper that avoids allocation when the normalized value is identical to the original line.

### 7. Unified renderer truncates silently and may exceed byte cap by one large line

File:

```text
Omicron.Core/Diff/UnifiedDiffRenderer.cs
```

`AppendLine(...)` checks limits before append:

```csharp
if (lineCount >= options.MaxOutputLines || byteCount >= options.MaxOutputBytes)
    return;
```

but it does not check whether the next line itself will exceed the byte cap, and it emits no truncation marker.

Impact:

- output can exceed `MaxOutputBytes` by a large line;
- consumers cannot tell whether diff output is complete or truncated.

Recommendation:

Add a truncation marker, e.g.:

```text
... diff truncated ...
```

and consider checking `byteCount + nextLineBytes > MaxOutputBytes` before appending. Long single lines may need clipping.

### 8. `IncludeNoNewlineMarkers` is unused

File:

```text
Omicron.Core/Diff/UnifiedDiffRenderer.cs
```

The option exists but is never read.

Recommendation:

Either implement newline-at-EOF tracking in the line splitting/rendering model or remove/defer this option. Current `TextLineSplitter` does not preserve enough metadata to correctly render `\ No newline at end of file`.

### 9. Prefix/suffix trimming optimization from the spec is not implemented

File:

```text
Omicron.Core/Diff/TextDiffEngine.cs
```

The spec recommended trimming common prefix/suffix before running the core algorithm. Current DP runs over the whole file.

Recommendation:

Implement prefix/suffix trim, especially if the DP implementation stays temporarily. This will dramatically reduce work for mostly-unchanged files.

## Recommendation

Do not integrate this diff engine into workspace transactions yet.

First follow-up pass should:

1. Replace O(N*M) DP with trace-based Myers O(ND), or explicitly add strict guardrails if DP is temporary.
2. Enforce or remove `MaxLineCount`.
3. Implement or remove `OptimizeForReadability`.
4. Fix multi-line replacement hunk rendering.
5. Add exact tests for multi-line replacements, repeated lines, option behavior, and max-size behavior.
6. Add a truncation marker or structured truncation result for renderer output.

The current code is a useful scaffold, but it is not yet the full native diff implementation described in `0003.1`.
