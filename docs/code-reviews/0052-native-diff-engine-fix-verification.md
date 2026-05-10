# Code Review 0052: Native Diff Engine Fix Verification

Date: 2026-05-08  
Scope: verification after fixes for `0051-native-diff-engine-review.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 299

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

Several findings from `0051` were addressed:

- the full O(N*M) DP table was removed;
- the engine now uses a Myers-style forward pass and reconstruction;
- common prefix/suffix trimming was added;
- `OptimizeForReadability` and `IncludeNoNewlineMarkers` were removed rather than left unused;
- multi-line replacement hunk rendering was improved;
- tests were strengthened for several core cases.

However, there are still important issues before this should be treated as production-ready for workspace transaction diffs. The biggest remaining problem is memory behavior of the current trace implementation.

## High-Priority Findings

### 1. Myers trace stores full V arrays for every D, causing large worst-case memory use

File:

```text
Omicron.Core/Diff/TextDiffEngine.cs
```

Current implementation snapshots the entire `V` array at every edit distance:

```csharp
int[] V = new int[2 * maxD + 1];
var trace = new List<int[]>();

for (int D = 0; D <= maxD; D++)
{
    var snapshot = new int[V.Length];
    Array.Copy(V, snapshot, V.Length);
    trace.Add(snapshot);
    ...
}
```

`V.Length` is `2 * (N + M) + 1`, and there can be `N + M + 1` snapshots. In worst-case all-different inputs, memory is roughly:

```text
(N + M + 1) * (2 * (N + M) + 1) * sizeof(int)
```

For 5,000 old lines and 5,000 new lines, this is approximately 800 MB just for trace arrays. This is better algorithmically than the old DP table for some cases, but still unsafe for transaction previews on large files.

Impact:

- default `MaxLineCount = 0` means no guard by default;
- large all-different files can allocate enormous memory;
- this undercuts the original performance/allocations goal.

Recommendation:

Choose one before integrating into workspace transactions:

1. **Set a conservative default max line count** in `TextDiffOptions.Default`, e.g. 5,000 or lower, and represent truncated/too-large diffs explicitly; or
2. **Store compact trace slices** instead of full `V` arrays per `D`; or
3. **Use divide-and-conquer Myers/LCS** for lower memory; or
4. **Use a hybrid strategy**: Myers trace for small/medium inputs, coarse full-file replace/truncated diff for large inputs.

For Phase 4 MVP, option 4 is probably best.

---

### 2. `MaxLineCount` guard returns a partial edit with no truncation metadata

File:

```text
Omicron.Core/Diff/TextDiffEngine.cs
```

Current guard:

```csharp
if (options.MaxLineCount > 0 && (oldLines.Count > options.MaxLineCount || newLines.Count > options.MaxLineCount))
{
    return new TextDiffResult(
        [new TextDiffEdit(0, Math.Min(oldLines.Count, options.MaxLineCount), 0, Math.Min(newLines.Count, options.MaxLineCount))],
        oldLines.Count, newLines.Count);
}
```

This returns an edit covering only the first `MaxLineCount` lines, while `OldLineCount`/`NewLineCount` report the full size. The result does not say it is truncated/too large. A renderer would produce a plausible-looking partial diff and silently omit the rest.

Impact:

- misleading diffs;
- dangerous for edit review workflows;
- no way for workspace transaction code to distinguish complete vs coarse/truncated diff.

Recommendation:

Add explicit metadata:

```csharp
internal sealed record TextDiffResult(
    IReadOnlyList<TextDiffEdit> Edits,
    int OldLineCount,
    int NewLineCount,
    bool IsTruncated = false,
    string? TruncationReason = null);
```

Then either:

- return a full-file coarse replacement edit with `IsTruncated = true`; or
- return no edits plus `IsTruncated = true`; or
- throw a clear exception and let workspace diff layer produce a metadata-only diff.

Do not return a silent partial edit.

---

### 3. Default `MaxLineCount = 0` leaves no safety guard

File:

```text
Omicron.Core/Diff/TextDiffModel.cs
```

The option comment says:

```csharp
/// Maximum number of lines to diff before truncating. 0 = no limit.
public int MaxLineCount { get; init; }
```

Because `TextDiffOptions.Default` uses `0`, the default diff path has no limit.

Given the current trace memory behavior, the default should not be unlimited unless the algorithm is made substantially more memory-efficient.

Recommendation:

Set a real default before any transaction integration, or make transaction code always pass a bounded option.

---

### 4. Renderer truncation issue remains unresolved

File:

```text
Omicron.Core/Diff/UnifiedDiffRenderer.cs
```

The previous review noted that renderer truncates silently and can exceed `MaxOutputBytes` by one huge line. Current code still does this:

```csharp
void AppendLine(string text)
{
    if (lineCount >= options.MaxOutputLines || byteCount >= options.MaxOutputBytes)
        return;
    sb.AppendLine(text);
    lineCount++;
    byteCount += Encoding.UTF8.GetByteCount(text) + 1;
}
```

Impact:

- consumers cannot tell whether rendered diff is complete;
- a single very long line can exceed the byte cap substantially.

Recommendation:

Add a visible truncation marker and check the next append size before appending:

```text
... diff truncated ...
```

If a single line is too large, clip it or emit a metadata line.

---

## Medium-Priority Findings

### 5. Hunk merge condition likely too permissive

File:

```text
Omicron.Core/Diff/TextDiffHunkBuilder.cs
```

Current merge condition:

```csharp
if (gapOld <= 2 * contextLines || gapNew <= 2 * contextLines)
```

Unified diff hunk merging usually requires ranges to overlap/abut in the combined old/new hunk view. Using `||` can merge hunks when only one side is close. This may be okay for some insert/delete cases, but it can create very large hunks in asymmetric edits.

Recommendation:

Add tests for asymmetric insert/delete cases far apart. If no reason for `||`, use `&&` or compute actual hunk ranges and merge only when expanded ranges overlap.

### 6. Exact tests still miss some important cases

Tests improved, but still add coverage for:

- `MaxLineCount` behavior and truncation metadata once added;
- renderer truncation marker;
- huge single-line byte limit behavior;
- exact multi-line replacement rendered text, not just engine edit;
- whitespace/case option behavior;
- asymmetric hunk merge behavior.

## Recommendation

This is a meaningful improvement over the DP version, but I would still avoid integrating it into workspace transactions until the truncation/guardrail semantics are fixed.

Minimum required before Phase 4 integration:

1. Add explicit truncation/too-large metadata to `TextDiffResult` or throw and handle at caller.
2. Set/enforce a safe default size limit or implement compact trace storage.
3. Add renderer truncation marker and byte-aware append behavior.
4. Add tests for the above.

Once those are fixed, the engine should be acceptable for Phase 4 MVP transaction diff previews.
