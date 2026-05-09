using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Omicron.Core.Models;

namespace Omicron.Core.Providers;

// ============================================================
// Provider Compatibility descriptors
// ============================================================

/// <summary>
/// Descriptors for provider compatibility quirks.
/// Each field should be added only when a test or provider requires it.
/// </summary>
public sealed record ProviderCompatibility
{
    /// <summary>Whether the provider supports the store parameter (server-side storage).</summary>
    public bool SupportsStore { get; init; }

    /// <summary>Whether usage info is available during streaming (vs. only at end).</summary>
    public bool SupportsStreamingUsage { get; init; } = true;

    /// <summary>Whether reasoning_effort parameter is supported.</summary>
    public bool SupportsReasoningEffort { get; init; }

    /// <summary>Whether image inputs are supported.</summary>
    public bool SupportsImages { get; init; }

    /// <summary>Whether strict mode for tools is supported (default true).</summary>
    public bool SupportsStrictTools { get; init; } = true;

    /// <summary>Whether tool_result messages require a tool_name field.</summary>
    public bool RequiresToolResultName { get; init; }

    /// <summary>Whether an assistant message must follow a tool_result.</summary>
    public bool RequiresAssistantAfterToolResult { get; init; }

    /// <summary>Whether assistant messages must include reasoning content (DeepSeek).</summary>
    public bool RequiresReasoningContentOnAssistantMessages { get; init; }

    /// <summary>Whether empty content strings are rejected.</summary>
    public bool RejectsEmptyContent { get; init; }

    /// <summary>Format of tool_call_id values expected by the provider.</summary>
    public ToolCallIdFormat ToolCallIdFormat { get; init; } = ToolCallIdFormat.Default;

    /// <summary>Which field name to use for max_tokens.</summary>
    public MaxTokensField MaxTokensField { get; init; } = MaxTokensField.MaxTokens;

    /// <summary>Thinking/thinking format supported.</summary>
    public ThinkingFormat ThinkingFormat { get; init; } = ThinkingFormat.None;

    /// <summary>Cache control format supported.</summary>
    public CacheControlFormat CacheControlFormat { get; init; } = CacheControlFormat.None;

    /// <summary>Whether to send session affinity headers.</summary>
    public bool SendSessionAffinityHeaders { get; init; }

    /// <summary>Whether long cache retention is supported.</summary>
    public bool SupportsLongCacheRetention { get; init; }

    /// <summary>
    /// Whether the provider supports previous_response_id for stateful continuation.
    /// Only Responses-compatible APIs support this. When true, the provider can receive
    /// only new input items along with a previous_response_id to continue a conversation.
    /// </summary>
    public bool SupportsPreviousResponseId { get; init; }

    /// <summary>Default compatibility for OpenAI Chat Completions.</summary>
    public static ProviderCompatibility OpenAiChat { get; } = new()
    {
        SupportsStreamingUsage = true,
        SupportsReasoningEffort = true,
        SupportsImages = true,
        SupportsStrictTools = true,
        SupportsPreviousResponseId = false
    };

    /// <summary>Default compatibility for OpenAI Responses API.</summary>
    public static ProviderCompatibility OpenAiResponses { get; } = new()
    {
        SupportsStore = true,
        SupportsStreamingUsage = true,
        SupportsReasoningEffort = true,
        SupportsImages = true,
        SupportsStrictTools = true,
        SupportsPreviousResponseId = true
    };

    /// <summary>Default compatibility for Anthropic Messages API.</summary>
    public static ProviderCompatibility AnthropicMessages { get; } = new()
    {
        SupportsStreamingUsage = false,
        SupportsImages = true,
        RequiresAssistantAfterToolResult = true,
        ToolCallIdFormat = ToolCallIdFormat.Anthropic,
        SupportsPreviousResponseId = false
    };

    /// <summary>Default compatibility for Google Generative AI.</summary>
    public static ProviderCompatibility GoogleGenAi { get; } = new()
    {
        SupportsStreamingUsage = false,
        SupportsImages = true,
        SupportsPreviousResponseId = false
    };
}

/// <summary>Tool call ID format expected by the provider.</summary>
public enum ToolCallIdFormat
{
    /// <summary>Default format (alphanumeric, typically prefixed with call_).</summary>
    Default,
    /// <summary>Anthropic format uses tooluse_ prefix.</summary>
    Anthropic,
    /// <summary>Google format.</summary>
    Google,
    /// <summary>Responses API uses item IDs for tool calls.</summary>
    ResponsesItem
}

