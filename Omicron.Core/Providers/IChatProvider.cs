using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using Omicron.Core.Content;
using Omicron.Core.Models;
using Omicron.Core.Sessions;

namespace Omicron.Core.Providers;

/// <summary>
///     Options for a single LLM call.
///     Also carries provider state context for stateful API support.
/// </summary>
public record ChatOptions
{
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public string? ApiKey { get; init; }
    public string? ReasoningEffort { get; init; }
    public CancellationToken CancellationToken { get; init; }

    // ============================================================
    // Provider state context (for stateful API support)
    // These are immutable request context fields set by AgentSession.
    // Providers must NOT mutate session or provider state directly;
    // all state persistence is owned by AgentSession.
    // ============================================================

    /// <summary>
    ///     The provider state key scoping this request.
    ///     Set by AgentSession before calling the provider.
    /// </summary>
    public ProviderStateKey? ProviderStateKey { get; init; }

    /// <summary>
    ///     The current provider turn state (if any) for stateful continuation.
    ///     When present and the model supports stateful mode, the provider
    ///     should send previous_response_id and only new input items.
    /// </summary>
    public ProviderTurnState? CurrentProviderState { get; init; }

    /// <summary>
    ///     Storage policy for this request. Overrides Model.StoragePolicy
    ///     if set. Used by Responses API shape to decide store flag and
    ///     whether to use previous_response_id.
    /// </summary>
    public ProviderStoragePolicy? StoragePolicy { get; init; }
}

/// <summary>
///     Streaming event emitted during an LLM response.
/// </summary>
public enum StreamEventType
{
    TextDelta,
    ToolCallStart,
    ToolCallDelta,
    ToolCallEnd,
    Done,
    Error
}

/// <summary>
///     A single chunk of a streaming LLM response.
/// </summary>
public class StreamEvent
{
    public StreamEventType Type { get; init; }
    public string? Delta { get; init; }
    public ToolCallContent? ToolCall { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public string? ErrorMessage { get; init; }
    public UsageInfo? Usage { get; init; }
    public StopReason? StopReason { get; init; }

    /// <summary>Reasoning/thinking text (DeepSeek, OpenAI o-series). Must be echoed back on subsequent requests.</summary>
    public string? ReasoningText { get; init; }
}

// ============================================================
// Stage 2: IChatProvider — routes through a backend, using
// the model's ApiType to select the right wire shape.
// ============================================================

/// <summary>
///     Interface all LLM providers must implement.
///     A provider is the "backend service" layer — it handles the base URL,
///     authentication, and model→shape routing.
/// </summary>
public interface IChatProvider
{
    /// <summary>Human-readable name (e.g. "OpenCode Zen", "OpenRouter").</summary>
    string Name { get; }

    /// <summary>The default base URL for this provider's API.</summary>
    string DefaultBaseUrl { get; }

    /// <summary>
    ///     Send messages to the LLM and stream the response.
    ///     Implementations use <see cref="Model.ApiType" /> to select the
    ///     appropriate <see cref="IApiShape" /> for wire formatting.
    /// </summary>
    IAsyncEnumerable<StreamEvent> StreamAsync(
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options);

