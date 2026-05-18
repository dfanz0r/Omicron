# AGENTS.md

## Notes for coding agents

- This repository targets .NET 10.
- For quick standalone C# repros/utilities, prefer the .NET 10 single-file runner when appropriate:

  ```bash
  dotnet run /tmp/repro.cs
  ```

  This avoids creating temporary `.csproj` files for small checks such as parser repros, terminal escape experiments, config serialization snippets, or other one-off diagnostics.

## Critical rules

- **Do not use scripts or bash commands to edit code.** Use the provided `edit` or `write` tools directly. Violating this will result in a work stoppage.

## C# Conventions & Optimized Patterns

### UTF-8 text: prefer `Utf8ValueStringBuilder` over `StringBuilder`

Use `ZString.CreateUtf8StringBuilder()` instead of `new StringBuilder()` for building text output.
`Utf8ValueStringBuilder.AppendLiteral("..."u8)` avoids string allocation entirely where the content is known at compile time.

### Use `using var` with `Utf8ValueStringBuilder` — but not with `ref`

```csharp
// CORRECT — calling methods that don't take ref:
using var builder = ZString.CreateUtf8StringBuilder();
builder.Append("hello");
builder.ToString();

// INCORRECT — passing a using var as ref won't compile (CS1657):
using var builder = ZString.CreateUtf8StringBuilder();
BuildOutput(ref builder); // error CS1657

// CORRECT — use try/finally instead:
var builder = ZString.CreateUtf8StringBuilder();
try { BuildOutput(ref builder); }
finally { builder.Dispose(); }
```

### UTF-8 literals with `AppendLiteral`

```csharp
// Prefer this (compile-time byte array, no string allocation):
builder.AppendLiteral("--- Headers ---"u8);

// Over this (runtime string encoding):
builder.Append("--- Headers ---");
```

### Avoid string interpolation with `Append($"...")`

`Append($"...")` allocates a temporary `string` via interpolation, then encodes it to UTF-8.
Use `AppendFormat` or piecewise `Append`/`AppendLiteral` instead:

```csharp
// BAD — temporary string allocation:
builder.Append($"[FILE] {path}  ({count} lines)");

// GOOD — AppendFormat writes directly to the buffer:
builder.AppendFormat("[FILE] {0}  ({1} lines)", path, count);

// ALSO GOOD — piecewise, no intermediate strings:
builder.AppendLiteral("[FILE] "u8);
builder.Append(path);
builder.AppendLiteral("  ("u8);
builder.Append(count);
builder.AppendLiteral(" lines)"u8);
```

`AppendFormat` supports 1–16 typed arguments (`{0}` through `{15}`) and writes formatted
output directly to the UTF-8 buffer without creating intermediate strings.

### `ReadOnlySpan<byte>` buffer corruption hazard

`ReadOnlySpan<byte>` is a ref struct backed by the underlying array. If you create a span from a reusable `byte[]` buffer and then modify the buffer (e.g. via `Buffer.BlockCopy`), the span's data becomes corrupted.

```csharp
// DANGEROUS — payload corrupts when buffer shifts:
var payload = lineSpan[6..];          // points into buffer[]
Buffer.BlockCopy(buffer, ...);        // modifies buffer[] → payload corrupted!
ParseSseChunk(payload, ...);          // reads corrupted data

// SAFE — copy to heap before buffer modification:
var payloadCopy = payload.ToArray();
Buffer.BlockCopy(buffer, ...);
ParseSseChunk(payloadCopy, ...);
```

### Lazy initialization of cached strings — no interlocked needed

```csharp
// CORRECT — simple null check + assignment is thread-safe because:
// 1. string is immutable and reference-assignment is atomic
// 2. both threads compute the same result (same bytes → same string)
// 3. a discarded allocation is harmless
private string? _decoded;
public override string ToString()
{
    if (_decoded is null)
        _decoded = Encoding.UTF8.GetString(Utf8Span);
    return _decoded;
}

// AVOID — Interlocked.CompareExchange is unnecessary ceremony here.
// Neither Span<T>.ToString() nor string.Concat use it.
```

### `Utf8String` — canonical UTF-8 text type

`Utf8String` is the canonical immutable UTF-8 text type. It supports three storage kinds:
- `ManagedArray` — owned `byte[]` (default for dynamic text)
- `StaticLiteral` — zero-copy pointer to compiler-emitted `u8` literal data (internal construction only)
- `Empty` — singleton zero-length instance

Key APIs:
- `Utf8String.FromString(...)`, `Utf8String.FromUtf8(...)` — public factories
- `.Utf8Span` — zero-alloc `ReadOnlySpan<byte>` access
- `.ToString()` — explicit string materialization, lazy-cached
- `.Length`, `.IsEmpty`, `.Kind`
- `Equals(Utf8String?)` — structural byte comparison
- `AppendTo(ref ...)`, `WriteJsonString(...)` — writer integration

See `docs/implementation-plans/0018-utf8string-type-redesign.md` for full design.

### Provider request serialization: `Utf8JsonWriter` over `JsonObject`

Use `ArrayBufferWriter<byte>` + `Utf8JsonWriter` instead of building `JsonObject` trees for
HTTP request bodies:

```csharp
var buffer = new ArrayBufferWriter<byte>();
using (var writer = new Utf8JsonWriter(buffer))
{
    writer.WriteStartObject();
    writer.WriteString("model", model.Id);
    writer.WriteString("content", msg.TextUtf8);   // zero-alloc UTF-8 span
    writer.WriteEndObject();
}
// Send as bytes:
var content = new ByteArrayContent(buffer.WrittenSpan.ToArray());
content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
```

Use `msg.TextUtf8` (returns `ReadOnlySpan<byte>`) with `Utf8JsonWriter.WriteString(string, ReadOnlySpan<byte>)`
to write message text without allocating a managed string.

### `ArrayPool<byte>` for temporary buffers

Use `ArrayPool<byte>.Shared.Rent(len)` for short-lived byte buffers (SSE line payloads, JSON fragments) instead of `new byte[len]`. Always `Return` the buffer to the pool when done.

### Thread-safe immutable types

For types where all fields are set once in the constructor and never modified, no synchronization is needed for reads. `string` and `ReadOnlySpan<byte>` follow this pattern. A cached `_decoded` string follows the simple null-check pattern above.