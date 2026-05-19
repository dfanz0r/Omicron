# Implementation Plan 0020: UTF-8 Composite Formatting Pipeline

**Date:** 2026-05-17  
**Status:** Design Proposal  
**Target:** Add an Omicron-owned UTF-8-native composite formatting pipeline for allocation-conscious display and transport text composition.  
**Depends on:** Plan 0019 (UTF-8 pipeline completion)

---

## Purpose

Omicron now has many call sites where the desired operation is conceptually simple:

- prefix + value
- value + suffix
- wrapper text + value + wrapper text
- small formatted messages with 1–4 arguments

Those sites are increasingly UTF-8-native, but the current ergonomics are poor:

```csharp
using (var builder = ZString.CreateUtf8StringBuilder())
{
    builder.AppendLiteral("  Agent: "u8);
    if (msg.HasText)
        builder.AppendLiteral(msg.TextUtf8);
    _store.AppendAssistantDelta(builder.AsSpan());
}
```

This is mechanically correct, but it is low-level boilerplate for code that is logically just “format a small UTF-8 message”.

`ZString.Format(...)` does not solve this problem because it returns `string`, and ZString’s UTF-8 formatting support still takes UTF-16 format templates (`string format`).

Omicron therefore needs its own UTF-8-native composite formatter that can accept:

```csharp
TryFormatUtf8("Test {0} 123 {1}"u8, destination, out written, 321, 456);
```

without forcing the template through UTF-16 or producing an intermediate `string`.

---

## Goals

1. Accept UTF-8 format templates as `ReadOnlySpan<byte>`.
2. Format directly into caller-provided `Span<byte>` or `IBufferWriter<byte>`.
3. Avoid `string` materialization for literal template text and for values that already support UTF-8 formatting.
4. Prefer compile-time rejection for unsupported argument types in the primary API.
5. Provide good ergonomics for common Omicron display/composition cases.
6. Reuse proven ideas from ZString where they fit, but keep the Omicron implementation byte-native end-to-end.
7. Keep allocation boundaries explicit.
8. Support prepared/cached templates for repeated formatting sites.

---

## Non-goals

1. Replace every existing `Utf8ValueStringBuilder` use immediately.
2. Build a culture-rich clone of all `string.Format` behavior in the first pass.
3. Support arbitrary object arrays or boxing-heavy varargs APIs.
4. Solve terminal cell-width measurement or grapheme-aware alignment in this plan.
5. Lock Omicron into one final long-term formatting abstraction before broader native-memory work is complete.

---

## Why Omicron needs its own formatter

The relevant gap in the BCL is not low-level UTF-8 formatting primitives. Those already exist.

The missing piece is a top-level UTF-8 composite formatter that can:

```text
parse UTF-8 template
→ copy UTF-8 literal segments
→ resolve placeholder index
→ format arguments directly to UTF-8
→ write into Span<byte> or IBufferWriter<byte>
```

without first converting the template into a UTF-16 `string`.

The BCL pieces Omicron can build on are already strong:

- `ReadOnlySpan<byte>` / `Span<byte>`
- `IBufferWriter<byte>`
- `Utf8Formatter.TryFormat(...)`
- `IUtf8SpanFormattable.TryFormat(...)`
- `Utf8.FromUtf16(...)` for fallback transcoding
- UTF-8 string literals (`"..."u8`)

The missing part is the composite formatter itself.

---

## Prior-art review from `READ_ONLY/ZString`

Omicron should borrow the good structural ideas from ZString, but not inherit its UTF-16 template assumptions.

### Relevant ZString lessons

From `READ_ONLY/ZString/src/ZString`:

1. **Tight format scanning/parser loops are worthwhile**
   - `FormatParser` uses simple brace scanning and a compact parse result.
   - A similar parser should exist for UTF-8 bytes, not UTF-16 chars.

2. **Prepared segment tables are worthwhile for repeated templates**
   - `PreparedFormatHelper` parses a format once into literal segments + placeholder metadata.
   - Omicron should do the same for repeated transcript / display templates.

3. **Typed overloads avoid boxing and `object[]` churn**
   - ZString emits overloads up to 16 arguments.
   - Omicron should use typed overloads rather than `params object?[]`.