/// <summary>Which field name to use for max_tokens in the request.</summary>
public enum MaxTokensField
{
    /// <summary>Use max_tokens (OpenAI, most providers).</summary>
    MaxTokens,
    /// <summary>Use max_tokens_to_sample (Anthropic).</summary>
    MaxTokensToSample
}

/// <summary>Thinking/thinking format supported.</summary>
public enum ThinkingFormat
{
    /// <summary>No thinking support.</summary>
    None,
    /// <summary>OpenAI-style reasoning_content field.</summary>
    ReasoningContent,
    /// <summary>Anthropic-style thinking blocks.</summary>
    ThinkingBlocks
}

/// <summary>Cache control format supported.</summary>
public enum CacheControlFormat
{
    /// <summary>No cache control.</summary>
    None,
    /// <summary>Anthropic-style ephemeral cache breakpoints.</summary>
    AnthropicEphemeral
}

// ============================================================
// Storage policy for provider-managed state
// ============================================================

/// <summary>
/// Policy for provider-side storage of conversation/response state.
/// Controls whether Omicron requests server-side storage and whether
/// it uses provider continuation tokens (previous_response_id).
/// </summary>
public enum ProviderStoragePolicy
{
    /// <summary>
    /// Prefer stateless: do not request server-side storage.
    /// Provider turn state (previous_response_id) is NOT used.
    /// Full conversation context is rebuilt from local history.
    /// </summary>
    PreferStateless,

    /// <summary>
    /// Allow provider turn state without requesting server-side storage.
    /// Provider turn state IS used (previous_response_id is sent), but
    /// store:false is requested where supported.
    /// </summary>
    AllowProviderStateNoStore,

    /// <summary>
    /// Allow provider-managed server-side stored state.
    /// store:true is requested where supported.
    /// </summary>
    AllowProviderStoredState
}

// ============================================================
// Stage 1: ApiShape — wire-protocol formatters & parsers
// ============================================================

/// <summary>
/// Accumulates tool call data across SSE chunks.
/// Id is the call_id (wire-level), ItemId is the provider's item identifier.
/// </summary>
public class ToolCallAccumulator
{
    public string? Id { get; set; }
    public string? ItemId { get; set; }
    public string Name { get; set; } = "";
    public StringBuilder Args { get; } = new();
}

/// <summary>
/// A wire-protocol shape: knows how to build an HTTP request body
/// and parse an SSE event stream for a specific LLM API format.
/// All shapes are stateless — any per-stream state lives in the
/// <see cref="ToolCallAccumulator"/> instances passed to ParseSseChunk.
/// </summary>
public interface IApiShape
{
    /// <summary>Human-readable name (e.g. "OpenAI Chat", "Anthropic Messages").</summary>
    string Name { get; }

    /// <summary>The ApiType this shape implements.</summary>
    ApiType ApiType { get; }

    /// <summary>
    /// Build the JSON body for a streaming request.
    /// </summary>
    JsonObject BuildRequestBody(
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options);

    /// <summary>
    /// Parse a single SSE data line and return either a StreamEvent,
    /// null (skip), or an error event.
    /// </summary>
    StreamEvent? ParseSseChunk(
        string data,
        Dictionary<int, ToolCallAccumulator> toolCallAccumulators);
}

// ============================================================
// OpenAI Chat Completions shape
// ============================================================

