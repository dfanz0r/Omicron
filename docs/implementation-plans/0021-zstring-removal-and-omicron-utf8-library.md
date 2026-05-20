# Implementation Plan 0021: Omicron UTF-8 Library and ZString Removal

**Date:** 2026-05-19  
**Status:** Proposed  
**Target:** Replace Omicron's direct ZString dependency with an Omicron-owned UTF-8 text/builder/formatting layer. Utf8StringInterpolation is used only as a reference implementation for selected modern .NET UTF-8 API patterns, not as a replacement dependency, architectural target, or ergonomics model. Interpolated-string-handler APIs are intentionally out of scope because C# interpolation literals enter as UTF-16 `string` values.

---

## Purpose

Omicron currently has three overlapping UTF-8 text-generation systems:

1. **ZString / `Cysharp.Text.Utf8ValueStringBuilder`**
   - third-party dependency
   - mutable UTF-8 builder primitive
   - currently embedded in content buffers, renderers, processors, transcript assembly, and formatter APIs

2. **Utf8StringInterpolation**
   - reference implementation only; not a planned Omicron dependency
   - primarily an interpolated-string-handler and `IBufferWriter<byte>` writer system
   - useful as a concrete example of selected modern .NET APIs for Omicron's goals: `IUtf8SpanFormattable`, `IBufferWriter<byte>`, pooled transient buffers, alignment, and format handling
   - not an ergonomics model for Omicron because its interpolated literal path is UTF-16-first

3. **Omicron's own UTF-8 system**
   - `Utf8String`
   - `Utf8ContentBuffer`
   - `Utf8TextAccumulator`
   - `Utf8CompositeFormat`
   - `PreparedUtf8CompositeFormat<T...>`
   - UTF-8-native provider/persistence/conversation work

The goal is to make Omicron's own system the single production dependency surface and remove ZString from Omicron product/test code as soon as possible.

---

## Current repository dependency inventory

Source inspection shows current production/test dependency on ZString is concentrated but still broad:

- `Omicron.Core/Omicron.Core.csproj`
  - `PackageReference Include="ZString" Version="2.6.0"`
- direct `using Cysharp.Text;` files: **27**
- direct `ZString.CreateUtf8StringBuilder(...)` calls: **53**
- direct `Utf8ValueStringBuilder` references: **56**

No non-builder ZString API is currently used in Omicron product code. Search found no production usage of APIs such as `ZString.Format`, `ZString.Join`, or `ZString.Utf8Format` outside reference docs/reports.

So the fast removal path is not to port all of ZString. It is to replace the subset Omicron actually uses:

- `Utf8ValueStringBuilder`
- `ZString.CreateUtf8StringBuilder()` factory semantics or an Omicron replacement factory
- builder append/line/span/memory/IBufferWriter behavior
- generic primitive formatting used by `builder.Append<T>(...)`
- disposal/pooled-buffer ownership behavior

---

## Reviewed implementations

### 1. ZString

Relevant files reviewed:

- `READ_ONLY/ZString/src/ZString/Utf8ValueStringBuilder.cs`
- `READ_ONLY/ZString/src/ZString/ZString.cs`
- `READ_ONLY/ZString/src/ZString/Utf8/Utf8ValueStringBuilder.AppendFormat.cs`
- `READ_ONLY/ZString/src/ZString/ZString.Utf8Format.cs`
- `READ_ONLY/ZString/src/ZString/FormatParser.cs`
- `READ_ONLY/ZString/src/ZString/PreparedFormatHelper.cs`
- `READ_ONLY/ZString/src/ZString/FastNumberWriter.cs`

Useful features to absorb:

1. **Pooled mutable UTF-8 builder**
   - `byte[]` from `ArrayPool<byte>`
   - `Span<byte>` append surface
   - `IDisposable` returns pooled storage
   - `IBufferWriter<byte>` support

2. **Convenient string-builder-like methods**
   - `Append(string)`
   - `Append(ReadOnlySpan<char>)`
   - `Append(char)`
   - `Append(char, int repeatCount)`
   - `AppendLine()` / `AppendLine(string)` / `AppendLine(ReadOnlySpan<char>)`
   - `AppendLiteral(ReadOnlySpan<byte>)`
   - `ToString()`
   - `AsSpan()` / `AsMemory()` / `AsArraySegment()`

3. **Composite format prior art**
   - brace scanning
   - typed arity overloads
   - prepared segment tables
   - alignment and format specifiers

4. **Per-type formatting cache**
   - useful idea, already partially present in `Utf8CompositeFormat.FormatterCache<T>`