4. **Per-type formatter delegate caching is worthwhile**
   - `Utf8ValueStringBuilder.FormatterCache<T>` caches formatting delegates.
   - Omicron should adopt the same general strategy for steady-state formatting.

5. **Writer-agnostic formatting is useful**
   - ZString has `IBufferWriter<byte>`-based formatting helpers.
   - Omicron should support both fixed-buffer and growable-writer destinations.

### Things not to copy blindly

1. **String template API**
   - ZString’s UTF-8 formatter still takes `string format`.
   - Omicron’s formatter must keep the template in UTF-8 from the start.

2. **Implicit UTF-16 round-tripping of literal segments**
   - Omicron should not re-encode literal template slices from UTF-16.

3. **Unexamined width/alignment semantics**
   - ZString’s UTF-8 width handling is practical, but not a definitive Unicode-display-width model.
   - Omicron should explicitly document what width means in each phase.

---

## Target API shape

The API should have two layers: a **strict primary API** with compile-time constraints, and an explicitly named **slow/convenience API** for narrower compatibility cases.

### 1. Strict one-shot fixed-buffer formatting

```csharp
public static class Utf8CompositeFormat
{
    public static bool TryFormat<T1>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        out int written,
        T1 arg1)
        where T1 : IUtf8SpanFormattable;

    public static bool TryFormat<T1, T2>(
        ReadOnlySpan<byte> format,
        Span<byte> destination,
        out int written,
        T1 arg1,
        T2 arg2)
        where T1 : IUtf8SpanFormattable
        where T2 : IUtf8SpanFormattable;

    // ... continue through a practical typed arity set
}
```

This is the canonical API.
Its purpose is to make unsupported types fail at compile time rather than relying on runtime type switches.

### 2. Explicit slow/convenience fixed-buffer formatting

```csharp
public static bool TryFormatSlow<T1, T2>(
    ReadOnlySpan<byte> format,
    Span<byte> destination,
    out int written,
    T1 arg1,
    T2 arg2);
```

`TryFormatSlow(...)` is optional and intentionally second-class:

- runtime-dispatched
- may use adapters/fallbacks
- intended for convenience/test code or transitional sites
- must not become the default path for core UTF-8-native formatting

### 3. One-shot writer-based formatting

```csharp
public static void Format<TBufferWriter, T1, T2>(
    ref TBufferWriter writer,
    ReadOnlySpan<byte> format,
    T1 arg1,
    T2 arg2)
    where TBufferWriter : IBufferWriter<byte>
    where T1 : IUtf8SpanFormattable
    where T2 : IUtf8SpanFormattable;
```

### 4. Builder-targeted helpers

```csharp
public static void AppendFormatUtf8<T1, T2>(
    ref Utf8ValueStringBuilder builder,
    ReadOnlySpan<byte> format,
    T1 arg1,
    T2 arg2)
    where T1 : IUtf8SpanFormattable
    where T2 : IUtf8SpanFormattable;
```

This is important because many Omicron call sites already use `Utf8ValueStringBuilder`.

### 5. Prepared format templates

```csharp
public sealed class PreparedUtf8CompositeFormat<T1, T2>
    where T1 : IUtf8SpanFormattable
    where T2 : IUtf8SpanFormattable
{
    public bool TryFormat(Span<byte> destination, out int written, T1 arg1, T2 arg2);
    public void Format<TBufferWriter>(ref TBufferWriter writer, T1 arg1, T2 arg2)
        where TBufferWriter : IBufferWriter<byte>;
}

public static PreparedUtf8CompositeFormat<T1, T2> Prepare<T1, T2>(ReadOnlySpan<byte> format)
    where T1 : IUtf8SpanFormattable
    where T2 : IUtf8SpanFormattable;
```

This follows the useful part of ZString’s prepared-format approach, but with UTF-8 template storage.

---

## Recommended arity strategy

### Initial implementation

Implement typed overloads for **1–4 arguments** first.

That covers the overwhelming majority of Omicron’s current motivating cases:

- `"  You: {0}"u8`
- `"  Agent: {0}"u8`
- `"\n[Error: {0}]"u8`
- `"[tool: {0}]"u8`
- small status or label formatting

### Expansion path

If adoption spreads beyond these cases, extend to **8** and then **16** arguments.

### Why not `params object?[]`

That would reintroduce:

- boxing
- array allocation
- type-erased dispatch
- slower hot structural paths

and would defeat much of the point of this work.

