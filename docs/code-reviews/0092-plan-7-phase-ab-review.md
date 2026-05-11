# Code Review 0092: Plan 7 Phase A+B — UTF-8 Text Store and Cell-Width Model

**Status:** Accepted — 1 critical finding, 1 high finding, 3 medium findings, 2 low findings  
**Date:** 2026-05-10  
**Build:** 0 errors, 581 tests (+35), 0 failures

---

## Verification

```text
dotnet build → 0 errors, 0 warnings
dotnet test  → 581 passing (+35), 0 failed
```

---

## Files Created (10)

| File | Review |
|------|--------|
| `TextChunk.cs` | Pooled 8 KB chunks with `Append()`, `AsSpan()`, `AsMemory()`. Clean. |
| `TextPosition.cs` | Readonly record struct — simple, correct. |
| `Utf8TextStore.cs` | Append-only, lock-based thread safety, zero-copy in-chunk Slice, cross-chunk copy. `FindChunkIndex` does reverse linear scan — fine for append-heavy use. |
| `LogicalLineInfo.cs` | Readonly record struct. `ByteLength` excludes line endings. |
| `LogicalLineIndex.cs` | Append-only scanning, LF/CRLF/CR support, `FindLineContaining` by byte offset. **See Finding 6 — streaming line bug.** |
| `GraphemeCluster.cs` | Readonly record struct. |
| `GraphemeSegmenter.cs` | ZWJ, combining marks, regional indicator pairs. Limitations documented in XML comments. **See Finding 7 — misclassified format characters.** |
| `CellWidthCalculator.cs` | Unicode 15.1 hardcoded tables with binary search. Wide/emoji/combining ranges. Rune+Span overloads. **See Finding 5 — Hangul jamo misclassified as combining.** |
| `TerminalCluster.cs` | Readonly record struct with CellWidth. |
| `Utf8TextStoreTests.cs` | 35 tests across Phase A and B. **See Observations — missing stress and >2-chunk slice tests.** |

---

## Findings (7 total)

### Finding 1: `TextChunk` buffers are never returned to `ArrayPool`

**Severity:** Medium  
**File:** `TextChunk.cs`, `Utf8TextStore.cs`

`TextChunk` rents from `ArrayPool<byte>.Shared` and exposes a `Dispose()` method that returns the buffer. But `Utf8TextStore` never calls `Dispose()` on its chunks — not in a `Clear()`, not in a finalizer, not in `IDisposable`. Every chunk allocated leaks its 8 KB buffer for the lifetime of the store.

For an append-only store that lives for the duration of a session, this is acceptable (chunks are live until the store is garbage collected). But long-running sessions or tests that create many stores will accumulate pooled buffers that were never returned.

**Fix:** Implement `IDisposable` on `Utf8TextStore` that iterates chunks and calls `Dispose()`. Or document that `TextChunk` ownership is transferred to the store and cleanup happens when the store is GC'd (acceptable for MVP since stores are session-lifetime).

---

### Finding 2: Cross-chunk `Slice()` rents from ArrayPool but never returns

**Severity:** Medium  
**File:** `Utf8TextStore.cs` (Slice method, lines ~95–120)

```csharp
byte[] copy = ArrayPool<byte>.Shared.Rent(byteLength);
// ... copy bytes ...
return copy.AsMemory(0, byteLength);
```

The `ReadOnlyMemory<byte>` wraps the rented array, but there's no mechanism to return it. Every cross-chunk slice permanently checks out a buffer from the pool. For append-heavy usage where slices are typically in-chunk (zero-copy), this is rare. But if someone calls `Slice` across chunk boundaries repeatedly, the pool will be exhausted.

**Fix:** Either (a) accept the leak and document that cross-chunk slices are rare and pool exhaustion is unlikely, (b) use `new byte[byteLength]` instead of `ArrayPool` for cross-chunk copies (GC collects it normally), or (c) note this as a known limitation and revisit when profiling shows it's a problem. **Option (b) is simplest and safest.**

