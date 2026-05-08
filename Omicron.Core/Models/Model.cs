using Omicron.Core.Providers;

namespace Omicron.Core.Models;

/// <summary>
/// The wire-protocol / API shape a model uses.
/// Each shape has its own request format, auth scheme, and SSE stream layout.
/// </summary>
public enum ApiType
{
    /// <summary>OpenAI Chat Completions (or any OpenAI-compatible backend).</summary>
    OpenAiChat,
    /// <summary>Anthropic Messages API.</summary>
    AnthropicMessages,
    /// <summary>OpenAI Responses API (used by some GPT-5+ models via OpenCode).</summary>
    OpenAiResponses,
    /// <summary>Google Generative AI API.</summary>
    GoogleGenAi
}

/// <summary>
/// Describes an LLM model.
/// </summary>
public class Model
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Which provider serves this model (e.g. "opencode", "openrouter", "openai", "anthropic").</summary>
    public string ProviderName { get; init; } = "";

    /// <summary>Which API wire-protocol this model speaks.</summary>
    public ApiType ApiType { get; init; } = ApiType.OpenAiChat;

    /// <summary>Base URL for API calls.</summary>
    public string BaseUrl { get; init; } = "";

    public bool SupportsReasoning { get; init; }
    public bool SupportsImages { get; init; }
    public int ContextWindow { get; init; }
    public int MaxTokens { get; init; }

    /// <summary>
    /// Cost per million tokens (input, output, cache-read, cache-write).
    /// </summary>
    public (double Input, double Output, double CacheRead, double CacheWrite) Cost { get; init; }

    /// <summary>Resolved provider instance that will handle requests for this model.</summary>
    public IChatProvider? Provider { get; set; }
}
