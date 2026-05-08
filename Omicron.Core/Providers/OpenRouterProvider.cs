using System.Net.Http.Headers;
using System.Text.Json;
using Omicron.Core.Models;

namespace Omicron.Core.Providers;

/// <summary>
/// OpenRouter provider.
///
/// OpenRouter provides access to hundreds of models from many providers
/// through a single OpenAI-compatible API.
///
/// All models use the OpenAI Chat Completions API shape, routed through
/// OpenRouter's base URL. OpenRouter handles model→provider routing
/// server-side based on the model ID.
///
/// Supports OpenRouter-specific features like provider routing preferences
/// via additional headers (sent in the request).
/// </summary>
public class OpenRouterProvider : ShapeBasedProvider
{
    public override string Name => "OpenRouter";
    public override string DefaultBaseUrl => "https://openrouter.ai/api/v1";

    private const string ApiBaseUrl = "https://openrouter.ai/api/v1";
    private readonly Dictionary<ApiType, IApiShape> _shapes;

    protected override IReadOnlyDictionary<ApiType, IApiShape> Shapes => _shapes;

    /// <summary>Optional OpenRouter-specific headers: allow_fallbacks, order, etc.</summary>
    public string? ProviderRouting { get; set; }

    public OpenRouterProvider(HttpClient? http = null) : base(http)
    {
        _shapes = new Dictionary<ApiType, IApiShape>
        {
            [ApiType.OpenAiChat] = new OpenAiChatShape()
        };
    }

    protected override void SetAuthHeader(HttpRequestMessage request, string? apiKey)
    {
        var key = apiKey;
        if (string.IsNullOrEmpty(key))
            key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (!string.IsNullOrEmpty(key))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        // OpenRouter recommends sending app identity headers
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/omicron-agent");
        request.Headers.TryAddWithoutValidation("X-Title", "Omicron Agent");
    }

    protected override string ResolveBaseUrl(Model model) => ApiBaseUrl;

    /// <summary>
    /// Fetch available models from OpenRouter's /models endpoint.
    /// Returns a list of model entries. Returns empty list on failure.
    /// </summary>
    public async Task<List<OpenRouterModelEntry>> FetchModelsAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/models");
            var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
            if (!string.IsNullOrEmpty(key))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
                return [];

            var models = new List<OpenRouterModelEntry>();
            foreach (var item in dataEl.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;

                // Parse context length
                int? contextLength = null;
                if (item.TryGetProperty("context_length", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Number)
                    contextLength = ctxEl.GetInt32();

                // Parse pricing
                double? promptCost = null, completionCost = null;
                if (item.TryGetProperty("pricing", out var pricingEl))
                {
                    if (pricingEl.TryGetProperty("prompt", out var pEl) && pEl.ValueKind == JsonValueKind.String)
                        double.TryParse(pEl.GetString(), out var pVal);
                    if (pricingEl.TryGetProperty("completion", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                        double.TryParse(cEl.GetString(), out var cVal);
                }

                models.Add(new OpenRouterModelEntry(
                    id, name ?? id, contextLength ?? 128000,
                    promptCost ?? 0, completionCost ?? 0
                ));
            }
            return models;
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>
/// A model entry fetched from OpenRouter's /models endpoint.
/// </summary>
public record OpenRouterModelEntry(
    string Id,
    string Name,
    int ContextLength,
    double PromptCost,
    double CompletionCost
);
