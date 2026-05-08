# RFC 0011: Rust/C# Interop and FFI Strategy

Status: **Research/Planned**

## Purpose

Clarify that Omicron is expected to become a mixed C# and Rust application. The primary application shell, core orchestration, UI abstractions, persistence model, and frontends may remain .NET/C#, while selected low-level or security-sensitive subsystems may be implemented in Rust.

This RFC defines how Rust and C# should interact with the least runtime overhead and the least long-term maintenance burden.

## Motivation

Several planned Omicron subsystems naturally pull Rust into the architecture:

- sandbox provider integrations and references from Codex/zerobox/heel-style projects;
- Wasmtime and WebAssembly plugin hosting;
- possible PTY/terminal emulation libraries;
- possible high-performance text, diff, parsing, or indexing components;
- platform-specific OS integrations where Rust crates already exist.

Without a deliberate interop strategy, Omicron could accumulate multiple incompatible binding styles, fragile build steps, and hard-to-debug lifetime bugs.

## Goals

- Keep the .NET side ergonomic and maintainable.
- Keep the Rust side idiomatic and testable.
- Minimize runtime overhead across the FFI boundary.
- Minimize generated-code churn and custom glue code.
- Support Windows, Linux, and macOS.
- Keep packaging and deployment understandable.
- Make ownership/lifetime/error/cancellation rules explicit.
- Avoid GC pressure from high-volume text streams and cross-runtime buffers.
- Enable zero-copy or copy-minimized exchange of UTF-8 text and binary data between C#, Rust, WASM/plugin hosts, and sandbox/terminal subsystems.

## Non-Goals

- Force all performance-sensitive code into Rust.
- Force all Rust APIs to be exposed to C#.
- Pick one binding generator for every possible use case before prototyping.
- Depend on experimental compiler projects for core functionality.

## Candidate Interop Approaches

### 1. Plain C ABI + .NET P/Invoke

References:

- https://www.strathweb.com/2023/06/calling-rust-code-from-csharp/
- https://khalidabuhakmeh.com/working-with-rust-libraries-from-csharp-dotnet-applications

Shape:

```rust
#[no_mangle]
pub extern "C" fn omicron_foo(input: *const u8, input_len: usize) -> OmicronResultHandle {
    // ...
}
```

```csharp
[LibraryImport("omicron_native")]
internal static partial IntPtr omicron_foo(byte[] input, nuint inputLen);
```

Pros:

- Lowest conceptual overhead.
- Lowest runtime overhead when designed carefully.
- Stable and well-understood across platforms.
- No dependency on a high-level binding generator.
- Easy to audit the ABI surface.

Cons:

- Manual binding maintenance unless paired with a generator.
- Manual memory ownership rules.
- Manual error/result marshaling.
- Manual async/callback patterns.

Best for:

- small stable ABI surfaces;
- hot paths where overhead matters;
- security-sensitive providers where explicitness is valuable;
- libraries where data crosses as bytes, handles, or simple structs.

### 2. C ABI + generated C# bindings with csbindgen

Reference:

- https://github.com/Cysharp/csbindgen/

Pros:

- Keeps the low-overhead C ABI model.
- Reduces hand-written P/Invoke boilerplate.
- Mature enough to evaluate seriously for production use.
- Fits handle-based APIs and explicit memory management.

Cons:

- Still requires careful ABI design.
- Generated API may not be the ideal public C# API; likely needs a hand-written safe wrapper layer.
- Need to validate support for Omicron's target platforms and NativeAOT/trimming scenarios.

Best for:

- Omicron's likely default low-level Rust interop path.
- Stable Rust libraries exposed through narrow C-compatible APIs.

### 3. UniFFI + uniffi-bindgen-cs

References:

- https://github.com/mozilla/uniffi-rs
- https://github.com/NordSecurity/uniffi-bindgen-cs

Pros:

- Higher-level IDL-oriented binding model.
- Handles more complex data models than raw C ABI.
- Can reduce glue code for object-like APIs.
- Good option when maintainability beats absolute minimal overhead.

Cons:

- More runtime/binding machinery than raw C ABI.
- C# support is via external/community tooling rather than the original Mozilla target set.
- Need to evaluate async, callbacks, exceptions/errors, and packaging fit.
- May be overkill for narrow hot-path APIs.

Best for:

- larger APIs with records/enums/interfaces;
- provider-style APIs where call frequency is moderate;
- cases where generated high-level ergonomics matter more than absolute overhead.

### 4. dotbridge-rs

Reference:

- https://github.com/devzolo/dotbridge-rs

Potential role:

- Research candidate for Rust/.NET bridging.
- Evaluate API shape, maintenance activity, platform support, generated-code quality, and deployment complexity.

