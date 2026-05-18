# Implementation Plan 0017: UTF-8 Message Pipeline Migration

**Date:** 2026-05-17
**Status:** Proposed
**Target:** Remove `Message.EffectiveText` and the `Text`/`Utf8Text` dual-state bridge by making message/event/provider text handling UTF-8-native end-to-end where practical.
**Depends on:** Plan 0016 (UTF-8 allocation hotspot remediation)
**Estimated Effort:** 4-7 days staged

---

## Purpose

`Message.EffectiveText` currently hides a decode-on-read allocation:

```csharp
public string EffectiveText => Utf8Text.HasValue
    ? Encoding.UTF8.GetString(Utf8Text.Value.Span)
    : Text ?? string.Empty;
```

This plan explains why that bridge exists today and lays out the architectural changes needed to remove it instead of normalizing the codebase around a dual `string`/`ReadOnlyMemory<byte>` message model.

---

## Why `EffectiveText` Exists Today

`EffectiveText` is not a primary design goal; it is a compatibility bridge created by a partial UTF-8 migration.

### 1. `Message` is still fundamentally string-shaped

`Omicron.Core/Models/Message.cs` currently has:

- `string? Text`
- `ReadOnlyMemory<byte>? Utf8Text`
- `string EffectiveText`

That means `Message` has **two possible sources of truth** for text, and callers that only know about “message text” need a unifying accessor.

### 2. Only tool results have a real UTF-8 path today

The UTF-8 message path is currently introduced only for tool results:

- `ToolResult.Utf8Data`
- `ToolInvocationCompletedEvent.ResultBytes`
- `Message.ToolResultMessageUtf8(...)`
- `SessionProjection` reconstructing `Message` with `Utf8Text = tic.ResultBytes`

User and assistant messages are still carried as strings.

### 3. Session replay/projection can produce `Message` objects with no string text

`SessionProjection` reconstructs tool result messages as:

```csharp
Text = tic.ResultBytes.HasValue ? null : tic.Result,
Utf8Text = tic.ResultBytes,
```

So downstream code that expects `msg.Text` to always exist would break without a bridge property.

### 4. Provider request builders are string-only today

Provider shapes currently build request bodies as `JsonObject` / `JsonNode` graphs:

- `IApiShape.BuildRequestBody(...) -> JsonObject`
- `ShapeBasedProvider` then does `JsonSerializer.Serialize(body)`

Those builders write message text through `msg.EffectiveText`, for example in:

- `Omicron.Core/Providers/ApiShape.cs`
- `Omicron.Core/Providers/AnthropicProvider.cs`

Because `JsonNode` content assignment expects strings, any UTF-8 tool result text gets decoded before it is sent back to the provider.

There is a second provider-side issue too: inbound transport/framing is still mostly string-based today even though it does not need to be. Current code uses:

- `StreamReader`
- line-oriented `ReadLineAsync`
- `ParseSseChunk(string data, ...)`

But `HttpClient` already gives us raw stream access, so this is an **easy technical replacement**, not a hard platform limitation. We can read raw bytes from `response.Content.ReadAsStreamAsync()`, scan for LF-delimited SSE frames, slice `data:` payloads as UTF-8 bytes, and pass them directly to `Utf8JsonReader`.

### 5. Replay/fork conversion paths are also string-based

`OmicronHost.BuildForkReplayEvents(...)` re-materializes replay events using `msg.EffectiveText` for:

- `UserMessageEvent`
- `AssistantResponseCompleteEvent`
- `ToolInvocationCompletedEvent`

So even if a projected message retained UTF-8 bytes, the replay layer currently decodes it back to a string.

### 6. The canonical conversation model is still string-based

`Omicron.Core/Models/Conversation.cs` uses:

- `TextContentItem(string Text)`
- `ToolResultContentItem(..., string Text, ...)`

and `ConversationConverter` calls `msg.EffectiveText` when converting `Message -> ConversationTurn`.

