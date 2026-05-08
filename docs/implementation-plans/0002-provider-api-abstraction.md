# Implementation Plan 0002: Provider API Abstraction and Stateful Responses Support

Status: Draft  
Inputs: `docs/reports/stateful-vs-stateless-api-abstraction.md`, RFC 0001, RFC 0009, Implementation Plan 0001

## Purpose

Omicron must support both stateless transcript-resend APIs and stateful provider-managed APIs such as OpenAI Responses. The current MVP already has a useful `Model.ApiType` concept, but `OpenAiResponsesShape` is only a Chat Completions fallback and does not yet model Responses API state, typed output items, or function-call output items.

This plan defines the provider architecture needed before implementing first-class Responses support.

Plan 0001 prerequisite: the core groundwork plan should already introduce minimal provider-state seams (`ProviderStateKey`, `ProviderTurnState`, `IProviderConversationStateStore`) and keep provider state out of the CLI. This plan extends those seams; it should not reintroduce a separate provider-state abstraction.

## Architectural Decision

Omicron should **support both stateless and stateful APIs with per-model routing**, not commit to Responses-only.

Rationale:

- Omicron wants broad provider/local-model coverage.
- Many providers remain Chat Completions-compatible only.
- OpenAI, newer GPT models, reasoning APIs, and some future providers will increasingly prefer Responses-style stateful/typed item APIs.
- The current `Model.ApiType` already aligns with model-level classification.
- Cross-provider handoff and session replay are important future capabilities.

This follows the pi-mono/opencode/pi-opencode-provider pattern more than the Codex Responses-only pattern.

## Key Lessons from the Report

### 1. Use a Canonical Internal Conversation Format

Provider wire formats should not become Omicron's core conversation model. Core should store a canonical representation that can convert to:

- OpenAI Chat Completions;
- OpenAI Responses;
- Anthropic Messages;
- Google Generative AI;
- future provider-specific APIs.

Current `Message` is a good MVP seed but is too flat for long-term support. It should evolve toward content-item messages:

```csharp
public sealed record ConversationTurn(
    MessageId Id,
    MessageRole Role,
    IReadOnlyList<ConversationContent> Content,
    ProviderOrigin? Origin,
    DateTimeOffset Timestamp);

public abstract record ConversationContent;
public sealed record TextContentItem(string Text) : ConversationContent;
public sealed record ImageContentItem(string Data, string MimeType) : ConversationContent;
public sealed record ReasoningContentItem(string? Summary, string? EncryptedContent, JsonElement? ProviderMetadata) : ConversationContent;
public sealed record ToolCallContentItem(string Id, string Name, JsonElement Arguments, JsonElement? ProviderMetadata) : ConversationContent;
public sealed record ToolResultContentItem(string ToolCallId, string ToolName, string Text, bool IsError) : ConversationContent;

public sealed record ProviderOrigin(
    string ProviderName,
    ApiType ApiType,
    string ModelId,
    JsonElement? ProviderMetadata);
```

MVP compatibility path:

- keep existing `Message` for now;
- add conversion helpers between `Message` and the richer canonical representation;
- avoid expanding provider-specific fields directly on `Message` unless needed for bridging.

### 2. Classify Models, Not Providers

The selected wire API should be a property of the model entry.

Current Omicron already has:

```csharp
public ApiType ApiType { get; init; }
```

This should remain central. Model discovery should classify each model into the correct API family:

- `OpenAiChat`
- `OpenAiResponses`
- `AnthropicMessages`
- `GoogleGenAi`
- future API families

Provider factories should not assume one provider has one API. OpenCode is the immediate example: GPT-like models may use Responses/Chat, Claude models use Anthropic Messages, Gemini models use Google APIs.

### 3. Make Provider Compatibility Explicit

Introduce model/provider compatibility descriptors instead of burying quirks in if/else chains.

Candidate shape:

```csharp
public sealed record ProviderCompatibility
{
    public bool SupportsStore { get; init; }
    public bool SupportsStreamingUsage { get; init; }
    public bool SupportsReasoningEffort { get; init; }
    public bool SupportsImages { get; init; }
    public bool SupportsStrictTools { get; init; } = true;
    public bool RequiresToolResultName { get; init; }
    public bool RequiresAssistantAfterToolResult { get; init; }
    public bool RequiresReasoningContentOnAssistantMessages { get; init; }
    public bool RejectsEmptyContent { get; init; }
    public ToolCallIdFormat ToolCallIdFormat { get; init; } = ToolCallIdFormat.Default;
    public MaxTokensField MaxTokensField { get; init; } = MaxTokensField.MaxTokens;
    public ThinkingFormat ThinkingFormat { get; init; } = ThinkingFormat.None;
    public CacheControlFormat CacheControlFormat { get; init; } = CacheControlFormat.None;
    public bool SendSessionAffinityHeaders { get; init; }
    public bool SupportsLongCacheRetention { get; init; }
}
```

