using System.Net.Http.Json;
using System.Text.Json;

namespace Omicron.Core.Models;

/// <summary>
/// Fetches model metadata from https://models.dev/api.json.
/// Maps response fields to <see cref="ModelMetadata"/>.
/// Fail-closed: returns empty on any network/parse error.
/// Propagates caller cancellation.
/// </summary>
public sealed class ModelsDevMetadataSource : IModelMetadataSource
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;

    public string Name => "models.dev";

    public ModelsDevMetadataSource(HttpClient? httpClient = null, Uri? endpoint = null)
    {
        _http = httpClient ?? new HttpClient();
        _endpoint = endpoint ?? new Uri("https://models.dev/api.json");
    }

    public async ValueTask<IReadOnlyList<ModelMetadata>> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await _http.GetFromJsonAsync<JsonDocument>(_endpoint, ct);
            if (doc is null) return Array.Empty<ModelMetadata>();

            return Parse(doc.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<ModelMetadata>();
        }
    }

    /// <summary>Parse raw JSON into metadata. Public for testing.</summary>
    public static IReadOnlyList<ModelMetadata> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Array.Empty<ModelMetadata>();

        var results = new List<ModelMetadata>();

        foreach (var providerProp in root.EnumerateObject())
        {
            var providerName = providerProp.Name;
            if (providerProp.Value.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var modelProp in providerProp.Value.EnumerateObject())
            {
                var modelId = modelProp.Name;
                if (modelProp.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var entry = modelProp.Value;
                var meta = ParseEntry(providerName, modelId, entry);
                if (meta is not null)
                    results.Add(meta);
            }
        }

        return results;
    }

    private static ModelMetadata? ParseEntry(string providerName, string modelId, JsonElement entry)
    {
        try
        {
            var displayName = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
            var contextWindow = entry.TryGetProperty("limit", out var limit)
                ? (limit.TryGetProperty("context", out var ctx) ? ctx.GetInt32() : (int?)null)
                : null;
            var maxOutput = entry.TryGetProperty("limit", out var limit2)
                ? (limit2.TryGetProperty("output", out var outL) ? outL.GetInt32() : (int?)null)
                : null;

            bool? supportsTools = entry.TryGetProperty("tool_call", out var tc)
                ? tc.GetBoolean() : null;
            bool? supportsReasoning = entry.TryGetProperty("reasoning", out var r)
                ? r.GetBoolean() : null;
            bool? supportsStructuredOutput = entry.TryGetProperty("structured_output", out var so)
                ? so.GetBoolean() : null;

            // Vision: attachment capability or image input modality
            bool? supportsVision = null;
            if (entry.TryGetProperty("attachment", out var att) && att.GetBoolean())
                supportsVision = true;
            else if (entry.TryGetProperty("modalities", out var mod) && mod.ValueKind == JsonValueKind.Object
                     && mod.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in inp.EnumerateArray())
                {
                    if (m.GetString() == "image") { supportsVision = true; break; }
                }
            }

            decimal? inputPrice = null;
            decimal? outputPrice = null;
            // models.dev uses "cost"; fall back to "pricing" if absent
            if (!entry.TryGetProperty("cost", out var pricing))
                entry.TryGetProperty("pricing", out pricing);

            if (pricing.ValueKind == JsonValueKind.Object)
            {
                if (pricing.TryGetProperty("input", out var ip))
                    inputPrice = ip.GetDecimal();
                if (pricing.TryGetProperty("output", out var op))
                    outputPrice = op.GetDecimal();
            }

            // Convert from per-token to per-million-tokens if values are small
            if (inputPrice.HasValue && inputPrice < 1m)
                inputPrice *= 1_000_000;
            if (outputPrice.HasValue && outputPrice < 1m)
                outputPrice *= 1_000_000;

            DateTimeOffset? lastUpdated = entry.TryGetProperty("updated", out var upd)
                ? (upd.TryGetDateTimeOffset(out var dt) ? dt : (DateTimeOffset?)null)
                : null;

            // Normalize model ID: strip provider name prefix only when it matches
            var normalizedId = modelId;
            if (normalizedId.StartsWith(providerName + "/", StringComparison.OrdinalIgnoreCase))
                normalizedId = normalizedId[(providerName.Length + 1)..];

            return new ModelMetadata(
                ModelId: normalizedId,
                ProviderName: providerName,
                DisplayName: displayName,
                ContextWindow: contextWindow,
                MaxOutputTokens: maxOutput,
                SupportsTools: supportsTools,
                SupportsReasoning: supportsReasoning,
                SupportsVision: supportsVision,
                SupportsStructuredOutput: supportsStructuredOutput,
                InputPricePerMillionTokens: inputPrice,
                OutputPricePerMillionTokens: outputPrice,
                LastUpdatedAt: lastUpdated,
                Source: "models.dev");
        }
        catch
        {
            return null;
        }
    }
}
