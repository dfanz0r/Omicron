# Code Review 0100: UTF-8 Text Generation Optimization Staged Changes

Date: 2026-05-15  
Scope: Full staged changes, with emphasis on UTF-8/content-block/text-generation optimization paths.

## Validation Run

- `dotnet build Omicron.CLI -c Debug` — passed
- `dotnet test Omicron.Core.Tests/Omicron.Core.Tests.csproj -c Debug --no-restore --logger 'console;verbosity=minimal'` — passed

## Summary

The staged changes move some rendering paths toward UTF-8-first APIs using `ZString`/`Utf8ValueStringBuilder`, especially semantic content block rendering and TUI transcript tool-result rendering.

The direction is good, but the current implementation has a serious persistence bug and some design hazards: pooled mutable UTF-8 builders are being stored inside long-lived/durable objects, while the hottest model/tool text paths still allocate full strings and line arrays.

## Findings

### F1. High: `ToolInvocationCompletedEvent.Blocks` breaks JSONL persistence

Files:

- `Omicron.Core/Events/OmicronEvent.cs`
- `Omicron.Core/Content/ContentBlock.cs`
- `Omicron.Core/Sessions/SessionStore.cs`

`ToolInvocationCompletedEvent` now contains:

```csharp
List<IContentBlock>? Blocks = null
```

`IContentBlock` implementations own `Utf8ContentBuffer`, and `Utf8ContentBuffer` exposes:

```csharp
public ref Utf8ValueStringBuilder Builder => ref _builder;
```

`System.Text.Json` cannot serialize this shape. A repro appending a `ToolInvocationCompletedEvent` with blocks to `JsonlSessionStore` fails with:

```text
The type 'Cysharp.Text.Utf8ValueStringBuilder&' of property 'Builder' on type
'Omicron.Core.Content.Utf8ContentBuffer' is invalid for serialization or
deserialization because it is a pointer type, is a ref struct, or contains
generic parameters that have not been replaced by specific types.
```

Because `PersistentEventSink` treats persistence failures as non-fatal, the app may continue running while silently failing to persist affected events. That can break resume/fork/session replay by dropping tool-completion events.

Recommendation:

- Do not rely on reflection serialization for `IContentBlock` / `Utf8ContentBuffer` / `Utf8ValueStringBuilder`.
- Keep `Blocks` as generic polymorphic content-block objects, but add explicit class-owned JSON serialization for content blocks.
- Use `Utf8JsonWriter` / `Utf8JsonReader` directly. JSON is natively UTF-8; block implementations should write property names and values from `ReadOnlySpan<byte>` where possible rather than centralizing through string DTOs.
- Add a content-block kind registry only for polymorphic dispatch, not for centralizing every block shape.
- Make `Utf8ContentBuffer.Builder` non-serializable (`[JsonIgnore]` or non-public) so accidental reflection serialization cannot reintroduce this failure mode.
- Add a regression test that persists and reloads a `ToolInvocationCompletedEvent` with structured content.

### F2. High: Long-lived content blocks own pooled mutable builders

File:

- `Omicron.Core/Content/ContentBlock.cs`

`Utf8ContentBuffer` stores a `Utf8ValueStringBuilder` and implements `IDisposable`. Content blocks are now retained in long-lived places:

- `ToolResult.Blocks`
- `ToolInvocationCompletedEvent.Blocks`
- `ToolCallBlock.ContentBlocks`
- `TranscriptStore`

I do not see clear ownership or disposal of those buffers after transcript/event usage. This risks leaking pooled buffers and makes content objects mutable/lifetime-sensitive.

Recommendation:

- `Utf8ValueStringBuilder` may remain the source of truth for content blocks, but the owning relationship must be explicit.
- `Utf8ContentBuffer` should own the builder. Public APIs should not expose a long-lived `ref` property to the builder.
- Replace direct public builder exposure with callback-based temporary mutable borrows using custom `ref` delegates:

```csharp
public delegate void Utf8BuilderMutator(ref Utf8ValueStringBuilder builder);
public delegate void Utf8BuilderMutator<TState>(ref Utf8ValueStringBuilder builder, TState state);
```

```csharp
public void Mutate(Utf8BuilderMutator mutator)
{
    ThrowIfDisposed();
    mutator(ref _builder);
}

public void Mutate<TState>(TState state, Utf8BuilderMutator<TState> mutator)
{
    ThrowIfDisposed();
    mutator(ref _builder, state);
}
```

- Use the generic state overload for allocation-free call sites instead of closure capture:

```csharp
buffer.Mutate(path, static (ref Utf8ValueStringBuilder b, string path) =>
{
    b.AppendLiteral("// "u8);
    b.AppendLine(path);
});
```

