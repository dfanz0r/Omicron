using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Omicron.Core.Models;

namespace Omicron.Core.Providers;

/// <summary>
///     Provider for Anthropic's Messages API (Claude).
/// </summary>
public class AnthropicProvider : IChatProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;

    public AnthropicProvider(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
    }

    public string Name => "Anthropic";
    public string DefaultBaseUrl => "https://api.anthropic.com/v1";

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options)
    {
        CancellationToken ct = options.CancellationToken;
        var buffer = new ArrayBufferWriter<byte>();
        using (var jsonWriter = new Utf8JsonWriter(buffer))
        {
            WriteRequestBody(jsonWriter, model, messages, systemPrompt, tools, options);
        }

        ReadOnlySpan<byte> jsonBytes = buffer.WrittenSpan;

        var content = new ByteArrayContent(jsonBytes.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{model.BaseUrl.TrimEnd('/')}/messages")
        {
            Content = content
        };
        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            request.Headers.Add("x-api-key", options.ApiKey);
        }

        request.Headers.Add("anthropic-version", "2023-06-01");

        SendResult sendResult = await SendRequestAsync(request, ct);
        if (sendResult.Error is not null)
        {
            yield return sendResult.Error;
            yield break;
        }

        Stream stream = sendResult.Stream!;
        var toolCallAccumulators =
            new Dictionary<int, (string Id, string Name, StringBuilder Args)>();

        // Read SSE frames using a reusable byte buffer, scanning for LF-delimited lines.
        byte[] sseBuffer = new byte[8192];
        int sseLen = 0;

        while (true)
        {
            int lfIndex = -1;
            for (int i = 0; i < sseLen; i++)
            {
                if (sseBuffer[i] == (byte)'\n')
                {
                    lfIndex = i;
                    break;
                }
            }

            if (lfIndex < 0)
            {
                if (sseLen >= sseBuffer.Length)
                {
                    yield return new StreamEvent
                    {
                        Type = StreamEventType.Error,
                        ErrorMessage = "SSE line exceeds buffer size"
                    };
                    yield break;
                }

                int read = await stream.ReadAsync(sseBuffer.AsMemory(sseLen), ct);
                if (read == 0)
                {
                    break;
                }

                sseLen += read;
                continue;
            }

            var lineSpan = new ReadOnlySpan<byte>(sseBuffer, 0, lfIndex);
            if (lineSpan.Length > 0 && lineSpan[^1] == (byte)'\r')
            {
                lineSpan = lineSpan[..^1];
            }

            // Strip UTF-8 BOM if present
            if (
                lineSpan.Length >= 3
                && lineSpan[0] == 0xEF
                && lineSpan[1] == 0xBB
                && lineSpan[2] == 0xBF
            )
            {
                lineSpan = lineSpan[3..];
            }

            bool isDataLine =
                lineSpan.Length >= 6
                && lineSpan[0] == (byte)'d'
                && lineSpan[1] == (byte)'a'
                && lineSpan[2] == (byte)'t'
                && lineSpan[3] == (byte)'a'
                && lineSpan[4] == (byte)':'
                && lineSpan[5] == (byte)' ';

            if (!isDataLine)
            {
                int skipRemaining = sseLen - lfIndex - 1;
                if (skipRemaining > 0)
                {
                    Buffer.BlockCopy(sseBuffer, lfIndex + 1, sseBuffer, 0, skipRemaining);
                }

                sseLen = skipRemaining;
                continue;
            }

            ReadOnlySpan<byte> payload = lineSpan[6..];
            // Trim trailing whitespace
            while (
                payload.Length > 0
                && (
                    payload[^1] == (byte)' '
                    || payload[^1] == (byte)'\t'
                    || payload[^1] == (byte)'\r'
                )
            )
            {
                payload = payload[..^1];
            }

            while (payload.Length > 0 && (payload[0] == (byte)' ' || payload[0] == (byte)'\t'))
            {
                payload = payload[1..];
            }

            // Copy payload to heap before shifting buffer
            byte[] payloadBytes = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.CopyTo(payloadBytes);
            Memory<byte> payloadMemory = payloadBytes.AsMemory(0, payload.Length);

            int dataRemaining = sseLen - lfIndex - 1;
            if (dataRemaining > 0)
            {
                Buffer.BlockCopy(sseBuffer, lfIndex + 1, sseBuffer, 0, dataRemaining);
            }

            sseLen = dataRemaining;

            // Check for [DONE]
            if (payloadMemory.Span.SequenceEqual("[DONE]"u8))
            {
                ArrayPool<byte>.Shared.Return(payloadBytes);
                break;
            }

            StreamEvent? parsed = ParseChunk(payloadMemory, toolCallAccumulators);
            ArrayPool<byte>.Shared.Return(payloadBytes);
            if (parsed is null)
            {
                continue;
            }

            yield return parsed;
            if (parsed.Type == StreamEventType.Error)
            {
                yield break;
            }
        }
    }

    /// <summary>
    ///     Helper to avoid yield-in-try issues for async iterators.
    /// </summary>
    private async Task<SendResult> SendRequestAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        try
        {
            HttpResponseMessage response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(ct);
                return new SendResult
                {
                    Error = new StreamEvent
                    {
                        Type = StreamEventType.Error,
                        ErrorMessage = $"HTTP {response.StatusCode}: {errorBody}"
                    }
                };
            }

            Stream stream = await response.Content.ReadAsStreamAsync(ct);
            return new SendResult
            {
                Stream = stream
            };
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

    /// <summary>
    ///     Parse a single SSE event from Anthropic. Returns null if no event should be emitted.
    /// </summary>
    private static StreamEvent? ParseChunk(
        ReadOnlyMemory<byte> data,
        Dictionary<int, (string Id, string Name, StringBuilder Args)> toolCallAccumulators)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            JsonElement root = doc.RootElement;
            string? type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "content_block_start":
                    {
                        string? blockType = root.GetProperty("content_block")
                            .GetProperty("type")
                            .GetString();
                        int index = root.GetProperty("index").GetInt32();
                        if (blockType == "tool_use")
                        {
                            string id =
                                root.GetProperty("content_block").GetProperty("id").GetString() ?? "";
                            string name =
                                root.GetProperty("content_block").GetProperty("name").GetString() ?? "";
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
                        JsonElement delta = root.GetProperty("delta");
                        string? deltaType = delta.GetProperty("type").GetString();
                        int index = root.GetProperty("index").GetInt32();

                        if (deltaType == "text_delta")
                        {
                            string? text = delta.GetProperty("text").GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                return new StreamEvent
                                {
                                    Type = StreamEventType.TextDelta,
                                    Delta = text
                                };
                            }
                        }
                        else if (
                            deltaType == "input_json_delta"
                            && toolCallAccumulators.TryGetValue(index,
                                out (string Id, string Name, StringBuilder Args) acc)
                        )
                        {
                            string? partial = delta.GetProperty("partial_json").GetString();
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
                        int index = root.GetProperty("index").GetInt32();
                        if (toolCallAccumulators.TryGetValue(index,
                                out (string Id, string Name, StringBuilder Args) acc))
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
                                ToolCall = new ToolCallContent(acc.Id, acc.Name,
                                    args ?? new Dictionary<string, object?>())
                            };
                        }

                        break;
                    }

                case "message_delta":
                    {
                        string? stopReason = root.GetProperty("delta")
                            .GetProperty("stop_reason")
                            .GetString();
                        UsageInfo? usage = root.TryGetProperty("usage", out JsonElement usageEl)
                            ? ParseUsage(usageEl)
                            : null;
                        string? responseId = root.TryGetProperty("id", out JsonElement idEl)
                            ? idEl.GetString()
                            : null;
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
                        JsonElement error = root.GetProperty("error");
                        string? msg = error.TryGetProperty("message", out JsonElement msgEl)
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

    private void WriteRequestBody(
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
        {
            writer.WriteNumber("temperature", options.Temperature.Value);
        }

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            writer.WriteString("system", systemPrompt);
        }

        writer.WritePropertyName("messages");
        writer.WriteStartArray();

        foreach (Message msg in messages)
        {
            switch (msg.Role)
            {
                case MessageRole.User:
                    writer.WriteStartObject();
                    writer.WriteString("role", "user");
                    writer.WriteString("content", msg.TextUtf8);
                    writer.WriteEndObject();
                    break;

                case MessageRole.Assistant
                    when msg.ToolCalls is { Count: > 0 } || msg.ToolCall is not null:
                    WriteAnthropicToolCalls(writer, msg);
                    break;

                case MessageRole.Assistant:
                    writer.WriteStartObject();
                    writer.WriteString("role", "assistant");
                    writer.WriteString("content", msg.TextUtf8);
                    writer.WriteEndObject();
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
            foreach (Tool tool in tools)
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

    private static StopReason MapStopReason(string? reason)
    {
        return reason switch
        {
            "end_turn" => StopReason.Stop,
            "max_tokens" => StopReason.Length,
            "tool_use" => StopReason.ToolUse,
            "error" => StopReason.Error,
            _ => StopReason.Stop
        };
    }

    private static void WriteAnthropicToolCalls(Utf8JsonWriter writer, Message msg)
    {
        IReadOnlyList<ToolCallContent> allToolCalls =
            msg.ToolCalls
            ?? (msg.ToolCall is not null
                ? new List<ToolCallContent>
                {
                    msg.ToolCall
                }
                : []);
        writer.WriteStartObject();
        writer.WriteString("role", "assistant");
        writer.WritePropertyName("content");
        writer.WriteStartArray();
        foreach (ToolCallContent tc in allToolCalls)
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

    private static UsageInfo ParseUsage(JsonElement el)
    {
        int input = 0,
            output = 0;
        if (el.TryGetProperty("input_tokens", out JsonElement it))
        {
            input = it.GetInt32();
        }

        if (el.TryGetProperty("output_tokens", out JsonElement ot))
        {
            output = ot.GetInt32();
        }

        return new UsageInfo(input, output);
    }

    private class SendResult
    {
        public Stream? Stream { get; init; }
        public StreamEvent? Error { get; init; }
    }
}