public class OpenAiChatShape : IApiShape
{
    public string Name => "OpenAI Chat";
    public ApiType ApiType => ApiType.OpenAiChat;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public JsonObject BuildRequestBody(
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options)
    {
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true }
        };

        if (options.MaxTokens.HasValue)
            body["max_tokens"] = options.MaxTokens.Value;
        if (options.Temperature.HasValue)
            body["temperature"] = options.Temperature.Value;
        if (options.ReasoningEffort is not null)
            body["reasoning_effort"] = options.ReasoningEffort;

        var messageList = new JsonArray();
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messageList.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = systemPrompt
            });
        }
        foreach (var msg in messages)
        {
            messageList.Add(msg.Role switch
            {
                MessageRole.User => BuildUserMessage(msg),
                MessageRole.Assistant => BuildAssistantMessage(msg),
                MessageRole.ToolResult => BuildToolResult(msg),
                _ => throw new ArgumentOutOfRangeException()
            });
        }
        body["messages"] = messageList;

        if (tools?.Count > 0)
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
                toolArray.Add(BuildToolDef(tool));
            body["tools"] = toolArray;
        }

        return body;
    }

    public StreamEvent? ParseSseChunk(
        string data,
        Dictionary<int, ToolCallAccumulator> toolCallAccumulators)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorEl))
                return ErrorEvent(errorEl.GetProperty("message").GetString() ?? "Unknown error");

            var choices = root.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return null;
            var delta = choices[0].GetProperty("delta");
            var finishReason = choices[0].TryGetProperty("finish_reason", out var frEl) ? frEl.GetString() : null;

            // Content delta
            if (delta.TryGetProperty("content", out var contentEl))
            {
                var text = contentEl.GetString();
                if (!string.IsNullOrEmpty(text))
                    return new StreamEvent { Type = StreamEventType.TextDelta, Delta = text };
            }

            // Reasoning / thinking content (DeepSeek, etc.)
            if (delta.TryGetProperty("reasoning_content", out var reasoningEl))
            {
                var text = reasoningEl.GetString();
                if (!string.IsNullOrEmpty(text))
                    return new StreamEvent { Type = StreamEventType.TextDelta, Delta = text, ReasoningText = text };
            }

            // Tool calls
            if (delta.TryGetProperty("tool_calls", out var toolCallsEl))
            {
                foreach (var tc in toolCallsEl.EnumerateArray())
                {
                    var index = tc.GetProperty("index").GetInt32();
                    var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    string? name = null, args = null;
                    if (tc.TryGetProperty("function", out var fnEl))
                    {
                        name = fnEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                        args = fnEl.TryGetProperty("arguments", out var argsEl) ? argsEl.GetString() : null;
                    }
                    if (id is not null)
                    {
                        toolCallAccumulators[index] = new ToolCallAccumulator { Id = id, Name = name ?? "" };
                        return new StreamEvent { Type = StreamEventType.ToolCallStart, ToolCallId = id, ToolName = name };
                    }
                    if (args is not null && toolCallAccumulators.TryGetValue(index, out var acc))
                    {
                        acc.Args.Append(args);
                        return new StreamEvent { Type = StreamEventType.ToolCallDelta, Delta = args };
                    }
                }
            }

            // Finish reason
            if (finishReason is not null && finishReason != "null")
            {
                foreach (var (_, acc) in toolCallAccumulators)
                {
                    var argsJson = acc.Args.Length > 0
                        ? JsonSerializer.Deserialize<Dictionary<string, object?>>(acc.Args.ToString())
                        : new Dictionary<string, object?>();
                    return new StreamEvent
                    {
                        Type = StreamEventType.ToolCallEnd,
                        ToolCall = new ToolCallContent(acc.Id ?? Guid.NewGuid().ToString("N")[..12], acc.Name, argsJson)
                    };
                }
                toolCallAccumulators.Clear();

                UsageInfo? usage = null;
                if (root.TryGetProperty("usage", out var usageEl))
                    usage = ParseUsage(usageEl);

                return new StreamEvent
                {
                    Type = StreamEventType.Done,
                    StopReason = MapFinishReason(finishReason),
                    Delta = root.TryGetProperty("id", out var idEl2) ? idEl2.GetString() : null,
                    Usage = usage
                };
            }
        }
        catch (JsonException)
        {
            return new StreamEvent { Type = StreamEventType.Error, ErrorMessage = "JSON parse error in chunk" };
        }
        return null;
    }

    private static JsonObject BuildUserMessage(Message msg)
    {
        if (msg.Images is { Count: > 0 })
        {
            var content = new JsonArray();
            content.Add(new JsonObject { ["type"] = "text", ["text"] = msg.Text ?? "" });
            foreach (var img in msg.Images)
                content.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = $"data:{img.MimeType};base64,{img.Data}", ["detail"] = "high" }
                });
            return new JsonObject { ["role"] = "user", ["content"] = content };
        }
        return new JsonObject { ["role"] = "user", ["content"] = msg.Text ?? "" };
    }

    private static JsonObject BuildAssistantMessage(Message msg)
    {
        var obj = new JsonObject { ["role"] = "assistant" };

        if (msg.Reasoning is not null)
            obj["reasoning_content"] = msg.Reasoning;

        var allToolCalls = msg.ToolCalls ?? (msg.ToolCall is not null ? [msg.ToolCall] : null);
        if (allToolCalls is { Count: > 0 })
        {
            obj["content"] = "";
            var tcArray = new JsonArray();
            foreach (var tc in allToolCalls)
            {
                tcArray.Add(new JsonObject
                {
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tc.Name,
                        ["arguments"] = JsonSerializer.Serialize(tc.Arguments, JsonOptions)
                    }
                });
            }
            obj["tool_calls"] = tcArray;
        }
        else
        {
            obj["content"] = msg.Text ?? "";
        }
        return obj;
    }

    private static JsonObject BuildToolResult(Message msg) =>
        new()
        {
            ["role"] = "tool",
            ["tool_call_id"] = msg.ToolCallId ?? "",
            ["content"] = msg.Text ?? ""
        };

    private static JsonObject BuildToolDef(Tool tool)
    {
        var def = new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description
            }
        };
        if (tool.Parameters.HasValue)
            def["function"]!["parameters"] = JsonNode.Parse(tool.Parameters.Value.GetRawText());
        return def;
    }

    private static StopReason MapFinishReason(string? r) => r switch
    {
        "stop" => StopReason.Stop,
        "length" => StopReason.Length,
        "tool_calls" => StopReason.ToolUse,
        "error" => StopReason.Error,
        _ => StopReason.Stop
    };

    private static UsageInfo ParseUsage(JsonElement el)
    {
        int input = 0, output = 0;
        int? cacheRead = null;
        if (el.TryGetProperty("prompt_tokens", out var pt)) input = pt.GetInt32();
        if (el.TryGetProperty("completion_tokens", out var ct)) output = ct.GetInt32();
        if (el.TryGetProperty("prompt_tokens_details", out var details) &&
            details.TryGetProperty("cached_tokens", out var cached))
            cacheRead = cached.GetInt32();
        return new UsageInfo(input, output, cacheRead);
    }

    private static StreamEvent ErrorEvent(string msg) =>
        new() { Type = StreamEventType.Error, ErrorMessage = msg };
}

