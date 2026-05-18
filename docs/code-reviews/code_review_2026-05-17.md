# Code-Review Validation – Omicron Core

**Date:** 2026-05-17
**Repository:** `/home/matt/development/Omicron`
**Target framework:** .NET 10
**Status:** Updated after manual source verification

---

## Executive Summary

The original report was reviewed against the current code. The main conclusion is that **two findings are clearly valid and should be fixed**, while the remaining findings are **partially valid or already mostly addressed**.

| Area | Verdict | Concern | Notes |
|---|---|---:|---|
| UTF-8 tool-result propagation | **Valid** | High | `SessionProjection` preserves UTF-8 tool results, but `ConversationConverter` and `AnthropicProvider` still read `Message.Text` directly and can drop UTF-8-only payloads. |
| Workspace diff truncation | **Partially valid** | Medium/Low | Modified-file diffs and the renderer already handle truncation correctly. The move branch still gates rendering on `diff.HasChanges`, but current staging only supports pure moves, not move+edit. |
| `TextProcessor` byte-budget accounting | **Valid** | Medium | Header/warning bytes are not counted, prefix cost is hard-coded as 8 bytes, and the first emitted line can exceed the byte limit. |
| `read_file_hashlines` byte-budget accounting | **Mostly already handled** | Low | Header and rendered hashline components are already counted. Remaining issues are edge cases around truncation notice accounting/future-proof prefix calculation. |

Recommended priority:

1. Fix UTF-8 tool-result propagation through `Message.EffectiveText`.
2. Fix exact rendered-byte accounting in `TextProcessor`.
3. Decide intended behavior for pure large file moves before changing move-diff truncation behavior.
4. Optionally clean up `read_file_hashlines` accounting edge cases and add regression tests.

---

## Scope of Validation

The following files were checked directly:

* `Omicron.Core/Models/Message.cs`
* `Omicron.Core/Models/Conversation.cs`
* `Omicron.Core/Providers/AnthropicProvider.cs`
* `Omicron.Core/Sessions/SessionProjection.cs`
* `Omicron.Core/Workspace/WorkspaceTransaction.cs`
* `Omicron.Core/Diff/UnifiedDiffRenderer.cs`
* `Omicron.Core/Diff/TextDiffEngine.cs`
* `Omicron.Core/Extensions/BuiltinWorkspaceToolsExtension.cs`
* `Omicron.Core/IO/Formats/TextProcessor.cs`

---

## Detailed Findings

### 1. UTF-8 tool-result propagation — **valid**

**Files:**

* `Omicron.Core/Sessions/SessionProjection.cs`
* `Omicron.Core/Models/Message.cs`
* `Omicron.Core/Models/Conversation.cs`
* `Omicron.Core/Providers/AnthropicProvider.cs`

`Message` intentionally supports text payloads in either `Text` or `Utf8Text`:

```csharp
public string EffectiveText => Utf8Text.HasValue
    ? Encoding.UTF8.GetString(Utf8Text.Value.Span)
    : Text ?? string.Empty;
```

`SessionProjection` correctly preserves UTF-8-only tool results:

```csharp
Text = tic.ResultBytes.HasValue ? null : tic.Result,
Utf8Text = tic.ResultBytes,
```

However, downstream code still reads `Text` directly:

* `ConversationConverter.ToCanonical()` uses `msg.Text ?? ""` for `ToolResultContentItem`.
* `AnthropicProvider.BuildRequestBody()` uses `msg.Text ?? ""` for user, assistant, and tool-result content.

That means a tool result with `Utf8Text` populated and `Text == null` can become an empty string during canonical conversion or Anthropic request serialization.

**Verdict:** valid issue.

**Recommended change:**

* In tool-result conversion/serialization paths, use `msg.EffectiveText` instead of `msg.Text ?? ""`.
* In `AnthropicProvider`, consider using `EffectiveText` for all message roles for consistency.

**Suggested tests:**

* `ConversationUsesEffectiveTextForToolResults`
* `AnthropicProviderUsesEffectiveTextForToolResults`
* `ToolResultUtf8Projection`

---

### 2. Workspace diff truncation handling — **partially valid**

**Files:**

