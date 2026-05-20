namespace Omicron.Core.Models;

/// <summary>
///     Provides model metadata from an external or internal source.
/// </summary>
public interface IModelMetadataSource
{
    /// <summary>Human-readable name for this source (e.g. "static", "models.dev", "openai").</summary>
    string Name { get; }

    /// <summary>Load metadata entries from this source. Should never throw; return empty on failure.</summary>
    ValueTask<IReadOnlyList<ModelMetadata>> LoadAsync(CancellationToken ct = default);
}

/// <summary>
///     A model metadata source scoped to a specific provider.
/// </summary>
public interface IProviderModelMetadataSource : IModelMetadataSource
{
    /// <summary>Provider name this source resolves metadata for (e.g. "openai", "openrouter").</summary>
    string ProviderName { get; }
}

/// <summary>
///     Merges multiple metadata sources with deterministic precedence.
///     Earlier sources provide baseline; later sources override fields they explicitly provide (non-null).
/// </summary>
public interface IModelMetadataMerger
{
    /// <summary>
    ///     Merge metadata from multiple sources, ordered by precedence (earlier = baseline, later = override).
    /// </summary>
    IReadOnlyList<ModelMetadata> Merge(params IReadOnlyList<ModelMetadata>[] sources);
}

/// <summary>
///     Default merger: later sources override earlier sources for non-null fields only.
/// </summary>
public sealed class ModelMetadataMerger : IModelMetadataMerger
{
    private static readonly CaseInsensitiveTupleComparer _keyComparer = new();

    public IReadOnlyList<ModelMetadata> Merge(params IReadOnlyList<ModelMetadata>[] sources)
    {
        if (sources.Length == 0)
        {
            return Array.Empty<ModelMetadata>();
        }

        var merged = new Dictionary<(string, string), ModelMetadata>(_keyComparer);

        foreach (var source in sources)
        {
            if (source is null)
            {
                continue;
            }

            foreach (var entry in source)
            {
                if (entry is null)
                {
                    continue;
                }

                (string ModelId, string ProviderName) key = (entry.ModelId, entry.ProviderName);

                if (merged.TryGetValue(key, out ModelMetadata? existing))
                {
                    merged[key] = Overlay(existing, entry);
                }
                else
                {
                    merged[key] = entry;
                }
            }
        }

        return merged
            .Values.OrderBy(m => m.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ModelMetadata Overlay(ModelMetadata existing, ModelMetadata overlay)
    {
        return existing with
        {
            DisplayName = overlay.DisplayName ?? existing.DisplayName,
            ContextWindow = overlay.ContextWindow ?? existing.ContextWindow,
            MaxOutputTokens = overlay.MaxOutputTokens ?? existing.MaxOutputTokens,
            SupportsTools = overlay.SupportsTools ?? existing.SupportsTools,
            SupportsReasoning = overlay.SupportsReasoning ?? existing.SupportsReasoning,
            SupportsVision = overlay.SupportsVision ?? existing.SupportsVision,
            SupportsStructuredOutput =
            overlay.SupportsStructuredOutput ?? existing.SupportsStructuredOutput,
            InputPricePerMillionTokens =
            overlay.InputPricePerMillionTokens ?? existing.InputPricePerMillionTokens,
            OutputPricePerMillionTokens =
            overlay.OutputPricePerMillionTokens ?? existing.OutputPricePerMillionTokens,
            LastUpdatedAt = overlay.LastUpdatedAt ?? existing.LastUpdatedAt,
            Source = overlay.Source ?? existing.Source
        };
    }
}

internal sealed class CaseInsensitiveTupleComparer : IEqualityComparer<(string, string)>
{
    private static readonly StringComparer _cmp = StringComparer.OrdinalIgnoreCase;

    public bool Equals((string, string) x, (string, string) y)
    {
        return _cmp.Equals(x.Item1, y.Item1) && _cmp.Equals(x.Item2, y.Item2);
    }

    public int GetHashCode((string, string) obj)
    {
        return HashCode.Combine(_cmp.GetHashCode(obj.Item1), _cmp.GetHashCode(obj.Item2));
    }
}
