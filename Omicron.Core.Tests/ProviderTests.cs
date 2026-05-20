using System.Net;
using Omicron.Core.Models;
using Omicron.Core.Providers;
using Xunit;

namespace Omicron.Core.Tests;

/// <summary>
///     A fake provider for testing agent behavior without real API calls.
/// </summary>
public class FakeProvider : IChatProvider
{
    public List<Func<Task<LlmResult>>> Responses { get; } = new();
    public string Name => "Fake";
    public string DefaultBaseUrl => "http://fake";

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options)
    {
        Func<Task<LlmResult>>? responseFunc = Responses.Count > 0 ? Responses[0] : null;
        if (Responses.Count > 0)
        {
            Responses.RemoveAt(0);
        }

        LlmResult result;
        if (responseFunc is not null)
        {
            result = await responseFunc();
        }
        else
        {
            result = new LlmResult
            {
                Text = "Default fake response"
            };
        }

        // Simulate streaming by yielding the full result
        if (!string.IsNullOrEmpty(result.Text))
        {
            yield return new StreamEvent
            {
                Type = StreamEventType.TextDelta,
                Delta = result.Text
            };
        }

        foreach (ToolCallContent tc in result.ToolCalls)
        {
            yield return new StreamEvent
            {
                Type = StreamEventType.ToolCallEnd,
                ToolCall = tc
            };
        }

        if (result.StopReason == StopReason.Error)
        {
            yield return new StreamEvent
            {
                Type = StreamEventType.Error,
                ErrorMessage = result.ErrorMessage
            };
            yield break;
        }

        yield return new StreamEvent
        {
            Type = StreamEventType.Done,
            StopReason = result.StopReason,
            Usage = result.Usage,
            Delta = result.ResponseId
        };
    }
}

public class ProviderTests
{
    [Fact]
    public void ProviderFactory_RegistersDefaults()
    {
        var factory = new ProviderFactory();
        Assert.Contains("openai", factory.ProviderNames);
        Assert.Contains("anthropic", factory.ProviderNames);
    }

    [Fact]
    public void ProviderFactory_GetProvider_ReturnsCorrectType()
    {
        var factory = new ProviderFactory();
        IChatProvider openAi = factory.GetProvider("openai");
        IChatProvider anthropic = factory.GetProvider("anthropic");

        Assert.IsType<OpenAiProvider>(openAi);
        Assert.IsType<AnthropicProvider>(anthropic);
    }

    [Fact]
    public void ProviderFactory_GetProvider_ThrowsForUnknown()
    {
        var factory = new ProviderFactory();
        KeyNotFoundException ex = Assert.Throws<KeyNotFoundException>(() => factory.GetProvider("nonexistent"));
        Assert.Contains("nonexistent", ex.Message);
    }

    [Fact]
    public void ProviderFactory_TryGetProvider_ReturnsFalseForUnknown()
    {
        var factory = new ProviderFactory();
        Assert.False(factory.TryGetProvider("nonexistent", out _));
    }

    [Fact]
    public void ProviderFactory_Register_CustomProvider()
    {
        var factory = new ProviderFactory();
        var fake = new FakeProvider();
        factory.Register("my-custom", fake);

        Assert.True(factory.TryGetProvider("my-custom", out IChatProvider? retrieved));
        Assert.Same(fake, retrieved);
    }

    [Fact]
    public void ProviderFactory_ProviderNames_IncludesCustom()
    {
        var factory = new ProviderFactory();
        factory.Register("custom", new FakeProvider());
        Assert.Contains("custom", factory.ProviderNames);
    }

    [Fact]
    public async Task FakeProvider_StreamAsync_YieldsEvents()
    {
        var provider = new FakeProvider();
        provider.Responses.Add(() =>
            Task.FromResult(new LlmResult
            {
                Text = "Hello from fake!",
                StopReason = StopReason.Stop,
                Usage = new UsageInfo(10, 20)
            }));

        var model = new Model
        {
            Id = "test",
            ProviderName = "fake"
        };
        var messages = new List<Message>
        {
            Message.UserMessage("Hi")
        };

        var events = new List<StreamEvent>();
        await foreach (
            StreamEvent evt in provider.StreamAsync(model, messages, null, null, new ChatOptions())
        )
        {
            events.Add(evt);
        }

        Assert.Contains(events,
            e => e.Type == StreamEventType.TextDelta && e.Delta == "Hello from fake!");
        Assert.Contains(events, e => e.Type == StreamEventType.Done);
        StreamEvent done = events.First(e => e.Type == StreamEventType.Done);
        Assert.Equal(StopReason.Stop, done.StopReason);
        Assert.NotNull(done.Usage);
        Assert.Equal(10, done.Usage!.InputTokens);
    }

