using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Omicron.Core.Models;

namespace Omicron.Core.Providers;

/// <summary>
/// Provider for Anthropic's Messages API (Claude).
/// </summary>
public class AnthropicProvider : IChatProvider
{
    public string Name => "Anthropic";
    public string DefaultBaseUrl => "https://api.anthropic.com/v1";

    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AnthropicProvider(HttpClient? http = null)
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
        var body = BuildRequestBody(model, messages, systemPrompt, tools, options);
        var json = JsonSerializer.Serialize(body, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{model.BaseUrl.TrimEnd('/')}/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(options.ApiKey))
            request.Headers.Add("x-api-key", options.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var sendResult = await SendRequestAsync(request, ct);
        if (sendResult.Error is not null)
        {
            yield return sendResult.Error;
            yield break;
        }

        using var reader = new StreamReader(sendResult.Stream!);
        var toolCallAccumulators = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();

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
    /// Parse a single SSE event from Anthropic. Returns null if no event should be emitted.
    /// </summary>
    private static StreamEvent? ParseChunk(
        string data,
        Dictionary<int, (string Id, string Name, StringBuilder Args)> toolCallAccumulators)
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
                        toolCallAccumulators[index] = (id, name, new StringBuilder());
                        return new StreamEvent
                        {
                            Type = StreamEventType.ToolCallStart,
                            ToolCallId = id,
                            ToolName = name
                        };
                    }
                    break;
                }

                case "content_block_delta":
                {
                    var delta = root.GetProperty("delta");
                    var deltaType = delta.GetProperty("type").GetString();
                    var index = root.GetProperty("index").GetInt32();

                    if (deltaType == "text_delta")
                    {
                        var text = delta.GetProperty("text").GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            return new StreamEvent
                            {
                                Type = StreamEventType.TextDelta,
                                Delta = text
                            };
                        }
                    }
                    else if (deltaType == "input_json_delta" && toolCallAccumulators.TryGetValue(index, out var acc))
                    {
                        var partial = delta.GetProperty("partial_json").GetString();
                        if (!string.IsNullOrEmpty(partial))
                        {
                            acc.Args.Append(partial);
                            return new StreamEvent
                            {
                                Type = StreamEventType.ToolCallDelta,
                                Delta = partial,
                                ToolCallId = acc.Id
                            };
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
                            args = new Dictionary<string, object?>();
                        }

                        toolCallAccumulators.Remove(index);
                        return new StreamEvent
                        {
                            Type = StreamEventType.ToolCallEnd,
                            ToolCall = new ToolCallContent(acc.Id, acc.Name, args ?? new())
                        };
                    }
                    break;
                }

                case "message_delta":
                {
                    var stopReason = root.GetProperty("delta").GetProperty("stop_reason").GetString();
                    var usage = root.TryGetProperty("usage", out var usageEl) ? ParseUsage(usageEl) : null;
                    var responseId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    return new StreamEvent
                    {
                        Type = StreamEventType.Done,
                        StopReason = MapStopReason(stopReason),
                        Delta = responseId,
                        Usage = usage
                    };
                }

                case "message_stop":
                    break;

                case "error":
                {
                    var error = root.GetProperty("error");
                    var msg = error.TryGetProperty("message", out var msgEl)
                        ? msgEl.GetString()
                        : error.GetRawText();
                    return new StreamEvent
                    {
                        Type = StreamEventType.Error,
                        ErrorMessage = msg ?? "Unknown Anthropic error"
                    };
                }
            }
        }
        catch (JsonException)
        {
            return new StreamEvent
            {
                Type = StreamEventType.Error,
                ErrorMessage = "JSON parse error in Anthropic stream chunk"
            };
        }

        return null;
    }

    private static JsonObject BuildRequestBody(
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
                MessageRole.User => new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = msg.Text ?? ""
                },
                MessageRole.Assistant when msg.ToolCalls is { Count: > 0 } || msg.ToolCall is not null => BuildAnthropicToolCalls(msg),
                MessageRole.Assistant => new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = msg.Text ?? ""
                },
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
                var toolObj = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description
                };
                if (tool.Parameters.HasValue)
                {
                    toolObj["input_schema"] = JsonNode.Parse(tool.Parameters.Value.GetRawText());
                }
                toolArray.Add(toolObj);
            }
            body["tools"] = toolArray;
        }

        return body;
    }

    private static StopReason MapStopReason(string? reason) => reason switch
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
