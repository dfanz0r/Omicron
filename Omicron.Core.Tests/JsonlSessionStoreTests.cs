using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Sessions;
using Xunit;

namespace Omicron.Core.Tests;

public class JsonlSessionStoreTests : IDisposable
{
    private readonly string _tempDir;

    public JsonlSessionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"omicron-jsonl-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private JsonlSessionStore CreateStore() => new(_tempDir);

    [Fact]
    public async Task CreateAndGetSession()
    {
        using var store = CreateStore();
        var request = new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat,
            SystemPrompt: "You are helpful.");

        var record = await store.CreateSessionAsync(request);

        Assert.NotEqual(default, record.SessionId);
        Assert.Equal("gpt-4o", record.ModelId);
        Assert.Equal("openai", record.ProviderName);
        Assert.Equal(ApiType.OpenAiChat, record.ApiType);
        Assert.Equal("You are helpful.", record.SystemPrompt);

        var retrieved = await store.GetSessionAsync(record.SessionId);
        Assert.NotNull(retrieved);
        Assert.Equal(record.SessionId, retrieved.SessionId);
    }

    [Fact]
    public async Task ListSessions()
    {
        using var store = CreateStore();
        await store.CreateSessionAsync(new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat));
        await store.CreateSessionAsync(new SessionCreateRequest("claude-3", "anthropic", ApiType.AnthropicMessages));

        var sessions = await store.ListSessionsAsync(new SessionListQuery());
        Assert.Equal(2, sessions.Count);
    }

    [Fact]
    public async Task AppendAndReadEvents()
    {
        using var store = CreateStore();
        var record = await store.CreateSessionAsync(
            new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat));

        var event1 = new SessionStartedEvent(new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow,
            record.SessionId), AgentId.New(), "gpt-4o", "openai");
        var event2 = new UserMessageEvent(
                new EventEnvelope(EventId.New(), 2, DateTimeOffset.UtcNow, record.SessionId), "Hello");

        await store.AppendEventsAsync(record.SessionId, new List<OmicronEvent> { event1, event2 });

        var readBack = new List<OmicronEvent>();
        await foreach (var e in store.ReadEventsAsync(record.SessionId, EventSequenceRange.All))
            readBack.Add(e);

        Assert.Equal(2, readBack.Count);
        Assert.IsType<SessionStartedEvent>(readBack[0]);
        Assert.IsType<UserMessageEvent>(readBack[1]);
        Assert.Equal("Hello", ((UserMessageEvent)readBack[1]).Text);
    }

    [Fact]
    public async Task ReopenAndListSessions()
    {
        SessionId sessionId;
        using (var store = CreateStore())
        {
            var record = await store.CreateSessionAsync(
                new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat));
            sessionId = record.SessionId;

            await store.AppendEventsAsync(sessionId, new List<OmicronEvent>
            {
                new SessionStartedEvent(new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow,
                    sessionId), AgentId.New(), "gpt-4o", "openai")
            });
        }

        // Reopen
        using var reopened = CreateStore();
        var sessions = await reopened.ListSessionsAsync(new SessionListQuery());
        Assert.Single(sessions);
        Assert.Equal(sessionId, sessions[0].SessionId);
        Assert.Equal("gpt-4o", sessions[0].ModelId);
    }

    [Fact]
    public async Task ReopenAndReadEvents()
    {
        SessionId sessionId;
        using (var store = CreateStore())
        {
            var record = await store.CreateSessionAsync(
                new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat));
            sessionId = record.SessionId;

            await store.AppendEventsAsync(sessionId, new List<OmicronEvent>
            {
                new SessionStartedEvent(new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow,
                    sessionId), AgentId.New(), "gpt-4o", "openai"),
                new UserMessageEvent(
                new EventEnvelope(EventId.New(), 2, DateTimeOffset.UtcNow, sessionId), "Hi after reopen")
            });
        }

        // Reopen and read
        using var reopened = CreateStore();
        var events = new List<OmicronEvent>();
        await foreach (var e in reopened.ReadEventsAsync(sessionId, EventSequenceRange.All))
            events.Add(e);

        Assert.Equal(2, events.Count);
        Assert.IsType<SessionStartedEvent>(events[0]);
        var userMsg = Assert.IsType<UserMessageEvent>(events[1]);
        Assert.Equal("Hi after reopen", userMsg.Text);
    }

    [Fact]
    public async Task GetEventCount_AfterReopen()
    {
        SessionId sessionId;
        using (var store = CreateStore())
        {
            var record = await store.CreateSessionAsync(
                new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat));
            sessionId = record.SessionId;

            await store.AppendEventsAsync(sessionId, new List<OmicronEvent>
            {
                new SessionStartedEvent(new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow,
                    sessionId), AgentId.New(), "gpt-4o", "openai"),
                new UserMessageEvent(
                new EventEnvelope(EventId.New(), 2, DateTimeOffset.UtcNow, sessionId), "Count me")
            });
        }

        using var reopened = CreateStore();
        var count = await reopened.GetEventCountAsync(sessionId);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task SessionEndedEvent_RoundTrip()
    {
        using var store = CreateStore();
        var record = await store.CreateSessionAsync(
            new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat));

        var ended = new SessionEndedEvent(
                new EventEnvelope(EventId.New(), 1, DateTimeOffset.UtcNow, record.SessionId), "user_requested");

        await store.AppendEventsAsync(record.SessionId, new List<OmicronEvent> { ended });

        var events = new List<OmicronEvent>();
        await foreach (var e in store.ReadEventsAsync(record.SessionId, EventSequenceRange.All))
            events.Add(e);

        var deserialized = Assert.IsType<SessionEndedEvent>(events[0]);
        Assert.Equal("user_requested", deserialized.Reason);
    }

    [Fact]
    public async Task DuplicateSessionId_Throws()
    {
        using var store = CreateStore();
        var request = new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat,
            SessionId: SessionId.New());
        var first = await store.CreateSessionAsync(request);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateSessionAsync(request).AsTask());
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task ProviderStateEvent_RoundTrip()
    {
        using var store = CreateStore();
        var record = await store.CreateSessionAsync(
            new SessionCreateRequest("gpt-5.5", "openai", ApiType.OpenAiResponses));

        var key = ProviderStateKey.Create(record.SessionId, AgentId.New(), "openai", "gpt-5.5", ApiType.OpenAiResponses);
        var pse = new ProviderStateUpdatedEvent(
                new EventEnvelope(EventId.New(), 1, DateTimeOffset.UtcNow, record.SessionId),
                new ProviderStateSnapshot(key, "resp_123", null, null, null),
                "test");

        await store.AppendEventsAsync(record.SessionId, new List<OmicronEvent> { pse });

        var events = new List<OmicronEvent>();
        await foreach (var e in store.ReadEventsAsync(record.SessionId, EventSequenceRange.All))
            events.Add(e);

        var deserialized = Assert.IsType<ProviderStateUpdatedEvent>(events[0]);
        Assert.Equal("resp_123", deserialized.State.PreviousResponseId);
        Assert.Equal("test", deserialized.Reason);
    }

    [Fact]
    public async Task HostWithJsonlStore_PersistsEvents()
    {
        using var store = new JsonlSessionStore(_tempDir);
        var host = new OmicronHost(Environment.CurrentDirectory, store);

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

        var session = host.CreateSession(model);
        await foreach (var _ in session.PromptAsync("Hi")) { }

        // Verify via reopened store
        using var reopened = new JsonlSessionStore(_tempDir);
        var sessions = await reopened.ListSessionsAsync(new SessionListQuery());
        Assert.NotEmpty(sessions);
        Assert.Contains(sessions, s => s.SessionId == session.Id);

        var events = new List<OmicronEvent>();
        await foreach (var e in reopened.ReadEventsAsync(session.Id, EventSequenceRange.All))
            events.Add(e);

        Assert.Contains(events, e => e is SessionStartedEvent);
        Assert.Contains(events, e => e is UserMessageEvent);
    }
}


