# UTF-8 Follow-up Build and Review

**Date:** 2026-05-19  
**Scope:** Validate the current UTF-8 migration changes after local .NET installation, confirm compile/test state, and review the current progress summary against actual source and build output.

## Validation Commands

Executed locally in `/root/development/Omicron`:

```bash
dotnet --info
dotnet build Omicron.slnx --nologo
dotnet test Omicron.slnx --nologo
```

Environment confirmed:

- .NET SDK `10.0.104`
- Runtime `10.0.4`
- Target environment is now available and functioning

## Build/Test Result

### Build

**Result:** failed

```text
Build FAILED.
1 Warning(s)
36 Error(s)
```

### Test

**Result:** not runnable to completion because the solution does not build.

`dotnet test Omicron.slnx --nologo` stops on the same compile errors.

---

## Executive Summary

The new work shows **real architectural progress**, but the current working tree is **not in a shippable state**.

Three classes of issues block the build:

1. **`using var` builders are being passed by `ref`** into `Utf8CompositeFormat.AppendFormatUtf8(...)`, which causes `CS1657`.
2. **Strict `AppendFormatUtf8(...)` is being used with `string` and other non-`IUtf8SpanFormattable` types**, which causes `CS0311` and likely hides additional follow-on type errors behind the current `CS1657` failures.
3. **`Utf8ValueStringBuilder.Append(...)` is being called with `ReadOnlySpan<byte>`**, which resolves to an invalid generic path and causes `CS9244`.

So the strategic direction remains good, but the current migration pass is **partially applied and uncompilable**.

---

## Confirmed Positives

These parts of the progress summary are genuinely correct from source review.

### 1. Provider API closure is real

Confirmed in `Omicron.Core/Providers/ApiShape.cs` and tests:

- `IApiShape.BuildRequestBody(...): JsonObject` is gone
- `IApiShape` now requires `WriteRequestBody(...)`
- `OpenAiResponsesShapeTests.cs` now validates via `WriteAndParse(...)`
- runtime request emission still goes through `Utf8JsonWriter`

This part of the work is structurally correct and is one of the strongest changes in the current batch.

### 2. LocalExecutionBroker moved in the right direction

Confirmed in `Omicron.Core/Execution/IExecutionBroker.cs`:

- switched from line-event string capture to direct `BaseStream` reads
- added `DrainStreamAsync(...)`
- uses `ArrayBufferWriter<byte>` for output capture
- overall design is materially more byte-oriented

### 3. Processor modernization is real in intent

Confirmed in:

- `Omicron.Core/IO/Formats/CsvProcessor.cs`
- `Omicron.Core/IO/Formats/NotebookProcessor.cs`
- `Omicron.Core/IO/Formats/TextProcessor.cs`

Specific improvements include:

- use of `Utf8CompositeFormat.AppendFormatUtf8(...)`
- removal of some string interpolation/prefix allocations
- `NotebookProcessor` now parses JSON directly from bytes via `JsonDocument.Parse(bytes)`

### 4. Plan 0020 adoption did materially expand

Confirmed in:

- `FormatSize.cs`
- `WorkspaceLlmTextRenderer.cs`
- `TranscriptStore.cs`
- `LegacyOfficeProcessor.cs`
- `EmailProcessor.cs`
- `NotebookProcessor.cs`
- `CsvProcessor.cs`
- `TextProcessor.cs`
- `IExecutionBroker.cs`

The migration of `AppendFormat(...)` call sites to `AppendFormatUtf8(...)` is real and substantial.

---

## Critical Findings

## F1. Build is broken by `using var` + `ref` builder usage

**Severity:** Critical  
**Impact:** solution does not compile

This is the dominant failure class.

### Cause

`Utf8CompositeFormat.AppendFormatUtf8(...)` takes the builder by `ref`:

```csharp
Utf8CompositeFormat.AppendFormatUtf8(ref builder, ...)
```

But many migrated call sites now use:

```csharp
using var builder = ZString.CreateUtf8StringBuilder();
```

A `using var` local cannot be passed by `ref`, so the compiler emits:

```text
CS1657: Cannot use 'builder' as a ref or out value because it is a 'using variable'
```

This is also explicitly consistent with the repository guidance in `AGENTS.md`, which already warns about this exact `using var` + `ref` pattern.

### Confirmed affected files

Build errors explicitly report this pattern in:

- `Omicron.Core/Execution/IExecutionBroker.cs`
- `Omicron.Core/IO/Formats/CsvProcessor.cs`
- `Omicron.Core/IO/Formats/NotebookProcessor.cs`
- `Omicron.Core/IO/Formats/LegacyOfficeProcessor.cs`
- `Omicron.Core/IO/Formats/EmailProcessor.cs`
- `Omicron.Core/Rendering/Transcript/TranscriptStore.cs`

### Representative examples

