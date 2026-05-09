using Omicron.Core.Config;
using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Providers;
using Omicron.Core.Sessions;
using Xunit;

namespace Omicron.Core.Tests;

public class PersistenceIntegrationTests
{
    [Fact]
    public async Task OmicronHost_CreateSession_CreatesSessionRecord()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var store = (InMemorySessionStore)host.SessionStore;

        var model = new Model
        {
            Id = "test-model",
            Name = "Test",
            ProviderName = "fake",
            ApiType = ApiType.OpenAiChat,
            Provider = new FakeProvider
            {
                Responses =
                {
                    () => Task.FromResult(new LlmResult { Text = "Hello", StopReason = StopReason.Stop })
                }
            }
        };

        var session = host.CreateSession(model, "You are a test.");

        var record = await store.GetSessionAsync(session.Id);
        Assert.NotNull(record);
        Assert.Equal(session.Id, record.SessionId);
        Assert.Equal("test-model", record.ModelId);
        Assert.Equal("fake", record.ProviderName);
        Assert.Equal(ApiType.OpenAiChat, record.ApiType);
        Assert.Equal("You are a test.", record.SystemPrompt);
        Assert.Equal(SessionStatus.Active, record.Status);
    }

    [Fact]
    public async Task SessionEvents_ArePersistedToStore()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var store = (InMemorySessionStore)host.SessionStore;

        var model = new Model
        {
            Id = "test-model",
            Name = "Test",
            ProviderName = "fake",
            ApiType = ApiType.OpenAiChat,
            Provider = new FakeProvider
            {
                Responses =
                {
                    () => Task.FromResult(new LlmResult
                    {
                        Text = "Response text",
                        StopReason = StopReason.Stop,
                        Usage = new UsageInfo(10, 20),
                        ResponseId = "test_resp_1"
                    })
                }
            }
        };

        var session = host.CreateSession(model);

        await foreach (var _ in session.PromptAsync("Hello")) { }

        // Read persisted events from the store
        var persisted = new List<OmicronEvent>();
        await foreach (var e in store.ReadEventsAsync(session.Id, EventSequenceRange.All))
            persisted.Add(e);

        // Should have at least: SessionStarted, UserMessage, TurnStarted, AssistantTextDelta, AssistantResponseComplete
        Assert.Contains(persisted, e => e is SessionStartedEvent);
        Assert.Contains(persisted, e => e is UserMessageEvent u && u.Text == "Hello");
        Assert.Contains(persisted, e => e is AssistantResponseCompleteEvent arc && arc.FullText == "Response text");

        // Verify sequence order is preserved
        for (int i = 1; i < persisted.Count; i++)
            Assert.True(persisted[i - 1].Sequence < persisted[i].Sequence);
    }

    [Fact]
    public async Task ProviderStateEvents_ArePersistedViaHostSink()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var store = (InMemorySessionStore)host.SessionStore;

        var model = new Model
        {
            Id = "test-model",
            Name = "Test",
            ProviderName = "fake",
            StoragePolicy = ProviderStoragePolicy.AllowProviderStateNoStore,
            ApiType = ApiType.OpenAiResponses,
            Provider = new FakeProvider
            {
                Responses =
                {
                    () => Task.FromResult(new LlmResult
                    {
                        Text = "Hello",
                        StopReason = StopReason.Stop,
                        ResponseId = "resp_abc123"
                    })
                }
            }
        };

        var session = host.CreateSession(model);

        // ProviderStateManager is wired to Events which is the persistent sink
        var key = ProviderStateKey.Create(session.Id, session.AgentId, "fake", "test-model", ApiType.OpenAiResponses);

        // Manually set provider state (simulating what AgentSession does)
        host.ProviderStateManager.Set(
            new ProviderTurnState(key, "resp_abc123", null, null, null),
            reason: "response_completed");

        // Read persisted events
        var persisted = new List<OmicronEvent>();
        await foreach (var e in store.ReadEventsAsync(session.Id, EventSequenceRange.All))
            persisted.Add(e);

        Assert.Contains(persisted, e => e is ProviderStateUpdatedEvent);
    }

    [Fact]
    public async Task ExecutionEvents_ArePersistedViaHostSink()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var store = (InMemorySessionStore)host.SessionStore;

        var model = new Model
        {
            Id = "test-model",
            Name = "Test",
            ProviderName = "fake",
            ApiType = ApiType.OpenAiChat,
            Provider = new FakeProvider()
        };

        var session = host.CreateSession(model);
        var key = ProviderStateKey.Create(session.Id, session.AgentId, "fake", "test-model", ApiType.OpenAiChat);

        // Emit an execution event via the host's event sink (which is PersistentEventSink)
        host.Events.Emit(new ExecutionStartedEvent(
                new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, session.Id), "echo test", "/tmp", null));

        var persisted = new List<OmicronEvent>();
        await foreach (var e in store.ReadEventsAsync(session.Id, EventSequenceRange.All))
            persisted.Add(e);

        Assert.Contains(persisted, e => e is ExecutionStartedEvent);
    }

    [Fact]
    public async Task PromptAsyncRun_PersistsAllEventsInOrder()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var store = (InMemorySessionStore)host.SessionStore;

        var model = new Model
        {
            Id = "test-model",
            Name = "Test",
            ProviderName = "fake",
            ApiType = ApiType.OpenAiChat,
            Provider = new FakeProvider
            {
                Responses =
                {
                    () => Task.FromResult(new LlmResult
                    {
                        Text = "Hello!",
                        StopReason = StopReason.Stop,
                        Usage = new UsageInfo(5, 15)
                    })
                }
            }
        };

        var session = host.CreateSession(model);

        var yieldedEvents = new List<OmicronEvent>();
        await foreach (var evt in session.PromptAsync("Hi"))
            yieldedEvents.Add(evt);

        // Read persisted
        var persisted = new List<OmicronEvent>();
        await foreach (var e in store.ReadEventsAsync(session.Id, EventSequenceRange.All))
            persisted.Add(e);

        // Every yielded event should also be in the store (persisted)
        foreach (var yielded in yieldedEvents)
        {
            Assert.Contains(persisted, p => p.Id == yielded.Id);
        }

        // Persisted events should be exactly in sequence order
        for (int i = 1; i < persisted.Count; i++)
            Assert.True(persisted[i - 1].Sequence < persisted[i].Sequence);

        // Verify we have the full conversation lifecycle
        var types = persisted.Select(e => e.GetType().Name).Distinct().ToList();
        Assert.Contains("SessionStartedEvent", types);
        Assert.Contains("UserMessageEvent", types);
        Assert.Contains("TurnStartedEvent", types);
        Assert.Contains("AssistantTextDeltaEvent", types);
        Assert.Contains("AssistantResponseCompleteEvent", types);
    }

    [Fact]
    public async Task PersistentEventSink_ReportsFailuresViaCallback()
    {
        var innerSink = new InMemoryEventSink();
        var store = new InMemorySessionStore();
        var sink = new PersistentEventSink(innerSink, store);

        Exception? capturedEx = null;
        OmicronEvent? capturedEvent = null;
        string? capturedLabel = null;
        sink.OnError = (ex, evt, label) =>
        {
            capturedEx = ex;
            capturedEvent = evt;
            capturedLabel = label;
        };

        var evt = new SessionStartedEvent(new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, SessionId.New()), AgentId.New(), "test", "test");

        // No session record created yet — AppendEventsAsync should throw
        sink.Emit(evt);

        Assert.NotNull(capturedEx);
        Assert.NotNull(capturedEvent);
        Assert.Equal("Emit", capturedLabel);
        Assert.Equal(1, sink.FailureCount);
        Assert.Equal(0, sink.PersistedCount);
    }

    [Fact]
    public async Task PersistentEventSink_CountsSuccessfulPersists()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var persistentSink = (PersistentEventSink)host.Events;

        var model = new Model
        {
            Id = "test-model",
            Name = "Test",
            ProviderName = "fake",
            ApiType = ApiType.OpenAiChat,
            Provider = new FakeProvider
            {
                Responses =
                {
                    () => Task.FromResult(new LlmResult { Text = "Hi", StopReason = StopReason.Stop })
                }
            }
        };

        var session = host.CreateSession(model);
        await foreach (var _ in session.PromptAsync("Hello")) { }

        Assert.True(persistentSink.PersistedCount > 0);
        Assert.Equal(0, persistentSink.FailureCount);
    }
}