// ============================================================
// OpenAI Responses shape
// ============================================================

/// <summary>
/// The OpenAI Responses API shape — builds requests for /v1/responses
/// and parses the Responses-specific SSE event stream.
///
/// Stateless: per-stream accumulator state lives entirely in the
/// toolCallAccumulators dictionary passed to ParseSseChunk.
/// Item-to-index mapping is done by scanning accumulator values for ItemId.
/// </summary>
public class OpenAiResponsesShape : IApiShape
{
    public string Name => "OpenAI Responses";
    public ApiType ApiType => ApiType.OpenAiResponses;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public JsonObject BuildRequestBody(
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options)
    {
        // Determine storage policy: options override model
        var storagePolicy = options.StoragePolicy ?? model.StoragePolicy;
        var compat = model.GetEffectiveCompatibility();

        // Determine whether to use stateful mode
        var currentState = options.CurrentProviderState;
        bool useStateful = currentState?.IsStateful == true
            && CompatibilityDetector.SupportsStatefulContinuation(ApiType, storagePolicy)
            && compat.SupportsPreviousResponseId;

        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["stream"] = true
        };

        // Check compatibility before sending optional fields
        // compat was already resolved above for the useStateful check

        if (options.MaxTokens.HasValue)
            body["max_output_tokens"] = options.MaxTokens.Value;
        if (options.Temperature.HasValue)
            body["temperature"] = options.Temperature.Value;
        if (options.ReasoningEffort is not null && compat.SupportsReasoningEffort)
            body["reasoning_effort"] = options.ReasoningEffort;

        // Instructions (system prompt)
        if (!string.IsNullOrEmpty(systemPrompt))
            body["instructions"] = systemPrompt;

        // Storage policy — only send store if the provider supports it
        if (compat.SupportsStore)
            body["store"] = storagePolicy == ProviderStoragePolicy.AllowProviderStoredState;

        if (useStateful && currentState!.PreviousResponseId is not null)
        {
            // Stateful mode: only send the latest user turn (new messages),
            // include previous_response_id for server-side continuation.
            body["previous_response_id"] = currentState.PreviousResponseId;

            // In stateful mode, only send the most recent input items
            // (the server already has the conversation history)
            var lastInput = BuildInputItems(GetLastUserTurn(messages));
            if (lastInput.Count > 0)
                body["input"] = lastInput;
        }
        else
        {
            // Stateless mode: rebuild full input from local message history
            var input = BuildInputItems(messages);
            if (input.Count > 0)
                body["input"] = input;
        }

