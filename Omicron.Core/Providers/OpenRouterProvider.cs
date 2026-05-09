using System.Globalization;
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
/// Models use OpenAI-compatible API shapes routed through OpenRouter's
/// base URL. Chat Completions is the default; models classified as
/// Responses-capable use OpenRouter's /responses endpoint.
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
            [ApiType.OpenAiChat] = new OpenAiChatShape(),
            [ApiType.OpenAiResponses] = new OpenAiResponsesShape()
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

                // Parse pricing. Missing/unparseable pricing is treated as unknown,
                // not free, so catalog filtering does not expose paid models as free.
                var promptCost = double.PositiveInfinity;
                var completionCost = double.PositiveInfinity;
                if (item.TryGetProperty("pricing", out var pricingEl))
                {
                    if (pricingEl.TryGetProperty("prompt", out var pEl))
                        promptCost = ParseOpenRouterPrice(pEl) ?? double.PositiveInfinity;
                    if (pricingEl.TryGetProperty("completion", out var cEl))
                        completionCost = ParseOpenRouterPrice(cEl) ?? double.PositiveInfinity;
                }

                models.Add(new OpenRouterModelEntry(
                    id, name ?? id, contextLength ?? 128000,
                    promptCost, completionCost
                ));
            }
            return models;
        }
        catch
        {
            return [];
        }
    }

    private static double? ParseOpenRouterPrice(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(
                element.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value) => value,
            _ => null
        };
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
