using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Sessions;
using Xunit;

namespace Omicron.Core.Tests;

public class InMemorySessionStoreTests
{
    [Fact]
    public async Task CreateSession_ReturnsRecordWithSessionId()
    {
        var store = new InMemorySessionStore();
        var request = new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat);

        SessionRecord record = await store.CreateSessionAsync(request);

        Assert.NotNull(record);
        Assert.NotEqual(default, record.SessionId);
        Assert.Equal("gpt-4o", record.ModelId);
        Assert.Equal("openai", record.ProviderName);
        Assert.Equal(ApiType.OpenAiChat, record.ApiType);
        Assert.Equal(SessionStatus.Active, record.Status);
    }

    [Fact]
    public async Task CreateSession_WithSystemPrompt()
    {
        var store = new InMemorySessionStore();
        var request = new SessionCreateRequest("gpt-4o",
            "openai",
            ApiType.OpenAiChat,
            SystemPrompt: "You are a helpful assistant.");

        SessionRecord record = await store.CreateSessionAsync(request);

        Assert.Equal("You are a helpful assistant.", record.SystemPrompt);
    }

    [Fact]
    public async Task GetSession_ReturnsRecord()
    {
        var store = new InMemorySessionStore();
        var request = new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat);
        SessionRecord created = await store.CreateSessionAsync(request);

        SessionRecord? retrieved = await store.GetSessionAsync(created.SessionId);

        Assert.NotNull(retrieved);
        Assert.Equal(created.SessionId, retrieved.SessionId);
        Assert.Equal(created.ModelId, retrieved.ModelId);
    }

    [Fact]
    public async Task GetSession_NotFound_ReturnsNull()
    {
        var store = new InMemorySessionStore();
        SessionRecord? result = await store.GetSessionAsync(SessionId.New());
        Assert.Null(result);
    }

    [Fact]
    public async Task ListSessions_ReturnsAllSessions()
    {
        var store = new InMemorySessionStore();
        await store.CreateSessionAsync(new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat));
        await store.CreateSessionAsync(new SessionCreateRequest("claude-3", "anthropic", ApiType.AnthropicMessages));
        await store.CreateSessionAsync(new SessionCreateRequest("gpt-5.5", "openai", ApiType.OpenAiResponses));

        IReadOnlyList<SessionRecord> sessions = await store.ListSessionsAsync(new SessionListQuery());

        Assert.Equal(3, sessions.Count);
    }

    [Fact]
    public async Task ListSessions_FiltersByStatus()
    {
        var store = new InMemorySessionStore();
        await store.CreateSessionAsync(new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat));

        IReadOnlyList<SessionRecord> active = await store.ListSessionsAsync(
            new SessionListQuery(StatusFilter: SessionStatus.Active));
        Assert.Single(active);

        IReadOnlyList<SessionRecord> archived = await store.ListSessionsAsync(
            new SessionListQuery(StatusFilter: SessionStatus.Archived));
        Assert.Empty(archived);
    }

    [Fact]
    public async Task ListSessions_FiltersByProvider()
    {
        var store = new InMemorySessionStore();
        await store.CreateSessionAsync(new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat));
        await store.CreateSessionAsync(new SessionCreateRequest("claude-3", "anthropic", ApiType.AnthropicMessages));

        IReadOnlyList<SessionRecord> openai =
            await store.ListSessionsAsync(new SessionListQuery(ProviderFilter: "openai"));
        Assert.Single(openai);
        Assert.All(openai, s => Assert.Contains("openai", s.ProviderName));
    }

    [Fact]
    public async Task ListSessions_RespectsLimitAndOffset()
    {
        var store = new InMemorySessionStore();
        for (int i = 0; i < 10; i++)
        {
            await store.CreateSessionAsync(new SessionCreateRequest($"model-{i}", "test", ApiType.OpenAiChat));
        }

        IReadOnlyList<SessionRecord> first5 = await store.ListSessionsAsync(new SessionListQuery(5));
        Assert.Equal(5, first5.Count);

        IReadOnlyList<SessionRecord> last5 = await store.ListSessionsAsync(new SessionListQuery(5, 5));
        Assert.Equal(5, last5.Count);

        IReadOnlyList<SessionRecord> past = await store.ListSessionsAsync(new SessionListQuery(5, 20));
        Assert.Empty(past);
    }

    [Fact]
    public async Task AppendAndReadEvents()
    {
        var store = new InMemorySessionStore();
        var request = new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat);
        SessionRecord record = await store.CreateSessionAsync(request);

        var events = new List<OmicronEvent>
        {
            new SessionStartedEvent(new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, record.SessionId),
                AgentId.New(),
                "gpt-4o",
                "openai"),
            new UserMessageEvent(new EventEnvelope(EventId.New(), 2, DateTimeOffset.UtcNow, record.SessionId),
                "Hello"u8)
        };

        await store.AppendEventsAsync(record.SessionId, events);

        var readEvents = new List<OmicronEvent>();
        await foreach (OmicronEvent e in store.ReadEventsAsync(record.SessionId, EventSequenceRange.All))
        {
            readEvents.Add(e);
        }

        Assert.Equal(2, readEvents.Count);
        Assert.IsType<SessionStartedEvent>(readEvents[0]);
        Assert.IsType<UserMessageEvent>(readEvents[1]);
    }

    [Fact]
    public async Task ReadEvents_WithSequenceRange()
    {
        var store = new InMemorySessionStore();
        var request = new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat);
        SessionRecord record = await store.CreateSessionAsync(request);

        await store.AppendEventsAsync(record.SessionId,
            new List<OmicronEvent>
            {
                new SessionStartedEvent(new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, record.SessionId),
                    AgentId.New(),
                    "gpt-4o",
                    "openai"),
                new UserMessageEvent(new EventEnvelope(EventId.New(), 2, DateTimeOffset.UtcNow, record.SessionId),
                    "Hello"u8),
                new TurnStartedEvent(new EventEnvelope(EventId.New(), 3, DateTimeOffset.UtcNow, record.SessionId),
                    "Hello")
            });

        // Read events with sequence > 1 and <= 3
        var readEvents = new List<OmicronEvent>();
        await foreach (
            OmicronEvent e in store.ReadEventsAsync(record.SessionId, new EventSequenceRange(1, 3))
        )
        {
            readEvents.Add(e);
        }

        Assert.Equal(2, readEvents.Count);
        Assert.Equal(2, readEvents[0].Sequence);
        Assert.Equal(3, readEvents[1].Sequence);
    }

    [Fact]
    public async Task GetEventCount()
    {
        var store = new InMemorySessionStore();
        var request = new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat);
        SessionRecord record = await store.CreateSessionAsync(request);

        Assert.Equal(0, await store.GetEventCountAsync(record.SessionId));

        await store.AppendEventsAsync(record.SessionId,
            new List<OmicronEvent>
            {
                new SessionStartedEvent(new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, record.SessionId),
                    AgentId.New(),
                    "gpt-4o",
                    "openai")
            });

        Assert.Equal(1, await store.GetEventCountAsync(record.SessionId));
    }

    [Fact]
    public async Task ReadEvents_EmptySession_ReturnsEmpty()
    {
        var store = new InMemorySessionStore();
        SessionRecord record = await store.CreateSessionAsync(
            new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat));

        var readEvents = new List<OmicronEvent>();
        await foreach (OmicronEvent e in store.ReadEventsAsync(record.SessionId, EventSequenceRange.All))
        {
            readEvents.Add(e);
        }

        Assert.Empty(readEvents);
    }

    [Fact]
    public async Task UpdateSession()
    {
        var store = new InMemorySessionStore();
        SessionRecord record = await store.CreateSessionAsync(
            new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat));

        SessionRecord? updated = await store.UpdateSessionAsync(record.SessionId,
            r => r with
            {
                Status = SessionStatus.Archived,
                Label = "test-session"
            });

        Assert.NotNull(updated);
        Assert.Equal(SessionStatus.Archived, updated.Status);
        Assert.Equal("test-session", updated.Label);
        Assert.Equal(record.SessionId, updated.SessionId);
    }

    [Fact]
    public async Task ConcurrentSessionsDoNotInterfere()
    {
        var store = new InMemorySessionStore();

        SessionRecord r1 = await store.CreateSessionAsync(
            new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat));
        SessionRecord r2 = await store.CreateSessionAsync(
            new SessionCreateRequest("claude-3", "anthropic", ApiType.AnthropicMessages));

        await store.AppendEventsAsync(r1.SessionId,
            new List<OmicronEvent>
            {
                new SessionStartedEvent(new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, r1.SessionId),
                    AgentId.New(),
                    "gpt-4o",
                    "openai")
            });

        await store.AppendEventsAsync(r2.SessionId,
            new List<OmicronEvent>
            {
                new SessionStartedEvent(new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, r2.SessionId),
                    AgentId.New(),
                    "claude-3",
                    "anthropic"),
                new UserMessageEvent(new EventEnvelope(EventId.New(), 2, DateTimeOffset.UtcNow, r2.SessionId),
                    "Hello"u8)
            });

        Assert.Equal(1, await store.GetEventCountAsync(r1.SessionId));
        Assert.Equal(2, await store.GetEventCountAsync(r2.SessionId));
    }
}
