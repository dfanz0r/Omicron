using System.Net;
using System.Text;
using System.Text.Json;
using Omicron.Core.Models;
using Omicron.Core.Providers;
using Xunit;

namespace Omicron.Core.Tests;

public class ModelMetadataTests
{
    // ================================================================
    // Merger tests
    // ================================================================

    [Fact]
    public void Merge_EmptySources_ReturnsEmpty()
    {
        var merger = new ModelMetadataMerger();
        IReadOnlyList<ModelMetadata> result = merger.Merge();
        Assert.Empty(result);
    }

    [Fact]
    public void Merge_SingleSource_ReturnsEntries()
    {
        var merger = new ModelMetadataMerger();
        var source = new List<ModelMetadata>
        {
            new("gpt-4o", "openai", "GPT-4o")
        };
        IReadOnlyList<ModelMetadata> result = merger.Merge(source);
        Assert.Single(result);
        Assert.Equal("gpt-4o", result[0].ModelId);
    }

    [Fact]
    public void Merge_LaterSourceOverridesNonNullFields()
    {
        var merger = new ModelMetadataMerger();
        var baseline = new List<ModelMetadata>
        {
            new("gpt-4o", "openai", "GPT-4o", 8000)
        };
        var overrides = new List<ModelMetadata>
        {
            new("gpt-4o", "openai", "Override", 128000)
        };
        IReadOnlyList<ModelMetadata> result = merger.Merge(baseline, overrides);
        Assert.Equal("Override", result[0].DisplayName);
        Assert.Equal(128000, result[0].ContextWindow);
    }

    [Fact]
    public void Merge_NullFieldsDoNotOverwrite()
    {
        var merger = new ModelMetadataMerger();
        var baseline = new List<ModelMetadata>
        {
            new("gpt-4o", "openai", "GPT-4o", 8000)
        };
        var overrides = new List<ModelMetadata>
        {
            new("gpt-4o", "openai")
        };
        IReadOnlyList<ModelMetadata> result = merger.Merge(baseline, overrides);
        Assert.Equal("GPT-4o", result[0].DisplayName);
        Assert.Equal(8000, result[0].ContextWindow);
    }