    /// <summary>
    ///     Non-streaming convenience: collects all events and returns a single LlmResult.
    /// </summary>
    async Task<LlmResult> CompleteAsync(
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options)
    {
        using var text = new Utf8TextAccumulator();
        var toolCalls = new List<ToolCallContent>();
        StopReason stopReason = StopReason.Stop;
        string? errorMessage = null;
        UsageInfo? usage = null;
        string? responseId = null;

        await foreach (StreamEvent evt in StreamAsync(model, messages, systemPrompt, tools, options))
        {
            switch (evt.Type)
            {
                case StreamEventType.TextDelta:
                    text.Append(evt.Delta);
                    break;
                case StreamEventType.ToolCallEnd when evt.ToolCall is not null:
                    toolCalls.Add(evt.ToolCall);
                    break;
                case StreamEventType.Done:
                    stopReason = evt.StopReason ?? StopReason.Stop;
                    usage = evt.Usage;
                    responseId = evt.Delta;
                    break;
                case StreamEventType.Error:
                    stopReason = StopReason.Error;
                    errorMessage = evt.ErrorMessage;
                    break;
            }
        }

        return new LlmResult
        {
            Text = text.ToString(),
            ToolCalls = toolCalls,
            StopReason = stopReason,
            ErrorMessage = errorMessage,
            Usage = usage,
            ResponseId = responseId
        };
    }
}

// ============================================================
// Shared HTTP helpers for providers that delegate to IApiShape
// ============================================================

/// <summary>
///     Base HTTP + SSE streaming logic used by providers that delegate
///     to an <see cref="IApiShape" /> for wire formatting.
/// </summary>
public abstract class ShapeBasedProvider : IChatProvider
{
    protected ShapeBasedProvider(HttpClient? http = null)
    {
        HttpClient = http ?? new HttpClient();
    }

    protected HttpClient HttpClient { get; }

    /// <summary>Map from ApiType to IApiShape instances.</summary>
    protected abstract IReadOnlyDictionary<ApiType, IApiShape> Shapes { get; }

