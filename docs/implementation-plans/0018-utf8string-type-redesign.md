# Implementation Plan 0018: Utf8String Type Redesign

**Date:** 2026-05-17  
**Status:** Design Proposal  
**Target:** Replace `Utf8OwnedText` with an RFC-aligned UTF-8 text primitive that can safely represent managed, static-literal, and native/off-heap storage without forcing eager `string` materialization.

---

## Summary

Omicron's RFCs justify a more powerful UTF-8 storage model than `Utf8OwnedText`, but the ownership model must remain explicit and provable.

This design rejects the following approaches:

- public `FromLiteral(ReadOnlySpan<byte>)`
- GCHandle-pinned managed arrays as the main dynamic storage model
- finalizer-driven cleanup on the canonical string type
- speculative zero-copy builder detachment without a proven ownership path
- pooled buffers as long-lived canonical ownership

This design also explicitly requires zero-copy support for trusted compiler-emitted `"..."u8` literals through an internal static-literal path.

This plan keeps the **goal** of an unsafe-capable, copy-minimized UTF-8 type while structuring it around the repository RFCs:

- **RFC 0011**: high-volume text should live in native/off-heap UTF-8 buffers where justified
- **RFC 0014**: remoting/protocol boundaries will be byte-oriented and transport-neutral
- **RFC 0012**: multi-agent/remoting/edit-harness work increases pressure on explicit ownership and copy minimization
- **RFC README / north star**: the system should remain frontend-agnostic and FFI-friendly

The result is a design that is still ambitious, still supports unsafe/native storage, but has a much tighter correctness story.

---

## Motivation

`Utf8OwnedText` is a good safe Phase 1 type, but it is intentionally conservative:

- it always owns a managed `byte[]`
- it always copies on `FromUtf8(...)`
- it always copies from builders/buffers
- it cannot directly adopt native/off-heap buffers
- it does not model cross-runtime/native ownership explicitly

That is acceptable for the current message migration, but it does not align with Omicron's longer-term architecture:

- native/off-heap transcript and tool-output buffers
- Rust/C# FFI
- binary remoting channels
- copy-minimized UTF-8 persistence and replay
- possible mmap/blob-backed replay/search/layout paths

So the redesign goal is real. The issue is not *whether* to evolve beyond `Utf8OwnedText`; it is *how to do so without introducing unsound lifetime behavior*.

---

## Design principles

1. **Unsafe is allowed, but only where the proof story is strong.**
2. **The canonical type must have a small, explicit owner-state machine.**
3. **Public APIs must not let arbitrary caller-provided spans masquerade as static-lifetime data.**
4. **Pinned managed arrays must not become the default long-lived storage model.**
5. **`ArrayPool<byte>` is for transient scratch/builders, not canonical long-lived text ownership.**
6. **Large/hot/cross-runtime text must be able to live in native/off-heap buffers.**
7. **String materialization remains explicit.**
8. **JSON persistence remains JSON-string based, not base64.**
9. **`Utf8String` is a leaf immutable text value, not the entire transcript-slab architecture.**

### Transitional note / TODO

The current mixed managed-array + static-literal setup is intentionally transitional.

It exists to let the message/runtime migration proceed safely while Omicron still has substantial managed-only call paths. It is **not** the intended final memory architecture for high-volume text.

Long term, once the UTF-8 transition is complete and the surrounding runtime/persistence/FFI/remoting layers are ready, Omicron should migrate the dominant text-storage paths toward **native manually managed memory regions** (for example slabs/arenas/owned native buffers with explicit lifetime control), with managed `byte[]` ownership retained primarily as a compatibility/fallback path where appropriate.

---

## What this design is and is not

### `Utf8String` should be

- the canonical immutable UTF-8 text value for message/event/tool-result text
- able to reference:
  - managed owned bytes
  - trusted static literal bytes
  - native/off-heap owned buffers
- explicit about ownership class
- cheap to serialize as UTF-8 JSON strings
- explicit about allocation boundaries for `ToString()`

### `Utf8String` should not be

- a public arbitrary borrowed-span wrapper
- a universal wrapper for temporary scratch spans
- the native transcript slab store itself
- a thin pointer bag with unclear ownership
- primarily backed by pinned GC-managed arrays

---

## Proposed storage model

`Utf8String` should use a small explicit storage discriminator.

