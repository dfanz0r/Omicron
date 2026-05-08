namespace Omicron.Core.Providers;

/// <summary>
/// Registry of LLM provider instances. Providers are identified by name (case-insensitive).
/// </summary>
public interface IProviderRegistry
{
    /// <summary>All registered provider names.</summary>
    IEnumerable<string> ProviderNames { get; }

    /// <summary>
    /// Register a provider by name. Replaces any existing registration with the same name.
    /// </summary>
    void Register(string name, IChatProvider provider);

    /// <summary>
    /// Get a provider by name. Throws if not found.
    /// </summary>
    IChatProvider GetProvider(string name);

    /// <summary>
    /// Try to get a provider by name.
    /// </summary>
    bool TryGetProvider(string name, out IChatProvider? provider);
}
