using Omicron.Core.Models;

namespace Omicron.Core.Providers;

/// <summary>
///     Detects and assigns compatibility descriptors and API types for models
///     based on provider name, model ID, and base URL patterns.
/// </summary>
public static class CompatibilityDetector
{
    /// <summary>
    ///     Known model IDs that use the Responses API (beyond the GPT-5+ prefix check).
    /// </summary>
    private static readonly HashSet<string> KnownResponsesModels = new(StringComparer.OrdinalIgnoreCase)
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
        "gpt-5-nano",
        "o1",
        "o1-mini",
        "o1-preview",
        "o3",
        "o3-mini",
        "o4",
        "o4-mini",
        "o4-codex",
        "o4-codex-mini"
    };

    /// <summary>
    ///     Resolve the ApiType for a model based on its provider, model ID, and base URL.
    ///     Uses the same logic as OpenCodeProvider.ResolveApiType for OpenCode models,
    ///     and provider-specific rules for others.
    /// </summary>
    public static ApiType ResolveApiType(
        string providerName,
        string modelId,
        string? baseUrl = null)
    {
        string provider = providerName.ToLowerInvariant();

        switch (provider)
        {
            case "anthropic":
                return ApiType.AnthropicMessages;

            case "opencode":
            case "opencode-go":
                return OpenCodeProvider.ResolveApiType(modelId, provider == "opencode-go");

            case "openrouter":
                // OpenRouter supports an OpenAI-compatible /responses endpoint,
                // but Chat Completions remains the default for broad model compatibility.
                // Route known Responses-capable model families to Responses.
                if (
                    IsResponsesApiModelId(modelId)
                    || baseUrl?.Contains("/responses", StringComparison.OrdinalIgnoreCase) == true
                )
                {
                    return ApiType.OpenAiResponses;
                }

                return ApiType.OpenAiChat;

            case "openai":
                // OpenAI: GPT-5+ and o-series models use Responses, others use Chat.
                if (IsResponsesApiModelId(modelId))
                {
                    return ApiType.OpenAiResponses;
                }

                // Check base URL: /responses endpoint indicates Responses API
                if (baseUrl?.Contains("/responses", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return ApiType.OpenAiResponses;
                }

                return ApiType.OpenAiChat;

            case "google":
            case "google-genai":
                return ApiType.GoogleGenAi;

            default:
                // Generic OpenAI-compatible provider
                return ApiType.OpenAiChat;
        }
    }

    /// <summary>
    ///     Resolve the ProviderCompatibility for a model based on its ApiType and provider.
    /// </summary>
    public static ProviderCompatibility ResolveCompatibility(ApiType apiType, string providerName)
    {
        string provider = providerName.ToLowerInvariant();

        return apiType switch
        {
            ApiType.OpenAiResponses => ProviderCompatibility.OpenAiResponses with
            {
                // Some Responses-compatible providers may not support store.
                // OpenRouter supports /responses, but provider-managed continuation
                // via previous_response_id is unreliable across its routed backends;
                // use stateless full-context Responses by default.
                SupportsStore = provider is "openai" or "opencode",
                SupportsPreviousResponseId = provider is "openai" or "opencode"
            },

            ApiType.AnthropicMessages => ProviderCompatibility.AnthropicMessages,

            ApiType.GoogleGenAi => ProviderCompatibility.GoogleGenAi,

            _ => ProviderCompatibility.OpenAiChat
        };
    }

    /// <summary>
    ///     Resolve the default ProviderStoragePolicy for a model.
    /// </summary>
    public static ProviderStoragePolicy ResolveStoragePolicy(ApiType apiType, string providerName)
    {
        string provider = providerName.ToLowerInvariant();

        return apiType switch
        {
            // Native Responses providers can use previous_response_id by default.
            ApiType.OpenAiResponses when provider is "openai" or "opencode" =>
                ProviderStoragePolicy.AllowProviderStateNoStore,

            // OpenRouter /responses is used statelessly by default because routed
            // backends may reject function_call_output + previous_response_id.
            ApiType.OpenAiResponses => ProviderStoragePolicy.PreferStateless,

            // Stateless APIs default to preferring stateless
            _ => ProviderStoragePolicy.PreferStateless
        };
    }

    /// <summary>
    ///     Determine whether a model supports stateful continuation based on its
    ///     ApiType and storage policy.
    /// </summary>
    public static bool SupportsStatefulContinuation(ApiType apiType, ProviderStoragePolicy policy)
    {
        if (apiType != ApiType.OpenAiResponses)
        {
            return false;
        }

        return policy switch
        {
            ProviderStoragePolicy.PreferStateless => false,
            ProviderStoragePolicy.AllowProviderStateNoStore => true,
            ProviderStoragePolicy.AllowProviderStoredState => true,
            _ => false
        };
    }

    private static bool IsResponsesApiModelId(string modelId)
    {
        string normalized = modelId;
        int slashIndex = normalized.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex < normalized.Length - 1)
        {
            normalized = normalized[(slashIndex + 1)..];
        }

        return normalized.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("o1-", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("o3-", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("o4-", StringComparison.OrdinalIgnoreCase)
               || KnownResponsesModels.Contains(normalized);
    }
}