* `Omicron.Core/Workspace/WorkspaceTransaction.cs`
* `Omicron.Core/Diff/UnifiedDiffRenderer.cs`
* `Omicron.Core/Diff/TextDiffEngine.cs`

The general truncation-aware no-op rule is correct:

```csharp
if (!diff.HasChanges && !diff.IsTruncated)
    return null;
```

`UnifiedDiffRenderer` already honors this rule and renders explicit truncation output when `diff.IsTruncated` is true.

The modified-file path in `WorkspaceTransaction.BuildDiff()` is also already correct:

```csharp
if (!diff.HasChanges && !diff.IsTruncated)
    return null;

var textDiff = UnifiedDiffRenderer.Render(...);
```

The remaining inconsistent branch is move handling in `WorkspaceTransaction.GetDiffAsync()`:

```csharp
var textDiff = diff.HasChanges
    ? UnifiedDiffRenderer.Render(fromPath, pathStr, oldLines, newLines, diff)
    : null;
```

This can suppress a `TextDiffResult` where `IsTruncated == true` and `HasChanges == false`.

However, current staging semantics make the practical impact narrower than the original report implied:

* `StageMove()` creates a pure move.
* `StageWrite()` rejects writes to a move destination.
* Therefore the current move branch does not support a true “move plus content edit” case.
* For pure moves, `entry.Content` is empty and `newContent` is set to `oldBytes`, so rendering a “diff truncated” notice for a large rename may be misleading if content was never intended to differ.

**Verdict:** partially valid.

**Recommended change:**

Choose one of these intentionally:

1. If move diffs should only report content changes, skip textual diffing for pure moves and leave `TextDiff` null.
2. If future move+edit support is desired, make that branch truncation-aware with:

   ```csharp
   var textDiff = diff.HasChanges || diff.IsTruncated
       ? UnifiedDiffRenderer.Render(fromPath, pathStr, oldLines, newLines, diff)
       : null;
   ```

Do not blindly add truncation output for pure large renames unless that UX is desired.

**Suggested tests:**

* Existing modified-file truncation rendering should remain covered.
* Add a test only after deciding expected pure-move behavior.

---

### 3. Byte-budget accounting

The repository advertises 2000-line / 50 KB caps for several text-rendering paths. Exact accounting matters because the rendered output includes headers, prefixes, separators, and truncation notices in addition to file content.

---

#### 3.1 `TextProcessor` — **valid**

**File:** `Omicron.Core/IO/Formats/TextProcessor.cs`

The original report is correct for `TextProcessor`.

Current issues in both UTF-8 and fallback paths:

1. Header bytes are not counted.

   ```csharp
   int writtenBytes = 0;
   ```

   This is initialized after the header has already been appended, so the `[FILE] ...` header and optional warning line do not count against the 50 KB cap.

2. Prefix size is hard-coded as 8 bytes.

   ```csharp
   int lineBytes = 8 + lineLength + 1;
   var lineBytes = 8 + Encoding.UTF8.GetByteCount(lineText) + 1;
   ```

   That only matches the format `$"{lineNumber,6}| "` while the line number fits in six columns. Larger line numbers expand the prefix.

3. The byte-limit check is guarded by `writtenBytes > 0`, so the first emitted line can exceed the 50 KB limit if it is large enough.

4. Truncation notice bytes are appended after the loop and are not included in accounting.

**Verdict:** valid issue.

**Recommended change:**

* Initialize `writtenBytes` from `builder.Length` after all headers/warnings are appended.
* Compute the actual prefix first:

  ```csharp
  var prefix = $"{i + 1,6}| ";
  var lineBytes = Encoding.UTF8.GetByteCount(prefix) + lineContentBytes + 1;
  ```

* Remove the `writtenBytes > 0` guard from byte-limit checks unless there is an explicit requirement to always emit at least one line.
* Consider accounting for the truncation notice as well, or document that the marker may exceed the nominal cap.

**Suggested tests:**

* `TextProcessorHeaderBudget`
* `TextProcessorLargeLineNumberBudget`
* `TextProcessorFirstLongLineBudget`

---

#### 3.2 `read_file_hashlines` — **mostly already handled**

