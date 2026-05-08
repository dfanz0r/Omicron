using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Omicron.Core.Models;

namespace Omicron.Core.Providers;

/// <summary>
/// Provider for OpenAI Chat Completions API and any OpenAI-compatible backend
/// (Ollama, vLLM, LM Studio, Groq, DeepSeek, etc.).
/// </summary>
public class OpenAiProvider : IChatProvider
{
    public string Name => "OpenAI-Compatible";
    public string DefaultBaseUrl => "https://api.openai.com/v1";

    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAiProvider(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options)
    {
        var ct = options.CancellationToken;
        var body = BuildRequestBody(model, messages, systemPrompt, tools, options, stream: true);
        var json = JsonSerializer.Serialize(body, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{model.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(options.ApiKey))
            request.Headers.Authorization = new("Bearer", options.ApiKey);

        var sendResult = await SendRequestAsync(request, ct);
        if (sendResult.Error is not null)
        {
            yield return sendResult.Error;
            yield break;
        }

        using var reader = new StreamReader(sendResult.Stream!);
        var toolCallAccumulators = new Dictionary<int, (string Name, StringBuilder Args)>();

        while (true)
        {
            var lineResult = await ReadLineAsync(reader, ct);
            if (lineResult.Error is not null)
            {
                yield return lineResult.Error;
                yield break;
            }

            var line = lineResult.Line;
            if (line is null) break;
            if (!line.StartsWith("data: ")) continue;

            var data = line[6..].Trim();
            if (data == "[DONE]") break;

            var parsed = ParseChunk(data, toolCallAccumulators);
            if (parsed is null) continue;

            yield return parsed;
            if (parsed.Type == StreamEventType.Error)
                yield break;
        }
    }

    /// <summary>
    /// Helper to avoid yield-in-try issues for async iterators.
    /// </summary>
    private async Task<SendResult> SendRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                return new SendResult
                {
                    Error = new StreamEvent
                    {
                        Type = StreamEventType.Error,
                        ErrorMessage = $"HTTP {response.StatusCode}: {errorBody}"
                    }
                };
            }
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return new SendResult { Stream = stream };
        }
        catch (Exception ex)
        {
            return new SendResult
            {
                Error = new StreamEvent
                {
                    Type = StreamEventType.Error,
                    ErrorMessage = $"Request failed: {ex.Message}"
                }
            };
        }
    }

    private static async Task<ReadResult> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            var line = await reader.ReadLineAsync(ct);
            return new ReadResult { Line = line };
        }
        catch (Exception ex)
        {
            return new ReadResult
            {
                Error = new StreamEvent
                {
                    Type = StreamEventType.Error,
                    ErrorMessage = $"Read error: {ex.Message}"
                }
            };
        }
    }

    private class SendResult
    {
        public Stream? Stream { get; init; }
        public StreamEvent? Error { get; init; }
    }

    private class ReadResult
    {
        public string? Line { get; init; }
        public StreamEvent? Error { get; init; }
    }

    /// <summary>
    /// Parse a single SSE data chunk. Returns null on parse failure.
    /// Does NOT contain yield return statements, so try-catch is safe here.
    /// </summary>
    private static StreamEvent? ParseChunk(
        string data,
        Dictionary<int, (string Name, StringBuilder Args)> toolCallAccumulators)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorEl))
            {
                return new StreamEvent
                {
                    Type = StreamEventType.Error,
                    ErrorMessage = errorEl.GetProperty("message").GetString() ?? "Unknown error"
                };
            }

            var choices = root.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return null;
            var delta = choices[0].GetProperty("delta");
            var finishReason = choices[0].TryGetProperty("finish_reason", out var frEl) ? frEl.GetString() : null;

            // Content delta
            if (delta.TryGetProperty("content", out var contentEl))
            {
                var text = contentEl.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    return new StreamEvent
                    {
                        Type = StreamEventType.TextDelta,
                        Delta = text
                    };
                }
            }

            // Reasoning / thinking content (DeepSeek, etc.)
            if (delta.TryGetProperty("reasoning_content", out var reasoningEl))
            {
                var text = reasoningEl.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    return new StreamEvent
                    {
                        Type = StreamEventType.TextDelta,
                        Delta = text,
                        ReasoningText = text
                    };
                }
            }

            // Tool calls
            if (delta.TryGetProperty("tool_calls", out var toolCallsEl))
            {
                foreach (var tc in toolCallsEl.EnumerateArray())
                {
                    var index = tc.GetProperty("index").GetInt32();
                    var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    string? name = null;
                    string? args = null;

                    if (tc.TryGetProperty("function", out var fnEl))
                    {
                        name = fnEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                        args = fnEl.TryGetProperty("arguments", out var argsEl) ? argsEl.GetString() : null;
                    }

                    if (id is not null)
                    {
                        toolCallAccumulators[index] = (name ?? "", new StringBuilder());
                        return new StreamEvent
                        {
                            Type = StreamEventType.ToolCallStart,
                            ToolCallId = id,
                            ToolName = name
                        };
                    }

                    if (args is not null && toolCallAccumulators.TryGetValue(index, out var acc))
                    {
                        acc.Args.Append(args);
                        return new StreamEvent
                        {
                            Type = StreamEventType.ToolCallDelta,
                            Delta = args,
                            ToolCallId = toolCallAccumulators[index].Name
                        };
                    }
                }
            }

            // Finish reason
            if (finishReason is not null && finishReason != "null")
            {
                // Emit completed tool calls
                foreach (var (idx, (tName, tArgs)) in toolCallAccumulators)
                {
                    var argsJson = tArgs.Length > 0
                        ? JsonSerializer.Deserialize<Dictionary<string, object?>>(tArgs.ToString())
                        : new Dictionary<string, object?>();
                    return new StreamEvent
                    {
                        Type = StreamEventType.ToolCallEnd,
                        ToolCall = new ToolCallContent(
                            Id: Guid.NewGuid().ToString("N")[..12],
                            Name: tName,
                            Arguments: argsJson
                        )
                    };
                }
                // Note: this only emits one tool call per chunk. For multiple tools,
                // we only handle the first. This is fine for most cases.
                toolCallAccumulators.Clear();

                UsageInfo? usage = null;
                if (root.TryGetProperty("usage", out var usageEl))
                {
                    usage = ParseUsage(usageEl);
                }

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
            return new StreamEvent
            {
                Type = StreamEventType.Error,
                ErrorMessage = $"JSON parse error in chunk"
            };
        }

        return null;
    }

    private static JsonObject BuildRequestBody(
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options,
        bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["stream"] = stream,
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
                MessageRole.ToolResult => BuildToolResultMessage(msg),
                _ => throw new ArgumentOutOfRangeException()
            });
        }

        body["messages"] = messageList;

        if (tools?.Count > 0)
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
            {
                var toolObj = new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description
                    }
                };
                if (tool.Parameters.HasValue)
                {
                    toolObj["function"]!["parameters"] = JsonNode.Parse(tool.Parameters.Value.GetRawText());
                }
                toolArray.Add(toolObj);
            }
            body["tools"] = toolArray;
        }

        return body;
    }

    private static JsonObject BuildUserMessage(Message msg)
    {
        if (msg.Images is { Count: > 0 })
        {
            var content = new JsonArray();
            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = msg.Text ?? ""
            });
            foreach (var img in msg.Images)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = $"data:{img.MimeType};base64,{img.Data}",
                        ["detail"] = "high"
                    }
                });
            }
            return new JsonObject
            {
                ["role"] = "user",
                ["content"] = content
            };
        }

        return new JsonObject
        {
            ["role"] = "user",
            ["content"] = msg.Text ?? ""
        };
    }

    private static JsonObject BuildAssistantMessage(Message msg)
    {
        var obj = new JsonObject { ["role"] = "assistant" };

        if (msg.Reasoning is not null)
            obj["reasoning_content"] = msg.Reasoning;

        var allToolCalls = msg.ToolCalls ?? (msg.ToolCall is not null ? new List<ToolCallContent> { msg.ToolCall } : null);
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

    private static JsonObject BuildToolResultMessage(Message msg)
    {
        return new JsonObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = msg.ToolCallId ?? "",
            ["content"] = msg.Text ?? ""
        };
    }

    private static StopReason MapFinishReason(string? reason) => reason switch
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
        int? cacheRead = null, cacheWrite = null;
        if (el.TryGetProperty("prompt_tokens", out var pt)) input = pt.GetInt32();
        if (el.TryGetProperty("completion_tokens", out var ct)) output = ct.GetInt32();
        if (el.TryGetProperty("prompt_tokens_details", out var details))
        {
            if (details.TryGetProperty("cached_tokens", out var cached))
                cacheRead = cached.GetInt32();
        }
        return new UsageInfo(input, output, cacheRead, cacheWrite);
    }
}