```csharp
internal enum Utf8StringStorageKind : byte
{
    Empty,
    ManagedArray,
    StaticLiteral,
    NativeBuffer,
}
```

Representative shape:

```csharp
public sealed unsafe class Utf8String : IEquatable<Utf8String>
{
    private readonly Utf8StringStorageKind _kind;

    // Managed storage
    private readonly byte[]? _managedBytes;

    // Native/static storage
    private readonly Utf8BufferOwner? _owner;
    private readonly byte* _ptr;

    private readonly int _offset;
    private readonly int _length;
    private string? _decoded;
}
```

### State meanings

| State | Backing | Lifetime source | Notes |
|------|---------|-----------------|-------|
| `Empty` | none | singleton | zero-length singleton |
| `ManagedArray` | owned `byte[]` | normal GC lifetime | safe default for small/dynamic values |
| `StaticLiteral` | trusted static bytes | module/process lifetime | internal-only construction for compiler-emitted `u8` data |
| `NativeBuffer` | `Utf8BufferOwner` + pointer/slice | explicit native owner | FFI/remoting/native-slab path |

The key constraint is that every instance is always in exactly one of these states.

---

## Ownership model

### 1. ManagedArray

Used for:

- ordinary dynamic text created in managed code
- string-originated values
- fallback path when no safe ownership transfer exists

Properties:

- owns a normal `byte[]`
- no pinning required for storage
- no finalizer required on `Utf8String`
- safe and cheap for small/medium values

This remains the default managed fallback.

### 2. StaticLiteral

Used for:

- compile-time UTF-8 literals
- repeated status/error/marker strings
- cached singleton text values

Support for zero-copy compiler-emitted `"..."u8` data is required.

The stable-pointer mechanism is:

- obtain the literal as `ReadOnlySpan<byte>`
- use `MemoryMarshal.GetReference(...)` to get a `ref byte`
- use `Unsafe.AsPointer(...)` to convert that reference to `byte*`
- store the pointer and explicit length in the `StaticLiteral` instance

For true compiler-emitted `u8` literals, the backing bytes live in module static data rather than the GC heap, so the pointer is stable for the module lifetime and does not require `fixed` pinning.

Representative internal helper:

```csharp
internal static unsafe Utf8String FromTrustedUtf8Literal(ReadOnlySpan<byte> literal)
{
    if (literal.IsEmpty)
    {
        return Empty;
    }

    ref byte first = ref MemoryMarshal.GetReference(literal);
    byte* ptr = (byte*)Unsafe.AsPointer(ref first);
    return new Utf8String(ptr, literal.Length, Utf8StringStorageKind.StaticLiteral);
}
```

Important rule:

> There must be **no public API** that turns an arbitrary `ReadOnlySpan<byte>` into a static-literal-backed `Utf8String`.

So the following public API shape is rejected:

```csharp
public static Utf8String FromLiteral(ReadOnlySpan<byte> literal)
```

The internal helper is valid only for trusted compiler-emitted `u8` literal call sites or equivalent audited static-storage paths. It must never be used for spans backed by stack memory, movable managed arrays, pooled buffers, or temporary slices.

At ordinary managed call sites, repeated literals may still be cached as singletons when the trusted internal literal helper is not being used directly.

### 3. NativeBuffer

Used for:

- FFI-returned UTF-8 text
- future native transcript/tool-output slabs
- remoting/persistence/native replay integrations
- native buffer adoption where Omicron explicitly owns the lifetime

This is the key RFC-aligned expansion.

Native-backed `Utf8String` instances should not store a raw free callback directly on the string. They should instead hold an explicit owner object/handle.

Representative direction:

```csharp
public abstract unsafe class Utf8BufferOwner : SafeHandle
{
    public abstract byte* Pointer { get; }
    public abstract int Length { get; }
}
```

or an equivalent explicit owner abstraction with retain/release semantics.

The important point is:

- ownership is explicit
- cleanup logic is centralized in the owner type
- `Utf8String` does not itself become a giant disposal/finalization state machine

---

## API direction

### Public factories

