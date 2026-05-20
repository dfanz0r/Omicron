namespace Omicron.Core.Models;

/// <summary>
///     Produces model metadata from the bundled static catalog.
///     Converts existing <see cref="Model" /> entries into <see cref="ModelMetadata" />.
///     Never throws; returns whatever was seeded by <see cref="ModelCatalogService.SeedFallbacks" />.
/// </summary>
public sealed class StaticModelMetadataSource : IModelMetadataSource
{
    private readonly IModelCatalog _catalog;

    public StaticModelMetadataSource(IModelCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public string Name => "static";

    public ValueTask<IReadOnlyList<ModelMetadata>> LoadAsync(CancellationToken ct = default)
    {
        var list = new List<ModelMetadata>(_catalog.Models.Count);

        foreach ((string key, Model model) in _catalog.Models)
        {
            decimal? inputPrice = model.Cost.Input > 0 ? (decimal)model.Cost.Input : null;
            decimal? outputPrice = model.Cost.Output > 0 ? (decimal)model.Cost.Output : null;

            list.Add(new ModelMetadata(model.Id,
                model.ProviderName,
                model.Name,
                model.ContextWindow > 0 ? model.ContextWindow : null,
                model.MaxTokens > 0 ? model.MaxTokens : null,
                null, // unknown until model explicitly declares capability
                model.SupportsReasoning ? true : null,
                model.SupportsImages ? true : null,
                InputPricePerMillionTokens: inputPrice,
                OutputPricePerMillionTokens: outputPrice,
                Source: $"static:{key}"));
        }

        return ValueTask.FromResult<IReadOnlyList<ModelMetadata>>(list);
    }
}
