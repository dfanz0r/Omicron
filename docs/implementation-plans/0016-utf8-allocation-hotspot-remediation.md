# Implementation Plan 0016: UTF-8 Allocation Hotspot Remediation

**Date:** 2026-05-17
**Status:** Proposed
**Target:** Remove remaining high-value text allocation hotspots after the initial `Utf8ContentBuffer` / `Utf8ValueStringBuilder` content-block work.
**Depends on:** Plan 0014 (OS-specific terminal backends), Plan 0015 (CLI orchestration extraction), UTF-8 content-block optimization review 0100
**Estimated Effort:** 5-8 days staged; each phase independently mergeable

---

## Purpose

The first UTF-8 optimization pass made structured content blocks UTF-8-native enough to avoid reflection serialization failures and to render/persist tool output without immediately flattening every block to strings. However, several larger hot paths still allocate full strings, string arrays, and per-frame byte arrays.

This plan tracks the next remediation pass. The goal is to reduce avoidable allocations without destabilizing provider behavior, event persistence, or TUI rendering.

---

## Confirmation of Findings

Most reported findings are valid. Two have important qualifications:

| ID | Status | Notes |
|----|--------|-------|
| F1 LLM response accumulation | Confirmed | `AgentSession`, `SessionProjector`, and `IChatProvider.CompleteAsync` still accumulate with `StringBuilder` and materialize final strings. Fully preserving UTF-8 through provider JSON requires contract changes because `Message.Text`, `LlmResult.Text`, and assistant events are string-based today. |
| F2 Diff rendering/splitting | Confirmed | Diff paths still decode/split whole text and allocate line strings. |
| F3 Tool result splitting | Confirmed | `AgentSession.TryExtractBase64Content` still uses `Replace(...).Split('\n')`. |
| F4 CSV processor | Confirmed | `CsvProcessor` decodes whole files and splits into string arrays. |
| F5 Workspace transaction line count | Confirmed | `WorkspaceTransaction` decodes content and uses `Replace(...).Split('\n').Length` only to count lines. |
| F6 Notebook/SVG processors | Confirmed | `NotebookProcessor` necessarily parses JSON from text today; `SvgProcessor` still decodes/splits whole text. Notebook can be improved with UTF-8 JSON reader, but is a larger parser rewrite. |
| F7 LocalExecutionBroker | Partially confirmed | Output accumulation is still string-based. Stdout truncation was improved, but stderr and command escaping still allocate unnecessarily. |
| F8 ShellTools | Confirmed, but likely legacy | `ShellTools.cs` remains allocation-heavy. First decide whether it is still used; if dead, remove instead of optimizing. |
| F9 TUI widgets | Confirmed | Widgets allocate byte arrays per frame via `Encoding.UTF8.GetBytes(...)`; `InputEditorWidget` is the most important. |
| F10 DisplayHelpers truncation | Confirmed, low priority | Still allocates substring plus concatenation. |
| F11 WorkspaceReadService double scan | Confirmed | Current `ParseLines` scans once, clears, and scans again. |
| F12 Content block equality | Already fixed | `CodeContentBlock`, `DiffContentBlock`, `FilePreviewContentBlock`, and `ErrorContentBlock` now include metadata in equality/hash code. Keep regression tests. |
| F13 converter registration | Mostly fixed; verify | `JsonlSessionStore` registers `ContentBlockJsonConverter` for write and read options. `PersistentEventSink` delegates through `ISessionStore`. Add a centralized options helper/regression tests to avoid future drift. |

---

## Goals

1. Remove whole-file `Replace(...).Split(...)` patterns from common file and tool paths.
2. Reduce final-response accumulation allocations in session/provider paths.
3. Add UTF-8 append APIs where string-returning APIs must remain for compatibility.
4. Avoid per-frame byte-array allocations in hot TUI widgets.
5. Preserve existing public behavior and event JSON compatibility.
6. Keep implementation staged and testable.

---

## Non-Goals

1. Do not rewrite all provider shapes in one pass.
2. Do not remove string properties such as `Message.Text`, `AssistantResponseCompleteEvent.FullText`, or `LlmResult.Text` yet; add UTF-8 paths alongside them first.
3. Do not introduce unsafe lifetime exposure of `Utf8ValueStringBuilder`.
4. Do not centralize content blocks into DTO switches beyond the existing converter dispatch.
5. Do not optimize cold CLI display paths before the model/tool/TUI hot paths.

---

## Design Principles

