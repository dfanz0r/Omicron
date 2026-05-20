using System.Net.Http.Headers;
using Omicron.Core.Models;

namespace Omicron.Core.Providers;

/// <summary>
///     Provider for OpenAI Chat Completions API and any OpenAI-compatible backend
///     (Ollama, vLLM, LM Studio, Groq, DeepSeek, etc.).
///     Now inherits from ShapeBasedProvider to avoid duplicating HTTP/SSE loop logic.
/// </summary>
public class OpenAiProvider : ShapeBasedProvider
{
    private readonly Dictionary<ApiType, IApiShape> _shapes;

    public OpenAiProvider(HttpClient? http = null)
        : base(http)
    {
        _shapes = new Dictionary<ApiType, IApiShape>
        {
            [ApiType.OpenAiChat] = new OpenAiChatShape(),
            [ApiType.OpenAiResponses] = new OpenAiResponsesShape()
        };
    }

    public override string Name => "OpenAI-Compatible";
    public override string DefaultBaseUrl => "https://api.openai.com/v1";

    protected override IReadOnlyDictionary<ApiType, IApiShape> Shapes => _shapes;

    protected override void SetAuthHeader(HttpRequestMessage request, string? apiKey)
    {
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    protected override string ResolveBaseUrl(Model model)
    {
        return model.BaseUrl;
    }
}
