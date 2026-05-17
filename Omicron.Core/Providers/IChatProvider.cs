using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Omicron.Core.Content;
using Omicron.Core.Models;
using Omicron.Core.Sessions;

namespace Omicron.Core.Providers;

/// <summary>
/// Options for a single LLM call.
/// Also carries provider state context for stateful API support.
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
    /// The provider state key scoping this request.
    /// Set by AgentSession before calling the provider.
    /// </summary>
    public ProviderStateKey? ProviderStateKey { get; init; }

    /// <summary>
    /// The current provider turn state (if any) for stateful continuation.
    /// When present and the model supports stateful mode, the provider
    /// should send previous_response_id and only new input items.
    /// </summary>
    public ProviderTurnState? CurrentProviderState { get; init; }

    /// <summary>
    /// Storage policy for this request. Overrides Model.StoragePolicy
    /// if set. Used by Responses API shape to decide store flag and
    /// whether to use previous_response_id.
    /// </summary>
    public ProviderStoragePolicy? StoragePolicy { get; init; }
}

/// <summary>
/// Streaming event emitted during an LLM response.
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
/// A single chunk of a streaming LLM response.
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
/// Interface all LLM providers must implement.
/// A provider is the "backend service" layer — it handles the base URL,
/// authentication, and model→shape routing.
/// </summary>
public interface IChatProvider
{
    /// <summary>Human-readable name (e.g. "OpenCode Zen", "OpenRouter").</summary>
    string Name { get; }

    /// <summary>The default base URL for this provider's API.</summary>
    string DefaultBaseUrl { get; }

    /// <summary>
    /// Send messages to the LLM and stream the response.
    /// Implementations use <see cref="Model.ApiType"/> to select the
    /// appropriate <see cref="IApiShape"/> for wire formatting.
    /// </summary>
    IAsyncEnumerable<StreamEvent> StreamAsync(
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options);

    /// <summary>
    /// Non-streaming convenience: collects all events and returns a single LlmResult.
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

        await foreach (var evt in StreamAsync(model, messages, systemPrompt, tools, options))
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
/// Base HTTP + SSE streaming logic used by providers that delegate
/// to an <see cref="IApiShape"/> for wire formatting.
/// </summary>
public abstract class ShapeBasedProvider : IChatProvider
{
    public abstract string Name { get; }
    public abstract string DefaultBaseUrl { get; }
    protected HttpClient HttpClient { get; }

    /// <summary>Map from ApiType to IApiShape instances.</summary>
    protected abstract IReadOnlyDictionary<ApiType, IApiShape> Shapes { get; }

    /// <summary>
    /// Optionally override the base URL per model (e.g. OpenCode Zen
    /// uses a different URL for Anthropic models).
    /// </summary>
    protected virtual string ResolveBaseUrl(Model model) => model.BaseUrl;

    /// <summary>
    /// Optionally set custom request headers per provider/model.
    /// </summary>
    protected virtual void SetAuthHeader(HttpRequestMessage request, string? apiKey) { }

    protected ShapeBasedProvider(HttpClient? http = null)
    {
        HttpClient = http ?? new HttpClient();
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options)
    {
        var ct = options.CancellationToken;

        // Resolve the ApiShape for this model
        if (!Shapes.TryGetValue(model.ApiType, out var shape))
        {
            yield return new StreamEvent
            {
                Type = StreamEventType.Error,
                ErrorMessage = $"No ApiShape registered for {model.ApiType} in provider '{Name}'"
            };
            yield break;
        }

        var body = shape.BuildRequestBody(model, messages, systemPrompt, tools, options);
        var json = JsonSerializer.Serialize(body);

        var baseUrl = ResolveBaseUrl(model);
        var endpoint = model.ApiType switch
        {
            ApiType.AnthropicMessages => $"{baseUrl.TrimEnd('/')}/messages",
            ApiType.OpenAiResponses => $"{baseUrl.TrimEnd('/')}/responses",
            ApiType.GoogleGenAi => throw new NotSupportedException("GoogleGenAi is not yet implemented."),
            _ => $"{baseUrl.TrimEnd('/')}/chat/completions"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // Auth header
        SetAuthHeader(request, options.ApiKey);

        // Anthropic version header
        if (model.ApiType == ApiType.AnthropicMessages && !string.IsNullOrEmpty(options.ApiKey))
        {
            request.Headers.Add("anthropic-version", "2023-06-01");
        }

        var sendResult = await SendRequestAsync(request, ct);
        if (sendResult.Error is not null)
        {
            yield return sendResult.Error;
            yield break;
        }

        using var reader = new StreamReader(sendResult.Stream!);
        var toolCallAccumulators = new Dictionary<int, ToolCallAccumulator>();

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

            var parsed = shape.ParseSseChunk(data, toolCallAccumulators);
            if (parsed is null) continue;

            yield return parsed;
            if (parsed.Type == StreamEventType.Error)
                yield break;
        }

        // Drain remaining tool call accumulators not emitted by the final
        // finish_reason chunk. OpenAI Chat sends a single finish chunk for
        // all tools, but ParseSseChunk only emits one ToolCallEnd per call.
        // Anthropic already removes each accumulator on content_block_stop
        // so this is typically a no-op for non-OpenAI shapes.
        foreach (var (_, acc) in toolCallAccumulators)
        {
            Dictionary<string, object?>? args = null;
            try
            {
                args = acc.Args.Length > 0
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(acc.Args.ToString())
                    : new Dictionary<string, object?>();
            }
            catch
            {
                args = new();
            }
            yield return new StreamEvent
            {
                Type = StreamEventType.ToolCallEnd,
                ToolCall = new ToolCallContent(
                    acc.Id ?? Guid.NewGuid().ToString("N")[..12],
                    acc.Name,
                    args ?? new())
            };
        }
    }

    private async Task<SendResult> SendRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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
}