### 7. TUI/CLI display paths still read `msg.Text`

Examples:

- `Omicron.CLI/Tui/TranscriptViewportWidget.cs`
- `Omicron.CLI/Program.cs`

Tool results already have one special-case UTF-8 path in the transcript widget, but user/assistant rendering remains string-based.

---

## Additional Important Finding

The current event UTF-8 field is also not the right long-term persistence shape.

`ToolInvocationCompletedEvent.ResultBytes : ReadOnlyMemory<byte>?` is serialized by `System.Text.Json` as base64. For example JSONL currently contains:

```json
"result_bytes":"aGVsbG8Kd29ybGQ="
```

So the code avoids some intermediate string allocations in memory, but persisted textual tool output is no longer represented as textual JSON. That is acceptable as a transitional bridge, but not ideal as the final architecture.

---

## Leaf-First Migration Principle

The migration should start at the **leaf sources that create or reconstruct `Message` objects**, not at the central bridge property.

That means the first target is:

- every place that **stores string text into `Message`**;
- every place that reconstructs `Message` from string-shaped events/turns;
- every factory/helper that makes it easy to keep creating `Message.Text` / `Message.Reasoning`.

Only after those leaf sources are converted should we remove the temporary bridge fields and consumer-side string fallbacks.

Why this order is better:

1. It reduces the number of fresh string payloads entering the message graph.
2. It prevents the migration from stalling with a UTF-8 core wrapped in string-producing factories.
3. It makes later bridge removal mostly mechanical because new `Message` instances are already UTF-8-native.
4. It isolates unavoidable string boundaries (provider JSON parsing, user CLI input, display output) from avoidable in-memory string storage.

---

## Inventory: Current String Sources Stored into `Message`

These are the primary leaf sources that still inject string text into `Message` today.

### A. `AgentSession` runtime construction

`Omicron.Core/Sessions/AgentSession.cs`

Current string-backed message creation includes:

- `PromptAsync(string text)` -> `Message.UserMessage(text)`
- provider error messages:
  - `Text = $"[Error: {errMsg}]"`
- assistant completions:
  - `var fullText = responseText.ToString()`
  - `var fullReasoning = reasoningText.ToString()`
  - stored into `new Message { Text = fullText, Reasoning = fullReasoning, ... }`
- unresolved tool-call repair messages:
  - `Message.ToolResultMessage(tc.Id, tc.Name, "(cancelled)", ...)`
- string error tool results:
  - tool not found
  - permission denied
  - execution exception text

This is the highest-value message creation path because it feeds both live provider requests and persisted replay.

### B. `SessionProjection` replay reconstruction

`Omicron.Core/Sessions/SessionProjection.cs`

Current string-backed reconstruction includes:

- assistant accumulated text/reasoning stored via `accumText.ToString()` / `accumReasoning.ToString()`
- projected session errors stored as `Text = $"[Error: {err.Message}]"`
- projected user turns stored as `Text = ue.Text`
- tool results still use string fallback when `ResultBytes` is absent

Even after runtime paths improve, replay will keep reintroducing string-backed messages until this layer is migrated.

### C. Canonical conversation back-conversion

`Omicron.Core/Models/Conversation.cs`

`FromCanonical(...)` currently reconstructs `Message` using strings:

- `TextContentItem(string Text)`
- `ToolResultContentItem(..., string Text, ...)`
- `string.Join("\n", textParts)`
- `new Message { Text = ..., Reasoning = ... }`

This path is not yet dominant at runtime, but if `ConversationTurn` grows in importance it will keep string text alive unless migrated too.

### D. Event contracts feeding replay

`Omicron.Core/Events/OmicronEvent.cs`

These text-carrying events are still primarily string-shaped:

- `UserMessageEvent(string Text)`
- `AssistantTextDeltaEvent(string Delta, string? ReasoningDelta)`
- `AssistantResponseCompleteEvent(string FullText, string? ReasoningText, ...)`
- `ToolInvocationCompletedEvent(string Result, ..., ReadOnlyMemory<byte>? ResultBytes = null)`

