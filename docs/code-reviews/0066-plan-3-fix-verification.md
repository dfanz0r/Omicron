# Code Review 0066 — Plan 3 Fix Verification

**Scope:** Verify the 10 immediate action items from Review 0065.  
**Date:** 2026-05-08  
**Build:** 0 warnings, 0 errors  
**Tests:** 346 passed, 0 failed  

---

## Verification Results

| # | Item | Claimed | Actual | Verdict |
|---|------|---------|--------|---------|
| 1 | Convert `Message` → `sealed record` | ✅ | ✅ **FIXED** | `Message` is now `public sealed record` with `init` properties. `List<>` collections changed to `IReadOnlyList<>`. `UsageInfo` and `LlmResult` also converted to records. |
| 2 | Remove redundant `Interlocked.Increment` inside `lock` | ✅ | ✅ **FIXED** | `InMemoryEventSink.Emit()` and `EmitBatch()` now use `++_globalSequence` inside the `lock` block. `Interlocked` removed. |
| 3 | Cache `JsonSerializerOptions` in `JsonlSessionStore` | ✅ | ❌ **NOT FIXED** | `SerializeEvent()` still allocates `new JsonSerializerOptions` **per event call** (line 363). The instance field `_jsonOptions` exists but is **not used** in `SerializeEvent` or `DeserializeEvent`. |
| 4 | Fix `TextLineSplitter` double `\r` replace | ✅ | ❌ **NOT FIXED** | `SplitLines()` still contains `.Replace("\r\n", "\n").Replace("\r", "\n")` (line 14). The second replace is redundant and still present. |
| 5 | Move `DisplayHelpers` out of `Program.cs` | Deferred | ❌ **NOT FIXED** | `DisplayHelpers` is still defined inside `Omicron.CLI/Program.cs` (lines 292–322). |
| 6 | Deduplicate `MaxOutputLines` / `MaxOutputBytes` constants | Noted for future | ❌ **NOT FIXED** | `WorkspaceReadService` (lines 24–25) and `LocalExecutionBroker` (lines 51–54) still independently declare identical constants. |
| 7 | Add `AnthropicProvider` streaming tests | Skipped | ❌ **NOT ADDED** | No new test file exists. `AnthropicProvider` still has zero direct test coverage. |
| 8 | Add `DivideAndConquerMyersDiffStrategy` direct tests | Covered indirectly | ❌ **NOT ADDED** | No direct unit tests for the D&C strategy. Only facade-level coverage through `TextDiffEngineTests`. |
| 9 | Add nested directory overlay test | Skipped | ❌ **NOT ADDED** | No new test added for `TransactionFileSystem.ReadDirectoryAsync` with nested paths. |
| 10 | Add `GoogleGenAi` stub / remove from enum | ✅ | ✅ **FIXED** | `ShapeBasedProvider.StreamAsync` now routes `ApiType.GoogleGenAi` to `throw new NotSupportedException("GoogleGenAi is not yet implemented.")` (line 221). |

---

## Detailed Findings

### ✅ Item 1 — `Message` converted to `sealed record`

**File:** `Omicron.Core/Models/Message.cs`

```csharp
public sealed record Message
{
    public MessageRole Role { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<ImageContent>? Images { get; init; }
    public IReadOnlyList<ToolCallContent>? ToolCalls { get; init; }
    // ...
}
```

- `class` → `sealed record` ✓
- `List<ImageContent>` → `IReadOnlyList<ImageContent>` ✓
- `List<ToolCallContent>` → `IReadOnlyList<ToolCallContent>` ✓
- Factory methods updated to accept `IReadOnlyList<>` ✓
- `UsageInfo` and `LlmResult` also converted to records ✓
- Build passes; all 346 tests pass ✓

**Status:** Correctly fixed. Value semantics are now enforced.

---

### ✅ Item 2 — `Interlocked.Increment` removed from `InMemoryEventSink`

**File:** `Omicron.Core/Events/IEventSink.cs`

```csharp
public OmicronEvent Emit(OmicronEvent evt)
{
    lock (_lock)
    {
        var seq = ++_globalSequence;  // was: Interlocked.Increment(ref _globalSequence)
        // ...
    }
}
```

Both `Emit()` and `EmitBatch()` now use `++_globalSequence` inside the lock.

**Status:** Correctly fixed. The lock already guarantees atomicity; the interlocked was redundant.

---

### ❌ Item 3 — `JsonSerializerOptions` NOT cached in `SerializeEvent`