- Keep safe read-only APIs such as `AsSpan()`, `AsMemory()`, `Length`, and `AppendTo(ref Utf8ValueStringBuilder destination)`.
- Keep semantic content blocks as real class objects with their own UTF-8-aware serialization/deserialization rather than flattening them into a central DTO.

### F3. High/Medium: Main text-generation hot paths are still string-heavy

Files:

- `Omicron.Core/Workspace/WorkspaceReadService.cs`
- `Omicron.Core/IO/Formats/TextProcessor.cs`
- `Omicron.Core/Tools/ToolRegistry.cs`
- `Omicron.Core/IO/ContentProcessorRegistry.cs`

The most important LLM-facing file-read paths still convert whole files into strings and split them:

```csharp
var text = Encoding.UTF8.GetString(bytes.Span);
var allLines = text.Replace("\r\n", "\n").Split('\n');
```

and:

```csharp
text = encoding.GetString(bytes.Span);
text = text.Replace("\r\n", "\n");
var lines = text.Split('\n');
var sb = new StringBuilder();
```

Also the core model/tool flow remains string-centered:

- `ToolResult.Text`
- `WorkspaceReadResult.Content`
- `ContentProcessorResult.Text`
- `Message.ToolResultMessage(...)`

So the new UTF-8 path mostly helps selected TUI/tool-result rendering, not the core model-facing text generation path.

Recommendation:

- Prioritize UTF-8 line scanning/rendering in `WorkspaceReadService.ParseLines` and `TextProcessor`.
- Consider a UTF-8 result type alongside or below `ContentProcessorResult.Text`.
- Avoid `Replace(...).Split(...)` for large files; scan spans and render requested line ranges directly.

### F4. Medium: `VfsWorkspaceAdapter.ReadPathAsync` now renders twice

File:

- `Omicron.Core/Workspace/VfsWorkspaceAdapter.cs`

Current flow:

```csharp
var content = await _readService.ReadAsync(path, options, ct);
var result = _renderer.Render(content);
var blocks = WorkspaceLlmTextRenderer.ToContentBlocks(content);
return result with { Blocks = blocks };
```

This eagerly builds both string output and structured blocks for every read, even if the caller only needs one representation.

Recommendation:

- Make content blocks lazy, caller-selected, or produced in a single render pass.
- If both string and blocks are needed, share the same underlying UTF-8/text representation where possible.

### F5. Medium: TUI content-block concatenation omits separators

File:

- `Omicron.CLI/Tui/TranscriptViewportWidget.cs`

Current code:

```csharp
blockSb.AppendLine();
foreach (var block in ticBlocks)
    ContentBlockTextRenderer.AppendUtf8To(ref blockSb, block);
```

`ContentBlockTextRenderer.RenderAll` inserts newlines between blocks, but this path does not. Multiple content blocks can run together.

Recommendation:

```csharp
blockSb.AppendLine();
ContentBlockTextRenderer.AppendAllUtf8To(ref blockSb, ticBlocks);
```

### F6. Medium: Content block value semantics regressed

File:

- `Omicron.Core/Content/ContentBlock.cs`

Old content blocks were records with value equality. Most are now classes without complete equality/hash-code implementations:

- `MarkdownContentBlock`
- `CodeContentBlock`
- `DiffContentBlock`
- `FilePreviewContentBlock`
- `ErrorContentBlock`

Some equality paths compare only text bytes and ignore metadata such as `Language`, `Path`, `Size`, `LineCount`, and `IsBinary`.

Recommendation:

- Restore record-like value semantics, or explicitly document reference identity.
- If equality is retained, include all semantic fields.
- Add tests for equality including metadata differences.

### F7. Low/Medium: `ToolCallContentBlock` no longer belongs to the content-block hierarchy

File:

- `Omicron.Core/Content/ContentBlock.cs`

`ToolCallContentBlock` is now:

```csharp
public sealed class ToolCallContentBlock : IDisposable
```

It does not implement `IContentBlock`, so `ContentBlockTextRenderer` cannot render it. If this is intentional, the name is misleading.

Recommendation:

- Either implement `IContentBlock`, or rename/move it out of the semantic content-block hierarchy.

## Preferred Persistence Design

The review should not be read as recommending a centralized `PersistedContentBlock` DTO switch. That would flatten the API and make future content-block extension worse.

Preferred design:

```csharp
public interface IContentBlock
{
    ReadOnlySpan<byte> KindUtf8 { get; }
    Utf8ContentBuffer TextBuffer { get; }
    void WriteJson(Utf8JsonWriter writer);
}
```

