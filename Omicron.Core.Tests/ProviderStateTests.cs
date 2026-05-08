using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Sessions;
using Xunit;

namespace Omicron.Core.Tests;

public class ProviderStateTests
{
    [Fact]
    public void ProviderStateKey_Create_NormalizesProviderNameCase()
    {
        var sessionId = SessionId.New();
        var agentId = AgentId.New();
        var lower = ProviderStateKey.Create(sessionId, agentId, "openai", "gpt-4o", ApiType.OpenAiChat);
        var mixed = ProviderStateKey.Create(sessionId, agentId, "OpenAI", "gpt-4o", ApiType.OpenAiChat);
        var upper = ProviderStateKey.Create(sessionId, agentId, "OPENAI", "gpt-4o", ApiType.OpenAiChat);

        Assert.Equal(lower, mixed);
        Assert.Equal(lower, upper);
        Assert.Equal("openai", lower.ProviderName);
    }

    [Fact]
    public void ProviderStateKey_ScopesBySessionAgentProviderModelApiType()
    {
        var sessionId = SessionId.New();
        var agentId = AgentId.New();
        var key1 = ProviderStateKey.Create(sessionId, agentId, "openai", "gpt-4o", ApiType.OpenAiChat);
        var key2 = ProviderStateKey.Create(sessionId, agentId, "openai", "gpt-4o", ApiType.OpenAiChat);
        var key3 = ProviderStateKey.Create(sessionId, agentId, "anthropic", "claude-3", ApiType.AnthropicMessages);

        Assert.Equal(key1, key2);
        Assert.NotEqual(key1, key3);
    }

    [Fact]
    public void InMemoryProviderConversationStateStore_SetAndGet()
    {
        var store = new InMemoryProviderConversationStateStore();
        var key = ProviderStateKey.Create(
            SessionId.New(), AgentId.New(), "openai", "gpt-4o", ApiType.OpenAiChat);
        var state = new ProviderTurnState(key, "resp_123", null, null, null);

        store.Set(state);
        var retrieved = store.Get(key);

        Assert.NotNull(retrieved);
        Assert.Equal("resp_123", retrieved!.PreviousResponseId);
    }

    [Fact]
    public void InMemoryProviderConversationStateStore_ClearRemovesKey()
    {
        var store = new InMemoryProviderConversationStateStore();
        var key = ProviderStateKey.Create(
            SessionId.New(), AgentId.New(), "openai", "gpt-4o", ApiType.OpenAiChat);
        store.Set(new ProviderTurnState(key, "resp_123", null, null, null));

        store.Clear(key);
        Assert.Null(store.Get(key));
    }

    [Fact]
    public void InMemoryProviderConversationStateStore_IsCaseInsensitive()
    {
        var store = new InMemoryProviderConversationStateStore();
        var sessionId = SessionId.New();
        var agentId = AgentId.New();

        store.Set(new ProviderTurnState(
            ProviderStateKey.Create(sessionId, agentId, "OpenAI", "gpt-4o", ApiType.OpenAiChat),
            "resp_1", null, null, null));

        var retrieved = store.Get(new ProviderStateKey(
            sessionId, agentId, "OPENAI", "gpt-4o", ApiType.OpenAiChat));

        Assert.NotNull(retrieved);
        Assert.Equal("resp_1", retrieved!.PreviousResponseId);

        store.Clear(new ProviderStateKey(
            sessionId, agentId, "openai", "gpt-4o", ApiType.OpenAiChat));

        Assert.Null(store.Get(new ProviderStateKey(
            sessionId, agentId, "OPENAI", "gpt-4o", ApiType.OpenAiChat)));
    }

    [Fact]
    public void InMemoryProviderConversationStateStore_ClearSessionRemovesAllKeysForSession()
    {
        var store = new InMemoryProviderConversationStateStore();
        var sessionId = SessionId.New();
        var agentId = AgentId.New();

        store.Set(new ProviderTurnState(
            ProviderStateKey.Create(sessionId, agentId, "openai", "gpt-4o", ApiType.OpenAiChat),
            "resp_1", null, null, null));
        store.Set(new ProviderTurnState(
            ProviderStateKey.Create(sessionId, agentId, "openai", "gpt-4o-mini", ApiType.OpenAiChat),
            "resp_2", null, null, null));

        store.ClearSession(sessionId);

        Assert.Null(store.Get(ProviderStateKey.Create(sessionId, agentId, "openai", "gpt-4o", ApiType.OpenAiChat)));
        Assert.Null(store.Get(ProviderStateKey.Create(sessionId, agentId, "openai", "gpt-4o-mini", ApiType.OpenAiChat)));
    }

    [Fact]
    public void ProviderStateManager_EmitsEventsOnSetAndClear()
    {
        var sink = new InMemoryEventSink();
        var manager = new ProviderStateManager(new InMemoryProviderConversationStateStore(), sink);
        var sessionId = SessionId.New();
        var key = ProviderStateKey.Create(sessionId, AgentId.New(), "openai", "gpt-4o", ApiType.OpenAiChat);

        manager.Set(new ProviderTurnState(key, "resp_123", "conv_456", null, null));

        var log = sink.GetAllEvents();
        Assert.Contains(log, e => e is ProviderStateUpdatedEvent);

        manager.Clear(key);

        var log2 = sink.GetAllEvents();
        Assert.Contains(log2, e => e is ProviderStateClearedEvent);
    }