- Prefer `Utf8ValueStringBuilder` or `Utf8ContentBuffer` for owned, growing UTF-8 text.
- Use callback-based mutation APIs rather than public long-lived `ref` builder exposure.
- Keep JSON persistence UTF-8-native with `Utf8JsonWriter` / `Utf8JsonReader` where practical.
- Retain string compatibility at API boundaries until provider/message/event contracts can be migrated safely.
- For existing string inputs, avoid array-producing `Split`; scan spans or use `StringReader`-style iteration.
- For byte inputs, decode only the slices that are returned or parsed semantically.

---

## Phase 0: Baseline and Guardrails

### Tasks

1. Add allocation-focused microbenchmarks or lightweight regression measurements for:
   - `AgentSession` streaming a large assistant response.
   - `WorkspaceReadService.ParseLines` on a large file with offset/limit.
   - `CsvProcessor.ProcessAsync` on a medium CSV.
   - `InputEditorWidget.Render` for a multiline input buffer.
2. Add tests that lock in current output behavior before optimization.
3. Add analyzer/check script entries for recurring patterns:
   - `Replace("\r\n", "\n").Split('\n')`
   - `Encoding.UTF8.GetString(...).Split(...)`
   - per-frame `Encoding.UTF8.GetBytes(...)` in TUI widgets.

### Acceptance Criteria

- Baseline measurements exist, even if they are simple BenchmarkDotNet/manual benchmarks.
- Current tests still pass before functional rewrites begin.

---

## Phase 1: Low-Risk Correctness and Allocation Fixes

### 1.1 Fix `WorkspaceReadService.ParseLines` double scan

Current code records line starts, clears the list, then scans again. Replace with a single pass:

```csharp
var lineStarts = new List<int> { 0 };
for (var i = 0; i < span.Length; i++)
{
    if (span[i] == (byte)'\n' && i + 1 < span.Length)
        lineStarts.Add(i + 1);
}
var totalLines = lineStarts.Count;
```

Be careful with files ending in `\n`: do not invent an extra empty line unless existing behavior requires it.

### 1.2 Replace simple line-count splits

In `WorkspaceTransaction`, replace:

```csharp
lineCount = text.Replace("\r\n", "\n").Split('\n').Length;
```

with span counting:

```csharp
lineCount = text.Length == 0 ? 0 : text.AsSpan().Count('\n') + 1;
```

If CR-only files matter, add a helper that treats `\r`, `\n`, and `\r\n` consistently without allocating.

### 1.3 Replace `AgentSession.TryExtractBase64Content` split

Scan `resultText.AsSpan()` line-by-line. Avoid allocating lines, `Substring`, and normalized copies. Only allocate final MIME/base64 strings when a match is found.

### 1.4 Finish `LocalExecutionBroker` split cleanup

- Replace stderr `Replace(...).Split('\n')` with span/StringReader line iteration.
- Replace command escaping double `Replace` with a single-pass helper using `ValueStringBuilder` or `StringBuilder` sized to input length.

### Acceptance Criteria

- Unit tests cover LF, CRLF, final-line-without-newline, and empty input cases.
- No behavior changes in workspace read/write, tool-result image bridge, or execution output formatting.

---

## Phase 2: Content Block and Event Serialization Hardening

### 2.1 Keep equality fixed and tested

Add/keep explicit equality tests proving metadata participates in equality:

- `CodeContentBlock`: text + `Language` + `Path`
- `DiffContentBlock`: text + `Path`
- `FilePreviewContentBlock`: text + `Path` + `Size` + `LineCount` + `IsBinary`
- `ErrorContentBlock`: text + details

### 2.2 Centralize event JSON options

Create one shared helper, for example:

```csharp
internal static class OmicronEventJson
{
    public static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new ContentBlockJsonConverter() }
    };
}
```

Use it in:

- `JsonlSessionStore` instance options.
- `JsonlSessionStore` static event read options.
- Any future fork/resume/export/import serializer.

### 2.3 Add serialization-path regression tests

Tests should cover:

- JSONL append/read round-trip for `ToolInvocationCompletedEvent.Blocks`.
- Session resume/project after reading persisted block events.
- Fork/replay path if it serializes/deserializes events in the future.

### Acceptance Criteria

- No serializer path can accidentally reflect over `Utf8ContentBuffer.Builder`.
- Round-trip tests fail if `ContentBlockJsonConverter` is removed.

---

## Phase 3: LLM Response Accumulation

This is the highest-impact area, but should be done in two stages to avoid a large provider/event contract break.

### 3.1 Stage A: Replace accumulators while preserving string boundaries

Introduce an internal accumulator helper:

```csharp
internal sealed class Utf8TextAccumulator : IDisposable
{
    private Utf8ContentBuffer _buffer = new();

    public int Length => _buffer.Length;
    public void Append(string? text) { /* append UTF-8 into buffer */ }
    public void AppendUtf8(ReadOnlySpan<byte> utf8) { /* append raw bytes */ }
    public string ToStringAndKeep() => _buffer.ToString();
    public Utf8ContentBuffer ToOwnedBuffer() { /* transfer ownership */ }
    public void Clear() { /* dispose/reset builder */ }
}
```

Use it in:

- `AgentSession.RunLoopAsync` for `responseText` and `reasoningText`.
- `SessionProjector` for replay accumulation.
- `IChatProvider.CompleteAsync` for non-streaming convenience.

Stage A still creates final strings because current records require strings, but it removes `StringBuilder` and establishes a UTF-8-native accumulator API.

### 3.2 Stage B: Add byte-backed message/event surfaces

Add compatible byte-backed paths without removing strings immediately:

- `Message` gains optional `Utf8ContentBuffer? TextBuffer` / `ReasoningBuffer`, or a new `MessageContent` abstraction.
- Assistant completion events gain optional content blocks or UTF-8 buffers while keeping `FullText` for compatibility.
- Provider shapes get write methods that can write `Message` text from UTF-8 spans directly to `Utf8JsonWriter`.

### 3.3 Stage C: Provider parse/write migration

- Where `JsonElement.GetString()` is only used to immediately append response deltas, use `Utf8JsonReader`/`ValueSpan` or `JsonElement.ValueKind` alternatives where possible.
- Add `StreamEvent` fields for UTF-8 deltas or `Utf8ContentBuffer` ownership if parser lifetimes are safe.
- Provider request serialization writes message text from `ReadOnlySpan<byte>` into `Utf8JsonWriter`.

### Acceptance Criteria

- Streaming behavior and persisted event JSON remain compatible.
- Large assistant response benchmark shows reduced allocation versus `StringBuilder` baseline.
- No public long-lived `ref Utf8ValueStringBuilder` exposure is introduced.

---

## Phase 4: Diff Pipeline UTF-8 Rewrite

### 4.1 Add line-span model

Introduce a line representation that avoids per-line strings for byte inputs:

```csharp
internal readonly record struct Utf8Line(ReadOnlyMemory<byte> Source, int Start, int Length);
```

Or, if the diff engine needs stable ownership, use an owning `Utf8TextBuffer` plus line ranges.

### 4.2 Replace `TextLineSplitter`

- `SplitLines(string)` should avoid `Replace` and array allocation for internal callers.
- `SplitLines(ReadOnlySpan<byte>)` should scan bytes and return ranges, not decode the whole file.
- Preserve string-returning overloads only for compatibility/tests.

### 4.3 Add `UnifiedDiffRenderer.AppendUtf8To`

Keep existing `Render()` but implement it on top of an append API:

```csharp
public static void AppendUtf8To(ref Utf8ValueStringBuilder builder, DiffResult diff)
```

Callers that need strings can still call `Render()`, but workspace/tool paths can append directly into content blocks.

### 4.4 Reduce normalization allocations in `TextDiffEngine`

Replace repeated `Trim()`, `ToUpperInvariant()`, and `StringBuilder.ToString()` keys with:

- Comparers that understand ignore-case/trim-whitespace.
- Hashing over spans where possible.
- Interned or pooled normalized keys only when required by the algorithm.

### Acceptance Criteria

- Existing diff tests pass unchanged.
- New tests cover CRLF/LF, whitespace-ignore, case-ignore, and large-file truncation.
- Byte overload no longer decodes the full input just to split lines.

---

## Phase 5: File Processor Cleanup

### 5.1 CSV processor

Rewrite CSV preview as a byte/span scanner:

- Decode only cell/row slices included in the output preview.
- Track byte budget incrementally instead of repeatedly calling `Encoding.UTF8.GetByteCount(sb.ToString() + rowText + "\n")`.
- Avoid `text.Split('\n')` and `line.Split(delimiter)`.

### 5.2 SVG processor

- Decode only the first `MaxSvgBytes` slice.
- Scan line spans and write preview lines directly to an accumulator.
- Avoid whole-text normalization and split arrays.

### 5.3 Notebook processor

This is a larger rewrite because JSON semantics matter.

- Replace full `Encoding.UTF8.GetString(bytes.Span)` + `JsonDocument.Parse(string)` with `JsonDocument.Parse(ReadOnlyMemory<byte>)` or `Utf8JsonReader`.
- Build output with `Utf8ValueStringBuilder`.
- Decode cell text slices only when needed for final preview output.

### Acceptance Criteria

- Processor output is byte-for-byte equivalent where tests assert exact text.
- Common CSV/SVG files avoid full-file string split allocations.

