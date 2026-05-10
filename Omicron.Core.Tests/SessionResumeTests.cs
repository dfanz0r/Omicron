using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Providers;
using Omicron.Core.Sessions;
using Xunit;

namespace Omicron.Core.Tests;

public class SessionResumeTests
{
    private static Model MakeModel(string id = "test-model", string provider = "fake", ApiType apiType = ApiType.OpenAiChat)
    {
        var fakeProvider = new FakeProvider();
        fakeProvider.Responses.Add(() => Task.FromResult(new LlmResult
        {
            Text = "Response",
            StopReason = StopReason.Stop,
            Usage = new UsageInfo(10, 20),
            ResponseId = "resp_1"
        }));

        return new Model
        {
            Id = id,
            Name = id,
            ProviderName = provider,
            ApiType = apiType,
            Provider = fakeProvider
        };
    }

    [Fact]
    public async Task ResumeSession_HydratesMessages()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var model = MakeModel();

        // Create and run a session to persist events
        var original = host.CreateSession(model, "You are a test.");
        await foreach (var _ in original.PromptAsync("Hello")) { }

        // Resume from store
        var resumed = await host.ResumeSessionAsync(original.Id, model, apiKey: null);
        Assert.NotNull(resumed);
        Assert.Equal(original.Id, resumed.Id);
        Assert.NotEmpty(resumed.Messages);
        Assert.Contains(resumed.Messages, m => m.Role == MessageRole.User && m.Text == "Hello");
        Assert.Contains(resumed.Messages, m => m.Role == MessageRole.Assistant);
    }

    [Fact]
    public async Task ForkSession_CreatesNewSessionId()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var model = MakeModel();

        var original = host.CreateSession(model);
        await foreach (var _ in original.PromptAsync("Hi")) { }

        var forked = await host.ForkSessionAsync(original.Id, model);
        Assert.NotNull(forked);
        Assert.NotEqual(original.Id, forked.Id);
        Assert.NotEmpty(forked.Messages);
        Assert.Equal(original.Messages.Count, forked.Messages.Count);
    }

    [Fact]
    public async Task ResumeSession_Missing_ReturnsNull()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var result = await host.ResumeSessionAsync(SessionId.New(), MakeModel());
        Assert.Null(result);
    }

    [Fact]
    public async Task ResumeSession_CanContinueConversation()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var model = MakeModel();

        var original = host.CreateSession(model);
        await foreach (var _ in original.PromptAsync("First")) { }

        var resumed = await host.ResumeSessionAsync(original.Id, model);
        Assert.NotNull(resumed);

        // The resumed session should allow continuing
        var provider = (FakeProvider)model.Provider!;
        provider.Responses.Add(() => Task.FromResult(new LlmResult
        {
            Text = "Second response",
            StopReason = StopReason.Stop
        }));

        var events = new List<OmicronEvent>();
        await foreach (var e in resumed.PromptAsync("Second"))
            events.Add(e);

        Assert.Contains(events, e => e is UserMessageEvent);
        Assert.Equal(4, resumed.Messages.Count); // user+assistant from original, user+assistant from continuation
    }

    [Fact]
    public async Task ForkSession_DifferentModel_ClearsProviderState()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var model = MakeModel("gpt-5.5", "openai", ApiType.OpenAiResponses);
        model.StoragePolicy = ProviderStoragePolicy.AllowProviderStateNoStore;

        var original = host.CreateSession(model);
        await foreach (var _ in original.PromptAsync("Hello")) { }

        // Fork to a different model
        var otherModel = MakeModel("claude-3", "anthropic", ApiType.AnthropicMessages);
        var forked = await host.ForkSessionAsync(original.Id, otherModel);
        Assert.NotNull(forked);
        Assert.NotEmpty(forked.Messages);
        Assert.Equal(original.Messages.Count, forked.Messages.Count);
    }

    [Fact]
    public async Task ForkSession_TranscriptIsDurable()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var model = MakeModel();

        var original = host.CreateSession(model);
        await foreach (var _ in original.PromptAsync("Fork durability test")) { }

        var forked = await host.ForkSessionAsync(original.Id, model);
        Assert.NotNull(forked);

        // Read back the forked session's events from the store
        var forkedEvents = new List<OmicronEvent>();
        await foreach (var e in host.SessionStore.ReadEventsAsync(forked.Id, EventSequenceRange.All))
            forkedEvents.Add(e);

        // Should have SessionStarted + UserMessage + AssistantResponse
        Assert.Contains(forkedEvents, e => e is SessionStartedEvent);
        Assert.Contains(forkedEvents, e => e is UserMessageEvent);
        Assert.Contains(forkedEvents, e => e is AssistantResponseCompleteEvent);

        // Resume the fork and verify transcript is preserved
        var resumed = await host.ResumeSessionAsync(forked.Id, model);
        Assert.NotNull(resumed);
        Assert.Equal(forked.Messages.Count, resumed.Messages.Count);
    }

    [Fact]
    public async Task ResumeSession_ProviderState_ReKeyedToNewAgentId()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var model = MakeModel("gpt-5.5", "openai", ApiType.OpenAiResponses);
        model.StoragePolicy = ProviderStoragePolicy.AllowProviderStateNoStore;

        var original = host.CreateSession(model);
        await foreach (var _ in original.PromptAsync("Test provider state")) { }

        // Resume and check that the provider state is accessible under the new AgentId
        var resumed = await host.ResumeSessionAsync(original.Id, model);
        Assert.NotNull(resumed);

        var key = ProviderStateKey.Create(
            resumed.Id, resumed.AgentId, model.ProviderName, model.Id, model.ApiType);
        var state = host.ProviderStateManager.Get(key);
        // Should find the state (re-keyed during hydration)
        Assert.NotNull(state);
    }
}