As long as replay is fed by string-first events, `SessionProjection` will keep reconstructing string-backed messages for user/assistant turns.

### E. Legacy message factories in `Message`

`Omicron.Core/Models/Message.cs`

Current helpers still bias callers toward strings:

- `UserMessage(string text, ...)`
- `AssistantMessage(string text)`
- `ToolResultMessage(string text, ...)`
- separate `ToolResultMessageUtf8(...)` instead of one canonical path

These helpers are important because they shape all future call sites.

---

## Ranked Leaf-First Execution Order

The following order is optimized for “easy wins first” while still collapsing the bridge architecture quickly.

| Order | Area | Why first | Main files |
|------:|------|-----------|------------|
| 1 | Canonical UTF-8 text type | Unblocks every other migration | `Omicron.Core/Models/Message.cs` (+ new owned text type) |
| 2 | `Message` factories | Stops new string-backed messages at the API entry points | `Omicron.Core/Models/Message.cs` |
| 3 | `AgentSession` message construction | Highest-value live runtime source of string-backed messages | `Omicron.Core/Sessions/AgentSession.cs` |
| 4 | `SessionProjection` reconstruction | Prevents replay from reintroducing strings | `Omicron.Core/Sessions/SessionProjection.cs` |
| 5 | `ToolResult` canonical storage | Many easy call-site wins in tools/extensions | `Omicron.Core/Tools/ToolRegistry.cs`, builtin extensions |
| 6 | Provider SSE ingest | Easy raw-byte transport replacement with high payoff | `Omicron.Core/Providers/IChatProvider.cs`, `AnthropicProvider.cs`, `ApiShape.cs` |
| 7 | Provider request serialization | Removes repeated decode-on-send cost | `Omicron.Core/Providers/ApiShape.cs`, `IChatProvider.cs` |
| 8 | Event payloads + persistence | Removes string-first replay/event bridge and base64 bytes field | `Omicron.Core/Events/OmicronEvent.cs`, `SessionStore.cs` |
| 9 | Canonical conversation + replay cleanup | Cleans remaining bridge surfaces | `Omicron.Core/Models/Conversation.cs`, `OmicronHost.cs` |
| 10 | Frontends | Final explicit display-boundary decode cleanup | `Omicron.CLI/Tui/TranscriptViewportWidget.cs`, `Omicron.CLI/Program.cs` |

---

## Concrete Leaf Checklists

### Checklist A: `Message` API surface

Target: make it hard to create string-backed messages by accident.

- Add the owned UTF-8 text type.
- Replace `Text` / `Utf8Text` / `Reasoning` with canonical UTF-8-owned fields.
- Replace:
  - `UserMessage(string text, ...)`
  - `AssistantMessage(string text)`
  - `ToolResultMessage(string text, ...)`
  - `ToolResultMessageUtf8(...)`
- with one canonical creation path plus optional convenience overloads that immediately snapshot into the UTF-8-owned type.
- Add explicit methods only:
  - `GetTextString()`
  - `GetReasoningString()`
  - `TextUtf8`
  - `ReasoningUtf8`

Success checkpoint:

- `Message` no longer exposes dual `Text` / `Utf8Text` state.
- New call sites cannot store raw string state without going through explicit UTF-8 ownership conversion.

### Checklist B: `AgentSession`

Target: stop the main runtime loop from storing string-backed messages.

Convert these sites first:

- `PromptAsync(string text)` -> user message storage
- provider error messages (`"[Error: ...]"`)
- assistant `fullText` / `fullReasoning`
- tool-not-found / permission-denied / exception result text
- synthetic `"(cancelled)"` tool-result insertion in `ResolvePendingToolCalls()`

Preferred pattern:

- keep string input at the external boundary if needed;
- immediately snapshot into owned UTF-8 text when storing into `Message` or events.

