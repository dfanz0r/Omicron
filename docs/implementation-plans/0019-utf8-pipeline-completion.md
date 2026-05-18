# Implementation Plan 0019: UTF-8 Pipeline Completion

**Date:** 2026-05-17  
**Status:** Proposed  
**Target:** Finish the remaining UTF-8 migration work after `Utf8String` adoption by removing string-first provider, event, replay, and canonical-conversation bridges.
**Depends on:** Plan 0018 (`Utf8String` type redesign)

---

## Purpose

`Message` and the immediate runtime paths are now `Utf8String`-backed, but several surrounding layers still force text back through string-shaped APIs.

This plan completes the migration in the places that still matter structurally:

- provider request serialization
- provider SSE / streaming ingest
- event payloads and persistence
- replay / fork reconstruction
- canonical conversation conversion
- frontend and display-boundary cleanup

The goal is not “never decode to `string` anywhere.” The goal is:

> keep text in UTF-8 form across storage, transport, replay, and serialization layers, and make any remaining string materialization explicit at real compatibility or display boundaries.

---

## Current State

The codebase has already completed the first major part of the migration:

- `Utf8String` exists and has replaced `Utf8OwnedText`
- `Message.TextData` / `ReasoningData` use `Utf8String`
- `AgentSession` stores user / assistant / tool-result text as `Utf8String`
- `SessionProjection` reconstructs `Message` using `Utf8String`
- `Utf8OwnedText` has been removed from code

However, the broader pipeline is still mixed.

### What is still string-first today

#### 1. Provider request bodies

`IApiShape.BuildRequestBody(...)` still returns `JsonObject`, and many shape implementations still call `msg.GetTextString()` while constructing request payloads.

That means text already stored as UTF-8 is decoded back to managed strings before JSON request serialization.

#### 2. Provider transport / streaming ingest

`ShapeBasedProvider` already has a raw-byte SSE path, but provider coverage is incomplete and some provider implementations still have older string-based streaming code paths.

#### 3. Event contracts and persistence

Several event types still carry text as `string`, and `ToolInvocationCompletedEvent` still uses the transitional `ResultBytes` bridge.

That means:

- replay is still fed by string-first event contracts for several message classes
- persistence still depends on reflection-based event serialization for most event text
- `ResultBytes` remains a byte bridge rather than ordinary textual JSON persistence

#### 4. Replay / fork orchestration

Replay and fork paths still materialize strings in places where the runtime model is already UTF-8-native.

#### 5. Canonical conversation conversion

`Conversation` / `ConversationConverter` are still largely string-shaped and can reintroduce string materialization into otherwise UTF-8-native paths.

#### 6. Frontend boundaries

CLI/TUI rendering still decodes to `string` in several places. Some of those are legitimate display boundaries; others are just compatibility leftovers.

---

## Scope

This plan covers the remaining UTF-8 migration work in core/runtime-adjacent layers.

### In scope

- writer-based provider request serialization
- completing raw-byte provider stream ingest
- UTF-8-native event payload redesign
- textual JSON persistence for UTF-8 event/message text
- replay/fork cleanup
- canonical conversation cleanup
- explicit frontend display-boundary decode cleanup

### Out of scope

- replacing `Utf8String` with native-buffer-backed storage in this plan
- transcript slab / arena / mmap architecture work
- large frontend rendering refactors unrelated to text representation

Those longer-term native-memory goals remain important, but this plan focuses on removing the remaining string bridges around the current `Utf8String` model.

---

## Design principles

1. **Keep UTF-8 bytes authoritative for message-like text.**
2. **Serialize directly from UTF-8 wherever practical.**
3. **Decode to `string` only at explicit compatibility or display boundaries.**
4. **Do not preserve transitional dual-state event shapes longer than necessary.**
5. **Event persistence for ordinary text should remain human-readable JSON text, not base64 payloads.**
6. **Provider transport work is part of the UTF-8 migration, not a separate optimization track.**
7. **Canonical conversation and replay paths must stop reintroducing avoidable string materialization.**

---

## Remaining structural problems

### A. `JsonObject` request construction forces stringification

Current provider shapes build object graphs like:

- `JsonObject`
- `JsonArray`
- `JsonNode`

and then serialize them afterward.

That keeps the request path string-centric even though the runtime model is now UTF-8-native.

### B. Event contracts are not aligned with the runtime text model

Current event shapes still distinguish between:

- ordinary string text fields
- a special `ResultBytes` bridge for tool results

That is no longer a good long-term fit now that `Utf8String` is the canonical runtime text type.

### C. Replay still depends on string-shaped event payloads