---

### Finding 3: `TextChunk` is `public` but plan says `internal`

**Severity:** Low  
**File:** `TextChunk.cs`

Plan says `internal sealed class`. Implementation is `public sealed class`. `TextChunk` is an implementation detail of `Utf8TextStore` — consumers should not directly create or dispose chunks.

**Fix:** Change to `internal`.

---

### Finding 4: `IsCombiningMark` allocates a `Rune` per code point

**Severity:** Low  
**File:** `GraphemeSegmenter.cs` (~line 123)

```csharp
return CellWidthCalculator.GetWidth(new Rune(code)) == 0
    && code > 0x0300;
```

Allocates a `new Rune(code)` just to call `GetWidth(Rune)`. `CellWidthCalculator` should expose a `GetWidth(int code)` overload, or `GraphemeSegmenter` should inline the combining-mark check directly.

**Fix:** Add `CellWidthCalculator.GetWidth(int code)` and use it here.

---

### Finding 5: `CellWidthCalculator` — Hangul Jamo classified as combining marks (width 0 instead of 2)

**Severity:** Critical (functional bug — breaks Korean text rendering)  
**File:** `CellWidthCalculator.cs` (`CombiningRanges` lines 236–238, `HangulJamoRanges` lines 471–473)

**Problem:**

The `CombiningRanges` array includes these three entries:

```csharp
(0x1100, 0x1159), // Hangul Jamo initial consonants
(0x1160, 0x11A2), // Hangul Jamo medial vowels
(0x11A8, 0x11F9), // Hangul Jamo final consonants
```

These characters (U+1100–U+11FF) are **NOT** combining marks. They are `Lo` (Other_Letter) characters in the Hangul Jamo block. In terminals they are universally rendered as **width 2** (wide). However, because `CombiningRanges` is checked **before** `HangulJamoRanges` in `GetWidth`, every Hangul Jamo character returns **width 0**.

The `HangulJamoRanges` array duplicates the exact same three ranges (lines 471–473), but this check is **unreachable** for those code points.

**Impact:**

Any Korean text containing Hangul Jamo (choseng, jungseong, jongseong) will be rendered as zero-width invisible characters. This breaks terminal layout for Korean users and for any model output containing Korean.

**Reproducer:**

```csharp
Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x1100))); // ᄀ — currently returns 0
Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x1161))); // ᅡ — currently returns 0
Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x11A8))); // ᆨ — currently returns 0
```

**Root cause:**

The `CombiningRanges` array was populated with a broad copy-paste that included Hangul Jamo blocks under the mistaken assumption that all jamo are combining marks. Only the medial vowels (`0x1160–0x11A2`) and some modern jamo used in Old Hangul are truly combining in certain contexts; the initial consonants (`0x1100–0x1159`) and final consonants (`0x11A8–0x11F9`) are standalone wide characters.

**Fix:**

Remove the three Hangul Jamo ranges from `CombiningRanges`. Keep them only in `HangulJamoRanges`. Add tests for Korean jamo width.

```csharp
// In CombiningRanges — REMOVE these three lines:
// (0x1100, 0x1159), // NOT a combining mark
// (0x1160, 0x11A2), // NOT a combining mark
// (0x11A8, 0x11F9), // NOT a combining mark

// In HangulJamoRanges — KEEP these (already present):
(0x1100, 0x1159), // Initial consonants — wide
(0x1160, 0x11A2), // Medial vowels — wide
(0x11A8, 0x11F9), // Final consonants — wide
```

**Also check:** The `CombiningRanges` array includes `(0x0900, 0x0903)` (Devanagari signs including `U+0902` ANUSVARA and `U+0903` VISARGA). These are spacing marks, not zero-width. However, they are relatively minor compared to the Hangul bug. Consider reviewing all ranges in `CombiningRanges` against the Unicode `DerivedGeneralCategory.txt` for General Category `Mn`, `Mc`, `Me` only.