    public abstract string Name { get; }
    public abstract string DefaultBaseUrl { get; }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options)
    {
        CancellationToken ct = options.CancellationToken;

        // Resolve the ApiShape for this model
        if (!Shapes.TryGetValue(model.ApiType, out IApiShape? shape))
        {
            yield return new StreamEvent
            {
                Type = StreamEventType.Error,
                ErrorMessage = $"No ApiShape registered for {model.ApiType} in provider '{Name}'"
            };
            yield break;
        }

        var requestBuffer = new ArrayBufferWriter<byte>();
        using (var jsonWriter = new Utf8JsonWriter(requestBuffer))
        {
            shape.WriteRequestBody(jsonWriter, model, messages, systemPrompt, tools, options);
        }

        ReadOnlySpan<byte> jsonBytes = requestBuffer.WrittenSpan;

        string baseUrl = ResolveBaseUrl(model);
        string endpoint = model.ApiType switch
        {
            ApiType.AnthropicMessages => $"{baseUrl.TrimEnd('/')}/messages",
            ApiType.OpenAiResponses => $"{baseUrl.TrimEnd('/')}/responses",
            ApiType.GoogleGenAi => throw new NotSupportedException("GoogleGenAi is not yet implemented."),
            _ => $"{baseUrl.TrimEnd('/')}/chat/completions"
        };

        var content = new ByteArrayContent(jsonBytes.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = content
        };

        // Auth header
        SetAuthHeader(request, options.ApiKey);

        // Anthropic version header
        if (model.ApiType == ApiType.AnthropicMessages && !string.IsNullOrEmpty(options.ApiKey))
        {
            request.Headers.Add("anthropic-version", "2023-06-01");
        }

        SendResult sendResult = await SendRequestAsync(request, ct);
        if (sendResult.Error is not null)
        {
            yield return sendResult.Error;
            yield break;
        }

        var toolCallAccumulators = new Dictionary<int, ToolCallAccumulator>();
        Stream stream = sendResult.Stream!;

        // Read SSE frames using a reusable byte sseBuffer, scanning for LF-delimited lines.
        // Avoids StreamReader and string allocation for each SSE line.
        byte[] sseBuffer = new byte[8192];
        int sseLen = 0;

        while (true)
        {
            // Find LF in the current sseBuffer
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
                // Need more data: fill the sseBuffer
                if (sseLen >= sseBuffer.Length)
                {
                    // Line too long — grow sseBuffer or skip
                    yield return new StreamEvent
                    {
                        Type = StreamEventType.Error,
                        ErrorMessage = "SSE line exceeds sseBuffer size"
                    };
                    yield break;
                }

                int read = await stream.ReadAsync(sseBuffer.AsMemory(sseLen), ct);
                if (read == 0)
                {
                    break; // EOF
                }

                sseLen += read;
                continue;
            }

            // Extract the line (without \n)
            var lineSpan = new ReadOnlySpan<byte>(sseBuffer, 0, lfIndex);

            // Remove \r if present before \n
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

            // Check for "data: " prefix BEFORE shifting the sseBuffer
            // (the span references the sseBuffer, so shifting would corrupt it).
            bool isDataLine = lineSpan.StartsWith("data: "u8);

            if (!isDataLine)
            {
                // Shift remaining data and skip
                int skipRemaining = sseLen - lfIndex - 1;
                if (skipRemaining > 0)
                {
                    Buffer.BlockCopy(sseBuffer, lfIndex + 1, sseBuffer, 0, skipRemaining);
                }

                sseLen = skipRemaining;
                continue;
            }

            // Slice after "data: " and trim trailing whitespace
            ReadOnlySpan<byte> payload = lineSpan[6..];
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

            // Copy payload to heap before shifting sseBuffer (span references the sseBuffer)
            byte[]? payloadBytes =
                payload.Length > 0 ? ArrayPool<byte>.Shared.Rent(payload.Length) : null;
            if (payloadBytes is not null)
            {
                payload.CopyTo(payloadBytes.AsSpan());
                // Trim to actual length (ArrayPool may return a larger sseBuffer)
                Span<byte> trimmedPayload = payloadBytes.AsSpan(0, payload.Length);

                // Shift remaining data in sseBuffer
                int dataRemaining = sseLen - lfIndex - 1;
                if (dataRemaining > 0)
                {
                    Buffer.BlockCopy(sseBuffer, lfIndex + 1, sseBuffer, 0, dataRemaining);
                }

                sseLen = dataRemaining;

                // Check for [DONE]
                if (trimmedPayload.SequenceEqual("[DONE]"u8))
                {
                    ArrayPool<byte>.Shared.Return(payloadBytes);
                    break;
                }

                StreamEvent? parsed = shape.ParseSseChunk(payloadBytes.AsMemory(0, payload.Length),
                    toolCallAccumulators);
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
            else
            {
                // Empty payload — shift sseBuffer and continue
                int dataRemaining = sseLen - lfIndex - 1;
                if (dataRemaining > 0)
                {
                    Buffer.BlockCopy(sseBuffer, lfIndex + 1, sseBuffer, 0, dataRemaining);
                }

                sseLen = dataRemaining;
            }
        }

        // Drain remaining tool call accumulators not emitted by the final
        // finish_reason chunk. OpenAI Chat sends a single finish chunk for
        // all tools, but ParseSseChunk only emits one ToolCallEnd per call.
        // Anthropic already removes each accumulator on content_block_stop
        // so this is typically a no-op for non-OpenAI shapes.
        foreach ((int _, ToolCallAccumulator acc) in toolCallAccumulators)
        {
            Dictionary<string, object?>? args = null;
            try
            {
                args =
                    acc.Args.Length > 0
                        ? JsonSerializer.Deserialize<Dictionary<string, object?>>(acc.Args.ToString())
                        : new Dictionary<string, object?>();
            }
            catch
            {
                args = new Dictionary<string, object?>();
            }

            yield return new StreamEvent
            {
                Type = StreamEventType.ToolCallEnd,
                ToolCall = new ToolCallContent(acc.Id ?? Guid.NewGuid().ToString("N")[..12],
                    acc.Name,
                    args ?? new Dictionary<string, object?>())
            };
        }
    }

    /// <summary>
    ///     Optionally override the base URL per model (e.g. OpenCode Zen
    ///     uses a different URL for Anthropic models).
    /// </summary>
    protected virtual string ResolveBaseUrl(Model model)
    {
        return model.BaseUrl;
    }

    /// <summary>
    ///     Optionally set custom request headers per provider/model.
    /// </summary>
    protected virtual void SetAuthHeader(HttpRequestMessage request, string? apiKey) { }

    private async Task<SendResult> SendRequestAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        try
        {
            HttpResponseMessage response = await HttpClient.SendAsync(request,
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

    private class SendResult
    {
        public Stream? Stream { get; init; }
        public StreamEvent? Error { get; init; }
    }
}