---

## Phase 6: Execution Broker and Shell Tool Consolidation

### 6.1 Decide ShellTools fate

Run reference search for `ShellTools` registrations/usages.

- If unused, delete it or mark obsolete and remove from extension registration.
- If used, port the `LocalExecutionBroker` improvements into it.

### 6.2 Output accumulation

For process output:

- Keep process stream decoding correct; `Process.OutputDataReceived` provides strings, so some string allocation is unavoidable unless moving to raw stream reads.
- Prefer raw async reads from `StandardOutput.BaseStream` / `StandardError.BaseStream` into byte buffers for high-output commands.
- Accumulate into `Utf8ContentBuffer` and decode only selected preview/truncation output.

### Acceptance Criteria

- Execution result formatting remains stable.
- Large stdout/stderr command benchmarks allocate less and respect byte/line truncation limits.

---

## Phase 7: TUI Per-Frame Allocation Reduction

### 7.1 Add render text helpers

Add helpers around `RenderContext.DrawText` so widgets can pass:

- UTF-8 literals.
- Cached byte arrays for stable labels.
- A reusable `Utf8ValueStringBuilder` for dynamic labels.

### 7.2 InputEditorWidget

Highest priority TUI widget.

- Avoid `StringBuilder.ToString()` for every visible line if possible.
- Cache encoded line bytes and invalidate on edit.
- Avoid slicing strings for cursor rendering; render spans around the cursor or cache per-line UTF-8.
- Reuse builder/byte buffers for completion menu labels.

### 7.3 Status/model picker widgets

- Cache status bar segments until model/provider/right text changes.
- Cache model picker row bytes for filtered list entries.
- Recompute only when filter, selection, scroll offset, or model list changes.

### Acceptance Criteria

- Frame rendering creates no per-cell/per-row transient byte arrays for unchanged text.
- Input editing remains correct with Unicode and wide characters.
- Existing TUI tests pass; add smoke test for cached render invalidation.

---

## Phase 8: CLI Display Helpers

Low-priority cleanup after hot paths.

- Replace `DisplayHelpers.DisplayTruncated` `Replace(...).Split('\n')` with line scanning.
- Replace `Truncate(this string...)` with a write-to-output helper that avoids substring allocation when writing to console.
- Keep string-returning `Truncate` only where API compatibility requires it.

---

## Testing Plan

1. Existing full test suite:
   - `dotnet test Omicron.Core.Tests/Omicron.Core.Tests.csproj -c Debug --no-restore`
2. Build matrix:
   - `dotnet build Omicron.CLI -c Debug --no-restore`
   - `dotnet build Omicron.CLI -c Release --no-restore`
3. Targeted regression tests:
   - Content-block equality and JSON round-trip.
   - Workspace line parsing with LF/CRLF/final no-newline/empty file.
   - CSV/SVG/Notebook preview equivalence.
   - Diff equivalence and truncation.
   - TUI render cache invalidation.
4. Manual validation:
   - Stream a large LLM response.
   - Run a tool returning large output.
   - Open TUI and type/paste multiline Unicode text.

---

## Implementation Order

1. Phase 1 quick wins: `WorkspaceReadService`, `WorkspaceTransaction`, `TryExtractBase64Content`, broker stderr split.
2. Phase 2 serializer/equality guard tests.
3. Phase 5 CSV/SVG processors.
4. Phase 3 Stage A accumulators.
5. Phase 4 diff append APIs and byte line model.
6. Phase 7 TUI caching.
7. Phase 3 Stages B/C provider/message contract migration.
8. Phase 6 ShellTools decision and raw stream execution broker.
9. Phase 8 CLI display helpers.

---

## Risks

- UTF-8 builder ownership bugs can cause use-after-dispose or pooled-buffer corruption. Keep callback mutation and fail-fast disposed checks.
- Provider/event contract migration can break persistence compatibility. Add new fields first; remove old string fields only in a later migration.
- TUI caching can create stale rendering if invalidation is incomplete. Keep cache scope small and add targeted tests.
- Diff algorithm changes can alter output subtly. Preserve existing string API and compare outputs before/after.

---

## Definition of Done

- All confirmed high/medium allocation hotspots have either been remediated or documented as requiring a larger contract migration.
- No `Replace("\r\n", "\n").Split('\n')` remains in hot paths.
- Provider/session response accumulation uses UTF-8-native accumulators at least internally.
- Common file processors avoid full-file decode/split when producing previews.
- TUI widgets avoid avoidable per-frame `Encoding.UTF8.GetBytes(...)` allocations for unchanged text.
- Debug and Release builds pass, and the full core test suite passes.