Even when `Message` is reconstructed as `Utf8String`, replay fidelity is limited by event contracts that still surface text as strings.

### D. Canonical conversation conversion remains string-heavy

That means canonical conversation logic can still become a source of fresh managed strings in otherwise UTF-8-native flows.

---

## Target architecture

The intended end state for this segment is:

```text
provider stream bytes
  ↓
byte-oriented SSE framing / Utf8JsonReader
  ↓
Utf8String-backed events and message state
  ↓
writer-based provider request serialization / textual JSON persistence
  ↓
explicit string decode only at true display or compatibility boundaries
```

For ordinary textual content, the pipeline should stop bouncing between:

```text
UTF-8 bytes -> string -> UTF-8 bytes -> string
```

unless a boundary truly requires it.

---

## Proposed work

## Phase 1: Writer-based provider request serialization

Replace provider request construction APIs that currently return `JsonObject`.

### Current direction to replace

```csharp
JsonObject BuildRequestBody(...)
```

### Target direction

```csharp
void WriteRequestBody(
    Utf8JsonWriter writer,
    Model model,
    IReadOnlyList<Message> messages,
    string? systemPrompt,
    IReadOnlyList<Tool>? tools,
    ChatOptions options)
```

### Required work

- change `IApiShape` from object-graph construction to writer-based emission
- update all shapes in `ApiShape.cs`
- update any provider-specific request builders such as `AnthropicProvider`
- change request sending code to write UTF-8 request bytes directly
- stop routing normal message text through `msg.GetTextString()` when the writer can emit UTF-8 directly

### Notes

- tool argument JSON may still need targeted compatibility handling where it already exists as structured data
- system prompt input may remain string-shaped initially if it is still configured and stored as string elsewhere
- this phase should use UTF-8 literals (`"..."u8`) where it improves clarity and avoids avoidable conversions

---

## Phase 2: Complete provider raw-byte transport ingest

Bring all provider streaming paths onto a consistent byte-oriented model.

### Target state

- request/response streaming uses `ReadAsStreamAsync()`
- SSE framing is scanned from raw bytes
- `ParseSseChunk(...)` consumes raw UTF-8 payload bytes
- `Utf8JsonReader` performs JSON parsing directly from those bytes

### Required work

- finish unifying providers on the shared raw-byte ingest path
- remove remaining `StreamReader` / `ReadLineAsync` SSE code paths used for live model streaming
- keep provider-specific parse logic byte-oriented
- ensure tool-call accumulation still works correctly across partial deltas

### Validation focus

- assistant text deltas
- reasoning deltas
- tool call start / delta / end framing
- final usage / finish reason events
- malformed chunk behavior

---

## Phase 3: Redesign text-carrying event contracts around `Utf8String`

Replace transitional string/byte event splits with one coherent UTF-8-native event model.

### Event types to revisit

At minimum:

- `UserMessageEvent`
- `AssistantTextDeltaEvent`
- `AssistantResponseCompleteEvent`
- `ToolInvocationCompletedEvent`
- any replay-critical text event that still stores message-like text as `string`

### Direction

Use `Utf8String` for ordinary textual event payloads where event persistence and replay need to preserve the runtime model.

Representative direction:

```csharp
public sealed record UserMessageEvent(
    EventEnvelope Envelope,
    Utf8String Text) : OmicronEvent(Envelope);
```

and similarly for assistant delta/complete/result payloads.

### Required work

- remove `ResultBytes` as the ordinary text bridge for tool results
- make tool-result textual payloads use the same canonical event text model as other message text
- preserve null-vs-empty semantics for reasoning fields where relevant
- update emitters and projectors accordingly

### Important constraint

Event shapes should still persist as textual JSON strings for ordinary text. `Utf8String` is the in-memory type; the persisted representation should remain readable JSON text.

---

## Phase 4: Add explicit UTF-8 event JSON serialization / deserialization

Reflection-based event serialization was acceptable while the event model was simpler, but the remaining UTF-8 work should move event text persistence toward explicit control.

### Goals

- persist text as JSON string values, not base64 for ordinary textual content
- preserve `Utf8String` payloads without bouncing through ad hoc bridge fields
- keep event JSON readable and stable

### Required work

- extend `OmicronEventJson` to own text-carrying event serialization rules explicitly
- update `SessionStore` event write/read paths to rely on that explicit event JSON layer rather than opportunistic reflection behavior where necessary
- ensure custom `IContentBlock` serialization and `Utf8String` serialization remain consistent

### Persistence goals

Ordinary tool-result text should look like text in JSONL, not like a base64 payload blob.

---

## Phase 5: Replay and fork cleanup

