using System.Net.Http.Json;
using System.Text.Json;

namespace Omicron.Core.Models;

/// <summary>
///     Fetches model metadata from https://models.dev/api.json.
///     Maps response fields to <see cref="ModelMetadata" />.
///     Fail-closed: returns empty on any network/parse error.
///     Propagates caller cancellation.
/// </summary>
public sealed class ModelsDevMetadataSource : IModelMetadataSource
{
    private readonly Uri _endpoint;
    private readonly HttpClient _http;

    public ModelsDevMetadataSource(HttpClient? httpClient = null, Uri? endpoint = null)
    {
        _http = httpClient ?? new HttpClient();
        _endpoint = endpoint ?? new Uri("https://models.dev/api.json");
    }

    public string Name => "models.dev";

    public async ValueTask<IReadOnlyList<ModelMetadata>> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            using JsonDocument? doc = await _http.GetFromJsonAsync<JsonDocument>(_endpoint, ct);
            if (doc is null)
            {
                return Array.Empty<ModelMetadata>();
            }

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
        {
            return Array.Empty<ModelMetadata>();
        }

        var results = new List<ModelMetadata>();

        foreach (JsonProperty providerProp in root.EnumerateObject())
        {
            string providerName = providerProp.Name;
            if (providerProp.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty modelProp in providerProp.Value.EnumerateObject())
            {
                string modelId = modelProp.Name;
                if (modelProp.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                JsonElement entry = modelProp.Value;
                ModelMetadata? meta = ParseEntry(providerName, modelId, entry);
                if (meta is not null)
                {
                    results.Add(meta);
                }
            }
        }

        return results;
    }

    private static ModelMetadata? ParseEntry(string providerName, string modelId, JsonElement entry)
    {
        try
        {
            string? displayName = entry.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
            int? contextWindow = entry.TryGetProperty("limit", out JsonElement limit)
                ? limit.TryGetProperty("context", out JsonElement ctx) ? ctx.GetInt32() : null
                : null;
            int? maxOutput = entry.TryGetProperty("limit", out JsonElement limit2)
                ? limit2.TryGetProperty("output", out JsonElement outL) ? outL.GetInt32() : null
                : null;

            bool? supportsTools = entry.TryGetProperty("tool_call", out JsonElement tc)
                ? tc.GetBoolean()
                : null;
            bool? supportsReasoning = entry.TryGetProperty("reasoning", out JsonElement r)
                ? r.GetBoolean()
                : null;
            bool? supportsStructuredOutput = entry.TryGetProperty("structured_output", out JsonElement so)
                ? so.GetBoolean()
                : null;

            // Modalities: parse modalities.input array
            IReadOnlySet<string>? modalities = null;
            if (
                entry.TryGetProperty("modalities", out JsonElement modObj)
                && modObj.ValueKind == JsonValueKind.Object
                && modObj.TryGetProperty("input", out JsonElement inp)
                && inp.ValueKind == JsonValueKind.Array
            )
            {
                var set = new HashSet<string>();
                foreach (JsonElement m in inp.EnumerateArray())
                {
                    string? val = m.GetString();
                    if (val is not null)
                    {
                        set.Add(val);
                    }
                }

                if (set.Count > 0)
                {
                    modalities = set;
                }
            }

            // Vision: attachment capability or image input modality
            bool? supportsVision = null;
            if (entry.TryGetProperty("attachment", out JsonElement att) && att.GetBoolean())
            {
                supportsVision = true;
            }
            else if (modalities?.Contains("image") == true)
            {
                supportsVision = true;
            }

            decimal? inputPrice = null;
            decimal? outputPrice = null;
            // models.dev uses "cost"; fall back to "pricing" if absent
            if (!entry.TryGetProperty("cost", out JsonElement pricing))
            {
                entry.TryGetProperty("pricing", out pricing);
            }

            if (pricing.ValueKind == JsonValueKind.Object)
            {
                if (pricing.TryGetProperty("input", out JsonElement ip))
                {
                    inputPrice = ip.GetDecimal();
                }

                if (pricing.TryGetProperty("output", out JsonElement op))
                {
                    outputPrice = op.GetDecimal();
                }
            }

            // Convert from per-token to per-million-tokens if values are small
            if (inputPrice.HasValue && inputPrice < 1m)
            {
                inputPrice *= 1_000_000;
            }

            if (outputPrice.HasValue && outputPrice < 1m)
            {
                outputPrice *= 1_000_000;
            }

            DateTimeOffset? lastUpdated = entry.TryGetProperty("updated", out JsonElement upd)
                ? upd.TryGetDateTimeOffset(out DateTimeOffset dt) ? dt : null
                : null;

            // Normalize model ID: strip provider name prefix only when it matches
            string normalizedId = modelId;
            if (normalizedId.StartsWith(providerName + "/", StringComparison.OrdinalIgnoreCase))
            {
                normalizedId = normalizedId[(providerName.Length + 1)..];
            }

            return new ModelMetadata(normalizedId,
                providerName,
                displayName,
                contextWindow,
                maxOutput,
                supportsTools,
                supportsReasoning,
                supportsVision,
                supportsStructuredOutput,
                inputPrice,
                outputPrice,
                lastUpdated,
                "models.dev",
                modalities);
        }
        catch
        {
            return null;
        }
    }
}
