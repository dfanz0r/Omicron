using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Permissions;
using Omicron.Core.Providers;
using Omicron.Core.Sessions;
using Omicron.Core.Tools;
using Xunit;

namespace Omicron.Core.Tests;

public class ProviderStateTests
{
    private static AgentSession CreateSession(Model model, IEventSink sink, IProviderStateManager? psm = null)
    {
        var ts = new ToolRegistry();
        var ps = new AllowAllPermissionService();
        var p = psm ?? new ProviderStateManager(new InMemoryProviderConversationStateStore(), sink);
        var config = SessionConfig.Create(model);
        return new AgentSession(config, ts, ps, sink, p);
    }
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
        Assert.Equal("openai", updatedEvent.State.Key.ProviderName);
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

    [Fact]
    public void InMemoryProviderConversationStateStore_PreservesStoragePolicy()
    {
        var store = new InMemoryProviderConversationStateStore();
        var sessionId = SessionId.New();
        var key = ProviderStateKey.Create(sessionId, AgentId.New(), "openai", "gpt-5.5", ApiType.OpenAiResponses);

        // Set state with custom StoragePolicy
        var state = new ProviderTurnState(key, "resp_123", null, null, null)
        {
            StoragePolicy = ProviderStoragePolicy.AllowProviderStoredState
        };
        store.Set(state);

        // Retrieve and verify StoragePolicy is preserved
        var retrieved = store.Get(key);
        Assert.NotNull(retrieved);
        Assert.Equal(ProviderStoragePolicy.AllowProviderStoredState, retrieved!.StoragePolicy);
        Assert.Equal("resp_123", retrieved.PreviousResponseId);
    }

    [Fact]
    public void ProviderStateManager_Set_PreservesStoragePolicy()
    {
        var sink = new InMemoryEventSink();
        var manager = new ProviderStateManager(new InMemoryProviderConversationStateStore(), sink);
        var sessionId = SessionId.New();
        var key = ProviderStateKey.Create(sessionId, AgentId.New(), "openai", "gpt-5.5", ApiType.OpenAiResponses);

        var state = new ProviderTurnState(key, "resp_123", null, null, null)
        {
            StoragePolicy = ProviderStoragePolicy.AllowProviderStoredState
        };
        manager.Set(state, reason: "test");

        var retrieved = manager.Get(key);
        Assert.NotNull(retrieved);
        Assert.Equal(ProviderStoragePolicy.AllowProviderStoredState, retrieved!.StoragePolicy);
        Assert.Equal("resp_123", retrieved.PreviousResponseId);
    }

    [Fact]
    public async Task AgentSession_PersistsResponseIdOnDone()
    {
        var eventSink = new InMemoryEventSink();
        var toolRegistry = new ToolRegistry();
        var permissionService = new AllowAllPermissionService();
        var providerStateManager = new ProviderStateManager(new InMemoryProviderConversationStateStore(), eventSink);

        var model = new Model
        {
            Id = "gpt-5.5",
            Name = "GPT-5.5",
            ProviderName = "fake",
            ApiType = ApiType.OpenAiResponses,
            StoragePolicy = ProviderStoragePolicy.AllowProviderStateNoStore,
            Provider = new FakeProvider
            {
                Responses =
                {
                    () => Task.FromResult(new LlmResult
                    {
                        Text = "Response from AI",
                        StopReason = StopReason.Stop,
                        Usage = new UsageInfo(10, 20),
                        ResponseId = "resp_abc123"
                    })
                }
            }
        };

        var session = CreateSession(model, eventSink, providerStateManager);

        var events = new List<OmicronEvent>();
        await foreach (var evt in session.PromptAsync("Hello"))
            events.Add(evt);

        // Verify the response ID was persisted in provider state
        var key = ProviderStateKey.Create(session.Id, session.AgentId, "fake", "gpt-5.5", ApiType.OpenAiResponses);
        var state = providerStateManager.Get(key);
        Assert.NotNull(state);
        Assert.Equal("resp_abc123", state!.PreviousResponseId);

        // Verify event was emitted
        var log = eventSink.GetSessionEvents(session.Id);
        Assert.Contains(log, e => e is ProviderStateUpdatedEvent);
    }

    [Fact]
    public async Task ChatModel_DoesNotPersistResponseId()
    {
        var eventSink = new InMemoryEventSink();
        var toolRegistry = new ToolRegistry();
        var permissionService = new AllowAllPermissionService();
        var providerStateManager = new ProviderStateManager(new InMemoryProviderConversationStateStore(), eventSink);

        var model = new Model
        {
            Id = "gpt-4o",
            Name = "GPT-4o",
            ProviderName = "fake",
            ApiType = ApiType.OpenAiChat,  // Chat API — should NOT persist
            StoragePolicy = ProviderStoragePolicy.PreferStateless,
            Provider = new FakeProvider
            {
                Responses =
                {
                    () => Task.FromResult(new LlmResult
                    {
                        Text = "Hello!",
                        StopReason = StopReason.Stop,
                        ResponseId = "chatcmpl_xyz"
                    })
                }
            }
        };

        var session = CreateSession(model, eventSink, providerStateManager);

        await foreach (var _ in session.PromptAsync("Hi")) { }

        var key = ProviderStateKey.Create(session.Id, session.AgentId, "fake", "gpt-4o", ApiType.OpenAiChat);
        var state = providerStateManager.Get(key);
        Assert.Null(state);

        var log = eventSink.GetSessionEvents(session.Id);
        Assert.DoesNotContain(log, e => e is ProviderStateUpdatedEvent);
    }