### 5. csharp_binder

Reference:

- https://docs.rs/csharp_binder/latest/csharp_binder/

Potential role:

- Research candidate for generated bindings.
- Evaluate whether it fits Omicron's desired ABI, .NET target versions, and packaging needs.

### 6. rustc_codegen_clr

Reference:

- https://fractalfir.github.io/generated_html/rustc_codegen_clr_v0_0_3_2.html

Potential role:

- Interesting long-term research.
- Not suitable as a core dependency until maturity, compatibility, and maintenance risks are much clearer.

## Recommended Initial Direction

Omicron should start with two interop lanes:

```text
Lane A: Low-level/native lane
  Rust cdylib/staticlib
  C ABI
  csbindgen-generated P/Invoke where useful
  hand-written safe C# wrapper

Lane B: Higher-level/provider lane
  Evaluate UniFFI + uniffi-bindgen-cs
  Use only if it materially reduces maintenance for larger APIs
```

Default recommendation:

> Prefer a narrow C ABI with generated C# bindings via csbindgen plus a safe C# wrapper. Reach for UniFFI only when the API is broad enough that IDL-driven bindings clearly reduce maintenance.

This balances low overhead, explicit control, and maintainability.

## Boundary Design

The FFI boundary should be coarse-grained. Avoid chatty cross-language calls.

Good boundaries:

```text
execute sandboxed process request
parse/advance terminal emulator chunk
compute diff/index batch
run search/indexing batch
load/evaluate WASM plugin call
```

Bad boundaries:

```text
call Rust once per terminal cell
call Rust once per token for trivial logic
cross FFI for every small object allocation
share complex object graphs directly
```

Prefer exchanging:

- byte buffers;
- handles;
- flat structs;
- JSON or MessagePack for non-hot control payloads;
- explicit result/error structs;
- host-owned streams/callbacks only when necessary.

## Native Memory and Text Buffer Architecture

Omicron will move large volumes of text through several runtimes: .NET, Rust, WASM plugins, subprocesses, PTYs, model providers, persistence, and renderers. This makes memory architecture a first-order design concern.

Key rule:

> High-volume transcript, terminal, tool-output, and FFI text data should be stored as UTF-8 bytes in native/off-heap buffers, not repeatedly materialized as managed `string` objects.

Reasons:

- reduce GC pressure from streaming model output and tool logs;
- avoid large-object-heap churn and fragmentation;
- avoid pinning many managed arrays for FFI;
- avoid accidental copies during marshaling;
- keep byte-oriented data directly consumable by Rust, terminal rendering, persistence, and sandbox providers;
- make ownership and lifetime explicit across runtime boundaries.

This does not mean Omicron never uses managed strings. Strings are fine for short-lived labels, commands, UI text, metadata, JSON control messages, and user-facing small values. The rule applies to large or hot-path text buffers.

## Native Buffer Types

Omicron should define a small native-buffer abstraction on the C# side backed by low-level allocation APIs such as `NativeMemory.Alloc`, `NativeMemory.AlignedAlloc`, or platform-specific allocators where justified.

Representative safe C# wrapper:

```csharp
public sealed unsafe class NativeByteBuffer : SafeHandle
{
    public long Length { get; }
    public long Capacity { get; }

    public Span<byte> Span { get; }
    public ReadOnlySpan<byte> ReadOnlySpan { get; }

    public NativeSlice Slice(long offset, int length);
    public void Append(ReadOnlySpan<byte> bytes);

    protected override bool ReleaseHandle();
}

public readonly unsafe record struct NativeSlice(
    byte* Ptr,
    nuint Length,
    NativeBufferOwner Owner);
```

The public application API should prefer safe wrappers, `ReadOnlySpan<byte>`, `ReadOnlyMemory<byte>`, or explicit handles. Raw pointers should stay inside interop/rendering layers.

## Slab/Chunk Model for Text

Large append-only text should use native slabs/chunks.

```text
NativeUtf8TextStore
  slab 0: native byte buffer
  slab 1: native byte buffer
  slab 2: native byte buffer
  ...

Indexes
  message/block index
  logical line index
  wrapped line index
  search/index metadata
```

Properties:

- append-only for streaming data;
- stable native addresses for FFI while slab is alive;
- no GC relocation risk;
- byte offsets remain stable;
- layout/index structures can reference `(slabId, offset, length)`;
- compaction/checkpointing can persist slabs or copy into content-addressed blobs.

Suggested default:

```text
small chunks:
  use pooled managed buffers only if they never cross FFI and are short-lived

large/hot chunks:
  use native slabs

persistent snapshots:
  serialize native slabs into blob storage or memory-mapped files
```

## Zero-Copy FFI Slices

