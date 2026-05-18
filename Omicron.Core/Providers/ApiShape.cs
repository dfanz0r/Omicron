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
    /// Write the request body directly to a <see cref="Utf8JsonWriter"/>.
    /// This is the long-term API; new shapes should implement this.
    /// The default implementation delegates to <see cref="BuildRequestBody"/>.
    /// </summary>
    void WriteRequestBody(
        System.Text.Json.Utf8JsonWriter writer,
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options)
    {
        var body = BuildRequestBody(model, messages, systemPrompt, tools, options);
        System.Text.Json.JsonSerializer.Serialize(writer, body);
    }

    /// <summary>
    /// Parse a single SSE data line (sans the "data: " prefix) and return
    /// either a StreamEvent, null (skip), or an error event.
    /// The payload is provided as raw UTF-8 bytes to avoid string allocation.
    /// </summary>
    StreamEvent? ParseSseChunk(
        ReadOnlyMemory<byte> data,
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
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteRequestBody(writer, model, messages, systemPrompt, tools, options);
        writer.Flush();
        var doc = JsonDocument.Parse(buffer.WrittenMemory);
        var root = JsonNode.Parse(doc.RootElement.GetRawText())!;
        return (JsonObject)root;
    }

    public void WriteRequestBody(
        Utf8JsonWriter writer,
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options)
    {
        writer.WriteStartObject();

        writer.WriteString("model", model.Id);
        writer.WriteBoolean("stream", true);
        writer.WritePropertyName("stream_options");
        writer.WriteStartObject();
        writer.WriteBoolean("include_usage", true);
        writer.WriteEndObject();

        if (options.MaxTokens.HasValue)
            writer.WriteNumber("max_tokens", options.MaxTokens.Value);
        if (options.Temperature.HasValue)
            writer.WriteNumber("temperature", options.Temperature.Value);
        if (options.ReasoningEffort is not null)
            writer.WriteString("reasoning_effort", options.ReasoningEffort);

        writer.WritePropertyName("messages");
        writer.WriteStartArray();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", systemPrompt);
            writer.WriteEndObject();
        }

        foreach (var msg in messages)
        {
            switch (msg.Role)
            {
                case MessageRole.User:
                    WriteUserMessage(writer, msg);
                    break;
                case MessageRole.Assistant:
                    WriteAssistantMessage(writer, msg);
                    break;
                case MessageRole.ToolResult:
                    WriteToolResult(writer, msg);
                    break;
            }
        }

        writer.WriteEndArray();

        if (tools?.Count > 0)
        {
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in tools)
                WriteToolDef(writer, tool);
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteUserMessage(Utf8JsonWriter writer, Message msg)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "user");

        if (msg.Images is { Count: > 0 })
        {
            writer.WritePropertyName("content");
            writer.WriteStartArray();

            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", msg.TextUtf8);
            writer.WriteEndObject();

            foreach (var img in msg.Images)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "image_url");
                writer.WritePropertyName("image_url");
                writer.WriteStartObject();
                writer.WriteString("url", $"data:{img.MimeType};base64,{img.Data}");
                writer.WriteString("detail", "high");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
        else
        {
            writer.WriteString("content", msg.TextUtf8);
        }

        writer.WriteEndObject();
    }

    private static void WriteAssistantMessage(Utf8JsonWriter writer, Message msg)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "assistant");

        // Write reasoning directly from UTF-8 span, no string allocation
        if (msg.ReasoningData is not null)
        {
            var reasoningSpan = msg.ReasoningUtf8;
            writer.WriteString("reasoning_content", reasoningSpan);
        }

        var allToolCalls = msg.ToolCalls ?? (msg.ToolCall is not null ? [msg.ToolCall] : null);
        if (allToolCalls is { Count: > 0 })
        {
            writer.WriteString("content", "");
            writer.WritePropertyName("tool_calls");
            writer.WriteStartArray();

            foreach (var tc in allToolCalls)
            {
                writer.WriteStartObject();
                writer.WriteString("id", tc.Id);
                writer.WriteString("type", "function");
                writer.WritePropertyName("function");
                writer.WriteStartObject();
                writer.WriteString("name", tc.Name);
                writer.WriteString("arguments", JsonSerializer.Serialize(tc.Arguments, JsonOptions));
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
        else
        {
            writer.WriteString("content", msg.TextUtf8);
        }

        writer.WriteEndObject();
    }

    private static void WriteToolResult(Utf8JsonWriter writer, Message msg)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "tool");
        writer.WriteString("tool_call_id", msg.ToolCallId ?? "");
        writer.WriteString("content", msg.TextUtf8);
        writer.WriteEndObject();
    }

    private static void WriteToolDef(Utf8JsonWriter writer, Tool tool)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "function");
        writer.WritePropertyName("function");
        writer.WriteStartObject();
        writer.WriteString("name", tool.Name);
        writer.WriteString("description", tool.Description);
        if (tool.Parameters.HasValue)
        {
            writer.WritePropertyName("parameters");
            JsonDocument.Parse(tool.Parameters.Value.GetRawText()).WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    public StreamEvent? ParseSseChunk(
        ReadOnlyMemory<byte> data,
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
                if (toolCallAccumulators.Count > 0)
                {
                    var first = toolCallAccumulators.First();
                    var acc = first.Value;
                    toolCallAccumulators.Remove(first.Key);
                    return BuildToolCallEndEvent(acc);
                }

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

    /// <summary>Build a ToolCallEnd event from an accumulator.</summary>
    internal static StreamEvent BuildToolCallEndEvent(ToolCallAccumulator acc)
    {
        var argsJson = acc.Args.Length > 0
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(acc.Args.ToString())
            : new Dictionary<string, object?>();
        return new StreamEvent
        {
            Type = StreamEventType.ToolCallEnd,
            ToolCall = new ToolCallContent(
                acc.Id ?? Guid.NewGuid().ToString("N")[..12],
                acc.Name,
                argsJson ?? new())
        };
    }
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
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteRequestBody(writer, model, messages, systemPrompt, tools, options);
        writer.Flush();
        var doc = JsonDocument.Parse(buffer.WrittenMemory);
        var root = JsonNode.Parse(doc.RootElement.GetRawText())!;
        return (JsonObject)root;
    }

    public void WriteRequestBody(
        Utf8JsonWriter writer,
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options)
    {
        var storagePolicy = options.StoragePolicy ?? model.StoragePolicy;
        var compat = model.GetEffectiveCompatibility();
        var currentState = options.CurrentProviderState;
        bool useStateful = currentState?.IsStateful == true
            && CompatibilityDetector.SupportsStatefulContinuation(ApiType, storagePolicy)
            && compat.SupportsPreviousResponseId;

        writer.WriteStartObject();

        writer.WriteString("model", model.Id);
        writer.WriteBoolean("stream", true);

        if (options.MaxTokens.HasValue)
            writer.WriteNumber("max_output_tokens", options.MaxTokens.Value);
        if (options.Temperature.HasValue)
            writer.WriteNumber("temperature", options.Temperature.Value);
        if (options.ReasoningEffort is not null && compat.SupportsReasoningEffort)
            writer.WriteString("reasoning_effort", options.ReasoningEffort);

        if (!string.IsNullOrEmpty(systemPrompt))
            writer.WriteString("instructions", systemPrompt);

        if (compat.SupportsStore)
            writer.WriteBoolean("store", storagePolicy == ProviderStoragePolicy.AllowProviderStoredState);

        if (useStateful && currentState!.PreviousResponseId is not null)
        {
            writer.WriteString("previous_response_id", currentState.PreviousResponseId);
            writer.WritePropertyName("input");
            writer.WriteStartArray();
            WriteInputItems(writer, GetLastUserTurn(messages));
            writer.WriteEndArray();
        }
        else
        {
            writer.WritePropertyName("input");
            writer.WriteStartArray();
            WriteInputItems(writer, messages);
            writer.WriteEndArray();
        }

        if (tools?.Count > 0)
        {
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in tools)
                WriteToolDef(writer, tool);
            writer.WriteEndArray();
        }

        writer.WritePropertyName("metadata");
        writer.WriteStartObject();
        writer.WriteString("user", "omicron-agent");
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteToolDef(Utf8JsonWriter writer, Tool tool)
    {
        // Responses API tool format: flat, no type/function nesting.
        // See https://platform.openai.com/docs/api-reference/responses
        writer.WriteStartObject();
        writer.WriteString("name", tool.Name);
        writer.WriteString("description", tool.Description);
        if (tool.Parameters.HasValue)
        {
            writer.WritePropertyName("input_schema");
            JsonDocument.Parse(tool.Parameters.Value.GetRawText()).WriteTo(writer);
        }
        writer.WriteEndObject();
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
    private static void WriteInputItems(Utf8JsonWriter writer, IReadOnlyList<Message> messages)
    {
        foreach (var msg in messages)
        {
            switch (msg.Role)
            {
                case MessageRole.User:
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", "user");

                    if (msg.Images is { Count: > 0 })
                    {
                        writer.WritePropertyName("content");
                        writer.WriteStartArray();

                        writer.WriteStartObject();
                        writer.WriteString("type", "input_text");
                        writer.WriteString("text", msg.TextUtf8);
                        writer.WriteEndObject();

                        foreach (var img in msg.Images)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "input_image");
                            writer.WriteString("image_url", $"data:{img.MimeType};base64,{img.Data}");
                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                    }
                    else
                    {
                        writer.WriteString("content", msg.TextUtf8);
                    }

                    writer.WriteEndObject();
                    break;
                }

                case MessageRole.Assistant:
                {
                    var allToolCalls = msg.ToolCalls ?? (msg.ToolCall is not null ? [msg.ToolCall] : null);

                    if (allToolCalls is { Count: > 0 })
                    {
                        if (msg.HasText)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "message");
                            writer.WriteString("role", "assistant");
                            writer.WritePropertyName("content");
                            writer.WriteStartArray();
                            writer.WriteStartObject();
                            writer.WriteString("type", "output_text");
                            writer.WriteString("text", msg.TextUtf8);
                            writer.WriteEndObject();
                            writer.WriteEndArray();
                            writer.WriteEndObject();
                        }

                        foreach (var tc in allToolCalls)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "function_call");
                            writer.WriteString("call_id", tc.Id);
                            writer.WriteString("name", tc.Name);
                            if (tc.Arguments is not null)
                            {
                                writer.WriteString("arguments", JsonSerializer.Serialize(tc.Arguments, JsonOptions));
                            }
                            writer.WriteEndObject();
                        }
                    }
                    else
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "message");
                        writer.WriteString("role", "assistant");
                        writer.WritePropertyName("content");
                        writer.WriteStartArray();
                        writer.WriteStartObject();
                        writer.WriteString("type", "output_text");
                        writer.WriteString("text", msg.TextUtf8);
                        writer.WriteEndObject();
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                    break;
                }

                case MessageRole.ToolResult:
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function_call_output");
                    writer.WriteString("call_id", msg.ToolCallId ?? "");
                    writer.WriteString("output", msg.TextUtf8);
                    writer.WriteEndObject();
                    break;
                }
            }
        }
    }


    /// <summary>
    /// Parse a Responses API SSE chunk.
    /// Stateless: per-stream state lives entirely in the toolCallAccumulators
    /// dictionary (keyed by int index). Item IDs are matched by scanning
    /// accumulator values for matching ItemId. This avoids mutable instance state.
    /// </summary>
    public StreamEvent? ParseSseChunk(
        ReadOnlyMemory<byte> data,
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
                    try
                    {
                        args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson);
                    }
                    catch
                    {
                        args = new();
                    }

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
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteRequestBody(writer, model, messages, systemPrompt, tools, options);
        writer.Flush();
        var doc = JsonDocument.Parse(buffer.WrittenMemory);
        var root = JsonNode.Parse(doc.RootElement.GetRawText())!;
        return (JsonObject)root;
    }

    public void WriteRequestBody(
        Utf8JsonWriter writer,
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("model", model.Id);
        writer.WriteNumber("max_tokens", options.MaxTokens ?? 4096);
        writer.WriteBoolean("stream", true);
        if (options.Temperature.HasValue)
            writer.WriteNumber("temperature", options.Temperature.Value);
        if (!string.IsNullOrEmpty(systemPrompt))
            writer.WriteString("system", systemPrompt);

        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        foreach (var msg in messages)
        {
            switch (msg.Role)
            {
                case MessageRole.User:
                    writer.WriteStartObject();
                    writer.WriteString("role", "user");
                    writer.WriteString("content", msg.TextUtf8);
                    writer.WriteEndObject();
                    break;
                case MessageRole.Assistant:
                    if (msg.ToolCalls is { Count: > 0 } || msg.ToolCall is not null)
                    {
                        WriteAnthropicToolCalls(writer, msg);
                    }
                    else
                    {
                        writer.WriteStartObject();
                        writer.WriteString("role", "assistant");
                        writer.WriteString("content", msg.TextUtf8);
                        writer.WriteEndObject();
                    }
                    break;
                case MessageRole.ToolResult:
                    writer.WriteStartObject();
                    writer.WriteString("role", "user");
                    writer.WritePropertyName("content");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_result");
                    writer.WriteString("tool_use_id", msg.ToolCallId ?? "");
                    writer.WriteString("content", msg.TextUtf8);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    break;
            }
        }
        writer.WriteEndArray();

        if (tools?.Count > 0)
        {
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                if (tool.Parameters.HasValue)
                {
                    writer.WritePropertyName("input_schema");
                    JsonDocument.Parse(tool.Parameters.Value.GetRawText()).WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteAnthropicToolCalls(Utf8JsonWriter writer, Message msg)
    {
        var allToolCalls = msg.ToolCalls ?? (msg.ToolCall is not null ? new List<ToolCallContent> { msg.ToolCall } : []);
        writer.WriteStartObject();
        writer.WriteString("role", "assistant");
        writer.WritePropertyName("content");
        writer.WriteStartArray();
        foreach (var tc in allToolCalls)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "tool_use");
            writer.WriteString("id", tc.Id);
            writer.WriteString("name", tc.Name);
            writer.WritePropertyName("input");
            JsonDocument.Parse(JsonSerializer.Serialize(tc.Arguments)).WriteTo(writer);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public StreamEvent? ParseSseChunk(
        ReadOnlyMemory<byte> data,
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
                        try
                        {
                            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(acc.Args.ToString());
                        }
                        catch
                        {
                            args = new();
                        }
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