`Omicron.Core/Rendering/Transcript/TranscriptStore.cs`

```csharp
using var tempBuilder = ZString.CreateUtf8StringBuilder();
Utf8CompositeFormat.AppendFormatUtf8(ref tempBuilder, "[tool: {0}]"u8, toolName);
```

`Omicron.Core/IO/Formats/CsvProcessor.cs`

```csharp
using var output = ZString.CreateUtf8StringBuilder();
Utf8CompositeFormat.AppendFormatUtf8(ref output, ...);
```

### Review conclusion

This is not a minor cleanup item. It is a systematic migration bug and the immediate reason the branch does not build.

---

## F2. Strict `AppendFormatUtf8(...)` is being used with incompatible types

**Severity:** Critical  
**Impact:** current build errors plus likely additional hidden failures once F1 is fixed

`Utf8CompositeFormat.AppendFormatUtf8(...)` is the **strict** path. Its generic parameters require `IUtf8SpanFormattable`.

That means calls like these are invalid:

- `string`
- many framework object types
- types that are printable but do not implement `IUtf8SpanFormattable`

### Confirmed build failures

`Omicron.Core/IO/Formats/TextProcessor.cs` currently fails with:

```text
CS0311: The type 'string' cannot be used as type parameter 'T1' ...
```

These are direct compile failures, not speculative issues.

### Important follow-on concern

Several other migrated files are very likely to hit the same class of problem once the `using var`/`ref` issue is resolved, because they currently call the strict formatter with string-shaped arguments.

Representative examples from source:

#### `Omicron.Core/Rendering/Transcript/TranscriptStore.cs`

```csharp
Utf8CompositeFormat.AppendFormatUtf8(ref tempBuilder, "[tool: {0}]"u8, toolName);
```

`toolName` is `string`.

#### `Omicron.Core/IO/Formats/EmailProcessor.cs`

```csharp
Utf8CompositeFormat.AppendFormatUtf8(ref output, "[FILE] {0}  ({1}, email)"u8, context.RelativePath, FormatSize.Format(bytes.Length));
Utf8CompositeFormat.AppendFormatUtf8(ref output, "Subject: {0}"u8, mimeMessage.Subject);
Utf8CompositeFormat.AppendFormatUtf8(ref output, "{0}: {1}"u8, key, value);
```

These arguments are string-shaped and not obviously `IUtf8SpanFormattable`.

#### `Omicron.Core/IO/Formats/LegacyOfficeProcessor.cs`

```csharp
Utf8CompositeFormat.AppendFormatUtf8(ref output, "[FILE] {0}  ({1}, {2})"u8, context.RelativePath, FormatSize.Format(bytes.Length), docType);
Utf8CompositeFormat.AppendFormatUtf8(ref output, "[CONVERTED] {0} -> {1}"u8, conversion.Method, conversion.ConvertedExtension);
```

Again, these are string-shaped values.

### Review conclusion

The current migration appears to have treated `AppendFormatUtf8(...)` as a direct syntactic replacement for ZString `AppendFormat(...)`. That is not correct.

There are two APIs with different contracts:

- `AppendFormatUtf8(...)` = strict / typed / `IUtf8SpanFormattable`
- `AppendFormatUtf8Slow(...)` = permissive runtime path

This distinction was not consistently respected in the migration.

---

## F3. `Utf8ValueStringBuilder.Append(...)` is being used with `ReadOnlySpan<byte>`

**Severity:** Critical  
**Impact:** build failure

Several newly byte-oriented paths now do the right *conceptual* thing—append raw UTF-8 bytes—but use the wrong builder method.

### Confirmed build failures

Examples from build output:

- `Omicron.Core/Execution/IExecutionBroker.cs`
- `Omicron.Core/IO/Formats/CsvProcessor.cs`
- `Omicron.Core/IO/Formats/NotebookProcessor.cs`

Compiler error:

```text
CS9244: The type 'ReadOnlySpan<byte>' may not be a ref struct ... in Utf8ValueStringBuilder.Append<T>(T)
```

### Representative examples

`Omicron.Core/Execution/IExecutionBroker.cs`

```csharp
builder.Append(stdoutSpan.Slice(lineStart, lineLen));
builder.Append(stderrSpan.Slice(stderrLineStart, lineLen));
```

`Omicron.Core/IO/Formats/CsvProcessor.cs`

```csharp
output.Append(span.Slice(lineStart, lineLen));
```

`Omicron.Core/IO/Formats/NotebookProcessor.cs`

```csharp
output.Append(cellBuffer.WrittenSpan);
```

### Review conclusion

The intent is correct, but the code must use the byte-oriented append API (`AppendLiteral(...)` or equivalent explicit byte-span method), not the generic `Append(...)` path.

---

## F4. The current build break masks second-order type issues