---

## Format grammar

The first grammar should stay intentionally close to .NET composite formatting:

```text
{index[,alignment][:format]}
```

Supported pieces:

- `{{` and `}}` escapes
- decimal argument index
- optional signed alignment
- optional format specifier

### Recommended limits

Borrow ZString’s practical constraints initially:

- placeholder index range: `0..15`
- alignment width upper bound: modest fixed limit such as `1000`

These should be explicit constants in the parser, not hidden magic behavior.

---

## Parser design

Add a byte-native parser rather than reusing ZString’s `string` parser.

### Proposed parser types

```csharp
internal static class Utf8CompositeFormatParser
{
    public static ParseResult ParsePlaceholder(ReadOnlySpan<byte> format, int openBraceIndex);
    public static ScanResult Scan(ReadOnlySpan<byte> format, ref int index);
}

internal readonly ref struct Utf8PlaceholderParseResult
{
    public int Index { get; }
    public int Alignment { get; }
    public ReadOnlySpan<byte> FormatUtf8 { get; }
    public int LastIndex { get; }
}
```

### Parsing rules

1. Scan byte-by-byte for `{` and `}`.
2. Treat `{{` and `}}` as escaped braces.
3. For a real `{`, parse:
   - decimal placeholder index
   - optional whitespace
   - optional alignment
   - optional `:format`
   - mandatory closing `}`
4. Reject malformed placeholders with `FormatException`.

### Important parser requirement

Keep literal slices as byte ranges into the original UTF-8 template as long as possible.
Do not transcode the whole template.

---

## Formatting pipeline design

The formatter should have a small number of explicit layers.

### Layer 1: template walk

Walk the UTF-8 template and emit either:

- literal byte slices
- formatted argument placeholders

### Layer 2: destination abstraction

Provide two write targets:

1. `Span<byte>` fixed-buffer path
2. `IBufferWriter<byte>` growable path

These should share the same formatting semantics, not separate logic trees.

### Layer 3: argument formatting dispatch

Resolve argument formatting with a cached type-specific delegate.

### Layer 4: optional prepared format reuse

Allow repeated templates to skip reparsing.

---

## Destination model

### Fixed-buffer path

For:

```csharp
TryFormat(..., Span<byte> destination, out int written, ...)
```

Behavior should be:

- return `false` when the destination is too small
- set `written = 0` or bytes-written-so-far according to a documented rule
- throw on malformed format templates

Recommended rule: on failure due to insufficient space, return `false` and set `written = 0`.
That is easier to reason about than partial-success semantics.

### Writer-based path

For:

```csharp
Format(ref writer, ...)
```

Behavior should be:

- never allocate except through the supplied writer’s own growth behavior
- throw on malformed format templates
- throw if an argument type cannot be formatted by the supported pipeline

### Builder path

`Utf8ValueStringBuilder` should be treated as just another byte writer target.
Where practical, the builder helper should reuse the same core formatting logic rather than open-coding its own parser.

---

## Argument formatting strategy

This is the most important design choice after the parser.

### Primary API strategy

The primary API should not rely on a large runtime `typeof(T)` switch to define the supported type universe.
Instead, it should make support visible in the API contract itself.

The preferred direction is:

1. constrain the canonical formatter surface to `IUtf8SpanFormattable`
2. make Omicron-native types such as `Utf8String` participate in that contract
3. require explicit adapters for types like `string` that are not natively UTF-8-formattable

That gives Omicron the desired behavior:

- unsupported types fail at compile time
- UTF-8 formatting capability is explicit
- hidden widening to arbitrary `ToString()`-shaped types is avoided

### Strict core dispatch order

Within the constrained/core implementation, use this order:

1. **Direct constrained call path**
   - `IUtf8SpanFormattable.TryFormat(...)`

2. **Exact Omicron/BCL fast paths only where they materially improve codegen or performance**
   - e.g. `Utf8String`
   - known primitive specializations if needed

3. **No string fallback in the strict API**

### Slow-path strategy

If Omicron keeps `TryFormatSlow(...)`, it may use a broader runtime-dispatch strategy for convenience, such as:

1. exact Omicron fast paths
2. `IUtf8SpanFormattable`
3. `ISpanFormattable` + UTF-16 transcoding
4. explicit final `ToString()` fallback, only if consciously allowed there