Initial descriptors can be small. Add fields only when tests or providers require them.

### 4. Provider Turn State Must Be First-Class

Stateful APIs need provider-managed continuation state, but Omicron must still own the logical session.

The minimal shape from Plan 0001 should be retained and expanded rather than replaced:

```csharp
public readonly record struct ProviderStateKey(
    SessionId SessionId,
    AgentId AgentId,
    string ProviderName,
    string ModelId,
    ApiType ApiType);

public sealed record ProviderTurnState(
    ProviderStateKey Key,
    string? PreviousResponseId,
    string? ConversationId,
    string? SessionAffinityKey,
    ProviderStoragePolicy StoragePolicy,
    JsonElement? ProviderMetadata);
```

If Plan 0001 initially ships a smaller `ProviderTurnState`, Plan 0002 should evolve it in place through additive fields/migration helpers.

Rules:

- provider state belongs to a specific `ProviderStateKey` scoped by session/agent/provider/model/API type;
- reset clears provider continuation;
- fork either clears provider continuation or records an explicit fork policy;
- persistence later stores provider state in session snapshots/events;
- stateless mode can always rebuild from canonical local history.

### 5. OpenAI Responses Should Have Two Modes

Omicron should support:

#### Stateful mode

- send only new input items when possible;
- include `previous_response_id`;
- store completed response ID as provider turn state;
- optionally send session/cache headers when configured.

#### Stateless fallback mode

- build full Responses `input[]` from local canonical history;
- omit `previous_response_id`;
- used for retries, provider-state loss, privacy settings, cross-provider replay, or provider incompatibility.

This mirrors Dirac's full-context retry behavior and avoids hard dependency on server-side state.

### 6. Default Server Storage Policy

Omicron should default to privacy-conscious behavior:

```text
store: false where supported
```

But this must be configurable per provider/model because some Responses-compatible providers may require or expect server-side storage for `previous_response_id` semantics.

Policy should be explicit in model/provider config:

```csharp
public enum ProviderStoragePolicy
{
    PreferStateless,
    AllowProviderStateNoStore,
    AllowProviderStoredState
}
```

Initial default: `AllowProviderStateNoStore` for OpenAI Responses if it works with the target API behavior; otherwise fail clearly and allow opt-in.

### 7. Tool Call ID Normalization Is Required

Cross-API replay requires stable logical tool IDs plus provider-safe wire IDs.

Add a utility:

```csharp
public interface IToolCallIdMapper
{
    string ToWireId(string logicalId, ApiType targetApi, ProviderCompatibility compat);
    string ToLogicalId(string wireId, ApiType sourceApi, ProviderCompatibility compat);
}
```

Initial implementation:

- sanitize invalid characters;
- truncate for providers with length limits;
- preserve mappings in provider metadata when lossy;
- handle Responses `call_id` vs item ID distinction.

### 8. Transformers Should Be Separated from HTTP Providers

Do not put all conversion logic inside provider HTTP classes.

Target layering:

```text
Canonical conversation/session state
  ↓
Provider compatibility transforms
  ↓
ApiShape request builder/parser
  ↓
HTTP/SSE/WebSocket transport
```

Suggested interfaces:

```csharp
public interface IConversationTransformer
{
    IReadOnlyList<ConversationTurn> Transform(
        IReadOnlyList<ConversationTurn> turns,
        Model model,
        ProviderCompatibility compatibility);
}

public interface IApiShape
{
    ApiType ApiType { get; }
    ProviderRequest BuildRequest(ProviderRequestContext context);
    IStreamEventParser CreateStreamParser(ProviderRequestContext context);
}
```

The current `IApiShape.BuildRequestBody` / `ParseSseChunk` can evolve in this direction without an immediate rewrite.

## Implementation Phases

### Phase A: Validate Plan 0001 Provider Seams

Deliverables:

- verify `ProviderStateKey`, `ProviderTurnState`, and `IProviderConversationStateStore` exist from Plan 0001;
- verify provider state is not owned or manipulated by CLI code;
- verify model catalog/registry exposes per-model `ApiType`;
- verify provider invocation can receive a context object capable of carrying provider state.

Acceptance criteria:

- no separate/duplicate provider-state abstraction is introduced in Plan 0002;
- reset clears provider state through the shared store;
- real Responses work can proceed without another CLI orchestration rewrite.

### Phase B: Provider Metadata and Compatibility

