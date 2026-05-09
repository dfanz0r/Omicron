using Omicron.Core.Providers;

namespace Omicron.Core.Models;

/// <summary>
/// Holds the model catalog, handles fallback seeding, and runs
/// provider-specific model discovery.
/// </summary>
public sealed class ModelCatalogService : IModelCatalog
{
    private readonly Dictionary<string, Model> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _freeModelKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly IProviderRegistry _providerRegistry;

    /// <summary>All models in the catalog.</summary>
    public IReadOnlyDictionary<string, Model> Models => _models;

    /// <summary>Keys of models known to be free.</summary>
    public IReadOnlySet<string> FreeModelKeys => _freeModelKeys;

    public ModelCatalogService(IProviderRegistry providerRegistry)
    {
        _providerRegistry = providerRegistry;
        SeedFallbacks();
    }

    /// <summary>
    /// Seed minimal hardcoded fallback models so the CLI always has something
    /// to show even if discovery fails or network is unavailable.
    /// </summary>
    private void SeedFallbacks()
    {
        EnsureModel("openai:gpt-4o", MakeModel("gpt-4o", "GPT-4o", "openai", ApiType.OpenAiChat,
            "https://api.openai.com/v1", 128000, 16384, true));
        EnsureModel("openai:gpt-4o-mini", MakeModel("gpt-4o-mini", "GPT-4o Mini", "openai", ApiType.OpenAiChat,
            "https://api.openai.com/v1", 128000, 16384, true));
        EnsureModel("anthropic:claude-sonnet-4", MakeModel("claude-sonnet-4-20250514", "Claude Sonnet 4",
            "anthropic", ApiType.AnthropicMessages, "https://api.anthropic.com/v1", 200000, 8192, true));
        EnsureModel("or:openai/gpt-5.4-mini", MakeModel("openai/gpt-5.4-mini", "OpenAI: GPT-5.4 Mini (OR)",
            "openrouter", ApiType.OpenAiResponses, "https://openrouter.ai/api/v1", 128000, 16384, true));
    }

    /// <summary>
    /// Add a model only if it does not already exist.
    /// </summary>
    public void EnsureModel(string key, Model model)
    {
        if (!_models.ContainsKey(key))
        {
            if (model.Provider is null && _providerRegistry.TryGetProvider(model.ProviderName, out var p))
                model.Provider = p;
            _models[key] = model;
        }
    }

    /// <summary>
    /// Mark a model key as free (zero-cost).
    /// </summary>
    public void MarkFree(string key)
    {
        _freeModelKeys.Add(key);
    }

    /// <summary>
    /// Check if a model is free by its catalog entry key.
    /// </summary>
    public bool IsFreeModel(string catalogKey) => _freeModelKeys.Contains(catalogKey);

    /// <summary>
    /// Resolve providers for any remaining unresolved models.
    /// </summary>
    public void ResolveProviders()
    {
        foreach (var model in _models.Values)
        {
            if (model.Provider is null && _providerRegistry.TryGetProvider(model.ProviderName, out var p))
                model.Provider = p;
        }
    }

    /// <summary>
    /// Discover models from all provider sources.
    /// </summary>
    public async Task<int> DiscoverAsync(bool quiet = false)
    {
        var before = _models.Count;

        // OpenCode Zen
        if (_providerRegistry.TryGetProvider("opencode", out var zen) && zen is OpenCodeProvider ocp)
        {
            var entries = await ocp.FetchModelsAsync();
            foreach (var e in entries)
            {
                // Use CompatibilityDetector as the central classification authority
                var apiType = CompatibilityDetector.ResolveApiType("opencode", e.Id);
                var baseUrl = apiType == ApiType.AnthropicMessages
                    ? "https://opencode.ai/zen"
                    : "https://opencode.ai/zen/v1";
                AddDiscovered("zen", e.Id, $"{e.Name} (Zen)", "opencode", apiType, baseUrl, 128000, 16384, false);
            }
        }

        // OpenCode Go — always Chat Completions
        if (_providerRegistry.TryGetProvider("opencode-go", out var go) && go is OpenCodeProvider ocpGo)
        {
            var entries = await ocpGo.FetchModelsAsync();
            foreach (var e in entries)
            {
                var apiType = CompatibilityDetector.ResolveApiType("opencode-go", e.Id);
                AddDiscovered("go", e.Id, $"{e.Name} (Go)", "opencode-go", apiType,
                    "https://opencode.ai/zen/go/v1", 128000, 16384, false);
            }
        }

        // OpenRouter
        if (_providerRegistry.TryGetProvider("openrouter", out var or) && or is OpenRouterProvider orp)
        {
            var entries = await orp.FetchModelsAsync();
            foreach (var e in entries)
            {
                var isFree = e.PromptCost == 0 && e.CompletionCost == 0;
                var apiType = CompatibilityDetector.ResolveApiType("openrouter", e.Id);
                AddDiscovered("or", e.Id, $"{e.Name} (OR)", "openrouter", apiType,
                    "https://openrouter.ai/api/v1", e.ContextLength, 16384, isFree);
            }
        }

        ResolveProviders();
        return _models.Count - before;
    }

    private void AddDiscovered(string prefix, string id, string name, string provider, ApiType apiType,
        string baseUrl, int ctx, int maxTokens, bool isFree, bool? supportsImages = null)
    {
        var key = $"{prefix}:{id}";
        if (_models.ContainsKey(key)) return;

        var model = MakeModel(id, name, provider, apiType, baseUrl, ctx, maxTokens,
            supportsImages ?? (apiType != ApiType.AnthropicMessages));
        if (_providerRegistry.TryGetProvider(provider, out var p))
            model.Provider = p;
        _models[key] = model;

        // Track free status under both the catalog key and the provider:model pattern
        if (isFree)
        {
            _freeModelKeys.Add(key);
            _freeModelKeys.Add(id);
            _freeModelKeys.Add($"{provider}:{id}");
        }
    }

    private static Model MakeModel(string id, string name, string provider, ApiType apiType,
        string baseUrl, int ctx, int maxTokens, bool supportsImages = false)
    {
        // Use CompatibilityDetector to assign compatibility descriptors and storage policy
        var compat = CompatibilityDetector.ResolveCompatibility(apiType, provider);
        var storagePolicy = CompatibilityDetector.ResolveStoragePolicy(apiType, provider);

        return new Model
        {
            Id = id,
            Name = name,
            ProviderName = provider,
            ApiType = apiType,
            BaseUrl = baseUrl,
            ContextWindow = ctx,
            MaxTokens = maxTokens,
            SupportsImages = supportsImages,
            Compatibility = compat,
            StoragePolicy = storagePolicy
        };
    }
}
