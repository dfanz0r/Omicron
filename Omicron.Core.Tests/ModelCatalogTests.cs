using System.Net;
using Omicron.Core.Models;
using Omicron.Core.Providers;
using Xunit;

namespace Omicron.Core.Tests;

public class ModelCatalogTests
{
    [Fact]
    public void ModelCatalogService_TracksFreeModels()
    {
        var providerFactory = new ProviderFactory();
        var catalog = new ModelCatalogService(providerFactory);

        var model = new Model
        {
            Id = "free-model",
            Name = "Free Model",
            ProviderName = "openrouter"
        };
        catalog.EnsureModel("or:free-model", model);
        catalog.MarkFree("or:free-model");

        Assert.True(catalog.IsFreeModel("or:free-model"));
        Assert.True(catalog.FreeModelKeys.Contains("or:free-model"));
    }

    [Fact]
    public void ModelCatalogService_SeedsOpenRouterGpt54MiniResponsesModel()
    {
        var providerFactory = new ProviderFactory();
        var catalog = new ModelCatalogService(providerFactory);

        Assert.True(catalog.Models.TryGetValue("or:openai/gpt-5.4-mini", out Model? model));
        Assert.Equal("openai/gpt-5.4-mini", model.Id);
        Assert.Equal("openrouter", model.ProviderName);
        Assert.Equal(ApiType.OpenAiResponses, model.ApiType);
        Assert.Equal("https://openrouter.ai/api/v1", model.BaseUrl);
        Assert.False(model.GetEffectiveCompatibility().SupportsPreviousResponseId);
        Assert.Equal(ProviderStoragePolicy.PreferStateless, model.StoragePolicy);
        Assert.False(catalog.IsFreeModel("or:openai/gpt-5.4-mini"));
    }

    [Fact]
    public async Task OpenRouterDiscovery_OnlyMarksZeroPricedModelsAsFree()
    {
        string json = """
                      {
                        "data": [
                          {
                            "id": "free/model:free",
                            "name": "Free Model",
                            "context_length": 4096,
                            "pricing": { "prompt": "0", "completion": "0" }
                          },
                          {
                            "id": "paid/model",
                            "name": "Paid Model",
                            "context_length": 8192,
                            "pricing": { "prompt": "0.00000015", "completion": "0.00000060" }
                          },
                          {
                            "id": "unknown/model",
                            "name": "Unknown Price Model",
                            "context_length": 8192
                          }
                        ]
                      }
                      """;

        var registry = new SingleProviderRegistry();
        registry.Register("openrouter",
            new OpenRouterProvider(new HttpClient(new JsonHandler(json))));
        var catalog = new ModelCatalogService(registry);

        await catalog.DiscoverAsync();

        Assert.True(catalog.IsFreeModel("or:free/model:free"));
        Assert.False(catalog.IsFreeModel("or:paid/model"));
        Assert.False(catalog.IsFreeModel("or:unknown/model"));
    }

    [Fact]
    public async Task OpenRouterProvider_FetchModels_ParsesPricingIntoEntries()
    {
        string json = """
                      {
                        "data": [
                          {
                            "id": "paid/model",
                            "name": "Paid Model",
                            "context_length": 8192,
                            "pricing": { "prompt": "1.5e-7", "completion": "6.0e-7" }
                          }
                        ]
                      }
                      """;

        var provider = new OpenRouterProvider(new HttpClient(new JsonHandler(json)));

        List<OpenRouterModelEntry> models = await provider.FetchModelsAsync();

        OpenRouterModelEntry model = Assert.Single(models);
        Assert.Equal("paid/model", model.Id);
        Assert.Equal(1.5e-7, model.PromptCost, 12);
        Assert.Equal(6.0e-7, model.CompletionCost, 12);
    }

    private sealed class SingleProviderRegistry : IProviderRegistry
    {
        private readonly Dictionary<string, IChatProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> ProviderNames => _providers.Keys;

        public void Register(string name, IChatProvider provider)
        {
            _providers[name] = provider;
        }

        public IChatProvider GetProvider(string name)
        {
            return _providers[name];
        }

        public bool TryGetProvider(string name, out IChatProvider? provider)
        {
            return _providers.TryGetValue(name, out provider);
        }
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
            return Task.FromResult(response);
        }
    }
}