The FFI ABI should include borrowed and owned slice types.

```c
typedef struct omicron_borrowed_bytes_t {
    const uint8_t* ptr;
    uintptr_t len;
} omicron_borrowed_bytes_t;

typedef struct omicron_owned_bytes_t {
    uint8_t* ptr;
    uintptr_t len;
    uintptr_t capacity;
    uint64_t owner_id;
} omicron_owned_bytes_t;
```

Borrowed slices:

```text
caller owns memory
callee may read only during the call
callee must not retain pointer after returning
```

Owned buffers:

```text
producer owns allocation initially
consumer must release through the matching owner/release function
ownership transfer is explicit
```

For long-running Rust operations that need to retain text, pass a native buffer handle or explicitly transfer/copy ownership. Do not let Rust retain pointers into managed memory or temporary spans.

## Reference-Counted Buffer Handles

For cross-runtime sharing, Omicron should prefer handles over raw pointers.

```c
typedef uint64_t omicron_buffer_handle_t;

omicron_buffer_handle_t omicron_buffer_retain(omicron_buffer_handle_t handle);
void omicron_buffer_release(omicron_buffer_handle_t handle);
omicron_borrowed_bytes_t omicron_buffer_get_slice(
    omicron_buffer_handle_t handle,
    uintptr_t offset,
    uintptr_t len);
```

A handle can represent:

- a native C#-allocated slab;
- a Rust-owned buffer;
- a memory-mapped snapshot blob;
- a plugin/sandbox output buffer that has been imported into Omicron.

Handles make lifetimes explicit and avoid passing complex object graphs across FFI.

## Managed Memory, Pinning, and GC Rules

Rules:

- Do not pin many small managed arrays for long durations.
- Do not pass pointers to managed memory to Rust if Rust may retain them after the call.
- Avoid allocating managed strings for every streaming delta.
- Decode UTF-8 to managed `string` only at UI/control boundaries where needed.
- Prefer `ReadOnlySpan<byte>` over `string` for parsers, layout, search, syntax, and FFI.
- Use `ArrayPool<byte>` for short-lived managed scratch buffers.
- Use native slabs for long-lived or cross-runtime buffers.

Pinned managed buffers may be acceptable for short synchronous calls when profiling shows no issue, but they should not be the default architecture for transcript/tool-output storage.

## Memory-Mapped Files and Snapshot Blobs

For very large sessions and workspace snapshots, memory-mapped files may be useful:

```text
native slabs during active streaming
  ↓
content-addressed blob on checkpoint
  ↓
mmap blob for replay/search/layout without loading all bytes into managed heap
```

This can support:

- fast session replay;
- low-GC search/indexing;
- sharing data with Rust without copying;
- snapshot diffing over stable byte ranges.

Memory mapping should be introduced only after the native slab model is stable.

## WASM Plugin Buffer Boundary

WASM plugins have their own linear memory. True zero-copy between host native memory and WASM linear memory is generally not available in the same way as C#/Rust native slices. Therefore:

- keep plugin payloads semantic and bounded;
- pass handles for large transcript/tool-output data;
- expose host functions that let plugins request bounded slices or summaries;
- avoid copying entire transcripts into WASM memory;
- prefer plugin APIs that operate on handles, ranges, and structured metadata.

For browser-side plugin possibilities, the same handle/range design can map to browser `ArrayBuffer`, OPFS, IndexedDB, or remote host calls.

## Text Encoding Policy

Canonical large text representation:

```text
UTF-8 bytes in native buffers
byte offsets for storage/indexing
Rune/grapheme/cell-width indexes for layout
managed string only at small display/control boundaries
```

Invalid UTF-8 handling must be explicit. Options:

- reject at subsystem boundary;
- replace with U+FFFD for display;
- preserve raw bytes and mark decoding errors in indexes.

Transcript/model/tool text should preserve original bytes where practical and decode incrementally for layout/search.

## Ownership and Memory Rules

All Rust exports must document ownership.

Recommended pattern:

```text
C# passes input buffer pointer + length.
Rust copies input if it needs to retain it.
Rust returns an opaque handle or owned output buffer.
C# must call the matching free/release function.
No pointer returned by Rust may be used after release.
No Rust function may retain a pointer to managed memory after returning.
```

Example ABI concepts:

```c
omicron_buffer_t
  ptr
  len
  capacity

omicron_result_t
  status_code
  value_handle
  error_handle

omicron_release_handle(handle)
omicron_free_buffer(buffer)
omicron_last_error_message(error_handle)
```

C# should expose only safe wrappers to the rest of Omicron.

## Error Handling

Rust panics must not cross FFI boundaries. Rust exports should catch panics where appropriate and convert failures to structured errors.