    [Fact]
    public async Task FakeProvider_CompleteAsync_ReturnsCorrectResult()
    {
        IChatProvider provider = new FakeProvider();
        ((FakeProvider)provider).Responses.Add(() =>
            Task.FromResult(new LlmResult
            {
                Text = "Four!",
                ToolCalls = new List<ToolCallContent>
                {
                    new("call_1",
                        "calculator",
                        new Dictionary<string, object?>
                        {
                            ["expression"] = "2+2"
                        })
                },
                StopReason = StopReason.ToolUse,
                Usage = new UsageInfo(5, 30)
            }));

        var model = new Model
        {
            Id = "test",
            ProviderName = "fake"
        };
        var messages = new List<Message>
        {
            Message.UserMessage("What's 2+2?")
        };

        LlmResult result = await provider.CompleteAsync(model, messages, null, null, new ChatOptions());

        Assert.Equal("Four!", result.Text);
        Assert.Single(result.ToolCalls);
        Assert.Equal("calculator", result.ToolCalls[0].Name);
        Assert.Equal(StopReason.ToolUse, result.StopReason);
    }

    [Fact]
    public async Task FakeProvider_StreamAsync_WithToolCalls()
    {
        var provider = new FakeProvider();
        provider.Responses.Add(() =>
            Task.FromResult(new LlmResult
            {
                Text = "Let me calculate",
                ToolCalls = new List<ToolCallContent>
                {
                    new("tc_1",
                        "calculator",
                        new Dictionary<string, object?>
                        {
                            ["expression"] = "2+2"
                        })
                },
                StopReason = StopReason.ToolUse
            }));

        var model = new Model
        {
            Id = "test",
            ProviderName = "fake"
        };
        var messages = new List<Message>
        {
            Message.UserMessage("2+2?")
        };

        var events = new List<StreamEvent>();
        await foreach (
            StreamEvent evt in provider.StreamAsync(model, messages, null, null, new ChatOptions())
        )
        {
            events.Add(evt);
        }

        StreamEvent? toolCallEnd = events.FirstOrDefault(e => e.Type == StreamEventType.ToolCallEnd);
        Assert.NotNull(toolCallEnd);
        Assert.NotNull(toolCallEnd.ToolCall);
        Assert.Equal("calculator", toolCallEnd.ToolCall.Name);
    }

    [Fact]
    public async Task FakeProvider_CompleteAsync_ErrorStopReason()
    {
        IChatProvider provider = new FakeProvider();
        ((FakeProvider)provider).Responses.Add(() =>
            Task.FromResult(new LlmResult
            {
                StopReason = StopReason.Error,
                ErrorMessage = "API error occurred"
            }));

        var model = new Model
        {
            Id = "test",
            ProviderName = "fake"
        };
        LlmResult result = await provider.CompleteAsync(model,
            new List<Message>(),
            null,
            null,
            new ChatOptions());

        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.Equal("API error occurred", result.ErrorMessage);
    }

    [Fact]
    public async Task OpenRouterProvider_ResponsesModel_RoutesToResponsesEndpoint()
    {
        const string response =
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_or_123\",\"status\":\"completed\"}}\n\n";
        var handler = new CaptureHandler(response);
        var provider = new OpenRouterProvider(new HttpClient(handler));
        var model = new Model
        {
            Id = "openai/gpt-5.5",
            ProviderName = "openrouter",
            ApiType = ApiType.OpenAiResponses,
            BaseUrl = "https://openrouter.ai/api/v1"
        };

        var events = new List<StreamEvent>();
        await foreach (
            StreamEvent evt in provider.StreamAsync(model,
                [Message.UserMessage("Hello")],
                null,
                null,
                new ChatOptions())
        )
        {
            events.Add(evt);
        }

        Assert.Equal("https://openrouter.ai/api/v1/responses", handler.RequestUri?.ToString());
        Assert.Contains(events, e => e.Type == StreamEventType.Done && e.Delta == "resp_or_123");
    }

    [Fact]
    public async Task OpenRouterProvider_ChatModel_RoutesToChatCompletionsEndpoint()
    {
        const string response =
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
        var handler = new CaptureHandler(response);
        var provider = new OpenRouterProvider(new HttpClient(handler));
        var model = new Model
        {
            Id = "openai/gpt-4o",
            ProviderName = "openrouter",
            ApiType = ApiType.OpenAiChat,
            BaseUrl = "https://openrouter.ai/api/v1"
        };

        await foreach (
            StreamEvent _ in provider.StreamAsync(model,
                [Message.UserMessage("Hello")],
                null,
                null,
                new ChatOptions())
        ) { }

        Assert.Equal("https://openrouter.ai/api/v1/chat/completions",
            handler.RequestUri?.ToString());
    }

    private sealed class CaptureHandler(string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        }
    }
}