**File:** `Omicron.Core/Sessions/SessionStore.cs` (line 363)

**Current code:**
```csharp
private static string SerializeEvent(OmicronEvent evt)
{
    var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    var json = JsonSerializer.Serialize((object)evt, evt.GetType(), opts);
    var node = JsonNode.Parse(json)!;
    node["$type"] = evt.GetType().Name;
    return node.ToJsonString(opts);
}
```

**Issue:** A new `JsonSerializerOptions` is allocated **on every single event serialization**. The instance field `_jsonOptions` (initialized in the constructor) is never referenced by `SerializeEvent` or `DeserializeEvent`.

**Required fix:**
```csharp
private string SerializeEvent(OmicronEvent evt)
{
    var json = JsonSerializer.Serialize((object)evt, evt.GetType(), _jsonOptions);
    var node = JsonNode.Parse(json)!;
    node["$type"] = evt.GetType().Name;
    return node.ToJsonString(_jsonOptions);
}
```

Note: `SerializeEvent` and `DeserializeEvent` should become instance methods (not `static`) to access `_jsonOptions`.

**Status:** **Not fixed.** The user's claim that it is "already cached as instance field" is true that the field exists, but false that it is being used.

---

### ❌ Item 4 — `TextLineSplitter` double `\r` replace NOT removed

**File:** `Omicron.Core/Diff/TextLineSplitter.cs` (line 14)

**Current code:**
```csharp
return text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
```

**Issue:** After `.Replace("\r\n", "\n")`, all standalone `\r` characters that were part of `\r\n` pairs are already gone. The second `.Replace("\r", "\n")` only handles isolated `\r` characters (classic Mac OS 9 style). This is not redundant if the input might contain isolated `\r`, but the review claimed it was removed and it is still present.

**Status:** **Not fixed.** The code is unchanged from the pre-review state.

---

### ❌ Item 5 — `DisplayHelpers` still in `Program.cs`

**File:** `Omicron.CLI/Program.cs` (lines 292–322)

`public static class DisplayHelpers` is still defined at the bottom of `Program.cs`.

**Status:** Deferred (user admits this). No action needed now, but should be tracked.

---

### ❌ Item 6 — Constants still duplicated

**Files:**
- `Omicron.Core/Workspace/WorkspaceReadService.cs` lines 24–25:
  ```csharp
  public const int MaxOutputLines = 2000;
  public const int MaxOutputBytes = 50 * 1024;
  ```
- `Omicron.Core/Execution/IExecutionBroker.cs` lines 51–54:
  ```csharp
  public const int MaxOutputBytes = 50 * 1024;
  public const int MaxOutputLines = 2000;
  ```

**Status:** Not fixed (user admits this). Should be tracked for future consolidation.

---

### ❌ Items 7–9 — Tests not added

No new test files were created:
- No `AnthropicProviderTests.cs`
- No `DivideAndConquerMyersDiffStrategyTests.cs`
- No nested-directory overlay test in `WorkspaceTransactionTests.cs`

**Status:** Skipped per user. These are legitimate testing gaps but the user chose to defer them.

---

### ✅ Item 10 — `GoogleGenAi` stub added

**File:** `Omicron.Core/Providers/IChatProvider.cs` (ShapeBasedProvider, line 221)

```csharp
var endpoint = model.ApiType switch
{
    ApiType.AnthropicMessages => $"{baseUrl.TrimEnd('/')}/messages",
    ApiType.OpenAiResponses => $"{baseUrl.TrimEnd('/')}/responses",
    ApiType.GoogleGenAi => throw new NotSupportedException("GoogleGenAi is not yet implemented."),
    _ => $"{baseUrl.TrimEnd('/')}/chat/completions"
};
```

**Status:** Correctly fixed. Runtime will throw a clear error instead of routing to the wrong endpoint.

---

## Summary

| Category | Count |
|----------|-------|
| ✅ Actually fixed | 3 |
| ❌ Claimed fixed but NOT actually fixed | 2 |
| ⏸️ Deferred / skipped (user admits) | 5 |

### Recommended Next Steps

1. **Fix Item 3** (`JsonSerializerOptions` caching) — trivial 2-line change.
2. **Fix Item 4** (`TextLineSplitter` double replace) — trivial 1-line change (or explicitly document that isolated `\r` handling is intentional).
3. **Decide on Items 5–9** — these are all low-priority and can remain deferred, but should stay in the backlog.

No blockers for Plan 4. The two mis-reported fixes are trivial and do not affect runtime correctness.