**File:** `Omicron.Core/Extensions/BuiltinWorkspaceToolsExtension.cs`

The original report overstated this issue for the current code.

The current implementation already seeds byte accounting from the rendered header:

```csharp
int writtenBytes = output.Length;
```

It also counts the rendered hashline components:

```csharp
var lineBytes = lineNumDigits + 2 + 1 + lineLength + 1;
```

This includes:

* line-number digits
* 2-letter anchor
* pipe separator
* line content bytes
* newline

For today’s 2000-line output cap, the manual digit calculation is effectively safe because rendered line numbers cannot exceed four digits. It is still less future-proof than computing the actual rendered prefix.

Remaining edge cases:

* Truncation notice bytes are not counted.
* If the first omitted line is the final source line, `truncated = i < totalLines - 1` suppresses the truncation notice even though a line was omitted.
* The manual digit calculation would become wrong if the line cap were raised high enough.

**Verdict:** mostly already handled; only low-priority edge cases remain.

**Recommended change:**

Optional cleanup:

* Compute hashline prefix bytes from the actual rendered prefix or helper.
* Set `truncated = true` whenever output stops before `endLine`/`totalLines` due to output limits.
* Decide whether the truncation marker itself must fit inside the 50 KB budget.

**Suggested tests:**

* `HashlineByteBudgetIncludesHeaderAndPrefix`
* `HashlineShowsTruncationWhenLastLineOmitted`

---

## Required Changes

### Should fix

1. **Use `Message.EffectiveText` for UTF-8-capable downstream consumers.**
   * `ConversationConverter.ToCanonical()` for tool results.
   * `AnthropicProvider.BuildRequestBody()` for user, assistant, and tool-result content.

2. **Fix `TextProcessor` byte-budget accounting.**
   * Count headers/warnings.
   * Count actual rendered prefixes.
   * Prevent the first emitted line from exceeding the byte budget unless explicitly intended.

### Should decide before changing

3. **Move-diff truncation behavior.**
   * Current modified-file truncation handling is correct.
   * Move handling has a truncation-unaware `diff.HasChanges` gate.
   * Because current moves are pure renames, decide whether large pure moves should render a truncation notice or no text diff.

### Optional cleanup

4. **`read_file_hashlines` edge cases.**
   * Current accounting already includes headers and main hashline components.
   * Cleanup is mainly for truncation marker accounting and future-proofing.

---

## Testing Gaps & Suggested Tests

| Test | Target | Purpose | Priority |
|---|---|---|---:|
| `ToolResultUtf8Projection` | `SessionProjection` / `AgentSession` | Verify UTF-8 tool results are preserved as `Utf8Text` and exposed through `EffectiveText`. | High |
| `ConversationUsesEffectiveTextForToolResults` | `ConversationConverter` | Verify canonical conversion does not drop UTF-8-only tool results. | High |
| `AnthropicProviderUsesEffectiveTextForToolResults` | `AnthropicProvider` | Verify Anthropic serialization includes UTF-8-only tool result content. | High |
| `TextProcessorHeaderBudget` | `TextProcessor` | Verify `[FILE] ...` header counts toward the 50 KB cap. | Medium |
| `TextProcessorLargeLineNumberBudget` | `TextProcessor` | Verify expanded line-number prefixes are counted accurately. | Medium |
| `TextProcessorFirstLongLineBudget` | `TextProcessor` | Verify the first rendered line cannot exceed the byte cap accidentally. | Medium |
| `MoveDiffTruncationBehavior` | `WorkspaceTransaction` | Add only after deciding expected pure-move behavior. | Low/Decision needed |
| `HashlineShowsTruncationWhenLastLineOmitted` | `read_file_hashlines` | Verify truncation notice appears when a final line is omitted due to byte cap. | Low |

---

## Conclusion

The source check confirms that the report identified real concerns, but the severity is mixed:

* **Definitely fix:** UTF-8 tool-result propagation and `TextProcessor` byte accounting.
* **Handle carefully:** move-diff truncation, because current moves are pure renames.
* **Optional cleanup:** `read_file_hashlines`, which already counts the main rendered bytes correctly but has truncation edge cases.