The key rule is that this broader strategy must stay out of the canonical primary API.

### Important implication

`string` should not be treated as an invisible first-class input to the strict API.
If a caller wants to pass UTF-16 text to the strict formatter, they should do so explicitly via an adapter or conversion such as:

```csharp
Utf8String.FromString(name)
```

or a dedicated Omicron wrapper if one is later introduced.

### Numeric fast-path note

Inspection of `READ_ONLY/ZString/src/ZString/FastNumberWriter.cs` confirms that specialized primitive formatting can outperform the general formatting path for common no-format numeric cases.

However, that implementation is `Span<char>`-based, not UTF-8-byte-based.

So Omicron should not copy it directly. Instead:

- **initial implementation** should use `Utf8Formatter` for primitive correctness and simpler delivery
- **later optimization work**, if benchmarks justify it, can introduce an Omicron-owned byte-native numeric writer such as `FastUtf8NumberWriter` or `AsciiNumberWriter`
- that specialized path should write ASCII digits directly into `Span<byte>`
- initial specialization scope should be limited to the most common cases:
  - `int`
  - `uint`
  - `long`
  - `ulong`
  - possibly `nint` / `nuint`
- the first candidate formats should be:
  - default decimal
  - optionally simple hex (`X` / `x`) if benchmarking justifies it

This keeps the first formatter implementation structurally correct and simpler, while preserving a clear follow-on optimization path for the cases where numeric formatting is proven to matter.

### Recommendation

Use a strict default for the core engine:

- no reflection
- no `object.ToString()` fallback unless explicitly opted into

Then expose a convenience layer if Omicron later decides that looser behavior is worth it for some display-only callers.

---

## Formatter cache design

Omicron should adopt the same general pattern as ZString’s `Utf8ValueStringBuilder.FormatterCache<T>`.

### Proposed shape

```csharp
internal delegate bool TryWriteUtf8<T>(
    T value,
    Span<byte> destination,
    out int written,
    Utf8FormatSpecifier specifier,
    IFormatProvider? provider);

internal static class Utf8FormatterCache<T>
{
    public static readonly TryWriteUtf8<T> Writer = Create();
}
```

### Why cache delegates

It avoids repeated per-call type inspection and allows:

- exact-type specialization
- enum specialization
- nullable specialization
- custom Omicron registrations later if needed

### Special cases worth handling explicitly

- `Utf8String`
- `string`
- enums
- nullable value types
- booleans
- numeric primitives
- GUID/date/time family

---

## Format specifier design

This is the awkward part because UTF-8-native formatting still intersects with APIs that want `ReadOnlySpan<char>`.

### Recommendation: two-tier specifier path

#### Tier 1: fast ASCII specifier path

Parse the UTF-8 format specifier bytes directly.

If the specifier is representable as `System.Buffers.StandardFormat`:

- one ASCII standard format symbol
- optional precision digit(s) within `StandardFormat`'s supported range
- e.g. `X2`, `D4`, `N`, `G`, `f3`

then construct `StandardFormat` directly without allocating.

Important constraint: `StandardFormat` is intentionally narrow. It represents:

- a single standard format symbol
- optional precision
- no arbitrary multi-token custom format language

So Omicron should treat it as the first fast-path target for standard primitive/BCL formatting, not as a universal format-specifier representation.

#### Tier 2: char-specifier fallback

If the target formatter only accepts `ReadOnlySpan<char>` and the specifier is not representable as `StandardFormat`:

- transcode the UTF-8 specifier bytes into `stackalloc char[]`
- call `ISpanFormattable.TryFormat(...)`

### Initial scope recommendation

Support **ASCII format specifiers only** in the first shipping version.

That covers realistic numeric/date/GUID formatting needs in Omicron without dragging in broad Unicode format-token complexity.

Within that scope, prefer this dispatch order:

1. parse UTF-8 specifier bytes directly into `StandardFormat` when possible
2. use that `StandardFormat` with `Utf8Formatter` / `IUtf8SpanFormattable`
3. only fall back to UTF-16 char-specifier transcoding for APIs that truly require `ReadOnlySpan<char>` and cannot be expressed as `StandardFormat`

If a non-ASCII format specifier appears, throw a clear `FormatException` or `NotSupportedException` depending on where it was rejected.

---

## Alignment semantics

