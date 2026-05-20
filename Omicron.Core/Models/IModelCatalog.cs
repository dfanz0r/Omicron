namespace Omicron.Core.Models;

/// <summary>
///     Catalog of known models, responsible for discovery and lookup.
///     Models are identified by catalog keys like "provider:model-id".
/// </summary>
public interface IModelCatalog
{
    /// <summary>All models in the catalog, keyed by catalog key.</summary>
    IReadOnlyDictionary<string, Model> Models { get; }

    /// <summary>Catalog keys of models known to be free (zero-cost).</summary>
    IReadOnlySet<string> FreeModelKeys { get; }

    /// <summary>
    ///     Discover models from all registered provider sources.
    ///     Returns the number of new models added.
    /// </summary>
    Task<int> DiscoverAsync(bool quiet = false);

    /// <summary>
    ///     Check if a model is free by its catalog entry key.
    /// </summary>
    bool IsFreeModel(string catalogKey);

    /// <summary>
    ///     Resolve IChatProvider instances for any unresolved models.
    /// </summary>
    void ResolveProviders();
}