Once event contracts are UTF-8-native, remove remaining replay/fork string bridges.

### Required work

- update `SessionProjection` to consume the revised event payloads directly
- update `OmicronHost` replay/fork construction paths to stop materializing strings where not needed
- ensure replay preserves explicit empty reasoning when present
- ensure replay/fork behavior stays deterministic across persisted and in-memory sessions

---

## Phase 6: Canonical conversation cleanup

Bring `Conversation` / `ConversationConverter` into line with the UTF-8-native runtime model.

### Required work

- stop using string-only content items for ordinary text where that would reintroduce avoidable allocations
- update conversion logic so `Message -> ConversationTurn -> Message` does not collapse UTF-8-native text back into string-only storage by default
- preserve null-vs-empty reasoning semantics
- ensure tool-result content follows the same canonical text rules

### Staging note

If canonical conversation records are still intentionally string-oriented for some narrower reason, document that boundary explicitly and keep it out of hot runtime paths.

---

## Phase 7: Frontend and display-boundary cleanup

Make remaining decode sites explicit and intentional.

### Required work

- review CLI/TUI text consumption sites
- keep decode-to-string at actual display boundaries where required
- remove compatibility decodes that are only leftovers from older message/event shapes
- prefer UTF-8-native rendering helpers where the renderer can consume bytes directly

### Goal

Frontends may still decode, but only because they are displaying text, not because upstream layers forced them to.

---

## Concrete file targets

### Provider request / transport

- `Omicron.Core/Providers/ApiShape.cs`
- `Omicron.Core/Providers/IChatProvider.cs`
- `Omicron.Core/Providers/AnthropicProvider.cs`
- any provider-specific shape helpers

### Event model / persistence

- `Omicron.Core/Events/OmicronEvent.cs`
- `Omicron.Core/Events/OmicronEventJson.cs`
- `Omicron.Core/Sessions/SessionStore.cs`
- any event projection / event writer helpers

### Replay / runtime conversion

- `Omicron.Core/Sessions/SessionProjection.cs`
- `Omicron.Core/OmicronHost.cs`
- `Omicron.Core/Models/Conversation.cs`

### Frontends

- `Omicron.CLI/Program.cs`
- `Omicron.CLI/Tui/TranscriptViewportWidget.cs`
- related display helpers

---

## Testing plan

### Provider tests

Add/expand tests for:

- byte-oriented SSE parsing across all supported provider shapes
- request serialization equivalence after moving from `JsonObject` to `Utf8JsonWriter`
- reasoning/null/empty handling
- tool-call accumulation and completion behavior

### Event/persistence tests

Add/expand tests for:

- event JSONL round-trip with `Utf8String` payloads
- persisted tool-result text remaining textual JSON
- replay preserving UTF-8 text and reasoning semantics
- fork/replay determinism

### Canonical conversation tests

Add/expand tests for:

- `Message -> ConversationTurn -> Message` round-trips
- explicit empty reasoning preservation
- tool-result text preservation

### Frontend tests

Add/expand tests where practical for:

- transcript projection / rendering from UTF-8-native messages
- display-boundary decode behavior

---

## Acceptance criteria

1. Provider request bodies are emitted through `Utf8JsonWriter`, not `JsonObject` as the primary API.
2. Live provider streaming ingest is byte-oriented across supported providers.
3. Text-carrying event contracts no longer rely on `ResultBytes` as a bridge for ordinary tool-result text.
4. Event persistence stores ordinary textual payloads as textual JSON strings, not base64 byte fields.
5. Replay/fork paths no longer depend on string-first message/event bridges.
6. Canonical conversation conversion does not reintroduce avoidable string-backed message storage.
7. Remaining string materialization sites are explicit compatibility or display boundaries.
8. Build/test coverage confirms null-vs-empty reasoning semantics remain correct.

---

## Transitional note / TODO

This plan intentionally targets completion of the UTF-8 pipeline around the current `Utf8String` model.

That model is still part of a broader transition. Long term, Omicron still intends to move dominant high-volume text storage toward native manually managed memory regions. The work in this plan should therefore improve byte-oriented boundaries and explicit ownership now without baking new long-lived assumptions around managed-array-backed text into provider, persistence, or replay APIs.

---

## Recommended execution order

1. writer-based provider request serialization
2. complete provider raw-byte ingest
3. redesign text-carrying event contracts
4. explicit event JSON serialization/persistence
5. replay/fork cleanup
6. canonical conversation cleanup
7. frontend display-boundary cleanup

This order finishes the biggest remaining UTF-8 fan-out points first: provider send, provider receive, and event persistence.