        // Tools
        if (tools?.Count > 0)
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
                toolArray.Add(BuildToolDef(tool));
            body["tools"] = toolArray;
        }

        // Metadata
        body["metadata"] = new JsonObject
        {
            ["user"] = "omicron-agent"
        };

        return body;
    }

    /// <summary>
    /// Extract the last user turn's messages (for stateful mode, only send new input).
    /// Returns only messages after the last assistant response, i.e., the newest user
    /// message and any tool results that preceded it.
    /// </summary>
    private static IReadOnlyList<Message> GetLastUserTurn(IReadOnlyList<Message> messages)
    {
        if (messages.Count == 0) return Array.Empty<Message>();

        // Find the last assistant message boundary
        int start = 0;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == MessageRole.Assistant)
            {
                // Start after the last assistant message
                start = i + 1;
                break;
            }
        }

        // Return messages from the last user input onwards
        return messages.Skip(start).ToList();
    }

    /// <summary>
    /// Build the input[] array from messages.
    /// Each message becomes a response item with role and content.
    /// </summary>
    private static JsonArray BuildInputItems(IReadOnlyList<Message> messages)
    {
        var items = new JsonArray();

        foreach (var msg in messages)
        {
            switch (msg.Role)
            {
                case MessageRole.User:
                {
                    var item = new JsonObject { ["role"] = "user" };
                    if (msg.Images is { Count: > 0 })
                    {
                        var contentArray = new JsonArray();
                        contentArray.Add(new JsonObject
                        {
                            ["type"] = "input_text",
                            ["text"] = msg.Text ?? ""
                        });
                        foreach (var img in msg.Images)
                        {
                            contentArray.Add(new JsonObject
                            {
                                ["type"] = "input_image",
                                ["image_url"] = $"data:{img.MimeType};base64,{img.Data}"
                            });
                        }
                        item["content"] = contentArray;
                    }
                    else
                    {
                        item["content"] = msg.Text ?? "";
                    }
                    items.Add(item);
                    break;
                }

                case MessageRole.Assistant:
                {
                    // For Responses API, assistant messages with function calls
                    // are represented as top-level function_call items alongside
                    // output_text items, not nested inside assistant message content.
                    // We split them into separate input items.

                    var allToolCalls = msg.ToolCalls ?? (msg.ToolCall is not null ? [msg.ToolCall] : null);

                    if (allToolCalls is { Count: > 0 })
                    {
                        // Emit text as a separate assistant message item only if there is text
                        if (!string.IsNullOrEmpty(msg.Text))
                        {
                            var textItem = new JsonObject
                            {
                                ["type"] = "message",
                                ["role"] = "assistant",
                                ["content"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["type"] = "output_text",
                                        ["text"] = msg.Text
                                    }
                                }
                            };
                            items.Add(textItem);
                        }

                        // Emit each function call as a top-level function_call item
                        foreach (var tc in allToolCalls)
                        {
                            var fcItem = new JsonObject
                            {
                                ["type"] = "function_call",
                                ["id"] = tc.Id,
                                ["call_id"] = tc.Id,
                                ["name"] = tc.Name,
                                ["arguments"] = JsonSerializer.Serialize(tc.Arguments ?? new Dictionary<string, object?>(), JsonOptions)
                            };
                            items.Add(fcItem);
                        }
                    }
                    else
                    {
                        // Plain assistant message (no tool calls)
                        var item = new JsonObject
                        {
                            ["type"] = "message",
                            ["role"] = "assistant"
                        };
                        var contentArray = new JsonArray();

                        if (!string.IsNullOrEmpty(msg.Text))
                        {
                            contentArray.Add(new JsonObject
                            {
                                ["type"] = "output_text",
                                ["text"] = msg.Text
                            });
                        }

                        // Do not replay MVP reasoning text as Responses reasoning items yet.
                        // The Responses schema expects a richer top-level reasoning item
                        // shape, and OpenRouter rejects assistant message content with
                        // ad-hoc reasoning blocks.

                        if (contentArray.Count > 0)
                            item["content"] = contentArray;
                        else if (!string.IsNullOrEmpty(msg.Text))
                            item["content"] = msg.Text;

                        items.Add(item);
                    }

                    break;
                }

                case MessageRole.ToolResult:
                {
                    // Responses API uses function_call_output items for tool results,
                    // keyed by call_id, not chat-style tool_result content inside user messages.
                    var item = new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = msg.ToolCallId ?? "",
                        ["output"] = msg.Text ?? ""
                    };
                    items.Add(item);
                    break;
                }
            }
        }

        return items;
    }

    /// <summary>
    /// Parse a Responses API SSE chunk.
    /// Stateless: per-stream state lives entirely in the toolCallAccumulators
    /// dictionary (keyed by int index). Item IDs are matched by scanning
    /// accumulator values for matching ItemId. This avoids mutable instance state.
    /// </summary>
    public StreamEvent? ParseSseChunk(
        string data,
        Dictionary<int, ToolCallAccumulator> toolCallAccumulators)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            // Top-level error event
            if (root.TryGetProperty("error", out var errorEl))
            {
                return new StreamEvent
                {
                    Type = StreamEventType.Error,
                    ErrorMessage = errorEl.TryGetProperty("message", out var msgEl)
                        ? msgEl.GetString() ?? "Unknown error"
                        : errorEl.GetRawText()
                };
            }

            // Responses API SSE events have a "type" field
            if (!root.TryGetProperty("type", out var typeEl))
                return null;

            var eventType = typeEl.GetString();

            switch (eventType)
            {
                case "response.output_item.added":
                {
                    var item = root.GetProperty("item");
                    var itemType = item.GetProperty("type").GetString();
                    var itemId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

                    if (itemType == "function_call" && itemId is not null)
                    {
                        var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : "";
                        var callId = item.TryGetProperty("call_id", out var callIdEl) ? callIdEl.GetString() : itemId;

                        // Find the next available index (avoid overwriting active entries)
                        var accIndex = toolCallAccumulators.Keys.Count > 0
                            ? toolCallAccumulators.Keys.Max() + 1
                            : 0;
                        toolCallAccumulators[accIndex] = new ToolCallAccumulator
                        {
                            Id = callId ?? itemId,
                            ItemId = itemId,
                            Name = name ?? ""
                        };

                        return new StreamEvent
                        {
                            Type = StreamEventType.ToolCallStart,
                            ToolCallId = callId ?? itemId,
                            ToolName = name
                        };
                    }
                    break;
                }

                case "response.output_text.delta":
                {
                    var delta = root.GetProperty("delta").GetString();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        return new StreamEvent
                        {
                            Type = StreamEventType.TextDelta,
                            Delta = delta
                        };
                    }
                    break;
                }

                case "response.refusal.delta":
                {
                    var delta = root.GetProperty("delta").GetString();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        return new StreamEvent
                        {
                            Type = StreamEventType.TextDelta,
                            Delta = delta,
                            ReasoningText = delta
                        };
                    }
                    break;
                }

                case "response.function_call_arguments.delta":
                {
                    var delta = root.GetProperty("delta").GetString();
                    var itemId = root.TryGetProperty("item_id", out var itemIdEl) ? itemIdEl.GetString() : null;

                    if (!string.IsNullOrEmpty(delta) && itemId is not null)
                    {
                        // Find accumulator by ItemId (scan values — dictionaries are small)
                        var acc = toolCallAccumulators.Values
                            .FirstOrDefault(a => a.ItemId == itemId);
                        if (acc is not null)
                        {
                            acc.Args.Append(delta);
                        }

                        return new StreamEvent
                        {
                            Type = StreamEventType.ToolCallDelta,
                            Delta = delta
                        };
                    }
                    break;
                }

                case "response.function_call_arguments.done":
                {
                    var itemId = root.TryGetProperty("item_id", out var itemIdEl) ? itemIdEl.GetString() : null;
                    var argsJson = root.GetProperty("arguments").GetString() ?? "{}";

                    // Try to get stored info from accumulators by scanning ItemId
                    string callId = itemId ?? "";
                    string name = "";

                    if (itemId is not null)
                    {
                        var kv = toolCallAccumulators
                            .FirstOrDefault(kv => kv.Value.ItemId == itemId);
                        if (kv.Value is not null)
                        {
                            var acc = kv.Value;
                            callId = acc.Id ?? callId;
                            name = acc.Name;
                            if (acc.Args.Length > 0)
                                argsJson = acc.Args.ToString();
                            toolCallAccumulators.Remove(kv.Key);
                        }
                    }

                    // Fall back to event-level fields if accumulators didn't have them
                    if (string.IsNullOrEmpty(name))
                        name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(callId) || callId == itemId)
                        callId = root.TryGetProperty("call_id", out var callIdEl) ? callIdEl.GetString() ?? callId : callId;

                    Dictionary<string, object?>? args = null;
                    try { args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson); } catch { args = new(); }

                    return new StreamEvent
                    {
                        Type = StreamEventType.ToolCallEnd,
                        ToolCall = new ToolCallContent(callId, name, args ?? new())
                    };
                }

                case "response.output_text.done":
                case "response.refusal.done":
                {
                    break;
                }

                case "response.output_item.done":
                {
                    break;
                }

                case "response.completed":
                {
                    var response = root.GetProperty("response");
                    var responseId = response.TryGetProperty("id", out var respIdEl) ? respIdEl.GetString() : null;
                    var status = response.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : "completed";

                    if (status == "failed")
                    {
                        var errMsg = "Response failed";
                        if (response.TryGetProperty("error", out var respErr))
                        {
                            errMsg = respErr.TryGetProperty("message", out var respMsgEl)
                                ? respMsgEl.GetString() ?? errMsg
                                : respErr.GetRawText();
                        }
                        return new StreamEvent
                        {
                            Type = StreamEventType.Error,
                            ErrorMessage = errMsg
                        };
                    }

                    UsageInfo? usage = null;
                    if (response.TryGetProperty("usage", out var usageEl))
                        usage = ParseUsage(usageEl);

                    var stopReason = status switch
                    {
                        "completed" => StopReason.Stop,
                        "incomplete" => StopReason.Length,
                        _ => StopReason.Stop
                    };

                    return new StreamEvent
                    {
                        Type = StreamEventType.Done,
                        StopReason = stopReason,
                        Delta = responseId,
                        Usage = usage
                    };
                }

                case "error":
                {
                    var errMsg = root.TryGetProperty("message", out var errMsgEl)
                        ? errMsgEl.GetString()
                        : "Unknown Responses API error";
                    return new StreamEvent
                    {
                        Type = StreamEventType.Error,
                        ErrorMessage = errMsg ?? "Unknown error"
                    };
                }
            }
        }
        catch (JsonException)
        {
            return new StreamEvent
            {
                Type = StreamEventType.Error,
                ErrorMessage = "JSON parse error in Responses API chunk"
            };
        }

        return null;
    }

    private static JsonObject BuildToolDef(Tool tool)
    {
        var def = new JsonObject
        {
            ["type"] = "function",
            ["name"] = tool.Name,
            ["description"] = tool.Description
        };
        if (tool.Parameters.HasValue)
            def["parameters"] = JsonNode.Parse(tool.Parameters.Value.GetRawText());
        return def;
    }

    private static UsageInfo ParseUsage(JsonElement el)
    {
        int input = 0, output = 0;
        int? cacheRead = null;

        if (el.TryGetProperty("input_tokens", out var it)) input = it.GetInt32();
        if (el.TryGetProperty("output_tokens", out var ot)) output = ot.GetInt32();

        if (el.TryGetProperty("input_tokens_details", out var details) &&
            details.TryGetProperty("cached_tokens", out var cached))
            cacheRead = cached.GetInt32();

        return new UsageInfo(input, output, cacheRead);
    }
}