Success checkpoint:

- `AgentSession` no longer assigns `Text = ...` / `Reasoning = ...` on `Message`.

### Checklist C: `SessionProjection`

Target: replay should reconstruct UTF-8-native `Message` instances.

Convert:

- accumulated assistant text / reasoning
- replayed user text
- replayed error text
- tool results without relying on string fallback except where event contracts still force it temporarily

Success checkpoint:

- projected `Message` instances do not depend on `EffectiveText` for internal correctness.

### Checklist D: `ToolResult`

Target: make tool outputs UTF-8-native by default.

Convert:

- `ToolResult(string? Text = null, ReadOnlyMemory<byte>? Utf8Data = null, ...)`
- `GetText()` so it becomes explicit compatibility materialization, not the primary storage path
- add either lazy materialization caching for repeated compatibility calls or a stronger push toward `AppendUtf8To`/direct UTF-8 consumption so `GetText()` does not become a repeated render-and-discard trap
- high-volume built-in tool returns in:
  - `BuiltinWorkspaceToolsExtension`
  - `BuiltinExecutionToolsExtension`
  - `BuiltinToolsExtension`

Easy wins:

- workspace reads already have UTF-8 data available;
- shell output path already builds UTF-8 buffers;
- many formatted error returns can be written directly into a UTF-8 builder.

Success checkpoint:

- `new ToolResult("...")` is convenience-only and uncommon in hot paths.

### Checklist E: Provider transport ingest

Target: remove unnecessary string creation before JSON parsing.

Convert:

- `StreamReader`
- `ReadLineAsync`
- `ParseSseChunk(string data, ...)`

To:

- raw response stream via `ReadAsStreamAsync()`;
- byte-buffer scanning for LF-delimited SSE lines;
- byte-span slicing of `data:` payloads;
- `ParseSseChunk(ReadOnlySpan<byte> data, ...)` or equivalent buffered API;
- `Utf8JsonReader` over those payload slices.

Success checkpoint:

- provider ingest path no longer requires `StreamReader` or string SSE frames.

---

## Easy Replacement Patterns

### Pattern 1: string input at boundary, UTF-8 storage internally

Use when input naturally arrives as a `string` (e.g. CLI prompt text):

```csharp
var msg = Message.UserMessage(Utf8OwnedText.FromString(text));
```

The string still exists at the boundary, but it is not stored as the long-lived message source of truth.

### Pattern 2: UTF-8 builder -> owned text snapshot

Use when content is already accumulated in UTF-8 form:

```csharp
using var acc = new Utf8TextAccumulator();
// append...
var text = Utf8OwnedText.FromBuffer(acc.ToOwnedBuffer());
```

This is the ideal path for assistant responses, reasoning, and many tool results.

### Pattern 3: raw bytes from tool/provider path -> owned text

Use when bytes are already available:

```csharp
var text = Utf8OwnedText.FromUtf8(resultUtf8Bytes.Span);
```

No intermediate string should be introduced.

### Pattern 4: explicit display decode only at UI boundary

Use in CLI/TUI/web rendering when text must be shown to a human:

```csharp
Console.WriteLine(msg.GetTextString());
```

This keeps decode costs visible and prevents them from being mistaken for cheap property access.

---

## Grep-Driven Migration Checkpoints

These are useful progress markers while implementing the plan.

### Checkpoint 1: stop new string-backed `Message` creation

Drive toward eliminating or sharply reducing patterns like:

- `Text = ` in `new Message { ... }`
- `Reasoning = ` in `new Message { ... }`
- `Message.UserMessage(string ...)`
- `Message.AssistantMessage(string ...)`
- `Message.ToolResultMessage(string ...)`

### Checkpoint 2: remove bridge reads from hot consumers

Drive toward eliminating:

- `msg.EffectiveText`
- `msg.Text ?? string.Empty`
- `msg.Utf8Text.HasValue ? ... : ...`