    [Fact]
    public void ProviderStateManager_Set_NormalizesKey()
    {
        var sink = new InMemoryEventSink();
        var manager = new ProviderStateManager(new InMemoryProviderConversationStateStore(), sink);
        var sessionId = SessionId.New();

        // Use a manually constructed key with mixed-case provider name
        var rawKey = new ProviderStateKey(sessionId, AgentId.New(), "OpenAI", "gpt-4o", ApiType.OpenAiChat);
        var state = new ProviderTurnState(rawKey, "resp_1", null, null, null);

        var result = manager.Set(state);

        // Returned state should have normalized key (lowercase provider name)
        Assert.Equal("openai", result.Key.ProviderName);

        // Stored state should be retrievable with any casing
        var retrieved = manager.Get(ProviderStateKey.Create(sessionId, result.Key.AgentId, "OPENAI", "gpt-4o", ApiType.OpenAiChat));
        Assert.NotNull(retrieved);
        Assert.Equal("resp_1", retrieved!.PreviousResponseId);

        // Event should carry normalized key
        var log = sink.GetAllEvents();
        var updatedEvent = log.OfType<ProviderStateUpdatedEvent>().Single();
        Assert.Equal("openai", updatedEvent.Key.ProviderName);
    }

    [Fact]
    public void ProviderStateManager_Clear_DoesNotEmitForMissingKey()
    {
        var sink = new InMemoryEventSink();
        var manager = new ProviderStateManager(new InMemoryProviderConversationStateStore(), sink);
        var sessionId = SessionId.New();
        var key = ProviderStateKey.Create(sessionId, AgentId.New(), "openai", "gpt-4o", ApiType.OpenAiChat);

        // Clear a key that was never set — should not emit an event
        manager.Clear(key);

        Assert.Empty(sink.GetAllEvents());

        // Set then clear — should emit one clear event
        manager.Set(new ProviderTurnState(key, "resp_1", null, null, null));
        sink.Clear();
        manager.Clear(key);

        var log = sink.GetAllEvents();
        var clearEvent = log.OfType<ProviderStateClearedEvent>().Single();
        Assert.NotNull(clearEvent);
    }

    [Fact]
    public void ProviderStateManager_SetAndClear_IncludeReason()
    {
        var sink = new InMemoryEventSink();
        var manager = new ProviderStateManager(new InMemoryProviderConversationStateStore(), sink);
        var sessionId = SessionId.New();
        var key = ProviderStateKey.Create(sessionId, AgentId.New(), "openai", "gpt-4o", ApiType.OpenAiChat);

        manager.Set(new ProviderTurnState(key, "resp_1", null, null, null), reason: "continuation");

        var log = sink.GetAllEvents();
        var updatedEvent = log.OfType<ProviderStateUpdatedEvent>().Single();
        Assert.Equal("continuation", updatedEvent.Reason);

        sink.Clear();
        manager.Clear(key, reason: "reset");

        var log2 = sink.GetAllEvents();
        var clearEvent = log2.OfType<ProviderStateClearedEvent>().Single();
        Assert.Equal("reset", clearEvent.Reason);
    }

    [Fact]
    public void InMemoryProviderConversationStateStore_ClearSession_ReturnsRemovedKeys()
    {
        var store = new InMemoryProviderConversationStateStore();
        var sessionId = SessionId.New();
        var agentId = AgentId.New();

        store.Set(new ProviderTurnState(
            ProviderStateKey.Create(sessionId, agentId, "openai", "gpt-4o", ApiType.OpenAiChat),
            "resp_1", null, null, null));
        store.Set(new ProviderTurnState(
            ProviderStateKey.Create(sessionId, agentId, "openai", "gpt-4o-mini", ApiType.OpenAiChat),
            "resp_2", null, null, null));

        var removed = store.ClearSession(sessionId);

        Assert.Equal(2, removed.Count);
        Assert.All(removed, k => Assert.Equal(sessionId, k.SessionId));
    }

    [Fact]
    public void ProviderStateManager_ClearSession_EmitsOneEventPerKeyWithReason()
    {
        var sink = new InMemoryEventSink();
        var manager = new ProviderStateManager(new InMemoryProviderConversationStateStore(), sink);
        var sessionId = SessionId.New();
        var agentId = AgentId.New();

        manager.Set(new ProviderTurnState(
            ProviderStateKey.Create(sessionId, agentId, "openai", "gpt-4o", ApiType.OpenAiChat),
            "resp_1", null, null, null));
        manager.Set(new ProviderTurnState(
            ProviderStateKey.Create(sessionId, agentId, "openai", "gpt-4o-mini", ApiType.OpenAiChat),
            "resp_2", null, null, null));

        sink.Clear();
        manager.ClearSession(sessionId, reason: "reset_all");

        var log = sink.GetAllEvents();
        var clearEvents = log.OfType<ProviderStateClearedEvent>().ToList();
        Assert.Equal(2, clearEvents.Count);
        Assert.All(clearEvents, e => Assert.Equal("reset_all", e.Reason));
        Assert.All(clearEvents, e => Assert.Equal(sessionId, e.SessionId));
    }
}
