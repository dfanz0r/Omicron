# Stateful vs Stateless API Provider Abstraction

**A cross-project analysis of how coding-agent ecosystems handle the tension between the OpenAI Responses API (stateful) and Chat Completions API (stateless)**

---

## Table of Contents

1. [The Core Tension](#the-core-tension)
2. [Project Survey](#project-survey)
   - [1. pi-mono — Canonical Internal Format + Per-API Providers](#1-pi-mono)
   - [2. codex (OpenAI Codex CLI) — Responses-API-Only (Chat Removed)](#2-codex-openai-codex-cli)
   - [3. opencode — Vercel AI SDK Provider Chain](#3-opencode)
   - [4. dirac — Per-Provider Handler Classes](#4-dirac)
   - [5. pi-opencode-provider — Model Discovery → API Routing](#5-pi-opencode-provider)
3. [Patterns and Tradeoffs](#patterns-and-tradeoffs)
4. [Key Design Decisions](#key-design-decisions)
5. [The Compatibility Matrix](#the-compatibility-matrix)
6. [Recommendations](#recommendations)

---

## The Core Tension

Modern LLM coding agents must talk to dozens of model providers, each exposing one or more wire protocols. The two dominant protocols from OpenAI illustrate a fundamental architectural choice:

| | Chat Completions API | Responses API |
|---|---|---|
| **State model** | **Stateless** — client sends the full conversation history (`messages[]`) on every request | **Stateful** — server maintains conversation context; client sends only new `input[]` items and optionally `previous_response_id` |
| **Message format** | Flat `[{role, content}, ...]` array | Heterogeneous items: `{type: "message" | "reasoning" | "function_call" | "function_call_output", ...}` |
| **System prompt** | `messages` array element with `role: "system"` | Top-level `instructions` field |
| **Tool calls** | Nested in assistant messages as `tool_calls[]` | Top-level items of type `function_call` |
| **Tool results** | Separate `role: "tool"` messages | Items of type `function_call_output` |
| **Caching** | `prompt_cache_key` parameter + cache-control markers in messages | `prompt_cache_key` + `prompt_cache_retention` + server-side session management |
| **Streaming** | SSE (Server-Sent Events) with chunk deltas | SSE with structured event types (`response.created`, `response.output_item.added`, `response.output_text.delta`, `response.completed`, etc.) |
| **Reasoning** | `reasoning_effort` parameter, reasoning appears in `reasoning_content` on message deltas | First-class `reasoning` items with `summary[]` parts, `encrypted_content`, and `reasoning_text.delta` events |
| **Transport** | HTTP POST with SSE streaming | HTTP POST with SSE streaming, or WebSocket with `response.create` framing |
| **Session continuity** | None — each request is independent | `previous_response_id`, `session_id` header, `x-session-affinity` for sticky routing |
| **Store** | N/A | `store: false` to disable server-side storage |

The fundamental question every project faces: **do you standardize on one protocol, support both and pick at runtime, or abstract the difference away?**

---

## Project Survey

### 1. pi-mono

**Location:** `READ_ONLY/pi-mono/packages/ai/`

**Approach:** Canonical internal message format → per-API provider implementations → pipeline of transforms

#### Architecture

```
                    ┌─────────────────────────┐
                    │  Canonical Message[]     │
                    │  UserMessage            │
                    │  | AssistantMessage     │
                    │  | ToolResultMessage    │
                    └──────────┬──────────────┘
                               │
                    ┌──────────▼──────────────┐
                    │  transformMessages()     │
                    │  - Cross-provider ID     │
                    │    normalization         │
                    │  - Image downgrade       │
                    │  - Thinking handling     │
                    │  - Orphan tool fixup     │
                    └──────────┬──────────────┘
                               │
              ┌────────────────┼────────────────┐
              ▼                ▼                 ▼
    ┌─────────────────┐ ┌──────────────┐ ┌──────────────┐
    │ openai-          │ │ openai-      │ │ anthropic    │
    │ completions.ts   │ │ responses.ts │ │ .ts          │
    │ (stateless)      │ │ (stateful)   │ │              │
    └─────────────────┘ └──────────────┘ └──────────────┘
```

#### Canonical Message Types (`types.ts`)

```typescript
type Message = UserMessage | AssistantMessage | ToolResultMessage

interface UserMessage {
  role: "user"
  content: string | (TextContent | ImageContent)[]
  timestamp: number
}

interface AssistantMessage {
  role: "assistant"
  content: (TextContent | ThinkingContent | ToolCall)[]
  api: Api                    // e.g. "openai-completions" or "openai-responses"
  provider: Provider
  model: string
  usage: Usage
  stopReason: StopReason
  timestamp: number
}

interface ToolResultMessage {
  role: "toolResult"
  toolCallId: string
  toolName: string
  content: (TextContent | ImageContent)[]
  isError: boolean
}
```

Every message carries **metadata about its origin** (`api`, `provider`, `model`). This enables the system to replay messages from one provider/API against another — the transformation layer uses this metadata to decide what to drop, normalize, or convert.

#### The Compatibility Interface

pi-mono uses explicit **compatibility descriptors** that describe which features a provider supports:

```typescript
interface OpenAICompletionsCompat {
  supportsStore?: boolean        // Can the provider handle `store: false`?
  supportsDeveloperRole?: boolean // Does it understand the `developer` role?
  supportsReasoningEffort?: boolean
  supportsUsageInStreaming?: boolean
  maxTokensField?: "max_completion_tokens" | "max_tokens"
  requiresToolResultName?: boolean
  requiresAssistantAfterToolResult?: boolean  // Anthropic via proxy needs this
  requiresThinkingAsText?: boolean   // Convert thinking blocks to plain text
  requiresReasoningContentOnAssistantMessages?: boolean  // DeepSeek needs empty reasoning
  thinkingFormat?: "openai" | "openrouter" | "deepseek" | "zai" | "qwen" | "qwen-chat-template"
  cacheControlFormat?: "anthropic"   // Anthropic-style cache_control markers
  sendSessionAffinityHeaders?: boolean
  supportsLongCacheRetention?: boolean
  // ... and more
}
```

These are **auto-detected** from `baseUrl` / `provider` via `detectCompat()`, then overridable via `model.compat`. A model like DeepSeek gets `thinkingFormat: "deepseek"` and `requiresReasoningContentOnAssistantMessages: true` automatically.

#### Session/Caching Difference

**Completions (stateless):**
```typescript
// Caching via Anthropic-style cache_control markers on message content
function applyAnthropicCacheControl(messages, tools, cacheControl) {
  addCacheControlToSystemPrompt(messages, cacheControl)
  addCacheControlToLastTool(tools, cacheControl)
  addCacheControlToLastConversationMessage(messages, cacheControl)
}
```
Sends full conversation every request; caching is a hint on specific content blocks.

**Responses (stateful):**
```typescript
// Caching via server-managed prompt_cache_key + session affinity headers
headers.session_id = sessionId
headers["x-client-request-id"] = sessionId
headers["x-session-affinity"] = sessionId
params.prompt_cache_key = sessionId        // ties cache to a session
params.prompt_cache_retention = "24h"      // or undefined for short-lived
```
The server manages the cache; client only passes identifiers. Server can reuse cached prefixes across turns.

#### Message Conversion: The Core Difference

**To Completions format** (`convertMessages()` in `openai-completions.ts`):
```
System prompt → { role: "system", content: "..." }
User message  → { role: "user", content: [...] }
Assistant     → { role: "assistant", content: "...", tool_calls: [...] }
Tool result   → { role: "tool", content: "...", tool_call_id: "..." }
```
Flat array, system prompt is just another message, thinking blocks are collapsed into regular text (with signatures preserved for llama.cpp compatibility), tool calls are nested inside assistant messages.

**To Responses format** (`convertResponsesMessages()` in `openai-responses-shared.ts`):
```
System prompt → { role: "developer" | "system", content: "..." }    (input item)
User          → { role: "user", content: [{ type: "input_text", text: "..." }] }
Assistant     → [
                   { type: "reasoning", ... },                       (if thinking)
                   { type: "message", role: "assistant", content: [...] },
                   { type: "function_call", call_id: "...", id: "fc_...", ... },
                ]
Tool result   → { type: "function_call_output", call_id: "...", output: "..." }
```
Heterogeneous items array, reasoning items are first-class citizens, tool calls are independent top-level items, message IDs are preserved for text block identity.

#### Tool Call ID Normalization

A critical cross-API concern: tool call IDs differ across APIs.

- OpenAI Responses API generates IDs like `call_abc|fc_1234567890abcdef...` (450+ chars with `|`, `+`, `/`, `=`)
- OpenAI Chat Completions uses simple `call_abc` (up to 40 chars, alphanumeric only)
- Anthropic requires `^[a-zA-Z0-9_-]+$` (max 64 chars)

pi-mono normalizes these via `normalizeToolCallId()`:
```typescript
// For completions: split on "|", take call_id part, sanitize, truncate to 40
// For responses:  split on "|", normalize call_id, build fc_ prefixed item id,
//                 ensure "fc_" prefix for Responses API requirement
```

When replaying from a Responses model to a Completions model (cross-provider handoff), `transformMessages()` strips thinking signatures, normalizes tool call IDs, and inserts synthetic empty tool results for orphaned calls.

#### Key Insight

pi-mono's approach is **treat the canonical message format as the single source of truth** and convert at the edges. The compatibility descriptors make the differences between providers explicit and configurable rather than buried in if-else chains.

---

### 2. codex (OpenAI Codex CLI)

**Location:** `READ_ONLY/codex/codex-rs/`

**Approach:** Responses-API-only (Chat Completions support removed entirely)

#### The Wire API Enum — Only One Variant

```rust
// model-provider-info/src/lib.rs
pub enum WireApi {
    #[default]
    Responses,
}
```

Previously had a `Chat` variant. It was removed with an explicit error message:
```
`wire_api = "chat"` is no longer supported.
How to fix: set `wire_api = "responses"` in your provider config.
```

This is the **most opinionated approach**: force all providers to either speak Responses natively or use an adapter.

#### Provider Architecture

```
┌──────────────────────────────────────────┐
│  ModelClient (per-session)               │
│  - session_id, thread_id                 │
│  - SharedModelProvider                   │
│  - cached_websocket_session              │
│  - disable_websockets flag               │
│  - turn_state token                      │
└──────────────┬───────────────────────────┘
               │
┌──────────────▼───────────────────────────┐
│  ModelProvider trait                     │
│  - info() → ModelProviderInfo            │
│  - auth() → CodexAuth                    │
│  - api_provider() → ApiProvider          │
│  - capabilities() → ProviderCapabilities │
└──────────────┬───────────────────────────┘
               │
     ┌─────────┼─────────────┐
     ▼         ▼             ▼
  OpenAI    Bedrock      OSS (Ollama,
  (native)  (adapter)    LM Studio)
```

#### Provider Implementations

1. **Default `ConfiguredModelProvider`** — Takes `ModelProviderInfo` (base URL, headers, auth config, retry settings) and constructs a Responses API client. Works for any OpenAI-Compatible Responses endpoint.

2. **`AmazonBedrockModelProvider`** — A specialized provider that adapts AWS Bedrock's Converse API to look like Responses. It uses AWS SigV4 signing, manages cross-region inference profiles, and handles Bedrock-specific auth credential providers. This is the **only adapter** in the codebase — everything else speaks Responses natively.

3. **OSS providers (Ollama, LM Studio)** — Configured with `wire_api: WireApi::Responses`. They point at local servers that expose an OpenAI-compatible Responses endpoint.

#### Session and State Management

Codex's `ModelClient` is session-scoped:
```rust
struct ModelClientState {
    session_id: SessionId,
    thread_id: ThreadId,
    provider: SharedModelProvider,
    cached_websocket_session: StdMutex<WebsocketSession>,
    disable_websockets: AtomicBool,
    // ...
}
```

Per-turn streaming uses `ModelClientSession` which:
- Maintains a WebSocket connection (opened lazily)
- Stores `x-codex-turn-state` for sticky routing
- Supports WebSocket prewarm (`response.create` with `generate=false`)
- Falls back to SSE if WebSocket fails

#### Conversation Input Format

The `Prompt` struct in `client_common.rs` uses `Vec<ResponseItem>` — Responses API-native items:
```rust
pub struct Prompt {
    pub input: Vec<ResponseItem>,  // Responses-API format
    pub tools: Vec<ToolSpec>,
    pub base_instructions: BaseInstructions,
    pub personality: Option<Personality>,
    pub output_schema: Option<Value>,
}
```

There is no message transformation layer because everything speaks Responses. The input is already in the correct format.

#### Key Insight

By **committing to one API**, Codex eliminates the complexity of format conversion, compatibility matrices, and cross-API handoff. The tradeoff is that third-party providers must implement a Responses-compatible endpoint. Bedrock gets a dedicated adapter; OSS providers are expected to expose Responses via local servers. This is an opinionated bet on the Responses API as the future standard.

---

### 3. opencode

**Location:** `READ_ONLY/opencode/`

**Approach:** Vercel AI SDK v3 provider chain with explicit Chat vs Responses routing

#### Architecture

OpenCode uses the **[Vercel AI SDK](https://sdk.vercel.ai/)** as its provider abstraction. Each provider is loaded via npm package and produces a `LanguageModelV3`:

```typescript
const BUNDLED_PROVIDERS = {
  "@ai-sdk/openai":           () => import("@ai-sdk/openai").then(m => m.createOpenAI),
  "@ai-sdk/anthropic":        () => import("@ai-sdk/anthropic").then(m => m.createAnthropic),
  "@ai-sdk/google":           () => import("@ai-sdk/google").then(m => m.createGoogleGenerativeAI),
  "@ai-sdk/openai-compatible": () => import("@ai-sdk/openai-compatible").then(m => m.createOpenAICompatible),
  // ... 20+ providers
}
```

#### The Dual-Model Pattern

OpenCode's custom `createOpenaiCompatible()` factory exposes **both** APIs from the same provider:

```typescript
interface OpenaiCompatibleProvider {
  (modelId: string): LanguageModelV3           // default → chat
  chat(modelId: string): LanguageModelV3       // stateless
  responses(modelId: string): LanguageModelV3  // stateful
  languageModel(modelId: string): LanguageModelV3
}
```

Each returns a different `LanguageModelV3` implementation:
- `chat()` → `OpenAICompatibleChatLanguageModel` (→ `/v1/chat/completions`)
- `responses()` → `OpenAIResponsesLanguageModel` (→ `/v1/responses`)

#### Routing Logic

Which API gets used for which model is decided at model-load time:

```typescript
// OpenAI: always Responses
"openai": () => Effect.succeed({
  async getModel(sdk, modelID) {
    return sdk.responses(modelID)  // ← always Responses
  }
}),

// GitHub Copilot: heuristic based on model version
"github-copilot": () => Effect.succeed({
  async getModel(sdk, modelID) {
    if (shouldUseCopilotResponsesApi(modelID))  // gpt-5+ (not mini)
      return sdk.responses(modelID)
    else
      return sdk.chat(modelID)
  }
}),

// Azure: separate chat/responses check
"azure": () => {
  async getModel(sdk, modelID, options) {
    // might use chat or responses depending on useCompletionUrls
    return selectAzureLanguageModel(sdk, modelID, options?.useCompletionUrls)
  }
},

// xAI: always Responses (for reasoning models)
"xai": () => Effect.succeed({
  async getModel(sdk, modelID) {
    return sdk.responses(modelID)
  }
})
```

The heuristic for Copilot:
```typescript
function shouldUseCopilotResponsesApi(modelID: string): boolean {
  const match = /^gpt-(\d+)/.exec(modelID)
  if (!match) return false
  return Number(match[1]) >= 5 && !modelID.startsWith("gpt-5-mini")
}
```

#### Message Transformation (`transform.ts`)

This file handles **provider-specific normalization** applied to messages before they reach the AI SDK:

1. **Cache control**: Adds `cache_control: { type: "ephemeral" }` to last content block in system prompts, last assistant messages, and last user messages — but **only for Anthropic, Bedrock, OpenRouter, Alibaba**

2. **Tool call ID scrubbing**: Different providers have different constraints:
   - Anthropic: `replace(/[^a-zA-Z0-9_-]/g, "_")` — strict alphanumeric + underscore + hyphen
   - Mistral: `replace(/[^a-zA-Z0-9]/g, "").slice(0, 9).padEnd(9, "0")` — exactly 9 alphanumeric chars
   - Claude: Only scrub if model ID contains "claude"

3. **Empty content filtering**: Anthropic and Bedrock reject messages with empty string content — filtered out

4. **Tool call ordering**: Anthropic rejects assistant turns where `tool_use` blocks are followed by non-tool content — reorder to `[text] + [tool_use, tool_use]`

5. **Mistral message bridging**: Tool messages cannot be followed by user messages — insert a synthetic assistant `"Done."` message

6. **DeepSeek reasoning**: All assistant messages require an empty `reasoning` part — adds `{ type: "reasoning", text: "" }` if missing

7. **Interleaved reasoning**: For models supporting `reasoning_content` / `reasoning_details`, strips reasoning parts from content array and puts them into `providerOptions.openaiCompatible[field]`

8. **Provider option key remapping**: The AI SDK expects `providerOptions` under specific keys (e.g., `copilot`, `anthropic`, `openai`). If the provider ID differs from the SDK key, options are remapped.

#### Reasoning/Thinking Variants

The `variants()` function maps provider-specific reasoning effort schemes:

| Provider | Effort Control |
|---|---|
| OpenAI | `reasoningEffort: "low" | "medium" | "high" | "xhigh"` |
| Anthropic | `thinking: { type: "enabled", budgetTokens: 16000 }` or `thinking: { type: "adaptive" }, effort: "high"` |
| Google Gemini 2.5 | `thinkingConfig: { includeThoughts: true, thinkingBudget: 16000 }` |
| Google Gemini 3 | `thinkingConfig: { includeThoughts: true, thinkingLevel: "high" }` |
| Bedrock (Anthropic) | `reasoningConfig: { type: "enabled", budgetTokens: 16000 }` |
| Bedrock (Nova) | `reasoningConfig: { type: "enabled", maxReasoningEffort: "high" }` |
| OpenRouter | `reasoning: { effort: "high" }` |
| DeepSeek | `reasoningEffort` via `openaiCompatible` options |
| xAI/Grok | `reasoningEffort: "low" | "high"` (only on Grok 3 Mini) |

#### Caching Session

In `options()`:
```typescript
// OpenAI: session-based caching
if (model.providerID === "openai" || providerOptions?.setCacheKey) {
  result["promptCacheKey"] = sessionID
}

// Azure: session-based caching  
result["promptCacheKey"] = sessionID

// OpenRouter: uses different field name
result["prompt_cache_key"] = sessionID
```

#### Key Insight

OpenCode treats the Chat/Responses distinction as **a property of the model, not the provider**. Each model is tagged with which API it should use. The AI SDK handles the wire protocol; OpenCode focuses on message normalization and provider-specific reasoning/caching configuration. The approach recognizes that some models work better (or only) with one API — no single API wins.

---

### 4. dirac

**Location:** `READ_ONLY/dirac/`

**Approach:** Per-provider handler classes with explicit Chat vs Responses subclasses

#### Architecture

```
src/core/api/
  index.ts              ← ApiHandler interface + factory
  adapters/             ← Tool call message transformation
  providers/
    openai.ts           ← Chat Completions (OpenAI/Azure compatible)
    openai-native.ts    ← Responses API (native OpenAI, with WebSocket)
    openai-responses-compatible.ts  ← Responses API (third-party)
    openai-codex.ts     ← Codex-specific
    anthropic.ts
    gemini.ts
    bedrock.ts
    vertex.ts
    ⋮ (30+ providers)
  transform/
    openai-format.ts    ← Messages → Chat Completions format
    openai-response-format.ts  ← Messages → Responses format
    anthropic-format.ts
    gemini-format.ts
    mistral-format.ts
    r1-format.ts        ← DeepSeek R1-specific
    o1-format.ts        ← o1-specific
    openrouter-stream.ts
```

#### The Handler Interface

```typescript
interface ApiHandler {
  createMessage(
    systemPrompt: string,
    messages: DiracStorageMessage[],
    tools?: DiracTool[],
    useResponseApi?: boolean
  ): ApiStream

  getModel(): ApiHandlerModel
}

interface ApiHandlerModel {
  id: string
  info: ModelInfo
}
```

#### Three OpenAI Flavors

Dirac has **three separate handlers** for OpenAI-compatible APIs:

| Handler | Wire Protocol | Use Case |
|---|---|---|
| `OpenAiHandler` | Chat Completions (`/v1/chat/completions`) | Generic OpenAI-compatible, Azure, local models, OpenRouter |
| `OpenAiNativeHandler` | Responses API (`/v1/responses`) | Native OpenAI with WebSocket transport, `store: true` |
| `OpenAiResponsesCompatibleHandler` | Responses API (`/v1/responses`) | Third-party providers with Responses endpoints |

**`OpenAiHandler` (Chat Completions):**
- Uses `client.chat.completions.create({ stream: true, ... })`
- Converts via `convertToOpenAiMessages()` — flat role/content/tool_calls array
- Supports Azure via `AzureOpenAI` SDK, identity auth, api-version
- Applies R1-format conversion for DeepSeek models
- Uses `reasoning_effort` parameter for think control

**`OpenAiNativeHandler` (Native Responses):**
- Uses `client.responses.create({ stream: true, ... })`
- Converts via `convertToOpenAIResponsesInput()`
- Optionally uses `ResponsesWebsocketManager` for WebSocket transport
- Sets `store: true` (vs `store: false` in pi-mono/openCode)
- Uses `previous_response_id` for turn continuity
- Handles WebSocket reconnection, `generate=false` prewarm

**`OpenAiResponsesCompatibleHandler` (Third-party Responses):**
- Same Responses API calls but for non-OpenAI providers
- Also sets `store: true`
- Uses `previous_response_id` for continuity
- Calls `shouldRetryWithFullContext()` on failures — sends full input without `previous_response_id` as a fallback

#### The Format Transformers

**`openai-format.ts` → Chat Completions format:**
- Sequential messages: `[{role: "system", content}, {role: "user", content}, {role: "assistant", content, tool_calls: [...]}, {role: "tool", tool_call_id, content}, ...]`
- System prompt as a message
- Tool calls nested inside assistant messages
- Tool results as separate `tool` role messages
- Reasoning content preserved in `reasoning_content` field on assistant message

**`openai-response-format.ts` → Responses format:**

The file contains extensive documentation about the structural differences:

> **Key Differences from Chat Completions API:**
>
> **Chat Completions API:**
> - Messages are simple role/content pairs
> - System prompts are separate messages with `role="system"`
> - No explicit reasoning item structure
> - More forgiving about message ordering
>
> **Responses API:**
> - Uses an "input" array of heterogeneous items
> - System prompts go in an `instructions` field, NOT as messages
> - Reasoning items MUST be immediately followed by a message or function_call
> - Strict ordering requirements match training data distribution

> **The Reasoning Item Constraint:** `Item 'rs_...' of type 'reasoning' was provided without its required following item`

This error occurs when reasoning items are orphaned. The fix is to keep complete assistant turns together:
```
✅ CORRECT: [reasoning, message, function_call]
❌ WRONG:   [reasoning]  ← orphaned
```

#### Key Insight

Dirac takes a **class-per-provider approach** with explicit subtypes for Chat vs Responses. The difference is not abstracted away — it's embraced in the type system. Each handler understands its own wire format. The shared transform layer does the heavy lifting of converting canonical messages, but the handlers themselves make API-specific decisions (store vs no-store, WebSocket vs SSE, previous_response_id vs full context retry).

Dirac's three OpenAI handlers show that even within "OpenAI", Chat and Responses are different enough to warrant separate implementations. And within Responses, native OpenAI and third-party compatible differ in how they handle `store`, `previous_response_id`, and retry behavior.

---

### 5. pi-opencode-provider

**Location:** `READ_ONLY/pi-opencode-provider/`

**Approach:** Model discovery → API classification → delegate to pi's implementations

#### Architecture

```
┌────────────────────────────────────────────────────────┐
│  Model Discovery Layer                                 │
│                                                        │
│  1. Fetch official /models endpoint from OpenCode      │
│  2. Fetch models.dev metadata (context window, etc.)   │
│  3. Merge: official IDs + models.dev metadata          │
│  4. Classify each model by API transport               │
└──────────────────┬─────────────────────────────────────┘
                   │
    ┌──────────────┼──────────────┬──────────────────┐
    ▼              ▼              ▼                   ▼
  chat         responses      google-genai      anthropic
  (completions) (responses)   (google)          (anthropic)
    │              │              │                   │
    ▼              ▼              ▼                   ▼
  toOpenAI-     toOpenAI-     toStandardModelConfig()
  Completions   Responses
  ModelConfig   ModelConfig
    │              │
    └──────┬───────┘
           ▼
  pi's existing provider implementations
  (openai-completions.ts, openai-responses.ts,
   google.ts, anthropic.ts)
```

#### Transport Resolution

```typescript
function resolveZenTransport(modelId: string): Transport {
  if (ZEN_RESPONSES_MODEL_IDS.has(modelId) || modelId.startsWith("gpt-")) 
    return "responses"
  if (ZEN_ANTHROPIC_MODEL_IDS.has(modelId) || modelId.startsWith("claude-")) 
    return "anthropic"
  if (ZEN_GOOGLE_MODEL_IDS.has(modelId) || modelId.startsWith("gemini-")) 
    return "google"
  return "chat"  // default fallback
}
```

Models are **bucketed** by transport at discovery time:
```typescript
buildZenProviderModels(buckets) → [
  ...buckets.chat.map(toOpenAICompletionsModelConfig),      // api: "openai-completions"
  ...buckets.responses.map(toOpenAIResponsesModelConfig),    // api: "openai-responses"
  ...buckets.google.map(m => toStandardModelConfig(m, "google-generative-ai")),
  ...buckets.anthropic.map(m => toStandardModelConfig(m, "anthropic-messages")),
]
```

Each model gets its `api` field set appropriately, and pi's runtime selects the correct provider implementation based on that field.

#### Backend URL Routing

Different APIs may point to different backend URLs:
```typescript
function rewriteProviderModelBaseUrls(models, providerId, defaultBaseUrl, anthropicBaseUrl) {
  return models.map(model => {
    if (model.api === "anthropic-messages") 
      return { ...model, baseUrl: anthropicBaseUrl }  // /v1/messages
    return { ...model, baseUrl: defaultBaseUrl }        // /v1
  })
}
```

Anthropic Messages uses a different base URL than the Chat/Responses endpoints.

#### Key Insight

This is the **classification approach**: don't abstract over APIs — explicitly discover which API each model uses, tag models accordingly, and let existing provider implementations handle the rest. The provider extension doesn't implement any API calls itself; it just feeds correctly-classified model configurations into pi's existing provider engine.

---

## Patterns and Tradeoffs

### Pattern 1: Commit to One API (codex)

| Pros | Cons |
|---|---|
| Zero conversion complexity | Third-party providers must implement that API |
| Single code path for streaming, errors, retries | Can't use Chat-only models |
| Simpler caching (one model) | Forces adapters for non-conforming providers |
| Consistent debugging | Removes user choice |

### Pattern 2: Support Both with Routing (pi-mono, opencode)

| Pros | Cons |
|---|---|
| Can use any model with its optimal API | Conversion complexity between formats |
| Future-proof (if one API wins, already supported) | Cross-provider handoff requires normalization |
| Per-model optimization possible | Two code paths to maintain |
| Users can override the routing | Compatibility matrix grows over time |

### Pattern 3: Handler Classes per Provider+API (dirac)

| Pros | Cons |
|---|---|
| Each handler is self-contained and testable | High implementation overhead (30+ files) |
| API-specific behavior is explicit | Duplication between Chat and Responses versions |
| Easy to add new providers | Cross-provider features harder |
| Handler can have provider-specific logic | New API features need updates in many files |

### Pattern 4: Classification + Delegation (pi-opencode-provider)

| Pros | Cons |
|---|---|
| Lightweight — doesn't implement API calls | Depends on downstream implementations |
| Separation of discovery from execution | Classification logic must stay current |
| Easy to add new models (just tag them) | Limited custom behavior per model |
| Works with existing infrastructure | Can't add API-specific optimizations |

---

## Key Design Decisions

### 1. Where does the system prompt live?

| API | System Prompt Location |
|---|---|
| Chat Completions | `messages[]` element with `role: "system"` (or `"developer"` for reasoning models) |
| Responses | Top-level `instructions` field |
| Anthropic Messages | Top-level `system` param (string or `[{type: "text", text, cache_control}]`) |
| Google Generative AI | `systemInstruction` field |

This is a **pervasive difference**. The transform layer must know which API it's converting for so it can put the system prompt in the right place.

### 2. How are tool calls modeled?

| API | Tool Call Representation |
|---|---|
| Chat Completions | Nested inside assistant message as `tool_calls[]` array; results as separate `role: "tool"` messages |
| Responses | Independent top-level items of type `function_call` and `function_call_output` |
| Anthropic | Independent top-level content blocks `{type: "tool_use"}` and `{type: "tool_result"}` (inside `content[]` of user messages) |

The Chat Completions model is **hierarchical** (tool calls belong to a specific assistant turn). The Responses model is **flat** (tool calls are standalone items in the input sequence). Converting between these requires understanding which assistant message "owns" which function_call items.

### 3. How is reasoning/thinking handled?

| API | Reasoning Representation |
|---|---|
| Chat Completions | `reasoning_content` field on assistant message delta (or `thinking` blocks in pi-mono's canonical format) |
| Responses | First-class `reasoning` items with `summary[]` parts, `encrypted_content`, structured events |
| Anthropic | `thinking` content blocks with `signature` for redaction persistence |
| Google | `thoughts` in response, toggle via `thinkingConfig` |

The Responses API has the richest reasoning model: separate `response.reasoning_summary_part.added`, `response.reasoning_text.delta`, and `response.reasoning_summary_part.done` events. When converting Responses reasoning to Chat Completions format, the summary text is collapsed into `reasoning_content` and the raw item is serialized as `thinkingSignature` for replay.

### 4. How is caching handled?

| API | Caching Mechanism |
|---|---|
| Chat Completions (OpenAI) | `prompt_cache_key` parameter + `prompt_cache_retention` |
| Chat Completions (Anthropic via proxy) | `cache_control: { type: "ephemeral", ttl: "1h" }` markers on content blocks |
| Responses (OpenAI) | `prompt_cache_key` + `prompt_cache_retention` + `session_id` header + `x-session-affinity` |
| Anthropic Messages | `cache_control: { type: "ephemeral" }` on content blocks |
| Bedrock | `cachePoint: { type: "default" }` |
| OpenRouter | `cache_control: { type: "ephemeral" }` |

The fundamental split: **in-band caching** (markers embedded in content) vs **out-of-band caching** (session identifiers in headers). pi-mono's `cacheControlFormat: "anthropic"` compat field bridges this gap.

### 5. What is stored on the server?

| API | Server Storage Behavior |
|---|---|
| Chat Completions | Nothing stored (stateless) |
| Responses | Can store responses with `store: true`; requires `store: false` to opt out |
| Anthropic | Nothing stored (stateless) |

pi-mono and OpenCode both default to `store: false`. Dirac defaults to `store: true`. This is a privacy/behavioral choice.

---

## The Compatibility Matrix

Across the projects, the following provider quirks must be handled:

| Quirk | Affected Providers | Mitigation |
|---|---|---|
| Tool call IDs must be ≤ 40 alphanumeric chars | Anthropic via Chat Completions, Mistral | Normalize/sanitize/truncate IDs |
| Tool call IDs must start with `fc_` | Responses API | Prefix normalized IDs |
| Tool call IDs use pipe separator (`call_id\|item_id`) | Responses API cross-provider | Split and normalize each part |
| `reasoning` items must be followed by a message/function_call | Responses API | Keep complete assistant turns together |
| User message cannot follow tool result | Mistral, some Chat providers | Insert synthetic assistant message |
| Tool results require `name` field | Some Chat providers | `requiresToolResultName` compat flag |
| Assistant messages must include empty reasoning | DeepSeek | `requiresReasoningContentOnAssistantMessages: true` |
| `tool_use` blocks cannot precede text in assistant | Anthropic | Reorder to text-first |
| Empty content rejected | Anthropic, Bedrock | Filter or insert placeholder |
| `strict` field on tools rejected | Moonshot, Cloudflare | `supportsStrictMode: false` |
| `store` field rejected | Many non-OpenAI Chat providers | `supportsStore: false` |
| `reasoning_effort` not supported | Grok, z.ai, Moonshot | `supportsReasoningEffort: false` |
| Thinking in different param formats | OpenRouter, DeepSeek, z.ai, Qwen | `thinkingFormat` compat field |
| Max tokens in different fields | Chutes, Moonshot | `maxTokensField: "max_tokens"` |
| Image input not supported | Various non-vision models | Replace images with placeholder text |
| Reasoning signature must be preserved | Anthropic thinking, OpenAI encrypted reasoning | Serialize to `thinkingSignature`/`thoughtSignature` |

---

## Recommendations

Based on the analysis of all five projects, here are design principles for handling stateful vs stateless API abstraction:

### 1. Use a Canonical Internal Format

All projects that support multiple APIs converge on a shared internal message representation. This should:
- Carry enough metadata to reconstruct provider-specific formats (`api`, `provider`, `model` on each message)
- Be unambiguous about which content blocks exist (text, thinking, tool call, image)
- Support round-trip conversion (thinking signatures, tool call IDs, message IDs preserved for replay)

### 2. Make Compatibility Explicit

Rather than `if (provider === "x")` chains, define a compatibility descriptor (like pi-mono's `OpenAICompletionsCompat` / `OpenAIResponsesCompat`). Auto-detect from URL, allow per-model overrides. This makes adding new providers a data task rather than a code change.

### 3. Classify Models, Not Providers

As pi-opencode-provider demonstrates, the right API depends on the **model**, not the provider. A provider like OpenCode exposes GPT (Responses), Claude (Anthropic Messages), and Gemini (Google Generative AI) models. Each must be tagged at discovery time with its correct API family.

### 4. Handle Cross-API Replay

When a conversation produced by one API is replayed against another, normalize:
- Tool call IDs (sanitize characters, truncate length, prefix for API requirements)
- Thinking blocks (collapse to text for non-reasoning models, preserve signatures for same-model replay)
- Message ordering (insert synthetic messages where APIs require bridging)
- Orphaned tool calls (insert synthetic empty results)

### 5. Decide on Server-Side Storage

Be explicit about `store: false` (pi-mono, OpenCode) vs `store: true` (Dirac). This affects caching behavior, privacy, and the ability to use `previous_response_id` for turn continuity.

### 6. Pick a Session/Caching Strategy

| If using primarily Chat Completions | If using primarily Responses API |
|---|---|
| Use in-band `cache_control` markers on content blocks | Use out-of-band `session_id` / `prompt_cache_key` headers |
| Cache system prompt + last tool + last message | Server manages cache transparently |
| Works cross-provider (Anthropic-style markers) | Provider-specific session affinity needed |

### 7. If Committing to One API

Codex's approach (Responses-only) is viable if:
- You control the backend or can require Responses compatibility
- You're willing to write adapters for non-conforming providers (like Bedrock)
- You want to leverage Responses-specific features (WebSocket streaming, server-side turn state, structured reasoning items)
- Removing Chat Completions support simplifies streaming error handling and retry logic

### 8. If Supporting Both APIs

pi-mono and dirac's approach (both paths) is better if:
- You need to support the broadest range of providers and local models
- You want per-model optimal API selection
- You're willing to maintain format conversion and a compatibility matrix
- You need to support cross-provider conversation handoff