Each concrete block writes its own payload:

```csharp
public void WriteJson(Utf8JsonWriter writer)
{
    writer.WriteString("kind"u8, "file_preview"u8);
    writer.WriteString("path"u8, Path);

    writer.WritePropertyName("text"u8);
    writer.WriteStringValue(TextBuffer.AsSpan());

    writer.WriteNumber("size"u8, Size);
    if (LineCount is not null)
        writer.WriteNumber("line_count"u8, LineCount.Value);
    writer.WriteBoolean("is_binary"u8, IsBinary);
}
```

A generic converter should only handle polymorphic dispatch:

```csharp
public sealed class ContentBlockJsonConverter : JsonConverter<IContentBlock>
{
    public override void Write(Utf8JsonWriter writer, IContentBlock value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        value.WriteJson(writer);
        writer.WriteEndObject();
    }

    public override IContentBlock Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Read `kind`, dispatch to registered concrete block reader.
        // The registry owns dispatch only; concrete block classes own payload shape.
    }
}
```

For reading text fields, avoid `GetString()` where practical. `Utf8JsonReader` can provide UTF-8 data directly:

```csharp
if (!reader.ValueIsEscaped && !reader.HasValueSequence)
{
    builder.AppendLiteral(reader.ValueSpan);
}
else
{
    var rented = ArrayPool<byte>.Shared.Rent(checked((int)(reader.HasValueSequence
        ? reader.ValueSequence.Length
        : reader.ValueSpan.Length)));
    try
    {
        var written = reader.CopyString(rented);
        builder.AppendLiteral(rented.AsSpan(0, written));
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(rented);
    }
}
```

This preserves the generic class-object API while keeping JSON persistence UTF-8-native.

## Missed Optimization Opportunities

### O1. TUI still frequently allocates strings/byte arrays

Examples:

- `TranscriptViewportWidget` uses `Encoding.UTF8.GetBytes($"  You: {ue.Text}")`.
- Pending assistant text accumulates in `StringBuilder`, then converts via `.ToString()` and `Encoding.UTF8.GetBytes(...)`.
- `TuiModelPicker`, `TuiApiKeyPrompt`, `InputEditorWidget`, and `SimpleStatusBar` allocate per-render strings/byte arrays.

These may be less important than the model/tool path, but they are still visible in frame rendering.

### O2. Execution and shell output trimming remains very allocation-heavy

Files:

- `Omicron.Core/Execution/IExecutionBroker.cs`
- `Omicron.Core/Tools/ShellTools.cs`

There are patterns like:

```csharp
Encoding.UTF8.GetByteCount(trimmed.ToString() + withNewline)
```

inside loops. `ShellTools.cs` may be dead/legacy, but active execution broker code still has avoidable allocations.

### O3. Content processors still use `StringBuilder` heavily

Many processors still build final text through `StringBuilder` and return `ContentProcessorResult.Text`. That is understandable for complex parsers, but simple processors like text/hex/csv can be incrementally moved to UTF-8 builders once the result model supports it.

## Recommended Fix Order

1. Fix persistence with explicit polymorphic content-block JSON serialization using `Utf8JsonWriter` / `Utf8JsonReader`.
2. Make `Utf8ContentBuffer` the explicit owner of its `Utf8ValueStringBuilder`, remove/avoid public long-lived builder `ref` exposure, and use custom `ref` mutator delegates for temporary mutable access.
3. Add regression tests around JSONL persistence of tool-completion events with structured content.
4. Rework `WorkspaceReadService.ParseLines` and `TextProcessor` to avoid whole-file `string` + `Split` in hot paths.
5. Avoid double rendering in `VfsWorkspaceAdapter`.
6. Fix TUI block separator issue.
7. Decide and document semantic content block equality/ownership/lifetime rules.

## Overall Assessment

The optimization direction is promising, especially the `AppendUtf8To` style APIs. However, the current implementation mixes transient pooled builders with durable semantic objects. That creates correctness and lifecycle risks while leaving the largest text-generation allocation paths mostly unchanged.

The safest architecture is:

- semantic/durable objects: real polymorphic class objects with explicit UTF-8-aware serialization;
- content buffers: `Utf8ContentBuffer` explicitly owns its `Utf8ValueStringBuilder` source of truth;
- mutable access: temporary callback-based borrows via custom `ref` delegates, not public long-lived `ref` properties;
- no reflection serialization of pooled builders or ref-like members;
- JSON persistence: use `Utf8JsonWriter` / `Utf8JsonReader` and write/read UTF-8 spans directly where possible;
- hot file/text readers: span-based scanning with direct UTF-8 append where possible.