// ============================================================
// Anthropic Messages shape
// ============================================================

public class AnthropicMessagesShape : IApiShape
{
    public string Name => "Anthropic Messages";
    public ApiType ApiType => ApiType.AnthropicMessages;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public JsonObject BuildRequestBody(
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options)
    {
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["max_tokens"] = options.MaxTokens ?? 4096,
            ["stream"] = true
        };
        if (options.Temperature.HasValue)
            body["temperature"] = options.Temperature.Value;
        if (!string.IsNullOrEmpty(systemPrompt))
            body["system"] = systemPrompt;

        var msgArray = new JsonArray();
        foreach (var msg in messages)
        {
            msgArray.Add(msg.Role switch
            {
                MessageRole.User => new JsonObject { ["role"] = "user", ["content"] = msg.Text ?? "" },
                MessageRole.Assistant when msg.ToolCalls is { Count: > 0 } || msg.ToolCall is not null => BuildAnthropicToolCalls(msg),
                MessageRole.Assistant => new JsonObject { ["role"] = "assistant", ["content"] = msg.Text ?? "" },
                MessageRole.ToolResult => new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = msg.ToolCallId ?? "",
                            ["content"] = msg.Text ?? ""
                        }
                    }
                },
                _ => throw new ArgumentOutOfRangeException()
            });
        }
        body["messages"] = msgArray;

        if (tools?.Count > 0)
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
            {
                var def = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description
                };
                if (tool.Parameters.HasValue)
                    def["input_schema"] = JsonNode.Parse(tool.Parameters.Value.GetRawText());
                toolArray.Add(def);
            }
            body["tools"] = toolArray;
        }
        return body;
    }

    public StreamEvent? ParseSseChunk(
        string data,
        Dictionary<int, ToolCallAccumulator> toolCallAccumulators)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "content_block_start":
                {
                    var blockType = root.GetProperty("content_block").GetProperty("type").GetString();
                    var index = root.GetProperty("index").GetInt32();
                    if (blockType == "tool_use")
                    {
                        var id = root.GetProperty("content_block").GetProperty("id").GetString() ?? "";
                        var name = root.GetProperty("content_block").GetProperty("name").GetString() ?? "";
                        toolCallAccumulators[index] = new ToolCallAccumulator { Id = id, Name = name };
                        return new StreamEvent { Type = StreamEventType.ToolCallStart, ToolCallId = id, ToolName = name };
                    }
                    break;
                }
                case "content_block_delta":
                {
                    var delta = root.GetProperty("delta");
                    var dt = delta.GetProperty("type").GetString();
                    var index = root.GetProperty("index").GetInt32();
                    if (dt == "text_delta")
                    {
                        var text = delta.GetProperty("text").GetString();
                        if (!string.IsNullOrEmpty(text))
                            return new StreamEvent { Type = StreamEventType.TextDelta, Delta = text };
                    }
                    else if (dt == "input_json_delta" && toolCallAccumulators.TryGetValue(index, out var acc))
                    {
                        var partial = delta.GetProperty("partial_json").GetString();
                        if (!string.IsNullOrEmpty(partial))
                        {
                            acc.Args.Append(partial);
                            return new StreamEvent { Type = StreamEventType.ToolCallDelta, Delta = partial };
                        }
                    }
                    break;
                }
                case "content_block_stop":
                {
                    var index = root.GetProperty("index").GetInt32();
                    if (toolCallAccumulators.TryGetValue(index, out var acc))
                    {
                        Dictionary<string, object?>? args = null;
                        try { args = JsonSerializer.Deserialize<Dictionary<string, object?>>(acc.Args.ToString()); } catch { args = new(); }
                        toolCallAccumulators.Remove(index);
                        return new StreamEvent { Type = StreamEventType.ToolCallEnd, ToolCall = new ToolCallContent(acc.Id ?? "", acc.Name, args ?? new()) };
                    }
                    break;
                }
                case "message_delta":
                {
                    var stopReason = root.GetProperty("delta").GetProperty("stop_reason").GetString();
                    var usage = root.TryGetProperty("usage", out var ue) ? ParseUsage(ue) : null;
                    var responseId = root.TryGetProperty("id", out var ie) ? ie.GetString() : null;
                    return new StreamEvent { Type = StreamEventType.Done, StopReason = MapStopReason(stopReason), Delta = responseId, Usage = usage };
                }
                case "error":
                {
                    var error = root.GetProperty("error");
                    var msg = error.TryGetProperty("message", out var me) ? me.GetString() : error.GetRawText();
                    return new StreamEvent { Type = StreamEventType.Error, ErrorMessage = msg ?? "Unknown Anthropic error" };
                }
            }
        }
        catch (JsonException)
        {
            return new StreamEvent { Type = StreamEventType.Error, ErrorMessage = "JSON parse error in Anthropic stream" };
        }
        return null;
    }

    private static StopReason MapStopReason(string? r) => r switch
    {
        "end_turn" => StopReason.Stop,
        "max_tokens" => StopReason.Length,
        "tool_use" => StopReason.ToolUse,
        "error" => StopReason.Error,
        _ => StopReason.Stop
    };

    private static JsonObject BuildAnthropicToolCalls(Message msg)
    {
        var allToolCalls = msg.ToolCalls ?? (msg.ToolCall is not null ? new List<ToolCallContent> { msg.ToolCall } : []);
        var contentArray = new JsonArray();
        foreach (var tc in allToolCalls)
        {
            contentArray.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = tc.Id,
                ["name"] = tc.Name,
                ["input"] = JsonNode.Parse(JsonSerializer.Serialize(tc.Arguments))
            });
        }
        return new JsonObject { ["role"] = "assistant", ["content"] = contentArray };
    }

    private static UsageInfo ParseUsage(JsonElement el)
    {
        int input = 0, output = 0;
        if (el.TryGetProperty("input_tokens", out var it)) input = it.GetInt32();
        if (el.TryGetProperty("output_tokens", out var ot)) output = ot.GetInt32();
        return new UsageInfo(input, output);
    }
}
