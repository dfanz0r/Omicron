namespace Omicron.Core.Providers;

/// <summary>
///     Simple factory that creates and caches IChatProvider instances by name.
///     No complex registry pattern — just a straightforward map.
/// </summary>
public class ProviderFactory : IProviderRegistry
{
    private readonly HttpClient _http;

    private readonly Dictionary<string, IChatProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public ProviderFactory(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        RegisterDefaults();
    }

    /// <summary>
    ///     Register a custom provider.
    /// </summary>
    public void Register(string name, IChatProvider provider)
    {
        _providers[name] = provider;
        OnProviderRegistered?.Invoke(name, provider);
    }

    /// <summary>
    ///     Get a provider by name. Throws if not found.
    /// </summary>
    public IChatProvider GetProvider(string name)
    {
        if (_providers.TryGetValue(name, out IChatProvider? provider))
        {
            return provider;
        }

        throw new KeyNotFoundException($"Unknown provider: '{name}'. Available: {string.Join(", ", _providers.Keys)}");
    }

    /// <summary>
    ///     Try to get a provider by name.
    /// </summary>
    public bool TryGetProvider(string name, out IChatProvider? provider)
    {
        return _providers.TryGetValue(name, out provider);
    }

    /// <summary>
    ///     All registered provider names.
    /// </summary>
    public IEnumerable<string> ProviderNames => _providers.Keys;

    /// <summary>
    ///     Raised when a provider is registered, so the CLI can discover
    ///     models dynamically at startup.
    /// </summary>
    public event Action<string, IChatProvider>? OnProviderRegistered;

    private void RegisterDefaults()
    {
        Register("openai", new OpenAiProvider(_http));
        Register("anthropic", new AnthropicProvider(_http));
        Register("opencode", new OpenCodeProvider(false, _http));
        Register("opencode-go", new OpenCodeProvider(true, _http));
        Register("openrouter", new OpenRouterProvider(_http));
    }
}
