# Code Review 0053: Diff Engine Strategy/Fallback Direction

Date: 2026-05-08  
Scope: decision after review of trace-based Myers memory behavior.

## Decision

Use a **multi-strategy diff engine** with an explicit fallback process.

Do not make semantic line-count caps the core solution. A core diff engine should either produce an exact edit script or explicitly fail/omit due to caller-defined resource policy. It should not return plausible partial edits.

However, we do not need to throw away the trace-based implementation. It can remain one strategy for small/medium/common cases, while a lower-memory divide-and-conquer Myers strategy acts as the safe fallback for large/high-risk cases.

## Rationale

Trace-based Myers:

- simpler to implement/debug;
- can be fast for small/medium inputs;
- reconstruction is straightforward;
- memory can blow up for large high-edit-distance inputs because of trace snapshots.

Divide-and-conquer Myers:

- more complex;
- still computes shortest edit scripts;
- uses O(N + M) working memory plus recursion/output;
- safer for generated files, lockfiles, whole-file rewrites, vendored files, and low-overlap inputs.

A strategy pipeline gives us both:

```text
common case speed/simplicity
  +
large/high-risk memory safety
```

## Proposed Internal Architecture

```csharp
internal interface ITextDiffStrategy
{
    string Name { get; }
    bool CanRun(TextDiffInputStats stats, TextDiffOptions options);
    TextDiffResult Diff(
        IReadOnlyList<int> oldCodes,
        IReadOnlyList<int> newCodes,
        TextDiffOptions options);
}
```

```csharp
internal sealed record TextDiffInputStats(
    int OldLineCount,
    int NewLineCount,
    int OldMiddleCount,
    int NewMiddleCount,
    double? EstimatedTokenOverlap = null);
```

Initial strategies:

```text
TraceMyersDiffStrategy
  - preferred for small/medium inputs where trace memory is bounded
  - exact diff

DivideAndConquerMyersDiffStrategy
  - fallback for large/high-risk inputs
  - exact diff
  - shortest-middle-snake recursion

ResourceLimit/Omitted strategy (optional future)
  - only if explicit caller budget/cancellation prevents exact diff
  - metadata-only result/status
  - never fake partial edits
```

## Fallback Process

```text
TextDiffEngine.DiffLines(...):
  normalize lines to comparable integer codes
  trim common prefix/suffix
  compute cheap stats
  if TraceMyersDiffStrategy.CanRun(stats, options):
      try trace strategy
      if success: return result
      if resource failure: continue
  try DivideAndConquerMyersDiffStrategy
  if success: return result
  if explicit caller budget prevents completion:
      return ResourceLimitExceeded/Omitted status
```

Do not return partial-looking edits for resource failure.

## API Guidance

Current/future `TextDiffResult` should distinguish complete exact diffs from omitted/resource-limited results if resource policy exists:

```csharp
internal enum TextDiffStatus
{
    Complete,
    ResourceLimitExceeded,
    Cancelled
}
```

Renderer output truncation is separate and remains valid:

```text
... diff truncated (output limit reached)
```

Algorithm resource fallback should produce metadata/status, not fake edits.

## Divide-and-Conquer Fallback Shape

High-level:

```text
DiffRange(oldLo, oldHi, newLo, newHi):
  trim equal prefix/suffix
  if old range empty: emit insert
  if new range empty: emit delete
  else:
    find middle snake using forward/reverse Myers search
    DiffRange(left side)
    skip equal middle snake
    DiffRange(right side)
```

Recommended internal type:

```csharp
private readonly record struct MiddleSnake(
    int OldStart,
    int NewStart,
    int OldEnd,
    int NewEnd);
```

## Tests Required

Keep existing tests and add/strengthen:

- strategy selection chooses trace for small input;
- strategy selection chooses divide-and-conquer for large/high-risk input;
- both strategies produce same edits for standard cases;
- low-overlap large input completes without line-count truncation metadata;
- all same;
- all changes;
- insert/delete at beginning/end;
- multi-line replacement 2 -> 3 and 3 -> 1;
- repeated lines;
- snake case;
- empty old/new;
- renderer output truncation still works independently of algorithm.

## External Reference

`READ_ONLY/Diff` implements a divide-and-conquer shortest-middle-snake variant and can be used as reference material only. Do not copy it verbatim. Keep Omicron-owned namespace, types, tests, and implementation style.