Error model:

```text
Rust internal error
  ↓
OmicronNativeError { code, message, details? }
  ↓
C# safe wrapper throws/returns Omicron exception/result
  ↓
core emits structured AgentEvent/diagnostic
```

## Async and Cancellation

Avoid exposing Rust futures directly across FFI in the first version.

Recommended initial options:

1. C# owns async orchestration and calls blocking Rust operations on controlled worker threads when acceptable.
2. Rust exposes an operation handle with poll/cancel functions for long-running work.
3. For provider-like APIs, evaluate UniFFI async support during spikes.

Cancellation should be explicit:

```text
operation_handle = start(...)
poll(operation_handle)
cancel(operation_handle)
release(operation_handle)
```

## Threading

Rules:

- Rust must not call arbitrary C# callbacks from unknown threads unless the callback contract explicitly allows it.
- UI updates must return to the frontend/UI scheduler.
- Long-running Rust operations should be cancellable and should not block core event dispatch.
- Shared Rust state must be protected by Rust-side synchronization or made single-threaded by design.

## Packaging

Omicron should package native libraries per runtime identifier:

```text
runtimes/win-x64/native/omicron_native.dll
runtimes/win-arm64/native/omicron_native.dll
runtimes/linux-x64/native/libomicron_native.so
runtimes/linux-arm64/native/libomicron_native.so
runtimes/osx-x64/native/libomicron_native.dylib
runtimes/osx-arm64/native/libomicron_native.dylib
```

Build should be reproducible from the repository using scripted commands. CI should verify that generated bindings are current.

## Versioning

The native ABI needs an explicit version handshake.

```c
uint32_t omicron_native_abi_version(void);
```

C# wrapper should fail fast if the loaded native library does not match the expected ABI version.

## Areas Likely to Use Rust

Near-term/research:

- sandbox providers based on Codex/zerobox/heel-style code;
- Wasmtime/plugin host if Rust host proves preferable to direct .NET Wasmtime bindings;
- PTY/process integration where Rust crates are strongest.

Possible later:

- terminal emulator engine;
- diff/search/indexing hot paths;
- Unicode segmentation/cell-width engine if .NET performance/libraries are insufficient.

Default assumption remains: do not move a subsystem to Rust unless it materially improves security, performance, reuse, or maintainability.

## Evaluation Matrix

| Approach | Runtime overhead | Maintenance | Ergonomics | Maturity risk | Best use |
| --- | --- | --- | --- | --- | --- |
| Manual C ABI + P/Invoke | Lowest | Medium/High | Low | Low | tiny stable hot-path APIs |
| C ABI + csbindgen | Low | Medium | Medium | Low/Medium | default low-level lane |
| UniFFI + uniffi-bindgen-cs | Medium | Low/Medium | High | Medium | broad provider APIs |
| dotbridge-rs | Unknown | Unknown | Unknown | Research | spike only |
| csharp_binder | Unknown | Unknown | Unknown | Research | spike only |
| rustc_codegen_clr | Unknown | Unknown | Potentially high | High | long-term research only |

## Decision Points Before Adoption

Before selecting a primary binding approach, Omicron should build the same small proof of concept with at least C ABI/csbindgen and UniFFI:

```text
Rust side:
  accept request bytes
  perform small operation
  return structured success/error
  support explicit free/release
  support borrowed slice vs owned buffer semantics
  support cancellation or operation handle if applicable

C# side:
  allocate native buffer/slab
  call generated binding with borrowed slice and/or handle
  wrap in safe API
  verify no managed string allocation in hot path
  run from test project
  package native library
  verify CI on Windows/Linux/macOS
```

Measure:

- call overhead;
- generated code size/readability;
- build complexity;
- debugging experience;
- error handling ergonomics;
- memory safety footguns;
- native allocation/free correctness;
- GC allocations per operation;
- zero-copy feasibility for representative text payloads;
- CI and packaging burden.

## Design Decisions

1. Omicron is allowed to be a mixed C# and Rust application.
2. Keep cross-language APIs narrow and coarse-grained.
3. Prefer C ABI + generated bindings for low overhead and explicitness.
4. Use safe C# wrapper APIs; generated P/Invoke is not the public application API.
5. Store high-volume text as UTF-8 bytes in native/off-heap slabs, not as managed strings.
6. Use handles, borrowed slices, and owned buffers to make cross-runtime lifetime rules explicit.
7. Avoid long-lived pinning of managed buffers; use native slabs for long-lived or FFI-visible data.
8. Evaluate UniFFI for broader provider-style APIs.
9. Avoid experimental CLR codegen for critical paths until maturity improves.
10. Every native library has ABI versioning, CI packaging, and ownership documentation.
