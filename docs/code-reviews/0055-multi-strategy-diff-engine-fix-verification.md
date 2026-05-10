# Code Review 0055: Multi-Strategy Diff Engine Fix Verification

Date: 2026-05-08  
Scope: verification after divide-and-conquer fallback fixes from `0054-multi-strategy-diff-engine-review.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 308

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

The major blocker from `0054` is fixed:

- divide-and-conquer fallback now handles >2000-line mostly-equal files with exact single replace/insert/delete edits;
- strategy no longer swallows all exceptions;
- tests now force the fallback path for large inputs.

This makes the multi-strategy architecture credible for Phase 4 transaction diff integration.

## Remaining Findings

### 1. Divide-and-conquer fallback can produce adjacent insert+replace edits that should be normalized together

Files:

```text
Omicron.Core/Diff/DivideAndConquerMyersDiffStrategy.cs
Omicron.Core/Diff/TraceMyersDiffStrategy.cs
```

I ran a temporary probe for a >2000-line multi-line replacement forcing the divide-and-conquer path:

```csharp
old[1000..1003] = old-A, old-B, old-C
new[1000..1004] = new-A, new-B, new-C, old line shifted
```

Expected a single replacement-ish edit spanning the changed region. Actual result:

```text
TextDiffEdit(OldStart = 1000, OldCount = 0, NewStart = 1000, NewCount = 1)   // insert
TextDiffEdit(OldStart = 1000, OldCount = 3, NewStart = 1001, NewCount = 3)   // replace
```

This is still a valid edit script, but it is less canonical/readable and may render as an insert followed by a replacement in a confusing order.

Root cause:

- `TraceMyersDiffStrategy` has `MergeToEdits(...)` and a limited delete+insert replacement merge.
- `DivideAndConquerMyersDiffStrategy` appends recursive edits directly and does not run a shared final normalization pass.
- The existing merge logic only handles delete+insert order, not insert+delete/replace adjacency or overlapping starts from recursive decomposition.

Recommendation:

Introduce a shared edit normalization utility, e.g.:

```csharp
internal static class TextDiffEditNormalizer
{
    public static List<TextDiffEdit> Normalize(IReadOnlyList<TextDiffEdit> edits);
}
```

It should:

- sort edits by `(OldStart, NewStart)` if needed;
- merge adjacent insert/delete pairs into replacements;
- merge insert+replace and replace+insert where they describe one contiguous changed region;
- merge delete+replace and replace+delete similarly;
- preserve exactness.

Run this normalization in the facade after any strategy returns:

```csharp
edits = TextDiffEditNormalizer.Normalize(edits);
```

Add tests for large fallback multi-line replacement with unequal counts.

This is not a blocker if renderer output is acceptable, but it is worth fixing before transaction integration because transaction diffs should be stable and readable.

---

### 2. `DivideAndConquerMyersDiffStrategy.FindMiddleSnake` still has fragile-looking initialization

File:

```text
Omicron.Core/Diff/DivideAndConquerMyersDiffStrategy.cs
```

Current initialization:

```csharp
fwdV[1 + offset] = oldLo;
revV[1 + offset] = oldHi;
```

This mirrors the older SMS style, but it is non-obvious and should be protected by more tests. The fallback now passes basic large replace/insert/delete tests, which is good. Still, shortest-middle-snake implementations are easy to get subtly wrong around parity and boundaries.

Recommendation:

Add more parity/boundary tests for the fallback path:

- large replacement where old/new changed spans have unequal lengths;
- insertion at beginning/end above threshold;
- deletion at beginning/end above threshold;
- repeated lines above threshold;
- all-different large input returns one replacement edit or otherwise stable exact edit script.

### 3. Core still uses `IsTruncated` for omitted/resource status

File:

```text
Omicron.Core/Diff/TextDiffModel.cs
```

`IsTruncated` now means two different things depending on layer:

- engine omitted exact diff due to `MaxLineCount`/strategy decline;
- renderer output was truncated due to display limits.

The renderer marker distinguishes these, but the model terminology is still slightly muddy.

Recommendation:

Non-blocking, but eventually replace with:

```csharp
TextDiffStatus.Complete
TextDiffStatus.Omitted
TextDiffStatus.Cancelled
```

and keep renderer truncation separate.

## Recommendation

The previous blocker is fixed, and the engine is close enough to proceed if needed. Before wiring it into workspace transactions, I recommend one small cleanup pass:

1. add a shared `TextDiffEditNormalizer` and normalize facade results from all strategies;
2. add fallback-path tests for large multi-line unequal replacements and boundary insert/delete cases;
3. optionally rename `IsTruncated` to an explicit status later.

After item 1 and tests, this should be ready for Phase 4 transaction diff integration.