Deliverables:

- add `ProviderCompatibility` minimal record;
- add `ProviderStoragePolicy`;
- extend `Model` or add sidecar `ModelCapabilities`/`ModelRuntimeInfo` for compatibility overrides;
- add compatibility detector service based on provider name/base URL/model ID;
- tests for classification and compatibility defaults.

Acceptance criteria:

- model discovery can mark OpenAI/OpenCode/OpenRouter models with intended API family and compatibility;
- no behavior regression in existing Chat/Anthropic paths.

### Phase C: Provider Turn State Expansion

Deliverables:

- extend Plan 0001 `ProviderTurnState` with storage policy/session affinity fields if not already present;
- add provider-state update/clear events;
- add debug/audit projection for provider state changes;
- define fork semantics: clear by default, explicitly continue only by policy.

Acceptance criteria:

- tests prove state is scoped by `ProviderStateKey` including `ApiType`;
- reset/fork semantics are explicit even before persistence;
- provider state changes are observable through core events.

### Phase D: Canonical Conversation Seed

Deliverables:

- richer internal content item records or an adapter layer around current `Message`;
- conversion from existing `Message` list to canonical turns;
- provider origin metadata on assistant turns where practical;
- tool call ID mapper utility.

Acceptance criteria:

- current provider shapes can still build requests from existing messages;
- tests cover tool-call ID normalization for Chat, Responses, and Anthropic constraints.

### Phase E: Real OpenAI Responses Shape

Deliverables:

- endpoint routing to `/v1/responses`;
- request builder using `model`, `instructions`, `input`, `tools`, `stream`, generation options, storage/cache settings;
- Responses SSE parser for typed events;
- function-call argument accumulation;
- parser emits Omicron `StreamEvent`s for text, tool call start/delta/end, done, error, usage if available;
- completed response ID captured and stored as `ProviderTurnState.PreviousResponseId`.

Acceptance criteria:

- text-only Responses streaming fixture passes;
- function-call Responses streaming fixture passes;
- stateful second-turn request includes `previous_response_id`;
- stateless fallback request omits `previous_response_id` and includes reconstructed input.

### Phase F: Retry and Fallback Policy

Deliverables:

- provider errors can indicate stateful continuation failure;
- retry option to rebuild full input without `previous_response_id`;
- clear audit/debug event when fallback happens.

Acceptance criteria:

- if a stored response expires or provider rejects `previous_response_id`, Omicron can retry statelessly when policy allows;
- user/debug output identifies the fallback.

### Phase G: Provider Configuration Surface

Deliverables:

- config options for storage policy, stateful/stateless preference, cache key/session affinity if needed;
- model override mechanism for API family and compatibility quirks;
- debug command or log output showing provider/model/API selection.

Acceptance criteria:

- users/developers can override misclassified model API family without code changes;
- tests cover config override precedence.

## Recommended Defaults

Initial defaults:

```text
Provider/model routing:
  use discovered ApiType where known
  OpenAI GPT/new reasoning models: OpenAiResponses
  generic OpenAI-compatible: OpenAiChat unless configured otherwise
  Anthropic: AnthropicMessages
  Gemini: GoogleGenAi once implemented

Storage:
  prefer store: false where accepted
  allow provider turn state via previous_response_id only when configured/supported

Fallback:
  if stateful continuation fails, retry stateless full-context once when safe

Debuggability:
  log selected provider, model, ApiType, stateful/stateless mode, storage policy, and response ID updates
```

## Risks

| Risk | Mitigation |
| --- | --- |
| Canonical format rewrite becomes too large | Start with adapters around existing `Message`; migrate incrementally. |
| Responses API behavior differs between OpenAI and compatible providers | Use compatibility descriptors and storage policy flags. |
| Tool call ID normalization loses correlation | Preserve logical↔wire mapping in provider metadata/events. |
| Server-side state conflicts with local replay/forking | Make provider state explicit and clear it on reset/fork unless explicitly continued. |
| Privacy concerns around `store: true` | Default to `store: false` where possible and expose policy clearly. |
| Too much provider-specific logic in `ApiShape` | Split transforms, shape, and transport over time. |

## Definition of Done

This provider abstraction phase is complete when:

- models are classified by API family at model-discovery/config time;
- provider compatibility is explicit and test-covered;
- Plan 0001 provider turn state has been expanded in place and remains session-scoped;
- OpenAI Responses has a real request builder and stream parser;
- Responses function calling works through the same tool pipeline as Chat Completions;
- stateful and stateless fallback modes are both tested;
- reset/fork behavior does not accidentally continue stale server-side state;
- the architecture can add future stateful APIs without changing the CLI.
