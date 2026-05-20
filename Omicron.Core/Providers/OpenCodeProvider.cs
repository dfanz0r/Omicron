using System.Net.Http.Headers;
using System.Text.Json;
using Omicron.Core.Models;

namespace Omicron.Core.Providers;

/// <summary>
///     OpenCode Zen & Go provider.
///     Routes requests through OpenCode's proxy, selecting the correct
///     <see cref="IApiShape" /> based on each model's <see cref="ApiType" />.
///     OpenCode Zen supports 4 transport types:
///     - OpenAI Chat Completions (most models)
///     - OpenAI Responses (GPT-5+ series)
///     - Anthropic Messages (Claude series)
///     - Google Generative AI (Gemini series)
///     OpenCode Go always uses OpenAI Chat Completions.
///     Anthropic models use a different base URL (https://opencode.ai/zen)
///     vs OpenAI-compatible models (https://opencode.ai/zen/v1).
/// </summary>
public class OpenCodeProvider : ShapeBasedProvider
{
    private const string ZenBaseUrl = "https://opencode.ai/zen/v1";
    private const string ZenAnthropicBaseUrl = "https://opencode.ai/zen";
    private const string GoBaseUrl = "https://opencode.ai/zen/go/v1";

    /// <summary>
    ///     Known model IDs that use the Responses API.
    /// </summary>
    private static readonly HashSet<string> ResponsesModelIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "gpt-5.5",
        "gpt-5.5-pro",
        "gpt-5.4",
        "gpt-5.4-pro",
        "gpt-5.4-mini",
        "gpt-5.4-nano",
        "gpt-5.3-codex",
        "gpt-5.3-codex-spark",
        "gpt-5.2",
        "gpt-5.2-codex",
        "gpt-5.1",
        "gpt-5.1-codex",
        "gpt-5.1-codex-max",
        "gpt-5.1-codex-mini",
        "gpt-5",
        "gpt-5-codex",
        "gpt-5-nano"
    };

    private readonly string? _apiKeyEnvVar;

    private readonly bool _isGo;
    private readonly Dictionary<ApiType, IApiShape> _shapes;

    public OpenCodeProvider(bool isGo = false, HttpClient? http = null)
        : base(http)
    {
        _isGo = isGo;
        _apiKeyEnvVar = isGo ? "OPENCODE_GO_API_KEY" : "OPENCODE_API_KEY";

        _shapes = new Dictionary<ApiType, IApiShape>
        {
            [ApiType.OpenAiChat] = new OpenAiChatShape(),
            [ApiType.OpenAiResponses] = new OpenAiResponsesShape(),
            [ApiType.AnthropicMessages] = new AnthropicMessagesShape()
        };
    }

    public override string Name => _isGo ? "OpenCode Go" : "OpenCode Zen";

    public override string DefaultBaseUrl =>
        _isGo ? "https://opencode.ai/zen/go/v1" : "https://opencode.ai/zen/v1";

    protected override IReadOnlyDictionary<ApiType, IApiShape> Shapes => _shapes;

    /// <summary>
    ///     OpenCode uses a different base URL for Anthropic models.
    /// </summary>
    protected override string ResolveBaseUrl(Model model)
    {
        if (_isGo)
        {
            return GoBaseUrl;
        }

        return model.ApiType == ApiType.AnthropicMessages ? ZenAnthropicBaseUrl : ZenBaseUrl;
    }

    protected override void SetAuthHeader(HttpRequestMessage request, string? apiKey)
    {
        string? key = apiKey;
        if (string.IsNullOrEmpty(key))
        {
            key =
                Environment.GetEnvironmentVariable(_apiKeyEnvVar!)
                ?? Environment.GetEnvironmentVariable("OPENCODE_API_KEY");
        }

        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    /// <summary>
    ///     Fetch available models from the OpenCode /models endpoint.
    ///     Returns a list of model entries with id and name.
    ///     Returns empty list on failure.
    /// </summary>
    public async Task<List<OpenCodeModelEntry>> FetchModelsAsync()
    {
        string modelsUrl = _isGo ? $"{GoBaseUrl}/models" : $"{ZenBaseUrl}/models";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
            string? key =
                Environment.GetEnvironmentVariable(_apiKeyEnvVar!)
                ?? Environment.GetEnvironmentVariable("OPENCODE_API_KEY");
            if (!string.IsNullOrEmpty(key))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            using HttpResponseMessage response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (
                !root.TryGetProperty("data", out JsonElement dataEl)
                || dataEl.ValueKind != JsonValueKind.Array
            )
            {
                return [];
            }

            var models = new List<OpenCodeModelEntry>();
            foreach (JsonElement item in dataEl.EnumerateArray())
            {
                string? id = item.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
                string? name = item.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : null;
                if (!string.IsNullOrEmpty(id))
                {
                    models.Add(new OpenCodeModelEntry(id, name ?? id));
                }
            }

            return models;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    ///     Determine the ApiType for an OpenCode Zen model based on its ID.
    /// </summary>
    public static ApiType ResolveApiType(string modelId, bool isGo = false)
    {
        if (isGo)
        {
            return ApiType.OpenAiChat;
        }

        // Anthropic models
        if (modelId.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            return ApiType.AnthropicMessages;
        }

        // Google/Gemini models
        if (modelId.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
        {
            return ApiType.GoogleGenAi;
        }

        // GPT-5+ series uses Responses API
        if (
            modelId.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || IsResponsesModel(modelId)
        )
        {
            return ApiType.OpenAiResponses;
        }

        // Default: OpenAI Chat Completions
        return ApiType.OpenAiChat;
    }

    private static bool IsResponsesModel(string modelId)
    {
        return ResponsesModelIds.Contains(modelId);
    }
}

/// <summary>
///     A model entry fetched from an OpenCode /models endpoint.
/// </summary>
public record OpenCodeModelEntry(string Id, string Name);