    [Fact]
    public async Task AnthropicModel_DoesNotPersistResponseId()
    {
        var eventSink = new InMemoryEventSink();
        var toolRegistry = new ToolRegistry();
        var permissionService = new AllowAllPermissionService();
        var providerStateManager = new ProviderStateManager(new InMemoryProviderConversationStateStore(), eventSink);

        var model = new Model
        {
            Id = "claude-sonnet-4",
            Name = "Claude Sonnet 4",
            ProviderName = "fake",
            ApiType = ApiType.AnthropicMessages,  // Anthropic — should NOT persist
            StoragePolicy = ProviderStoragePolicy.PreferStateless,
            Provider = new FakeProvider
            {
                Responses =
                {
                    () => Task.FromResult(new LlmResult
                    {
                        Text = "Hello!",
                        StopReason = StopReason.Stop,
                        ResponseId = "msg_xyz"
                    })
                }
            }
        };

        var session = CreateSession(model, eventSink, providerStateManager);

        await foreach (var _ in session.PromptAsync("Hi")) { }

        var key = ProviderStateKey.Create(session.Id, session.AgentId, "fake", "claude-sonnet-4", ApiType.AnthropicMessages);
        var state = providerStateManager.Get(key);
        Assert.Null(state);

        var log = eventSink.GetSessionEvents(session.Id);
        Assert.DoesNotContain(log, e => e is ProviderStateUpdatedEvent);
    }

    [Fact]
    public async Task ResponsesModel_PreferStateless_DoesNotPersistResponseId()
    {
        var eventSink = new InMemoryEventSink();
        var toolRegistry = new ToolRegistry();
        var permissionService = new AllowAllPermissionService();
        var providerStateManager = new ProviderStateManager(new InMemoryProviderConversationStateStore(), eventSink);

        var model = new Model
        {
            Id = "gpt-5.5",
            Name = "GPT-5.5",
            ProviderName = "fake",
            ApiType = ApiType.OpenAiResponses,
            StoragePolicy = ProviderStoragePolicy.PreferStateless,  // PreferStateless — should NOT persist
            Provider = new FakeProvider
            {
                Responses =
                {
                    () => Task.FromResult(new LlmResult
                    {
                        Text = "Hello!",
                        StopReason = StopReason.Stop,
                        ResponseId = "resp_xyz"
                    })
                }
            }
        };

        var session = CreateSession(model, eventSink, providerStateManager);

        await foreach (var _ in session.PromptAsync("Hi")) { }

        var key = ProviderStateKey.Create(session.Id, session.AgentId, "fake", "gpt-5.5", ApiType.OpenAiResponses);
        var state = providerStateManager.Get(key);
        Assert.Null(state);

        var log = eventSink.GetSessionEvents(session.Id);
        Assert.DoesNotContain(log, e => e is ProviderStateUpdatedEvent);
    }

    [Fact]
    public async Task ResponsesModel_SupportsPreviousResponseIdFalse_DoesNotPersist()
    {
        var eventSink = new InMemoryEventSink();
        var toolRegistry = new ToolRegistry();
        var permissionService = new AllowAllPermissionService();
        var providerStateManager = new ProviderStateManager(new InMemoryProviderConversationStateStore(), eventSink);

        var model = new Model
        {
            Id = "gpt-5.5",
            Name = "GPT-5.5",
            ProviderName = "fake",
            ApiType = ApiType.OpenAiResponses,
            StoragePolicy = ProviderStoragePolicy.AllowProviderStateNoStore,
            Compatibility = new ProviderCompatibility { SupportsPreviousResponseId = false },
            Provider = new FakeProvider
            {
                Responses =
                {
                    () => Task.FromResult(new LlmResult
                    {
                        Text = "Hello!",
                        StopReason = StopReason.Stop,
                        ResponseId = "resp_xyz"
                    })
                }
            }
        };

        var session = CreateSession(model, eventSink, providerStateManager);

        await foreach (var _ in session.PromptAsync("Hi")) { }

        var key = ProviderStateKey.Create(session.Id, session.AgentId, "fake", "gpt-5.5", ApiType.OpenAiResponses);
        var state = providerStateManager.Get(key);
        Assert.Null(state);
    }
}



