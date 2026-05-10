namespace Omicron.Core.Models;

/// <summary>
/// Extension methods for <see cref="IModelCatalog"/> providing metadata-backed helpers.
/// </summary>
public static class ModelCatalogExtensions
{
    /// <summary>
    /// Look up metadata for a given model.
    /// Matches by (ModelId, ProviderName) case-insensitively.
    /// </summary>
    public static ModelMetadata? GetMetadata(this IModelCatalog catalog, Model model)
    {
        if (catalog is ModelCatalogService svc)
        {
            var key = (model.Id, model.ProviderName);
            return svc.Metadata.TryGetValue(key, out var meta) ? meta : null;
        }
        return null;
    }

    /// <summary>
    /// Get the effective context window: metadata overrides model static value if present.
    /// Returns <c>null</c> if neither source has a known value.
    /// </summary>
    public static int? GetEffectiveContextWindow(this IModelCatalog catalog, Model model)
    {
        var meta = catalog.GetMetadata(model);
        if (meta?.ContextWindow is not null)
            return meta.ContextWindow.Value;
        return model.ContextWindow > 0 ? model.ContextWindow : null;
    }

    /// <summary>
    /// Get the effective max output tokens: metadata overrides model static value if present.
    /// Returns <c>null</c> if neither source has a known value.
    /// </summary>
    public static int? GetEffectiveMaxOutputTokens(this IModelCatalog catalog, Model model)
    {
        var meta = catalog.GetMetadata(model);
        if (meta?.MaxOutputTokens is not null)
            return meta.MaxOutputTokens.Value;
        return model.MaxTokens > 0 ? model.MaxTokens : null;
    }
}
