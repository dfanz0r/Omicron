using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Omicron.Core.Models;

namespace Omicron.Core.Providers;

// ============================================================
// Stage 1: ApiShape — wire-protocol formatters & parsers
// ============================================================

/// <summary>
/// Accumulates tool call data across SSE chunks.
/// </summary>
public class ToolCallAccumulator
{
    public string? Id { get; set; }
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

public class OpenAiResponsesShape : IApiShape
{
    public string Name => "OpenAI Responses";
    public ApiType ApiType => ApiType.OpenAiResponses;

    public JsonObject BuildRequestBody(
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options)
    {
        // Responses API uses a different structure. For now, fall back to
        // OpenAI Chat shape. Real implementation would use the /responses endpoint.
        // Most OpenCode Zen GPT models still work through chat completions.
        return new OpenAiChatShape().BuildRequestBody(model, messages, systemPrompt, tools, options);
    }

    public StreamEvent? ParseSseChunk(
        string data,
        Dictionary<int, ToolCallAccumulator> toolCallAccumulators)
    {
        // Same SSE format as chat completions for most models proxied through OpenCode
        return new OpenAiChatShape().ParseSseChunk(data, toolCallAccumulators);
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