5. **Optional thread-static scratch builder**
   - useful, but not required for first dependency-removal pass because Omicron currently uses `CreateUtf8StringBuilder()` and `new Utf8Builder(false)` rather than `notNested: true`.

Features not worth copying wholesale now:

- UTF-16 template-based UTF-8 formatting APIs
- generated arity set up to 16 before Omicron has call-site demand
- custom number writers before benchmarks justify them
- public `Cysharp.Text` namespace leakage

### 2. Utf8StringInterpolation

Utf8StringInterpolation is reviewed as implementation reference material only. Omicron should borrow selected implementation ideas from it where they fit, but should not add it as a package dependency and should not shape Omicron's canonical UTF-8 storage, formatting, or ergonomics model around its ref-struct interpolated-string-handler design.

Relevant files reviewed:

- `READ_ONLY/Utf8StringInterpolation/README.md`
- `READ_ONLY/Utf8StringInterpolation/src/Utf8StringInterpolation/Utf8String.cs`
- `READ_ONLY/Utf8StringInterpolation/src/Utf8StringInterpolation/Utf8StringWriter.cs`
- `READ_ONLY/Utf8StringInterpolation/src/Utf8StringInterpolation/Utf8StringWriter.AppendFormatted.cs`
- `READ_ONLY/Utf8StringInterpolation/src/Utf8StringInterpolation/Utf8StringBuffer.cs`
- `READ_ONLY/Utf8StringInterpolation/src/Utf8StringInterpolation/Internal/ArrayBufferWriterPool.cs`
- representative tests under `READ_ONLY/Utf8StringInterpolation/tests/...`

Useful implementation ideas to absorb:

1. **Direct writer targeting**
   - write to `IBufferWriter<byte>` without intermediate `string` or `byte[]` where the caller already owns a writer
   - keep writer growth and finalization explicit

2. **Initial capacity heuristics**
   - derive cheap estimates from known literal byte length and expected formatted value count where Omicron has byte-native templates
   - use these estimates for transient buffers and builder growth, not as a reason to accept UTF-16 interpolation literals

3. **Convenience formatting ladder for explicitly slow APIs**
   - `IUtf8SpanFormattable`
   - `ISpanFormattable`
   - `IFormattable`
   - `ToString()` fallback
   - this belongs only in explicitly named slow/convenience paths

4. **Pooled transient output helpers**
   - `ArrayBufferWriterPool`
   - `Utf8StringBuffer`-style disposable buffer owner

5. **Alignment and format-specifier behavior**
   - use its tests and implementation as reference material when validating Omicron's own byte-template formatter

Ideas intentionally not adopted:

- Interpolated string handler ergonomics. C# supplies interpolated literals to handlers as UTF-16 `string` values, which violates Omicron's goal for byte-native templates and composition.
- A ref-struct writer as the core long-lived text storage abstraction. Omicron needs owned buffers/content objects and eventually native managed regions for high-volume text.

### 3. Omicron internal system

Relevant files reviewed:

- `Omicron.Core/Text/Utf8CompositeFormat.cs`
- `Omicron.Core/Text/Utf8CompositeFormatPrepared.cs`
- `Omicron.Core/Content/ContentBlock.cs`
- `Omicron.Core/Content/Utf8TextAccumulator.cs`
- `Omicron.Core/Content/Utf8String.cs`
- current production usage through renderers/processors/transcript/tool paths

Current strengths:

- byte-native composite templates: `ReadOnlySpan<byte>` / `"..."u8`
- strict primary formatter API with compile-time `IUtf8SpanFormattable` constraints
- explicitly named slow/convenience runtime-dispatch path
- prepared byte-template support
- UTF-8-native `Utf8String`
- content-block ownership model through `Utf8ContentBuffer`

Current weakness:

- the mutable builder/storage primitive is still ZString's type
- `Utf8CompositeFormat` builder overloads are tied directly to `Cysharp.Text.Utf8ValueStringBuilder`
- `Utf8String.AppendTo(...)` is tied directly to `Cysharp.Text.Utf8ValueStringBuilder`
- formatting logic is not yet centralized enough for a builder, composite formatter, prepared formatter, and transient writer helpers to share one implementation

---

## Core direction

Do **not** replace ZString with Utf8StringInterpolation as a package swap. Utf8StringInterpolation is a useful case study in selected modern .NET UTF-8 APIs, not the target architecture. Omicron should also avoid adding interpolated-string-handler ergonomics because that API surface is UTF-16-literal-first.

Instead:

1. Implement an Omicron-owned mutable UTF-8 builder.
2. Move all production signatures from `Cysharp.Text.Utf8ValueStringBuilder` to the Omicron builder.
3. Remove the ZString package reference.
4. Reuse ZString's proven builder/parser/prepared-format ideas where applicable.
5. Reuse Utf8StringInterpolation only as reference material for writer targeting, pooled transient buffers, `IUtf8SpanFormattable`, and formatting behavior.
6. Keep Omicron's ergonomic composition APIs byte-template-first, using `ReadOnlySpan<byte>` / `"..."u8` templates and prepared UTF-8 formats.

### UTF-8 purity rule

New Omicron formatting/composition APIs should not introduce UTF-16 templates or C# interpolated-string-handler surfaces. UTF-16 `string` inputs remain acceptable only as explicit data-boundary/compatibility inputs, such as existing file paths, exception messages, external library values, or display materialization. They should not become the template representation for Omicron's core formatting pipeline.

---

## Naming recommendation

Use an Omicron namespace and avoid keeping `Cysharp.Text` alive in product code.

First-pass type names (as implemented):

```csharp
namespace Omicron.Core.Text;

public struct Utf8Builder : IDisposable, IBufferWriter<byte>
{
    public Utf8Builder();
    public Utf8Builder(bool useThreadStaticScratch);

    public int Length { get; }
    public bool IsDisposed { get; }
    public ReadOnlySpan<byte> AsSpan();
    public ReadOnlyMemory<byte> AsMemory();
    public ArraySegment<byte> AsArraySegment();

    public void AppendLiteral(ReadOnlySpan<byte> utf8);
    // Explicit UTF-16 data-boundary helpers; not template APIs.
    public void Append(string? value);
    public void Append(ReadOnlySpan<char> value);
    public void Append(char value);
    public void Append(char value, int repeatCount);
    public void Append<T>(T value);

    public void AppendLine();
    public void AppendLine(string? value);
    public void AppendLine(ReadOnlySpan<char> value);
    public void AppendLine<T>(T value);

    public void Clear();
    public override string ToString();

    // IBufferWriter<byte>
    public Span<byte> GetSpan(int sizeHint = 0);
    public Memory<byte> GetMemory(int sizeHint = 0);
    public void Advance(int count);

    public void CopyTo(IBufferWriter<byte> writer);
    public bool TryCopyTo(Span<byte> destination, out int bytesWritten);

    public void Dispose();
}
```

Factory helper:

```csharp
namespace Omicron.Core.Text;

public static class Utf8Text
{
    public static Utf8Builder CreateBuilder();
    public static Utf8Builder CreateBuilder(bool useThreadStaticScratch);
}
```

Reasoning:

- A short name `Utf8Builder` avoids name conflicts during migration (ZString's type is `Utf8ValueStringBuilder`).
- Moving it to `Omicron.Core.Text` removes dependency leakage.
- A separate `Utf8Text.CreateBuilder()` replaces `ZString.CreateUtf8StringBuilder()` without preserving the third-party namespace.

Avoid a long-lived compatibility shim in `namespace Cysharp.Text` unless the migration needs an emergency stopgap. A shim would remove the package quickly, but it would keep the wrong namespace in Omicron code and make the cleanup less explicit.

---

## Phased implementation plan

## Phase 0 — Freeze behavior with tests

Before replacing the backing builder, add focused tests for the subset Omicron depends on.

New test file:

```text
Omicron.Core.Tests/Utf8BuilderTests.cs
```

Coverage:

1. Construction/disposal
   - `Utf8Text.CreateBuilder()`
   - `new Utf8Builder()`
   - `new Utf8Builder(false)`
   - default struct behavior where possible
   - double dispose is safe

2. Append basics
   - `AppendLiteral("abc"u8)`
   - `AppendUtf8(...)`
   - `Append(string)`
   - `Append(null)` no-op
   - `Append(ReadOnlySpan<char>)`
   - `Append(char)` ASCII/non-ASCII
   - `Append(char, repeatCount)` ASCII/non-ASCII/negative count

3. Line behavior
   - `AppendLine()` uses `Environment.NewLine`
   - `AppendLine(string)`
   - `AppendLine(ReadOnlySpan<char>)`
   - `AppendLine<T>`

4. Primitive formatting
   - `Append<int>`
   - `Append<long>`
   - `Append<double>`
   - `Append<decimal>`
   - `Append<bool>`
   - `Append<Guid>`
   - `Append<DateTime>` / `DateTimeOffset` / `TimeSpan` if currently required

5. Span/memory access
   - `AsSpan()`
   - `AsMemory()`
   - `AsArraySegment()`
   - `ToString()`

6. `IBufferWriter<byte>` behavior
   - `GetSpan` + manual write + `Advance`
   - growth across multiple spans
   - invalid negative/too-large `Advance` throws

7. Ownership/growth
   - content remains valid across grow
   - `Clear()` resets length without returning storage
   - `Dispose()` returns pooled storage and invalidates access consistently

Also add integration regression tests around:

- `Utf8ContentBuffer`
- `Utf8TextAccumulator`
- `ContentBlockTextRenderer`
- `Utf8CompositeFormat.AppendFormatUtf8(...)`
- `TranscriptViewportWidget` if cheap

## Phase 1 — Implement Omicron-owned builder

Add:

```text
Omicron.Core/Text/Utf8Builder.cs
Omicron.Core/Text/Utf8Text.cs
```

Implementation details:

1. Backing storage
   - managed `byte[]` rented from `ArrayPool<byte>.Shared`
   - default initial capacity should be conservative but practical; start with 4 KB or 16 KB, not necessarily ZString's 64 KB
   - grow by max(current * 2, required)
   - return pooled arrays on dispose

2. Default struct safety
   - Unlike ZString, make `new Utf8Builder()` usable.
   - Methods should lazily initialize if `_buffer is null` and not disposed.
   - `default(Utf8Builder)` can either lazily initialize or throw a clear exception; prefer lazy initialization because `ToolResult.GetText()` currently uses `new Utf8Builder()`.

3. Disposal semantics
   - double dispose safe
   - after dispose, read/write APIs should throw `ObjectDisposedException`
   - this differs from ZString's looser behavior but matches Omicron's fail-fast ownership direction

4. `IBufferWriter<byte>` semantics
   - `GetSpan(sizeHint)` returns writable space at current index
   - `GetMemory(sizeHint)` same
   - `Advance(count)` validates count and updates length

5. Encoding
   - use `Encoding.UTF8.GetBytes(ReadOnlySpan<char>, Span<byte>)`
   - use `Encoding.UTF8.GetMaxByteCount(...)` to reserve for string/char spans
   - avoid string materialization in builder append paths

6. Newline
   - cache `Environment.NewLine` as UTF-8 bytes once
   - support LF/CRLF correctly

7. Generic append
   - `Append<T>(T value)` should call shared Omicron formatting logic, not duplicate a large type ladder locally.

## Phase 2 — Extract shared UTF-8 value formatting core

Current `Utf8CompositeFormat` has useful but local formatting pieces:

- `FormatterCache<T>`
- runtime dispatch in `TryFormatValue<T>(...)`
- alignment handling
- ASCII format-specifier transcoding

Move reusable value formatting into a dedicated internal component:

```text
Omicron.Core/Text/Utf8ValueFormatter.cs
```

Target shape:

```csharp
internal static class Utf8ValueFormatter
{
    public static Utf8FormatResult TryFormat<T>(
        T value,
        Span<byte> destination,
        out int written,
        ReadOnlySpan<byte> formatSpec = default,
        IFormatProvider? provider = null);

    public static Utf8FormatResult TryFormatSlow<T>(
        T value,
        Span<byte> destination,
        out int written,
        ReadOnlySpan<byte> formatSpec = default,
        IFormatProvider? provider = null);
}
```

Use this from:

- `Utf8Builder.Append<T>`
- `Utf8CompositeFormat`
- transient buffer/writer helpers

Rules:

1. Strict paths remain strict.
2. Slow paths are explicitly named and documented.
3. BCL primitive fast paths should pass format specifiers through correctly.
4. `string` and `Utf8String` write UTF-8 directly.
5. `ISpanFormattable` / `IFormattable` fallback is allowed only in slow/convenience APIs.
6. Unsupported types in strict paths fail at compile time where possible or throw clearly in builder append paths.

This phase is also the right time to eliminate any remaining drift between:

- one-shot composite formatting
- prepared composite formatting
- builder `Append<T>`
- slow/convenience formatting

### Amendment: source-generated custom formatter fast paths

`Utf8ValueFormatter` should be designed with a Roslyn incremental source generator extension point for custom type fast paths. The source generator is not required for the first ZString-removal milestone, but it is the preferred long-term design for zero-boxing custom struct formatting and NativeAOT compatibility.

#### Why source generation

The runtime generic-cache approach can avoid per-call boxing on CoreCLR/JIT, but usually relies on reflection such as `MakeGenericType(...)` and `Activator.CreateInstance(...)` to build typed wrappers. That is not ideal for NativeAOT/trimming and introduces one-time runtime initialization work.

A source generator can instead emit explicit generic type branches at compile time:

```csharp
if (typeof(T) == typeof(global::Omicron.Core.SomeCustomStruct))
{
    return Unsafe.As<T, global::Omicron.Core.SomeCustomStruct>(ref value)
        .TryFormat(destination, out written, default, provider);
}
```

This preserves the same advantage as the primitive `typeof(T) == typeof(int)` fast paths:

- no interface cast;
- no boxing for custom structs;
- no reflection;
- no generated runtime generic construction;
- NativeAOT-friendly because all target types are statically visible;
- JIT-friendly because closed generic `typeof(T)` checks can be constant-folded.

#### Registration API

Add an attribute in `Omicron.Core`:

```csharp
namespace Omicron.Core.Text;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class Utf8FormatterAttribute<T> : Attribute { }
```

Recommended usage for Omicron-internal types:

```csharp
[assembly: Utf8Formatter<MyCustomStruct>]
[assembly: Utf8Formatter<AnotherStruct>]
```

Assembly-level registration is preferred because the generated formatter belongs to the compilation, not to an individual call site. Class/struct-targeted usage may be allowed only if it proves useful and unambiguous.

#### Shared result enum and generated partial hook

There is already an internal `Utf8FormatResult` enum in `Omicron.Core/Text/Utf8CompositeFormat.cs`. Do not create a second enum. Move the existing enum to a shared file such as:

```text
Omicron.Core/Text/Utf8FormatResult.cs
```

and extend it with `NoFormatter`:

```csharp
internal enum Utf8FormatResult
{
    Success,
    InsufficientSpace,
    UnsupportedType,
    NoFormatter,
}
```

Then update `Utf8CompositeFormat`, `PreparedUtf8CompositeFormat`, `Utf8ValueFormatter`, `Utf8Builder`, and the future generator hook to use this shared result type.

Make the formatter class `partial` and reserve a generated hook:

```csharp
internal static partial class Utf8ValueFormatter
{
    private static partial Utf8FormatResult TryFormatGenerated<T>(
        ref T value,
        Span<byte> destination,
        out int written,
        ReadOnlySpan<byte> formatSpec,
        IFormatProvider? provider);
}
```

The non-generated fallback implementation should exist so Omicron builds without the generator:

```csharp
private static partial Utf8FormatResult TryFormatGenerated<T>(
    ref T value,
    Span<byte> destination,
    out int written,
    ReadOnlySpan<byte> formatSpec,
    IFormatProvider? provider)
{
    written = 0;
    return Utf8FormatResult.NoFormatter;
}
```

If C# partial-method rules make a default implementation awkward, use a generated partial class plus a small manually maintained fallback method name instead. The API shape should remain internal. The containing type must be declared `partial` in the non-generated source.

#### Result contract

Do not overload `written == 0` to distinguish formatter absence from insufficient space. Use the shared `Utf8FormatResult` enum described above.

Rules:

1. `Success`: `written` is bytes written.
2. `InsufficientSpace`: destination was too small; `written` is either a useful size hint or `0` if the formatter cannot estimate.
3. `NoFormatter`: no generated/runtime formatter path exists; only this result permits explicit slow fallback to `ToString()`.
4. `UnsupportedType`: a formatter path exists but the requested format/specifier is unsupported; do not retry or fall back silently.

This avoids the bug where BCL or custom formatters return `false, written = 0` for insufficient space and the caller misinterprets that as “safe to call `ToString()`”.

#### Generator behavior

The incremental generator should:

1. Find `Utf8FormatterAttribute<T>` registrations.
2. Resolve each `T` to an `ITypeSymbol`.
3. Validate whether `T` implements `IUtf8SpanFormattable` and/or `ISpanFormattable`.
4. Prefer `IUtf8SpanFormattable` when both are present.
5. Emit explicit `typeof(T) == typeof(...)` branches using fully qualified metadata names.
6. Use `Unsafe.As<T, TRegistered>(ref value)` for zero-boxing calls.
7. Reuse shared format-specifier conversion helpers from `Utf8ValueFormatter` / `Utf8CompositeFormat` rather than duplicating byte-to-char parsing logic in generated code.
8. Return `UnsupportedType` for unsupported non-ASCII format specifiers rather than silently widening or materializing strings.
9. Emit deterministic source for stable builds.
10. Report diagnostics for invalid registrations, such as types implementing neither formatter interface.

Generated `IUtf8SpanFormattable` branch shape:

```csharp
if (typeof(T) == typeof(global::MyNamespace.MyStruct))
{
    if (!typeof(T).IsValueType && value is null)
    {
        written = 0;
        return Utf8FormatResult.Success;
    }

    return Unsafe.As<T, global::MyNamespace.MyStruct>(ref value)
        .TryFormat(destination, out written, formatChars, provider)
        ? Utf8FormatResult.Success
        : Utf8FormatResult.InsufficientSpace;
}
```

The actual implementation must account for converting `ReadOnlySpan<byte> formatSpec` to a `ReadOnlySpan<char>` without heap allocation for the supported ASCII specifier scope. This conversion should be centralized in a shared helper so generated, builder, one-shot, and prepared-format paths all reject/accept the same specifiers.

Generated `ISpanFormattable` branch shape:

```csharp
if (typeof(T) == typeof(global::MyNamespace.MySpanOnlyStruct))
{
    if (!typeof(T).IsValueType && value is null)
    {
        written = 0;
        return Utf8FormatResult.Success;
    }

    int charCapacity = 256;
    const int maxCharCapacity = 1024 * 1024;

    while (true)
    {
        Span<char> chars = charCapacity <= 512
            ? stackalloc char[charCapacity]
            : new char[charCapacity]; // slow bridge; acceptable only for explicit slow/convenience path

        if (Unsafe.As<T, global::MyNamespace.MySpanOnlyStruct>(ref value)
            .TryFormat(chars, out var charsWritten, formatChars, provider))
        {
            int maxBytes = Encoding.UTF8.GetMaxByteCount(charsWritten);
            if (maxBytes > destination.Length)
            {
                written = maxBytes;
                return Utf8FormatResult.InsufficientSpace;
            }

            written = Encoding.UTF8.GetBytes(chars[..charsWritten], destination);
            return Utf8FormatResult.Success;
        }

        if (charCapacity >= maxCharCapacity)
        {
            written = 0;
            return Utf8FormatResult.InsufficientSpace;
        }

        charCapacity = Math.Min(charCapacity * 2, maxCharCapacity);
    }
}
```

`ISpanFormattable` support should remain a slow/convenience bridge. The canonical high-quality path for Omicron-owned high-volume types is `IUtf8SpanFormattable`.

#### Project layout

Add a generator project only after the builder migration is stable:

```text
Omicron.Core.Generators/
  Omicron.Core.Generators.csproj
  Utf8FormatterGenerator.cs
  Utf8FormatterGenerator.Diagnostics.cs
  Utf8FormatterGenerator.Models.cs
```

Reference it from `Omicron.Core` as an analyzer:

```xml
<ProjectReference Include="..\Omicron.Core.Generators\Omicron.Core.Generators.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

Generator tests should live separately from ordinary runtime tests if practical:

```text
Omicron.Core.Generators.Tests/
```

Use Roslyn generator test helpers or compile small in-memory projects and inspect generated source/diagnostics.

#### Staging

1. Keep immediate ZString removal independent of the generator.
2. Extract `Utf8ValueFormatter` first with explicit primitive branches, shared specifier conversion helpers, the shared `Utf8FormatResult` enum, and a generated-hook placeholder.
3. Add generator support for a small test struct implementing `IUtf8SpanFormattable`.
4. Add a test struct whose `ToString()` intentionally returns different text to prove the generated formatter is used.
5. Add large-output tests to prove insufficient-space retry does not fall back to `ToString()`.
6. Add `ISpanFormattable` bridge support only after UTF-8-native generated paths are stable.
7. Add NativeAOT/trimming notes and, if practical, a small publish smoke test later.

#### Non-goals for the generator

- Do not discover arbitrary runtime/plugin types.
- Do not use hashing for generic dispatch unless benchmarks prove direct branches are a problem.
- Do not generate interpolated-string-handler APIs.
- Do not allow generated fallback to silently materialize strings for formatter-capable types.
- Do not require the generator for ordinary Omicron builds until the runtime fallback path is already correct.

## Phase 3 — Retarget existing Omicron APIs from ZString to Omicron builder

Replace imports:

```csharp
using Cysharp.Text;
```

with:

```csharp
using Omicron.Core.Text;
```

or remove the import when already inside `Omicron.Core.Text`.

Replace factory calls:

```csharp
var builder = ZString.CreateUtf8StringBuilder();
```

with:

```csharp
var builder = Utf8Text.CreateBuilder();
```

Retarget signatures:

```csharp
ref Cysharp.Text.Utf8ValueStringBuilder
```

or unqualified `Utf8ValueStringBuilder` from `Cysharp.Text` to:

```csharp
ref Omicron.Core.Text.Utf8Builder
```

Important files:

- `Omicron.Core/Content/ContentBlock.cs`
- `Omicron.Core/Content/ContentBlockTextRenderer.cs`
- `Omicron.Core/Content/Utf8TextAccumulator.cs`
- `Omicron.Core/Content/Utf8String.cs`
- `Omicron.Core/Text/Utf8CompositeFormat.cs`
- `Omicron.Core/Text/Utf8CompositeFormatPrepared.cs`
- `Omicron.Core/Workspace/WorkspaceLlmTextRenderer.cs`
- `Omicron.Core/Diff/UnifiedDiffRenderer.cs`
- `Omicron.Core/Execution/IExecutionBroker.cs`
- `Omicron.Core/Extensions/*`
- `Omicron.Core/IO/Formats/*`
- `Omicron.Core/Rendering/Transcript/TranscriptStore.cs`
- `Omicron.CLI/Tui/TranscriptViewportWidget.cs`
- `Omicron.Core.Tests/Utf8CompositeFormatTests.cs`

After retargeting, remove:

```xml
<PackageReference Include="ZString" Version="2.6.0" />
```

Validation gates:

```bash
dotnet build Omicron.CLI -c Debug --no-restore
dotnet test Omicron.Core.Tests/Omicron.Core.Tests.csproj -c Debug --no-restore --logger 'console;verbosity=minimal'
rg -n "Cysharp\.Text|ZString|CreateUtf8StringBuilder" Omicron.Core Omicron.CLI Omicron.Core.Tests
```

The final `rg` should return zero product/test references.

## Phase 4 — Add UTF-8-native transient pooled buffer helpers

Inspired by Utf8StringInterpolation's `Utf8StringBuffer` and `ArrayBufferWriterPool`, but without adopting interpolated-string-handler APIs.

Potential API:

```csharp
public sealed class Utf8Buffer : IDisposable
{
    public Utf8Buffer();
    public Utf8Buffer(int initialCapacity); // throws on negative

    public int WrittenCount { get; }
    public ReadOnlySpan<byte> WrittenSpan { get; }
    public byte[] ToArray();
    public Utf8String ToUtf8String();
    public void Append(ReadOnlySpan<byte> data);
    public void Append(byte b);
    public bool TryCopyTo(Span<byte> destination, out int bytesWritten);
    public void CopyTo<TBufferWriter>(ref TBufferWriter writer) where TBufferWriter : IBufferWriter<byte>;
    public override string ToString();
    public void Dispose();
}

// WrittenMemory intentionally omitted — ReadOnlyMemory<byte> over a rented array
// can outlive the buffer and observe stale/corrupt data after Grow() or Dispose().
```

Use cases:

- temporary formatting where caller wants `byte[]`
- tests
- provider/request helpers
- one-shot conversions to `Utf8String`

Do not use this for long-lived content-block storage. Long-lived high-volume text should continue moving toward explicit owned/native memory regions as planned in the `Utf8String` roadmap.

## Phase 5 — Expand composite formatter arity only if needed

ZString supports larger typed arity sets. Omicron currently implements 1-4.

Do not expand immediately unless call sites require it.

If needed, extend in this order:

1. arity 5-8
2. prepared formats 5-8
3. only then consider 9-16

Avoid `params object?[]` as the primary API. It reintroduces array allocation, boxing, and runtime type ambiguity.

## Phase 6 — Add source-generated custom formatter fast paths

After `Utf8ValueFormatter` exists and ZString removal is stable, implement the Roslyn incremental source generator described in the Phase 2 amendment.

Deliverables:

- `Omicron.Core.Generators` project;
- `Utf8FormatterAttribute<T>` registration attribute;
- generated `TryFormatGenerated<T>(...)` hook;
- diagnostics for invalid registrations;
- tests proving custom struct `IUtf8SpanFormattable` formatting does not call `ToString()`;
- tests proving large generated formatter output retries until success or throws at the safety limit;
- documentation that generated fast paths are the NativeAOT-compatible alternative to reflection-based formatter caches.

This phase should not introduce UTF-16 template APIs or interpolated-string-handler ergonomics.

## Phase 7 — Optional optimized primitive writers

ZString's `FastNumberWriter` is useful prior art but should not block dependency removal.

Only consider custom byte-native numeric writers after:

1. ZString package is removed.
2. `Utf8ValueFormatter` has a stable explicit result contract.
3. Source-generated custom formatter fast paths are either implemented or explicitly deferred.
4. Benchmarks identify primitive formatting as material.
5. Correctness tests exist for signed/unsigned min/max, decimals, format specifiers, and cultures/providers if supported.

Until then, use BCL `Utf8Formatter` / `IUtf8SpanFormattable` first.

---

## Fastest path to remove ZString

Minimum dependency-removal sequence:

1. Add Omicron `Utf8Builder` with the subset listed above.
2. Add `Utf8Text.CreateBuilder()`.
3. Update `Utf8CompositeFormat`, `Utf8ContentBuffer`, `Utf8String`, and all call sites to use Omicron builder.
4. Remove `PackageReference Include="ZString"`.
5. Run full build/tests.
6. Verify zero references:

```bash
rg -n "Cysharp\.Text|ZString|CreateUtf8StringBuilder" Omicron.Core Omicron.CLI Omicron.Core.Tests
```

This is independent of any future convenience layer and should be completed before additional API expansion.

---

## Risks and mitigations

### Risk 1: Builder copy/dispose hazards

A mutable pooled struct can be accidentally copied, causing double-return or stale-span bugs.

Mitigations:

- keep ownership transfer patterns explicit
- preserve current `Utf8ContentBuffer(ref builder)` transfer constructor, but set source to default after transfer
- document that builder must not be copied after mutation
- add tests around transfer and disposal
- consider a future class-backed owner if copy hazards become hard to control

### Risk 2: Behavior differences from ZString

Omicron may accidentally depend on a ZString edge behavior.

Mitigations:

- Phase 0 tests cover the subset Omicron uses
- compare outputs for representative processors/renderers before removing package
- avoid unnecessary semantic changes during the initial removal

### Risk 3: UTF-16 convenience APIs creep back into composition paths

C# interpolated strings and `string`-template helpers are ergonomic, but their literals are UTF-16 at the language/API boundary. That conflicts with Omicron's byte-native composition goal.

Mitigations:

- do not implement interpolated-string-handler ergonomics in this plan
- keep `Utf8CompositeFormat` as the canonical byte-template API
- prefer `"..."u8` templates in core byte-native paths
- require explicitly named slow/convenience APIs when a UTF-16 string boundary is unavoidable

### Risk 4: Slow fallback becomes default

A permissive formatting ladder can quietly reintroduce string materialization.

Mitigations:

- preserve strict vs slow naming
- strict composite formatter remains compile-time constrained
- builder `Append<T>` may be convenient, but composite APIs should still prefer strict overloads where types are known

### Risk 5: Prepared/composite/builder formatting drift

Multiple formatting paths can disagree on specifier/alignment behavior.

Mitigations:

- centralize value formatting in `Utf8ValueFormatter`
- add equivalence tests across:
  - one-shot constrained
  - one-shot slow
  - prepared
  - builder append-format
  - transient buffer/writer helpers
  - generated custom formatter fast paths

---

## Acceptance criteria

ZString removal is complete when:

1. `Omicron.Core.csproj` has no ZString package reference.
2. Product/test code has no `using Cysharp.Text;`.
3. Product/test code has no `ZString.CreateUtf8StringBuilder(...)`.
4. Product/test code has no `Cysharp.Text.Utf8ValueStringBuilder` references.
5. `Utf8CompositeFormat` builder APIs use Omicron's builder.
6. `Utf8ContentBuffer` owns Omicron's builder.
7. `Utf8String.AppendTo(...)` targets Omicron's builder.
8. Full build and tests pass.
9. Existing UTF-8 content-block persistence tests still pass.
10. Existing provider/event/session UTF-8 tests still pass.

Validation commands:

```bash
dotnet build Omicron.CLI -c Debug --no-restore
dotnet build Omicron.CLI -c Release --no-restore
dotnet test Omicron.Core.Tests/Omicron.Core.Tests.csproj -c Debug --no-restore --logger 'console;verbosity=minimal'
git diff --check
rg -n "Cysharp\.Text|ZString|CreateUtf8StringBuilder" Omicron.Core Omicron.CLI Omicron.Core.Tests
```

---

## Recommendation

Prioritize the package-removal and byte-native library path:

1. implement Omicron builder,
2. centralize value formatting,
3. retarget code,
4. remove ZString,
5. validate,
6. add only UTF-8-native convenience helpers such as prepared byte templates, writer-target APIs, and pooled transient buffers,
7. add the source generator for NativeAOT-compatible, zero-boxing custom formatter fast paths.

This gets ZString out of the build while preserving Omicron's core rule: composition templates and owned text paths should stay UTF-8-native wherever the language/runtime makes that possible. The generator is the preferred long-term path for custom struct formatting, but it should not block the initial dependency removal.