**Severity:** High  
**Impact:** more errors are likely after the first compile pass is fixed

Because so many files currently fail early on `CS1657`, the compiler does not yet surface every downstream type mismatch.

In particular, files like these are very likely to reveal more formatter-constraint errors once `using var` is replaced with ref-safe locals:

- `Omicron.Core/Rendering/Transcript/TranscriptStore.cs`
- `Omicron.Core/IO/Formats/EmailProcessor.cs`
- `Omicron.Core/IO/Formats/LegacyOfficeProcessor.cs`

So the current `36` errors should not be interpreted as the complete issue set. They are the first visible layer.

---

## F5. NotebookProcessor improvement is real, but the progress wording overstates it

**Severity:** Medium  
**Impact:** status-report accuracy

The progress summary said:

> String decode eliminated

That is too strong.

### What is true

The expensive whole-buffer decode is gone:

```csharp
JsonDocument.Parse(bytes)
```

This is a real improvement.

### What is not true

The processor still materializes strings in multiple places:

- `ctProp.GetString()`
- `dn.GetString()`
- `sourceLine.GetString()`
- `output.Append(cellType)`

So the accurate wording is:

> full-document string decode eliminated

not

> all string decode eliminated

---

## F6. CsvProcessor is improved, but still only partially byte-native

**Severity:** Medium  
**Impact:** status-report accuracy and future optimization expectations

The progress summary described byte-oriented improvements, which is fair.

But it is important not to overstate the rewrite.

### The robust CsvHelper path still does this

- `MemoryStream(bytes.ToArray())`
- `StreamReader`
- `csv.GetField(i)` returning strings

So this remains a hybrid approach:

- improved prefixes/budget/output composition
- still string-shaped parsing in the CsvHelper path

That is fine, but it is not a fully byte-native CSV pipeline.

---

## F7. Plan 0020 adoption is real, but the migration is not compile-clean yet

**Severity:** Medium  
**Impact:** status reporting

The adoption effort is genuine. However, the current state is not yet suitable to report as “done” or “solidified” without qualification, because:

- the branch does not build
- the strict/slow formatter distinction was applied inconsistently
- builder lifetime rules were violated in multiple files

The right characterization is:

> substantial migration progress, not yet validated or compile-clean

---

## F8. One warning remains in NotebookProcessor

**Severity:** Low  
**Impact:** code quality

The build produced one warning:

```text
CS8604: Possible null reference argument for parameter 'value' in 'void Utf8ValueStringBuilder.Append(string value)'.
```

Location:

- `Omicron.Core/IO/Formats/NotebookProcessor.cs`

This is not a build blocker, but it is still a correctness/clarity issue that should be resolved before considering the work complete.

---

## Non-Blocking Confirmations from the Earlier Review

The earlier “still pending” items remain accurate from source review.

### Confirmed still pending

- `ShellTools` remains present and still uses string-heavy patterns
- CLI display helpers in `Omicron.CLI/Program.cs` remain string-based
- `TranscriptViewportWidget.cs` still contains:

```csharp
_store.AppendUserMessage(Encoding.UTF8.GetBytes($"  You: {ue.Text}"));
```

- several remaining processors still use ZString `AppendFormat(...)`, including:
  - `ArchiveProcessor.cs`
  - `AudioVideoProcessor.cs`
  - `HexDumpProcessor.cs`
  - `OpenXmlProcessor.cs`
  - `SvgProcessor.cs`
  - `EbookProcessor.cs`

These are lower priority than the current compile failures.

---

## Corrected Assessment of the Current Progress Summary

## What can be accepted as correct

- Provider API closure: **confirmed**
- LocalExecutionBroker direction: **confirmed**
- CsvProcessor / NotebookProcessor / TextProcessor improvement direction: **confirmed**
- Plan 0020 adoption expansion: **confirmed**

## What needs correction

### 1. The work is not currently compile-validated

The code does **not** compile today.

### 2. “60+ call sites” should not be used without recounting

The earlier source review suggested the real number for the specifically listed migrated files is closer to **~40+** than `60+`.

### 3. “String decode eliminated” for NotebookProcessor is too broad

Should be narrowed to:

- **whole-buffer string decode eliminated**

### 4. “Done” should not be used for the migration batch yet

Because the migration introduced a large compile break across multiple files.

---

## Overall Conclusion

The architecture work is moving in the right direction, but the current working tree is **mid-migration, not complete**.

### Strongest completed item

- **Provider API closure** is the cleanest confirmed success in this batch.

### Main blockers

- ref-unsafe `using var` builder usage
- incorrect use of the strict formatter with string-shaped values
- incorrect byte-span append method calls

### Final judgment

> This is a promising but currently broken migration pass. The design direction is good, the intended optimizations are real, and several source-level improvements are valuable, but the branch must be made compile-clean before the progress can be reported as complete or fully validated.