from provider/runtime/replay layers first.

### Checkpoint 3: remove string-based provider framing

Drive toward eliminating:

- `StreamReader`
- `ReadLineAsync`
- `ParseSseChunk(string ... )`

from provider transport code.

---

## Current Problems with `EffectiveText`

1. **Hidden allocation boundary**  
   `EffectiveText` looks like a cheap property but may allocate on every access.

2. **Repeated re-decoding across turns**  
   Provider request builders may decode the same stored tool result on every subsequent model call.

3. **Dual-source-of-truth model**  
   Callers must reason about `Text`, `Utf8Text`, and `EffectiveText` rather than one canonical representation.

4. **Asymmetric migration**  
   Only tool results are UTF-8-native. Assistant/user messages, reasoning text, replay, and canonical conversation are not.

5. **Persistence mismatch**  
   `ReadOnlyMemory<byte>` event fields become base64 in JSONL instead of direct JSON string text.

6. **Provider serialization bottleneck**  
   As long as providers construct `JsonObject` trees, UTF-8-native message storage still gets decoded before request emission.

---

## Recommendation

Do **not** keep expanding the `Text` + `Utf8Text` + `EffectiveText` pattern.

Instead, migrate to a **single UTF-8-native text representation** for messages/events and make string materialization explicit only at true display or parser boundaries.

---

## Proposed Architecture

### A. Replace dual `Message` text fields with one canonical text abstraction

Introduce a small immutable UTF-8 text type, for example:

```csharp
[JsonConverter(typeof(Utf8OwnedTextJsonConverter))]
public sealed class Utf8OwnedText
{
    public ReadOnlyMemory<byte> Utf8 { get; }
    public int Length { get; }

    public void AppendTo(ref Utf8ValueStringBuilder builder);
    public void WriteJsonString(Utf8JsonWriter writer);
    public string ToString(); // explicit allocation boundary
}
```

Key design requirements:

- **single source of truth** is UTF-8 bytes;
- can be created from `string`, `ReadOnlySpan<byte>`, or a transferred `Utf8ContentBuffer`/builder;
- does **not** expose a hidden cheap-looking property that allocates;
- immutable / ownership-safe for long-lived `Message` and event graphs;
- cheap enough to use as the default storage in `Message`, `ToolResult`, and eventually event payloads.

This should become the canonical target for all easy leaf migrations:

- `Message.UserMessage(...)`
- `Message.AssistantMessage(...)`
- `Message.ToolResultMessage(...)`
- replay reconstruction in `SessionProjection`
- synthetic error/cancelled messages in `AgentSession`
- tool-return helpers that still construct `ToolResult(string)`

Serialization policy should be explicit up front:

- `Utf8OwnedText` should have a dedicated converter that writes a **JSON string** from the UTF-8 span and reads back into owned UTF-8 storage;
- owning serializers (especially event persistence) may still write/read it manually for tighter control or performance;
- regardless of call path, textual payloads should persist as textual JSON, not base64 byte arrays.

### Why not use `Utf8ContentBuffer` directly everywhere?

`Utf8ContentBuffer` is mutable + disposable and was designed around builder ownership during construction/rendering. `Message` and persisted event objects are long-lived value-like records. A dedicated immutable owned UTF-8 text type is a better fit there.

`Utf8ContentBuffer` can still be the **construction path**, but `Message` should store an immutable owned UTF-8 snapshot.

---

### B. Make `Message` UTF-8-native

The key requirement is not just “store UTF-8 somewhere in `Message`”, but to eliminate leaf APIs that keep creating string-backed `Message` instances.

Replace:

```csharp
string? Text
ReadOnlyMemory<byte>? Utf8Text
string EffectiveText
string? Reasoning
```

with something like:

```csharp
Utf8OwnedText? TextData
Utf8OwnedText? ReasoningData
```

and explicit helpers:

```csharp
public bool HasText => TextData is not null;
public ReadOnlySpan<byte> TextUtf8 => TextData?.Utf8.Span ?? [];
public string GetTextString() => TextData?.ToString() ?? string.Empty;
```

This makes allocation boundaries obvious and removes “which field is authoritative?” ambiguity.

`Reasoning` should follow the same pattern as ordinary text rather than remaining a special string-only side channel.

All roles — user, assistant, tool result — should use the same text representation.

---

### C. Change provider request construction from `JsonObject` to `Utf8JsonWriter`

This is the most important architectural change for outbound serialization, and it pairs naturally with a byte-native provider ingest path.

Current bottleneck:

```csharp
JsonObject BuildRequestBody(...)
var json = JsonSerializer.Serialize(body)
```

Proposed replacement:

```csharp
void WriteRequestBody(Utf8JsonWriter writer, ...)
```

Preferred contract choice:

- use `WriteRequestBody(Utf8JsonWriter writer, ...)` as the shape interface;
- let `ShapeBasedProvider` own the output buffer via `ArrayBufferWriter<byte>` (or equivalent) and construct the `Utf8JsonWriter`;
- keep `IBufferWriter<byte>` as an implementation detail of the provider, not the public shape contract.

Benefits:

- provider shapes can write text directly from `Utf8OwnedText.Utf8` using `Utf8JsonWriter`;
- no `msg.EffectiveText`-style decode is needed for tool results or stored assistant/user text;
- request emission can use `ByteArrayContent` / `ReadOnlyMemoryContent` instead of `StringContent`.

This is an **easy .NET transport migration** because `HttpClient` already supports the raw-byte APIs we need. The main code changes are architectural, not infrastructural.

Related inbound follow-up:

- replace `StreamReader` SSE handling with byte-buffer scanning over `ReadAsStreamAsync()`;
- change `ParseSseChunk(string ...)` to a UTF-8 byte/span-based API;
- use `Utf8JsonReader` on those payload slices instead of routing through intermediate strings.

Interface migration note:

- `IApiShape.ParseSseChunk(...)` should move to the byte-based signature in one repo-wide change, updating all in-tree shapes together;
- do not keep long-lived parallel string and byte overloads unless a short-lived migration shim is absolutely needed inside the same implementation phase.

This change affects:

- `Omicron.Core/Providers/ApiShape.cs`
- `Omicron.Core/Providers/AnthropicProvider.cs`
- `Omicron.Core/Providers/IChatProvider.cs` (`ShapeBasedProvider`)

---

### D. Make event text payloads UTF-8-native too

Current event model is mostly string-only:

- `UserMessageEvent(string Text)`
- `AssistantTextDeltaEvent(string Delta, ...)`
- `AssistantResponseCompleteEvent(string FullText, ...)`
- `ToolInvocationCompletedEvent(string Result, ..., ReadOnlyMemory<byte>? ResultBytes = null)`

Proposed direction:

- move to the same owned UTF-8 text abstraction for event text fields;
- stop using parallel `string + bytes` payloads;
- add custom event JSON serialization so text is persisted as **JSON strings written from UTF-8 spans**, not base64 byte arrays.
- event persistence should either rely on `Utf8OwnedText`'s converter semantics or write the same JSON-string shape manually; in either case, persisted text should remain human-readable JSON text.

This likely means event persistence should eventually stop relying on reflection serialization for these text-carrying event types and instead use explicit write/read helpers.

---

### E. Update session projection and replay to stay UTF-8-native

`SessionProjection` should project UTF-8 text into messages without needing a `Text` fallback.

`OmicronHost.BuildForkReplayEvents(...)` should not call `msg.EffectiveText`. Two viable options:

1. **UTF-8-native replay events** — best long-term option.
2. **Fork directly from projected messages/conversation turns** instead of reconstructing a string-only event stream first.

---

### F. Evolve the canonical conversation model