Alignment is the most likely place to accidentally promise more than the engine can really guarantee.

### Recommendation for initial implementation

Support .NET-style alignment syntax, but define width in terms of:

- emitted UTF-8 **byte count** for UTF-8-native values
- emitted UTF-8 byte count after transcoding for fallback paths

This keeps the implementation straightforward and allocation-conscious.

### Why this is acceptable initially

The immediate Omicron use cases are small ASCII wrapper strings such as:

- `"  Agent: {0}"`
- `"[tool: {0}]"`
- `"[Error: {0}]"`

For those cases, byte width and visible width are usually equivalent enough.

### Important documentation note

Do **not** describe this as terminal-cell-width-aware or grapheme-aware formatting.
It is not.

---

## Prepared format design

Prepared formats are worth it for repeated templates in transcript/display code.

### Proposed representation

```csharp
internal readonly struct Utf8FormatSegment
{
    public int Offset;
    public int Count;
    public int FormatIndex; // -1 for literal
    public Utf8StandardOrUtf16Format Format;
    public int Alignment;
}
```

and a prepared object holding:

- an owned UTF-8 template/literal buffer
- a segment array
- typed formatter metadata

### Preparation strategy

When preparing:

1. parse the format once
2. copy literal UTF-8 bytes into a compact owned buffer
3. store placeholder metadata in a segment array

This mirrors the useful part of ZString’s `PreparedFormatHelper`, but without UTF-16 templates.

### Template ownership

Public `Prepare(ReadOnlySpan<byte>)` should copy into owned storage.

If Omicron later wants an internal trusted-literal zero-copy template path, it can add that separately, following the same trusted-lifetime rules already used elsewhere for `u8` literals.

---

## Suggested file layout

```text
Omicron.Core/Text/
  Utf8CompositeFormat.cs
  Utf8CompositeFormatParser.cs
  Utf8CompositeFormatSegments.cs
  Utf8CompositeFormatPrepared.cs
  Utf8CompositeValueFormatter.cs
  Utf8CompositeValueFormatterCache.cs
```

### Likely responsibilities

- `Utf8CompositeFormat.cs`
  - public API surface
  - one-shot overloads
  - writer/builder integration

- `Utf8CompositeFormatParser.cs`
  - UTF-8 byte parser
  - placeholder parse structs

- `Utf8CompositeFormatPrepared.cs`
  - prepared template types
  - cached segment execution

- `Utf8CompositeValueFormatter*.cs`
  - per-type delegate cache
  - primitive / `Utf8String` / `string` formatting paths
  - fallback transcoding paths

---

## Implementation phases

## Phase 1: core byte parser + one-shot fixed-buffer formatter

### Deliverables

- `TryFormat<T1...T4>(ReadOnlySpan<byte>, Span<byte>, out int, ...)`
- byte-native parser for:
  - `{index}`
  - `{{` / `}}`
- direct literal copy path
- primitive formatting via `Utf8Formatter`
- `Utf8String` and `string` support

### Deliberate limits for Phase 1

- 1–4 typed args only
- no alignment yet, or alignment only if it adds little complexity
- no prepared format caching yet
- no broad fallback-to-`ToString()` behavior

### Validation focus

- basic placeholder replacement
- malformed braces
- insufficient buffer
- non-ASCII literal segments
- non-ASCII `Utf8String` argument values

---

## Phase 2: writer/builder integration + formatter cache

### Deliverables

- `Format(ref IBufferWriter<byte>, ...)`
- `AppendFormatUtf8(ref Utf8ValueStringBuilder, ...)`
- per-type delegate cache
- enum and nullable handling
- `IUtf8SpanFormattable` path

### Validation focus

- writer-based formatting equivalence with fixed-buffer formatting
- no extra string allocations for UTF-8-native values
- repeated formatting of the same types stabilizes on cached delegates

---

## Phase 3: prepared format templates

### Deliverables

- `Prepare<T1...T4>(ReadOnlySpan<byte>)`
- prepared segment arrays + owned template bytes
- execution path that skips reparsing

### Validation focus

- prepared vs one-shot output equivalence
- escape handling correctness
- repeated-call behavior

---

## Phase 4: format specifiers + alignment

### Deliverables

- `:X2`, `:D4`, etc. support
- signed alignment parsing
- byte-count padding behavior
- char-specifier fallback path for `ISpanFormattable`