    [Fact]
    public void Merge_DifferentModels_Combines()
    {
        var merger = new ModelMetadataMerger();
        IReadOnlyList<ModelMetadata> result = merger.Merge(new List<ModelMetadata>
            {
                new("gpt-4o", "openai")
            },
            new List<ModelMetadata>
            {
                new("claude-3", "anthropic")
            });
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Merge_PartialOverride_PreservesUnchangedFields()
    {
        var merger = new ModelMetadataMerger();
        IReadOnlyList<ModelMetadata> result = merger.Merge(new List<ModelMetadata>
            {
                new("gpt-4o",
                    "openai",
                    "GPT-4o",
                    8000,
                    SupportsVision: true)
            },
            new List<ModelMetadata>
            {
                new("gpt-4o", "openai", ContextWindow: 128000)
            });
        Assert.Equal("GPT-4o", result[0].DisplayName);
        Assert.Equal(128000, result[0].ContextWindow);
        Assert.True(result[0].SupportsVision);
    }

    [Fact]
    public void Merge_CaseInsensitiveModelId()
    {
        var merger = new ModelMetadataMerger();
        IReadOnlyList<ModelMetadata> result = merger.Merge(new List<ModelMetadata>
            {
                new("GPT-4o", "openai", "GPT-4o")
            },
            new List<ModelMetadata>
            {
                new("gpt-4o", "openai", ContextWindow: 128000)
            });
        Assert.Single(result);
        Assert.Equal(128000, result[0].ContextWindow);
    }

    [Fact]
    public void Merge_NullSources_AreSkipped()
    {
        var merger = new ModelMetadataMerger();
        IReadOnlyList<ModelMetadata> result = merger.Merge(null!, new List<ModelMetadata>
        {
            new("gpt-4o", "openai")
        });
        Assert.Single(result);
    }

    [Fact]
    public void Merge_OutputIsOrderedByProviderThenModelId()
    {
        var merger = new ModelMetadataMerger();
        var source = new List<ModelMetadata>
        {
            new("z-model", "anthropic"),
            new("a-model", "openai"),
            new("b-model", "openai")
        };
        IReadOnlyList<ModelMetadata> result = merger.Merge(source);
        Assert.Equal("anthropic", result[0].ProviderName);
        Assert.Equal("openai", result[1].ProviderName);
        Assert.Equal("a-model", result[1].ModelId);
        Assert.Equal("b-model", result[2].ModelId);
    }

    // ================================================================
    // Static metadata source tests
    // ================================================================

    [Fact]
    public async Task StaticSource_LoadsFromCatalog()
    {
        var providerRegistry = new ProviderFactory();
        var catalog = new ModelCatalogService(providerRegistry);
        var source = new StaticModelMetadataSource(catalog);
        IReadOnlyList<ModelMetadata> metadata = await source.LoadAsync();
        Assert.NotEmpty(metadata);
        Assert.Contains(metadata, m => m.ModelId == "gpt-4o");
    }

    [Fact]
    public async Task StaticSource_IncludesContextWindow()
    {
        var providerRegistry = new ProviderFactory();
        var catalog = new ModelCatalogService(providerRegistry);
        var source = new StaticModelMetadataSource(catalog);
        IReadOnlyList<ModelMetadata> metadata = await source.LoadAsync();
        ModelMetadata gpt4o = metadata.First(m => m.ModelId == "gpt-4o");
        Assert.NotNull(gpt4o.ContextWindow);
        Assert.True(gpt4o.ContextWindow > 0);
    }

    [Fact]
    public void StaticSource_NullCatalog_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StaticModelMetadataSource(null!));
    }

    // ================================================================
    // Catalog integration tests
    // ================================================================

    [Fact]
    public async Task Catalog_RefreshMetadata_EmptySources_ReturnsZero()
    {
        var providerRegistry = new ProviderFactory();
        var catalog = new ModelCatalogService(providerRegistry);
        int count = await catalog.RefreshMetadataAsync(Array.Empty<IModelMetadataSource>());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Catalog_RefreshMetadata_LayersFromStaticSource()
    {
        var providerRegistry = new ProviderFactory();
        var catalog = new ModelCatalogService(providerRegistry);
        var staticSource = new StaticModelMetadataSource(catalog);
        int count = await catalog.RefreshMetadataAsync(new IModelMetadataSource[]
        {
            staticSource
        });
        Assert.True(count > 0);
        Assert.NotEmpty(catalog.Metadata);
    }

    [Fact]
    public async Task Catalog_RefreshMetadata_OverlayWorks()
    {
        var providerRegistry = new ProviderFactory();
        var catalog = new ModelCatalogService(providerRegistry);
        var staticSource = new StaticModelMetadataSource(catalog);
        await catalog.RefreshMetadataAsync(new IModelMetadataSource[]
        {
            staticSource
        });

        var overlay = new List<ModelMetadata>
        {
            new("gpt-4o", "openai", ContextWindow: 999999)
        };
        var overlaySource = new StaticOverlaySource(overlay);
        await catalog.RefreshMetadataAsync(new IModelMetadataSource[]
        {
            staticSource, overlaySource
        });

        var key = ("gpt-4o", "openai");
        Assert.True(catalog.Metadata.ContainsKey(key));
        Assert.Equal(999999, catalog.Metadata[key].ContextWindow);
    }

    [Fact]
    public async Task Catalog_RefreshMetadata_FailingSource_DoesNotThrow()
    {
        var providerRegistry = new ProviderFactory();
        var catalog = new ModelCatalogService(providerRegistry);
        int count = await catalog.RefreshMetadataAsync(new IModelMetadataSource[]
        {
            new ThrowingMetadataSource()
        });
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Catalog_RefreshMetadata_PreservesExistingAfterEmptyRefresh()
    {
        var providerRegistry = new ProviderFactory();
        var catalog = new ModelCatalogService(providerRegistry);
        var staticSource = new StaticModelMetadataSource(catalog);
        await catalog.RefreshMetadataAsync(new IModelMetadataSource[]
        {
            staticSource
        });
        int before = catalog.Metadata.Count;
        Assert.True(before > 0);

        int count = await catalog.RefreshMetadataAsync(new IModelMetadataSource[]
        {
            new ModelsDevMetadataSource(new HttpClient(new FakeHandler("{}")))
        });
        Assert.Equal(before, count);
        Assert.Equal(before, catalog.Metadata.Count);
    }

    [Fact]
    public async Task Catalog_RefreshMetadata_PropagatesCancellation()
    {
        var providerRegistry = new ProviderFactory();
        var catalog = new ModelCatalogService(providerRegistry);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog
                .RefreshMetadataAsync(new IModelMetadataSource[]
                    {
                        new CancellingMetadataSource()
                    },
                    cts.Token)
                .AsTask());
    }

    // ================================================================
    // Models.dev metadata source tests (via static Parse)
    // ================================================================

    [Fact]
    public void ModelsDevSource_NameIsCorrect()
    {
        var source = new ModelsDevMetadataSource();
        Assert.Equal("models.dev", source.Name);
    }

    [Fact]
    public void ModelsDevSource_Parse_MapsContextWindow()
    {
        string json =
            @"{""openai"":{""gpt-4o"":{""name"":""GPT-4o"",""limit"":{""context"":128000,""output"":16384}}}}";
        var doc = JsonDocument.Parse(json);
        IReadOnlyList<ModelMetadata> result = ModelsDevMetadataSource.Parse(doc.RootElement);
        Assert.NotEmpty(result);
        ModelMetadata gpt4o = result.First(m => m.ModelId == "gpt-4o");
        Assert.Equal(128000, gpt4o.ContextWindow);
        Assert.Equal(16384, gpt4o.MaxOutputTokens);
    }

    [Fact]
    public void ModelsDevSource_Parse_MapsCapabilityBooleans()
    {
        string json =
            @"{""openai"":{""gpt-4o"":{""tool_call"":true,""reasoning"":true,""structured_output"":true,""attachment"":true}}}";
        var doc = JsonDocument.Parse(json);
        IReadOnlyList<ModelMetadata> result = ModelsDevMetadataSource.Parse(doc.RootElement);
        ModelMetadata gpt4o = result.First(m => m.ModelId == "gpt-4o");
        Assert.True(gpt4o.SupportsTools);
        Assert.True(gpt4o.SupportsReasoning);
        Assert.True(gpt4o.SupportsStructuredOutput);
        Assert.True(gpt4o.SupportsVision);
    }

    [Fact]
    public void ModelsDevSource_Parse_NormalizesProviderPrefixedModelId()
    {
        string json =
            @"{""anthropic"":{""anthropic/claude-sonnet-4"":{""name"":""Claude Sonnet 4""}}}";
        var doc = JsonDocument.Parse(json);
        IReadOnlyList<ModelMetadata> result = ModelsDevMetadataSource.Parse(doc.RootElement);
        Assert.NotEmpty(result);
        ModelMetadata claude = result.First();
        Assert.Equal("claude-sonnet-4", claude.ModelId);
        Assert.Equal("anthropic", claude.ProviderName);
    }

    [Fact]
    public void ModelsDevSource_Parse_EmptyObject_ReturnsEmpty()
    {
        var doc = JsonDocument.Parse("{}");
        IReadOnlyList<ModelMetadata> result = ModelsDevMetadataSource.Parse(doc.RootElement);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ModelsDevSource_HttpError_ReturnsEmpty()
    {
        var handler = new FakeHandler("")
        {
            StatusCode = HttpStatusCode.InternalServerError
        };
        var source = new ModelsDevMetadataSource(new HttpClient(handler));
        IReadOnlyList<ModelMetadata> result = await source.LoadAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task ModelsDevSource_PropagatesCancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var source = new ModelsDevMetadataSource(new HttpClient(new FakeHandler("{}")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.LoadAsync(cts.Token).AsTask());
    }

    // ================================================================
    // Model catalog extension tests
    // ================================================================

    [Fact]
    public void Extension_GetMetadata_ReturnsNullForUnknown()
    {
        var prov = new ProviderFactory();
        var catalog = new ModelCatalogService(prov) as IModelCatalog;
        var model = new Model
        {
            Id = "unknown",
            ProviderName = "none"
        };
        Assert.Null(catalog.GetMetadata(model));
    }

    [Fact]
    public async Task Extension_GetMetadata_ReturnsAfterRefresh()
    {
        var prov = new ProviderFactory();
        var catalog = new ModelCatalogService(prov) as IModelCatalog;
        var svc = (ModelCatalogService)catalog;

        var meta = new List<ModelMetadata>
        {
            new("test-model",
                "test-prov",
                "Test",
                99999,
                5000)
        };
        await svc.RefreshMetadataAsync(new IModelMetadataSource[]
        {
            new StaticOverlaySource(meta)
        });

        var model = new Model
        {
            Id = "test-model",
            ProviderName = "test-prov"
        };
        ModelMetadata? result = catalog.GetMetadata(model);
        Assert.NotNull(result);
        Assert.Equal(99999, result!.ContextWindow);
    }

    [Fact]
    public async Task Extension_GetEffectiveContextWindow_MetadataOverridesModel()
    {
        var prov = new ProviderFactory();
        var catalog = new ModelCatalogService(prov) as IModelCatalog;

        var model = new Model
        {
            Id = "m",
            ProviderName = "p",
            ContextWindow = 8000
        };
        var meta = new List<ModelMetadata>
        {
            new("m", "p", ContextWindow: 128000)
        };
        await ((ModelCatalogService)catalog).RefreshMetadataAsync(new IModelMetadataSource[]
        {
            new StaticOverlaySource(meta)
        });

        Assert.Equal(128000, catalog.GetEffectiveContextWindow(model));
    }

    [Fact]
    public void Extension_GetEffectiveContextWindow_FallsBackToModel()
    {
        var prov = new ProviderFactory();
        var catalog = new ModelCatalogService(prov) as IModelCatalog;
        var model = new Model
        {
            Id = "m",
            ProviderName = "p",
            ContextWindow = 8000
        };
        Assert.Equal(8000, catalog.GetEffectiveContextWindow(model));
    }

    [Fact]
    public void Extension_GetEffectiveContextWindow_NullWhenNoData()
    {
        var prov = new ProviderFactory();
        var catalog = new ModelCatalogService(prov) as IModelCatalog;
        var model = new Model
        {
            Id = "m",
            ProviderName = "p",
            ContextWindow = 0
        };
        Assert.Null(catalog.GetEffectiveContextWindow(model));
    }

    [Fact]
    public async Task Extension_GetEffectiveMaxOutputTokens_MetadataOverridesModel()
    {
        var prov = new ProviderFactory();
        var catalog = new ModelCatalogService(prov) as IModelCatalog;
        var meta = new List<ModelMetadata>
        {
            new("m", "p", MaxOutputTokens: 32000)
        };
        await ((ModelCatalogService)catalog).RefreshMetadataAsync(new IModelMetadataSource[]
        {
            new StaticOverlaySource(meta)
        });
        var model = new Model
        {
            Id = "m",
            ProviderName = "p",
            MaxTokens = 4096
        };
        Assert.Equal(32000, catalog.GetEffectiveMaxOutputTokens(model));
    }

    [Fact]
    public void Extension_GetEffectiveMaxOutputTokens_FallsBackToModel()
    {
        var prov = new ProviderFactory();
        var catalog = new ModelCatalogService(prov) as IModelCatalog;
        var model = new Model
        {
            Id = "m",
            ProviderName = "p",
            MaxTokens = 4096
        };
        Assert.Equal(4096, catalog.GetEffectiveMaxOutputTokens(model));
    }

    [Fact]
    public async Task Extension_MetadataLookup_IsCaseInsensitive()
    {
        var prov = new ProviderFactory();
        var catalog = new ModelCatalogService(prov) as IModelCatalog;
        var meta = new List<ModelMetadata>
        {
            new("Test-Model", "Test-Prov", ContextWindow: 5000)
        };
        await ((ModelCatalogService)catalog).RefreshMetadataAsync(new IModelMetadataSource[]
        {
            new StaticOverlaySource(meta)
        });

        var model = new Model
        {
            Id = "test-model",
            ProviderName = "test-prov"
        };
        ModelMetadata? result = catalog.GetMetadata(model);
        Assert.NotNull(result);
        Assert.Equal(5000, result!.ContextWindow);
    }

    // ================================================================
    // Models.dev Parse — routed ID and pricing tests
    // ================================================================

    [Fact]
    public void ModelsDevSource_Parse_PreservesRoutedModelId()
    {
        // OpenRouter model IDs like "openai/gpt-5.4-mini" should NOT have the prefix stripped
        // because the prefix does not match the provider name
        string json = @"{""openrouter"":{""openai/gpt-5.4-mini"":{""name"":""GPT-5.4 Mini""}}}";
        var doc = JsonDocument.Parse(json);
        IReadOnlyList<ModelMetadata> result = ModelsDevMetadataSource.Parse(doc.RootElement);
        Assert.NotEmpty(result);
        ModelMetadata entry = result.First();
        Assert.Equal("openai/gpt-5.4-mini", entry.ModelId);
        Assert.Equal("openrouter", entry.ProviderName);
    }

    [Fact]
    public void ModelsDevSource_Parse_MapsCostPricing()
    {
        // models.dev uses "cost" with per-token values; source converts to per-million
        string json = @"{""openai"":{""gpt-4o"":{""cost"":{""input"":0.0000025,""output"":0.00001}}}}";
        var doc = JsonDocument.Parse(json);
        IReadOnlyList<ModelMetadata> result = ModelsDevMetadataSource.Parse(doc.RootElement);
        Assert.NotEmpty(result);
        ModelMetadata gpt4o = result.First();
        Assert.NotNull(gpt4o.InputPricePerMillionTokens);
        Assert.NotNull(gpt4o.OutputPricePerMillionTokens);
        // 0.0000025 per token * 1,000,000 = 2.5 per million
        Assert.Equal(2.5m, gpt4o.InputPricePerMillionTokens!.Value);
        // 0.00001 per token * 1,000,000 = 10.0 per million
        Assert.Equal(10.0m, gpt4o.OutputPricePerMillionTokens!.Value);
    }

    [Fact]
    public void ModelsDevSource_Parse_FallsBackToPricingField()
    {
        string json =
            @"{""openai"":{""gpt-4o"":{""pricing"":{""input"":0.0000025,""output"":0.00001}}}}";
        var doc = JsonDocument.Parse(json);
        IReadOnlyList<ModelMetadata> result = ModelsDevMetadataSource.Parse(doc.RootElement);
        Assert.NotEmpty(result);
        ModelMetadata gpt4o = result.First();
        Assert.Equal(2.5m, gpt4o.InputPricePerMillionTokens!.Value);
        Assert.Equal(10.0m, gpt4o.OutputPricePerMillionTokens!.Value);
    }
}

internal sealed class FakeHandler : DelegatingHandler
{
    private readonly string _response;

    public FakeHandler(string response)
    {
        _response = response;
    }

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(StatusCode)
        {
            Content = new StringContent(_response, Encoding.UTF8, "application/json")
        });
    }
}

internal sealed class StaticOverlaySource : IModelMetadataSource
{
    private readonly IReadOnlyList<ModelMetadata> _entries;

    public StaticOverlaySource(IReadOnlyList<ModelMetadata> entries)
    {
        _entries = entries;
    }

    public string Name => "overlay";

    public ValueTask<IReadOnlyList<ModelMetadata>> LoadAsync(CancellationToken ct = default)
    {
        return new ValueTask<IReadOnlyList<ModelMetadata>>(_entries);
    }
}

internal sealed class ThrowingMetadataSource : IModelMetadataSource
{
    public string Name => "throwing";

    public ValueTask<IReadOnlyList<ModelMetadata>> LoadAsync(CancellationToken ct = default)
    {
        throw new InvalidOperationException("Simulated source failure");
    }
}

internal sealed class CancellingMetadataSource : IModelMetadataSource
{
    public string Name => "cancelling";

    public async ValueTask<IReadOnlyList<ModelMetadata>> LoadAsync(CancellationToken ct = default)
    {
        await Task.Delay(10000, ct);
        return Array.Empty<ModelMetadata>();
    }
}