---

### Finding 6: `LogicalLineIndex.AppendScan` produces incorrect lines for streaming text

**Severity:** High (functional bug — breaks streaming transcript line indexing)  
**File:** `LogicalLineIndex.cs`

**Problem:**

`AppendScan` eagerly emits an incomplete trailing line when the scanned bytes do not end with a newline. If a subsequent `AppendScan` call continues the same logical line, the index becomes permanently incorrect because the first incomplete line was already committed and cannot be extended.

**Reproducer:**

```csharp
var index = new LogicalLineIndex();
index.AppendScan("hello"u8, 0);          // emits line 0: "hello" (incomplete)
index.AppendScan(" world\n"u8, 5);       // emits line 1: "" (empty, from \n boundary)
                                         // emits line 2: "world"
Assert.Equal(1, index.LineCount);        // EXPECTED: 1 line ("hello world\n")
                                         // ACTUAL:   3 lines
```

The actual text is one line `"hello world"` followed by a newline, but the index reports three lines: `"hello"`, `""`, and `"world"`.

**Impact:**

This breaks the transcript viewport's line-based layout for any streaming output where a model emits text in multiple deltas without a trailing newline. The line count, line wrapping, and `FindLineContaining` will all be wrong. This is the primary use case for `LogicalLineIndex`.

**Root cause:**

`AppendScan` has no concept of a "pending incomplete line." It always emits the trailing content as a line, regardless of whether a newline was found.

**Fix:**

Don't emit incomplete lines in `AppendScan`. Only emit when a newline is found. Track a `_pendingLineStart` for trailing content. Provide a `Flush()` method that emits the final pending line with length computed from the current store length. Update `LineCount` to return `_lines.Count + (_hasPendingLine ? 1 : 0)`.

**Tests to add:**

- `AppendScan_StreamingMidLine_DoesNotEmitUntilNewline`
- `AppendScan_StreamingCompletesLine_EmitsOneLine`
- `AppendScan_StreamingMultipleChunks_CorrectLineCount`
- `AppendScan_Flush_EmitsPendingLine`

---

### Finding 7: `GraphemeSegmenter.IsCombiningMark` misclassifies ZWJ and other format characters as combining marks

**Severity:** Medium (semantic inaccuracy — benign for common cases but fragile)  
**File:** `GraphemeSegmenter.cs` (~line 123)

**Problem:**

```csharp
private static bool IsCombiningMark(int code)
{
    return CellWidthCalculator.GetWidth(new Rune(code)) == 0
        && code > 0x0300;
}
```

This heuristic classifies **any** character with width 0 and code point > U+0300 as a "combining mark." This catches:

- True combining marks (Mn, Mc, Me) ✅
- ZWJ (U+200D), ZWNJ (U+200C), ZWSP (U+200B) ❌ (format characters)
- Variation selectors (U+FE00–U+FE0F, U+E0100–U+E01EF) ❌ (format characters)
- Bidi controls (U+202A–U+202E, U+2066–U+2069) ❌ (format characters)
- Tag characters (U+E0020–U+E007F) ❌ (format characters)

For the segmenter, this is **benign** in common cases because all of these characters should merge with the preceding base character anyway. However:

1. **The method name is misleading.** It claims to check "General Categories Mn, Mc, Me" but actually checks "anything with width 0 above U+0300."
2. **It is fragile.** If a future width-table change assigns width 0 to a character that should NOT merge (e.g., a new line-breaking format character), the segmenter would incorrectly merge it.
3. **It wastes work.** ZWJ is already handled explicitly by `previousRune.Value == 0x200D` and `IsZeroWidthModifier`. The `IsCombiningMark` check for ZWJ is redundant.

**Fix:**

Exclude format characters that are handled by other branches:

```csharp
private static bool IsCombiningMark(int code)
{
    if (code == 0x200B || code == 0x200C || code == 0x200D ||
        (code >= 0xFE00 && code <= 0xFE0F) ||
        (code >= 0xE0100 && code <= 0xE01EF))
        return false; // Handled by IsZeroWidthModifier or explicit ZWJ checks

    return CellWidthCalculator.GetWidth(new Rune(code)) == 0 && code > 0x0300;
}
```

This makes the intent explicit and prevents double-handling.

---

## Observations (Non-blocking)

### Observation 1: `Slice` reads `_chunks` without locking

`Slice()` calls `FindChunkIndex(byteOffset, out chunkOffset)` which accesses `_chunks` and `_chunks.Count` without acquiring `_appendLock`. While `_chunks` only grows and `List<T>` array replacement is atomic on modern .NET, this is technically a data race. In practice it is benign because:
- `_chunks.Count` is read once at loop start.
- `_chunks[i]` uses the array reference at access time.
- The array only grows; old arrays remain valid.

**Recommendation:** Document this as intentional (read-only access to append-only list) or add a lightweight read lock.

### Observation 2: `WideRanges` includes an extremely broad fallback `(0x40000, 0xDFFFF)`

This covers planes 4–13 (most of the Tertiary Ideographic Plane and unassigned planes). Many characters in this range are not CJK and may be width 1 in terminals. However, for MVP this is a conservative over-estimate and acceptable. Consider narrowing in a future hardening pass.

### Observation 3: `EmojiRanges` includes Dingbats `(0x2702, 0x27B0)` which are mostly width 1

Characters like `U+27A1` (→) in this range are typically narrow in terminals. The range is intentionally broad for MVP. Documented in the code comment. Acceptable for now.

### Observation 4: Missing stress/perf tests

The plan specifies:
- `Stress_AppendOneMillionLines_NoLargeStringAllocations`
- Performance: 10k graphemes segmented in <10 ms

These are not in the current test file. Add them before claiming Phase B performance targets are met.

### Observation 5: No test for `Utf8TextStore.Slice` across >2 chunks

Current tests cover single-chunk and two-chunk slices. A slice spanning 3+ chunks (e.g., 24 KB slice from an 8 KB chunk store) is untested. The copy loop in `Slice` supports this, but the test matrix should include it.

---

## Recommended Fix Order

| Priority | Finding | Effort | Blocks Phase C? |
|----------|---------|--------|-----------------|
| P0 | **5** Hangul Jamo width bug | Small | Yes — breaks CJK rendering |
| P0 | **6** Streaming line index bug | Medium | Yes — breaks transcript layout |
| P1 | **2** Cross-chunk Slice ArrayPool leak | Small | Yes — pool exhaustion under load |
| P1 | **1** TextChunk/Utf8TextStore IDisposable | Small | No — session-lifetime acceptable |
| P2 | **3** TextChunk visibility `public`→`internal` | Tiny | No |
| P2 | **4** `IsCombiningMark` Rune allocation | Tiny | No |
| P2 | **7** IsCombiningMark semantic cleanup | Small | No |

---

## Definition of Done for Fix Verification

- [ ] `CellWidthCalculator.GetWidth(new Rune(0x1100))` returns 2 (Hangul initial consonant).
- [ ] `CellWidthCalculator.GetWidth(new Rune(0x1161))` returns 2 (Hangul medial vowel).
- [ ] `CellWidthCalculator.GetWidth(new Rune(0x11A8))` returns 2 (Hangul final consonant).
- [ ] `LogicalLineIndex` streaming test: two `AppendScan` calls completing one line produce `LineCount == 1`.
- [ ] `Utf8TextStore.Slice` across 3+ chunks produces correct bytes.
- [ ] `Utf8TextStore.Slice` cross-chunk uses `new byte[]` or returns pool memory safely.
- [ ] `TextChunk` is `internal`.
- [ ] `GraphemeSegmenter.IsCombiningMark` does not allocate `Rune`.
- [ ] All 581+ tests pass, 0 warnings.