`ConversationTurn` content items are currently string-based. If `ConversationTurn` remains the long-term canonical runtime model, it should evolve to share the same UTF-8 text abstraction:

- `TextContentItem(Utf8OwnedText Text)`
- `ToolResultContentItem(..., Utf8OwnedText Text, ...)`
- possibly `ReasoningContentItem(Utf8OwnedText? Summary, ...)`

If `ConversationTurn` is not going to be the runtime model soon, defer this until after `Message` and provider serialization are migrated.

---

## Suggested Migration Phases

### Phase 1: Introduce the canonical owned UTF-8 message text type

Before touching consumers, add the replacement text primitive:

- introduce `Utf8OwnedText` (or equivalent immutable UTF-8 text owner);
- give it explicit string materialization (`ToString()` / `GetString()`), `Utf8` span access, and `Utf8JsonWriter` integration;
- give it a dedicated JSON converter that persists as a JSON string from UTF-8 bytes;
- keep it usable from existing UTF-8 construction helpers (`Utf8ContentBuffer`, accumulators, raw spans, strings).

This gives all later leaf migrations a single target type.

### Phase 2: Migrate message factories and leaf constructors first

This is the most important phase for the order you want.

Change the places that create `Message` so they stop storing new string payloads:

- replace `Message.UserMessage(string)` / `AssistantMessage(string)` / `ToolResultMessage(string)` with UTF-8-native creation paths;
- make `Message` itself store only the canonical UTF-8 text representation for text/reasoning;
- use `Utf8TextAccumulator.ToOwnedBuffer() -> Utf8OwnedText.FromBuffer(...)` as the canonical streaming-construction path for assistant text and reasoning;
- convert `AgentSession` runtime message creation:
  - user messages
  - assistant completions
  - assistant reasoning
  - tool results
  - synthetic error/cancelled messages;
- convert `SessionProjection` so replay reconstructs UTF-8-native `Message` instances, not strings;
- convert `ConversationConverter.FromCanonical(...)` if that path remains active enough to keep injecting strings.

Goal of this phase: **new `Message` instances should stop being born string-backed**.

This phase should explicitly prioritize the easiest leaf wins first:

1. `Message` factory methods
2. `AgentSession` synthetic/runtime message construction
3. `SessionProjection` replay reconstruction
4. `ToolResult` helper/factory storage
5. `ConversationConverter.FromCanonical(...)` if still on an active path

### Phase 3: Make remaining string boundaries explicit

Once leaf creation paths are UTF-8-native:

- remove or obsolete `EffectiveText`;
- replace it with explicit allocating helpers like `GetTextString()` only where unavoidable;
- add `TextUtf8` / `ReasoningUtf8` / `HasText` helpers for consumers.

This phase should happen only after Phase 2 is substantially complete, so bridge removal does not fight newly-created string-backed messages.

### Phase 4: Migrate provider transport + request serialization

- replace `StreamReader` / string-line SSE handling with byte-buffer scanning over `ReadAsStreamAsync()`;
- change `ParseSseChunk(string ...)` to operate on UTF-8 payload bytes/spans;
- use `Utf8JsonReader` directly on provider payload slices;
- change `IApiShape.BuildRequestBody(...)` to a writer-based API;
- update all shapes to write UTF-8 directly;
- change `ShapeBasedProvider` to emit request bytes directly instead of `JsonObject -> string -> StringContent`.

This is the phase that removes the biggest repeated decode cost from long-lived conversation history.

UTF-8 validation guidance:

- JSON payloads should rely on `Utf8JsonReader` for UTF-8 validity during parsing;
- additional explicit validation is only needed at truly raw non-JSON text boundaries;
- do not overcomplicate early phases with a separate validation layer for ordinary provider JSON payloads.

### Phase 5: Migrate event payloads + persistence

- replace string-first event text fields and `ResultBytes` bridge fields with proper UTF-8-native text payloads;
- add custom event JSON write/read paths so persisted text remains textual JSON, not base64;
- update `SessionProjection` and replay accordingly.