```csharp
public sealed unsafe class Utf8String : IEquatable<Utf8String>
{
    public static Utf8String Empty { get; }

    public static Utf8String FromString(string text);
    public static Utf8String FromUtf8(ReadOnlySpan<byte> utf8);

    internal static Utf8String FromOwnedArray(byte[] bytes, int length);
    internal static unsafe Utf8String FromTrustedUtf8Literal(ReadOnlySpan<byte> literal);
    internal static Utf8String FromNative(Utf8BufferOwner owner, int offset, int length);

    public ReadOnlySpan<byte> Utf8Span { get; }
    public int Length { get; }
    public bool IsEmpty { get; }

    public override string ToString();
    public void CopyTo(Span<byte> destination);
    public void AppendTo(ref Utf8ValueStringBuilder builder);
    public void WriteJsonString(Utf8JsonWriter writer);

    public bool Equals(Utf8String? other);
    public override bool Equals(object? obj);
    public override int GetHashCode();

    /// <summary>
    /// Implicit conversion from a UTF-8 byte span. Copies data into a managed array.
    /// This is a convenience for callers that already have a ReadOnlySpan<byte> (e.g.
    /// from a "..."u8 literal) and want to avoid the explicit FromUtf8(...) call.
    /// It does NOT use the zero-copy static-literal path — the data is always copied.
    /// </summary>
    public static implicit operator Utf8String(ReadOnlySpan<byte> utf8);
}
```

### Public byte access shape

The public byte surface should favor operations that work naturally across managed, static-literal, and native-backed storage.

That means the design should not force every storage mode through `ReadOnlyMemory<byte>` if doing so would require unnecessary `MemoryManager<byte>` machinery or dilute the value of zero-copy literal/native backing.

The primary public byte view should therefore be:

```csharp
public ReadOnlySpan<byte> Utf8Span { get; }
```

with helper methods such as:

- `AppendTo(...)`
- `WriteJsonString(...)`
- `CopyTo(...)`

A `ReadOnlyMemory<byte>` projection may still be added later for storage modes that support it cleanly, but it should not be the defining constraint on the representation.

This keeps the API aligned with zero-copy `u8` literals and native/off-heap buffers while preserving explicit ownership on the implementation side.

---

## Builder and buffer transfer

Zero-copy transfer from `Utf8ValueStringBuilder` / `Utf8ContentBuffer` remains desirable.

That is still a desirable optimization, but it must not be the foundation of the redesign until ownership transfer is proven.

### Revised rule

- `FromBuilder(ref ...)` and `FromBuffer(...)` may initially remain **copying** operations
- later, if Cysharp/ZString or Omicron wrappers expose a proven ownership-detach path, add an internal transfer path
- do not design the canonical type around speculative detach support

Representative staged API:

```csharp
public static Utf8String FromBuilder(ref Utf8ValueStringBuilder builder); // copy-first
internal static Utf8String FromOwnedArray(byte[] bytes, int length);       // transfer-ready
internal static Utf8String FromNative(Utf8BufferOwner owner, int offset, int length);
```

If a safe detach path becomes available later, the implementation of `FromBuilder` can change without changing the public model.

---

## JSON serialization

### Write path

Keep JSON persistence UTF-8-native:

```csharp
writer.WriteStringValue(value.Utf8Span);
```

For native-backed values, `WriteJsonString(...)` may use an internal fast path if needed.

### Read path

The initial converter should stay correctness-first:

```csharp
var s = reader.GetString();
return s is null ? null : Utf8String.FromString(s);
```

That preserves escaped-string correctness and avoids mixing premature parser micro-optimizations into the canonical text type redesign.

If later profiling proves JSON decode is materially important, optimize the converter separately.

---

## Equality and hashing

Equality remains structural.

```csharp
public bool Equals(Utf8String? other)
    => other is not null && Utf8Span.SequenceEqual(other.Utf8Span);
```

Hashing should also be byte-content based, independent of storage mode.

Static literal, managed array, and native buffer instances with identical bytes must compare equal.

---

## What this design rejects

### Rejected: public `FromLiteral(ReadOnlySpan<byte>)`

Reason: cannot prove the source has static/process lifetime.

### Rejected: GCHandle as the universal owner model

Reason:

- RFC 0011 explicitly warns against long-lived pinning as default architecture
- many pinned managed arrays are the wrong baseline for transcript/tool-output storage
- native/off-heap support is a better long-term fit for FFI-heavy paths

### Rejected: ArrayPool-backed canonical ownership

Reason:

- pooled buffers require deterministic return
- canonical message/event text is often long-lived relative to scratch buffers
- pool ownership is better left to builders/accumulators/scratch paths

### Rejected: disposable/finalizable canonical text as the primary design

Reason:

- it pushes disposal/finalization burden into every message/event graph
- it complicates correctness auditing
- owner cleanup should live in explicit native owner abstractions where needed

### Rejected: forcing transcript-slab architecture into `Utf8String`

Reason:

- `Utf8String` is a leaf immutable value type for message/event text
- transcript stores, replay slabs, mmap blobs, and remoting channels need their own owner/index abstractions
- `Utf8String` should be able to reference native owners from those systems, not replace them

---

## Relationship to RFC 0011 native buffer architecture

This redesign should be treated as a **consumer-facing leaf type** that is compatible with future RFC 0011 native buffer work.

That means:

- `Utf8String` may reference a native owner/handle
- large transcript/tool-output stores may still use separate slab/index abstractions
- FFI should prefer owned/borrowed byte handles at coarse boundaries
- `Utf8String` is where small/medium canonical text values become easy to persist, compare, replay, and surface in the runtime model

A likely future layering is:

```text
native slab / mmap blob / FFI-owned buffer
  ↓
Utf8BufferOwner / buffer handle
  ↓
Utf8String slice/value
  ↓
Message / event / tool-result / replay model
```

---

## Migration strategy

### Phase 0: prerequisite owner abstractions

Before replacing `Utf8OwnedText`, define the ownership primitives needed for native-backed text:

- `Utf8BufferOwner` / native buffer handle abstraction
- internal storage discriminator/state invariants
- tests proving owner lifetime and disposal behavior

### Phase 1: introduce `Utf8String` alongside `Utf8OwnedText`

- implement managed-array and empty states first
- add internal-only static-literal construction
- add JSON converter
- add structural equality/hash tests

### Phase 2: convert `Message` and existing UTF-8-native runtime paths

- replace `Utf8OwnedText` fields with `Utf8String`
- preserve explicit string materialization APIs
- keep copying builder/buffer conversions initially if necessary

### Phase 3: add native-owner adoption

- enable `Utf8String.FromNative(...)`
- add FFI/remoting-facing tests
- validate that UTF-8 JSON persistence and replay still work unchanged

### Phase 4: optimize repeated literals and trusted transfer paths

- replace repeated string-originated constants with cached `Utf8String` singletons or trusted internal literal helpers
- adopt safe builder/buffer ownership transfer only when the detach contract is proven and tested
- evaluate whether some message/event literals should use internal static-literal construction

### Phase 5: remove `Utf8OwnedText`

- remove the transitional type once `Message`, providers, replay, and persistence all use `Utf8String`
- keep `Utf8TextAccumulator` / `Utf8ContentBuffer` as transient builders unless later consolidation is justified independently

---

## Acceptance criteria

1. The redesign is explicitly aligned with RFC 0011/0014 rather than treating native/off-heap support as out of scope.
2. There is no public API equivalent to `FromLiteral(ReadOnlySpan<byte>)`.
3. Managed, static-literal, and native-buffer ownership modes are clearly distinguished and testable.
4. Trusted compiler-emitted `"..."u8` literals can be stored with zero copy through an internal static-literal path.
5. The default dynamic managed path does not rely on long-lived pinned GC handles.
6. `Utf8String` preserves explicit `ToString()` allocation boundaries.
7. JSON persistence remains JSON-string based and UTF-8-native on write.
8. Structural equality and hashing behave the same across all storage kinds.
9. Builder/buffer zero-copy transfer is only enabled after ownership transfer is proven correct.
10. The design leaves a clean path for future FFI/native/remoting integration.

---

## Bottom line

The RFCs justify moving beyond `Utf8OwnedText`. They do **not** justify an unsound public borrowed-span literal API or a GCHandle-pinned-everything ownership model.

So the design direction is:

- keep the UTF-8-first canonical text goal
- keep unsafe/native capability in scope
- model ownership explicitly
- support zero-copy trusted `u8` literals
- expose a byte surface that works naturally for managed, literal, and native storage
- use explicit native owner handles for off-heap storage
- treat zero-copy builder transfer as an optimization phase, not a premise

That gives Omicron a UTF-8 text primitive that is compatible with the repo's FFI/remoting/native-buffer architecture without taking correctness shortcuts.