### Validation focus

- primitive standard formats
- left/right alignment
- unsupported/non-ASCII format token behavior
- fallback transcoding correctness

---

## Phase 5: targeted Omicron adoption

### Initial migration targets

- `Omicron.CLI/Tui/TranscriptViewportWidget.cs`
- `Omicron.Core/Rendering/Transcript/TranscriptStore.cs`
- small display helpers that currently build wrapper messages with manual builders

### Example target conversions

```csharp
Utf8CompositeFormat.AppendFormatUtf8(ref builder, "  Agent: {0}"u8, msg.TextData ?? Utf8String.Empty);
Utf8CompositeFormat.AppendFormatUtf8(ref builder, "\n[Error: {0}]"u8, tic.Result);
Utf8CompositeFormat.AppendFormatUtf8(ref builder, "[tool: {0}]"u8, toolName);
```

### Goal

Move repetitive small-format composition to one consistent UTF-8-native helper rather than hand-assembling those fragments each time.

---

## Testing plan

Add focused tests under:

```text
Omicron.Core.Tests/Utf8CompositeFormatTests.cs
Omicron.Core.Tests/Utf8CompositePreparedFormatTests.cs
```

### Parser tests

- plain literals
- escaped braces
- `{0}` / `{1}` / `{15}`
- malformed placeholders
- missing closing brace
- invalid alignment syntax
- invalid format specifier syntax

### Value formatting tests

- all integral primitives
- floating point
- `Guid`
- `DateTime`
- `TimeSpan`
- enums
- nullable primitives
- `Utf8String`
- `string`
- non-ASCII text values

### Destination tests

- exact-fit destination
- undersized destination returns `false`
- writer output matches fixed-buffer output
- prepared output matches one-shot output

### Regression tests for Omicron use cases

- `"  You: {0}"u8`
- `"  Agent: {0}"u8`
- `"\n[Error: {0}]"u8`
- `"[tool: {0}]"u8`

### Behavior tests

- no `string` intermediate for `Utf8String` values
- no template UTF-16 transcode in byte-native literal copy path

---

## Benchmark plan

Add small benchmarks only after correctness is in place.

### Compare against

1. manual `Utf8ValueStringBuilder` composition
2. `string.Format(...)` + `Encoding.UTF8.GetBytes(...)`
3. ZString UTF-8 formatting with UTF-16 template, where comparable
4. if later added, Omicron byte-native numeric fast paths vs `Utf8Formatter`

### Measure

- small 1-arg transcript wrapper format
- small 2-arg status format
- repeated prepared-format use
- fixed-buffer vs writer destination
- primitive numeric formatting in default decimal format
- primitive numeric formatting in simple hex format, if implemented

### Important benchmark framing

Do not oversell “hot path” claims.
State this as a structural ergonomics + UTF-8-boundary improvement with measurable steady-state formatting benefits where repeated small-format composition exists.

---

## Acceptance criteria

1. Omicron has a UTF-8-native composite formatter that accepts `ReadOnlySpan<byte>` templates.
2. Literal template text is copied directly from UTF-8 bytes without first becoming `string`.
3. Primitive and `Utf8String` arguments format without intermediate `string` creation.
4. Typed overloads avoid boxing and `object[]` allocation.
5. A prepared-format path exists for repeated templates.
6. Transcript/display call sites can use the formatter instead of manual builder boilerplate for simple formatted UTF-8 output.
7. Tests cover malformed templates, non-ASCII text, insufficient destinations, and prepared-format equivalence.

---

## Recommended execution order

1. Build the byte parser.
2. Build the fixed-buffer one-shot formatter for 1–4 args.
3. Add formatter delegate caching and UTF-8-native value dispatch.
4. Add writer/builder integration.
5. Add prepared formats.
6. Add ASCII specifier + alignment support.
7. Migrate transcript/display call sites.
8. Add benchmarks and only then decide whether more arity/codegen work is warranted.

---

## Final recommendation

Use ZString as a design reference for:

- parser structure
- prepared segment representation
- typed overload strategy
- formatter delegate caching
- writer-based output

but keep Omicron’s implementation firmly centered on this different requirement:

> the format template itself must stay UTF-8-native.

That is the core reason Omicron needs its own pipeline instead of just wrapping the existing ZString UTF-8 formatting APIs.
