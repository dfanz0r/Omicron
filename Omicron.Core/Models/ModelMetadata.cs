namespace Omicron.Core.Models;

/// <summary>
/// Describes model capabilities and properties.
/// Metadata is layered from multiple sources (static, models.dev, provider endpoints, user config).
/// Null fields mean "not specified by this source" — they do not overwrite known values from earlier sources.
/// </summary>
public sealed record ModelMetadata(
    string ModelId,
    string ProviderName,
    string? DisplayName = null,
    int? ContextWindow = null,
    int? MaxOutputTokens = null,
    bool? SupportsTools = null,
    bool? SupportsReasoning = null,
    bool? SupportsVision = null,
    bool? SupportsStructuredOutput = null,
    decimal? InputPricePerMillionTokens = null,
    decimal? OutputPricePerMillionTokens = null,
    DateTimeOffset? LastUpdatedAt = null,
    string? Source = null,
    IReadOnlySet<string>? Modalities = null);