### Phase 6: Replay, canonical conversation, and frontend cleanup

- update `OmicronHost` replay/fork paths so they do not re-stringify message text;
- update `ConversationConverter.ToCanonical(...)` and canonical content items if `ConversationTurn` stays in the runtime path;
- update TUI/CLI rendering helpers to consume UTF-8 directly where sensible, with decode only at final display boundaries.

### Phase 7: Delete temporary bridge state

Remove:

- `Message.EffectiveText`
- `Message.Text` + `Message.Utf8Text` dual storage
- `ToolInvocationCompletedEvent.ResultBytes` as a bridge-only field
- any UTF-8-vs-string split message factory overloads that were only transitional

---

## Concrete Files Impacted

### Root cause / bridge layer

- `Omicron.Core/Models/Message.cs`
- `Omicron.Core/Sessions/SessionProjection.cs`
- `Omicron.Core/Sessions/AgentSession.cs`

### Provider request serialization

- `Omicron.Core/Providers/IChatProvider.cs`
- `Omicron.Core/Providers/ApiShape.cs`
- `Omicron.Core/Providers/AnthropicProvider.cs`

### Replay / host orchestration

- `Omicron.Core/OmicronHost.cs`

### Event contracts / persistence

- `Omicron.Core/Events/OmicronEvent.cs`
- `Omicron.Core/Events/OmicronEventJson.cs`
- `Omicron.Core/Sessions/SessionStore.cs`
- possibly dedicated event converters/serializers

### Canonical conversation model

- `Omicron.Core/Models/Conversation.cs`

### Frontends

- `Omicron.CLI/Tui/TranscriptViewportWidget.cs`
- `Omicron.CLI/Program.cs`

---

## Staging Notes

- The long-term direction is still to reduce string materialization broadly, including frontends where practical.
- During staged migration, some explicit decode-at-render sites may temporarily remain at true display boundaries; those should be visible and intentional, not hidden behind generic accessors like `EffectiveText`.
- This plan should prioritize the leaf/core paths already enumerated in the ranked execution order — the places that create, reconstruct, ingest, and serialize message text (`Message` factories, `AgentSession`, `SessionProjection`, `ToolResult`, provider ingest/serialization) — and then clean up remaining frontend/display decode sites.

---

## Acceptance Criteria

1. No `Message.EffectiveText` remains.
2. `Message` has one canonical text representation, not `Text` + `Utf8Text` dual state.
3. `Reasoning` follows the same owned UTF-8 representation pattern as ordinary message text.
4. `Utf8OwnedText` does not expose a cheap-looking implicit/non-obvious string accessor beyond explicit compatibility materialization.
5. Provider request bodies no longer require decoding stored UTF-8 tool results into strings just to serialize JSON.
6. Event persistence stores text as textual JSON, not base64 byte payloads for ordinary message/result text.
7. Session replay/fork does not rely on string bridge accessors.
8. String materialization is explicit and limited to true display or compatibility boundaries.

---

## Immediate Recommendation

Do **not** add more `EffectiveText` call sites.

Also, do **not** start by only renaming the bridge property. The better first move is to attack the leaf sources that still create string-backed `Message` instances.

Recommended first implementation slices:

1. Introduce the immutable owned UTF-8 message text type.
2. Convert `Message` factories to target it.
3. Convert `AgentSession` message creation and `SessionProjection` replay reconstruction so new messages stop storing string text.
4. Convert tool-return helpers so `ToolResult(string)` becomes convenience-only, not the primary storage path.
5. Start provider transport migration with raw-byte SSE ingestion (`ReadAsStreamAsync()` + byte scanning + `Utf8JsonReader`).
6. Then make remaining decode call sites explicit and remove the temporary bridge.

That ordering matches the architecture goal: stop fresh strings from entering `Message`, then remove the temporary bridge fields once the graph is mostly UTF-8-native.
