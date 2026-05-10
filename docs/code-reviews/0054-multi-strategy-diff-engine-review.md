# Code Review 0054: Multi-Strategy Diff Engine Review

Date: 2026-05-08  
Scope: review of multi-strategy diff engine after strategy/fallback refactor.

## Validation

Baseline validation after removing temporary probe test:

```text
dotnet test Omicron.slnx --nologo
Passed: 304

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

I also ran a temporary probe test for the divide-and-conquer fallback path:

```csharp
var oldLines = Enumerable.Range(0, 3000).Select(i => $"line-{i}").ToArray();
var newLines = Enumerable.Range(0, 3000).Select(i => i == 1500 ? "changed" : $"line-{i}").ToArray();
var diff = TextDiffEngine.DiffLines(oldLines, newLines);
```

Expected: one exact replacement edit at line 1500.  
Actual: `TextDiffResult.IsTruncated = true`, `TruncationReason = "All diff strategies declined. Old=3000, New=3000"`.

The temporary probe file was removed after running.

## Summary

The high-level multi-strategy architecture is the right direction:

```text
TextDiffEngine facade
  -> TraceMyersDiffStrategy for small inputs
  -> DivideAndConquerMyersDiffStrategy for larger/lower-memory fallback
  -> explicit omitted/truncated status if all exact strategies decline
```

The renderer now correctly handles `diff.IsTruncated` even when there are no edits, which fixes the previous silent-empty rendering issue.

However, the divide-and-conquer fallback does not appear to work for a basic large mostly-equal file with one changed line. Since that fallback is the whole reason the multi-strategy architecture exists, this is currently a blocker before workspace transaction integration.

## Blocking Findings

### 1. Divide-and-conquer fallback fails on a basic large mostly-equal edit

Files:

```text
Omicron.Core/Diff/TextDiffEngine.cs
Omicron.Core/Diff/DivideAndConquerMyersDiffStrategy.cs
```

`TextDiffEngine` routes inputs larger than `TraceThreshold = 2000` to `DivideAndConquerMyersDiffStrategy`.

A 3000-line file with one changed line should be an ideal case for the fallback: large input, tiny middle changed range after prefix/suffix trimming. Instead, all strategies decline and the engine returns a truncated/omitted result.

This means any workspace transaction diff over a file larger than 2000 lines may omit diffs even for trivial single-line changes.

Likely root cause is in `FindMiddleSnake(...)`.

Suspicious code:

```csharp
for (int k = -D; k <= D; k++)
{
    if ((k & 1) != 0) continue; // only even k for this D
    ...
}
```

In Myers, the diagonal parity depends on `D`; the loop should usually step by 2 from `-D` to `D`, preserving the parity of `D`:

```csharp
for (int k = -D; k <= D; k += 2)
```

For odd `D`, valid diagonals are odd. The current code skips all odd `k`, so odd-depth expansion does not happen correctly.

The reverse pass also appears suspicious:

```csharp
revV[1 + offset] = oldHi;
...
if (k == -D || (k != D && revV[k - 1 + offset] > revV[k + 1 + offset]))
    x = revV[k - 1 + offset] - 1;
else
    x = revV[k + 1 + offset];
```

At `D = 0`, this can read sentinel values instead of the initialized reverse diagonal and produce invalid negative coordinates. The strategy catches all exceptions and returns null, which hides these correctness failures as graceful fallback.

Recommendation:

- Fix `FindMiddleSnake` parity and reverse-vector initialization/indexing.
- Add direct tests that force the divide-and-conquer strategy path:
  - 3000-line mostly equal file with one replacement;
  - 3000-line file with one insertion;
  - 3000-line file with one deletion;
  - 3000-line all same returns no edits;
  - 3000-line low-overlap either returns exact diff or explicit omitted for a valid reason.

---

### 2. Divide-and-conquer strategy catches all exceptions and converts bugs into omitted diffs

File:

```text
Omicron.Core/Diff/DivideAndConquerMyersDiffStrategy.cs
```

Current code:

```csharp
try
{
    var edits = new List<TextDiffEdit>();
    BuildEdits(...);
    return edits;
}
catch
{
    return null; // Fall back gracefully
}
```

This is reasonable for a production fallback boundary only after the strategy is well tested, but right now it masks implementation bugs. The facade converts null into:

```text
All diff strategies declined
```

Impact:

- tests can pass while the fallback is broken;
- real diffs become omitted instead of surfacing implementation errors;
- debugging is harder because the exception detail is lost.

Recommendation:

During development/tests, either:

- remove the blanket catch; or
- catch known resource exceptions only; or
- include exception details in the returned decline reason/status; or
- add an internal diagnostic callback/reason.

At minimum, tests should directly exercise the fallback and fail if it declines on normal input.

---

### 3. No tests verify strategy selection/fallback correctness

Files:

```text
Omicron.Core.Tests/TextDiffEngineTests.cs
Omicron.Core.Tests/UnifiedDiffRendererTests.cs
```

Current test count increased, but there does not appear to be a direct test proving that the large-input divide-and-conquer strategy produces exact edits. Existing large tests either:

- trigger omitted/truncated behavior intentionally; or
- use input sizes under the trace threshold.

Recommendation:

Add tests around the facade behavior at the threshold boundary:

```csharp
Diff_LargeMostlyEqual_UsesFallbackAndReturnsExactSingleReplace()
Diff_LargeMostlyEqual_UsesFallbackAndReturnsExactSingleInsert()
Diff_LargeMostlyEqual_UsesFallbackAndReturnsExactSingleDelete()
Diff_TraceAndFallbackProduceSameEdit_ForRepresentativeCases()
```

If direct strategy testing is preferred, expose strategy classes to tests via `internal` as they already are, and instantiate `DivideAndConquerMyersDiffStrategy` directly with normalized int arrays.

## Medium-Priority Findings

### 4. `MaxLineCount` is still in core options, but now acts as explicit omit policy

File:

```text
Omicron.Core/Diff/TextDiffModel.cs
```

This is better than fabricated partial edits, but it is still a policy concern mixed into the core algorithm API. Since default is `0`, it is not currently harmful. Keep it only if we want the core facade to own resource policy.

Longer term, consider renaming/reshaping this into something explicit:

```csharp
TextDiffResourcePolicy
TextDiffResourceBudget
```

with a status enum:

```csharp
TextDiffStatus.Complete
TextDiffStatus.Omitted
TextDiffStatus.Cancelled
```

`IsTruncated` is slightly misleading for engine-level omitted diffs because no partial diff was produced.

### 5. Trace threshold is hardcoded

File:

```text
Omicron.Core/Diff/TextDiffEngine.cs
```

```csharp
private const int TraceThreshold = 2000;
```

This is acceptable for MVP, but if we keep multi-strategy selection, thresholds may eventually belong in options or resource policy. Do not expose prematurely; just note it.

## Recommendation

Do not integrate the diff engine into workspace transactions yet.

Next required pass:

1. Fix divide-and-conquer middle-snake implementation.
2. Remove or narrow blanket exception swallowing while developing.
3. Add tests that force the fallback strategy and verify exact edits for large mostly-equal files.
4. Re-run full validation.

Once fallback produces exact diffs for normal large inputs, the multi-strategy architecture will be suitable for Phase 4 integration.
