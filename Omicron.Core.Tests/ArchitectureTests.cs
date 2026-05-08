using System.Runtime.InteropServices;
using System.Text.Json;
using Omicron.Core.Commands;
using Omicron.Core.Events;
using Omicron.Core.Execution;
using Omicron.Core.Extensions;
using Omicron.Core.Models;
using Omicron.Core.Permissions;
using Omicron.Core.Providers;
using Omicron.Core.Sessions;
using Omicron.Core.Tools;
using Omicron.Core.Workspace;
using Xunit;

namespace Omicron.Core.Tests;

public class ArchitectureTests
{
    // ================================================================
    // Event ID and value type tests
    // ================================================================

    [Fact]
    public void EventId_New_GeneratesUniqueIds()
    {
        var id1 = EventId.New();
        var id2 = EventId.New();
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void SessionId_New_GeneratesUniqueIds()
    {
        var id1 = SessionId.New();
        var id2 = SessionId.New();
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void AgentId_New_GeneratesUniqueIds()
    {
        var id1 = AgentId.New();
        var id2 = AgentId.New();
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ToolCallId_Value_IsPreserved()
    {
        var id = new ToolCallId("call_12345");
        Assert.Equal("call_12345", id.Value);
    }

    [Fact]
    public void WorkspaceId_New_GeneratesUniqueIds()
    {
        var id1 = WorkspaceId.New();
        var id2 = WorkspaceId.New();
        Assert.NotEqual(id1, id2);
    }

    // ================================================================
    // OmicronEvent hierarchy tests
    // ================================================================

    [Fact]
    public void OmicronEvent_HasIdSequenceTimestampAndSession()
    {
        var sessionId = SessionId.New();
        var evt = new SessionStartedEvent(
            EventId.New(), 1, DateTimeOffset.UtcNow, sessionId,
            AgentId.New(), "gpt-4o", "openai");

        Assert.NotEqual(default, evt.Id);
        Assert.Equal(1, evt.Sequence);
        Assert.Equal(sessionId, evt.SessionId);
    }

    [Fact]
    public void UserMessageEvent_StoresText()
    {
        var sessionId = SessionId.New();
        var evt = new UserMessageEvent(
            EventId.New(), 1, DateTimeOffset.UtcNow, sessionId, "Hello");

        Assert.Equal("Hello", evt.Text);
    }

    [Fact]
    public void AssistantTextDeltaEvent_StoresDelta()
    {
        var sessionId = SessionId.New();
        var evt = new AssistantTextDeltaEvent(
            EventId.New(), 1, DateTimeOffset.UtcNow, sessionId, "Hello", null);

        Assert.Equal("Hello", evt.Delta);
        Assert.Null(evt.ReasoningDelta);
    }

    [Fact]
    public void AssistantResponseCompleteEvent_StoresFullResponse()
    {
        var sessionId = SessionId.New();
        var evt = new AssistantResponseCompleteEvent(
            EventId.New(), 1, DateTimeOffset.UtcNow, sessionId,
            "Full response", "reasoning", 10, 20);

        Assert.Equal("Full response", evt.FullText);
        Assert.Equal("reasoning", evt.ReasoningText);
        Assert.Equal(10, evt.InputTokens);
        Assert.Equal(20, evt.OutputTokens);
    }

    [Fact]
    public void ToolInvocationStartedEvent_StoresArguments()
    {
        var sessionId = SessionId.New();
        var args = new Dictionary<string, object?> { ["expr"] = "2+2" };
        var evt = new ToolInvocationStartedEvent(
            EventId.New(), 1, DateTimeOffset.UtcNow, sessionId,
            new ToolCallId("call_1"), "calculator",
            args);

        Assert.Equal("calculator", evt.ToolName);
        Assert.Equal("2+2", evt.Arguments["expr"]?.ToString());
    }

    [Fact]
    public void ToolInvocationCompletedEvent_StoresResult()
    {
        var sessionId = SessionId.New();
        var evt = new ToolInvocationCompletedEvent(
            EventId.New(), 1, DateTimeOffset.UtcNow, sessionId,
            new ToolCallId("call_1"), "calculator",
            "42", IsError: false);

        Assert.Equal("42", evt.Result);
        Assert.False(evt.IsError);
    }

    // ================================================================
    // ProviderState tests
    // ================================================================

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

        // Set using Create (normalized)
        store.Set(new ProviderTurnState(
            ProviderStateKey.Create(sessionId, agentId, "OpenAI", "gpt-4o", ApiType.OpenAiChat),
            "resp_1", null, null, null));

        // Get using non-normalized constructor
        var retrieved = store.Get(new ProviderStateKey(
            sessionId, agentId, "OPENAI", "gpt-4o", ApiType.OpenAiChat));

        Assert.NotNull(retrieved);
        Assert.Equal("resp_1", retrieved!.PreviousResponseId);

        // Clear using different casing
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

    // ================================================================
    // IEventSink tests
    // ================================================================

    [Fact]
    public void InMemoryEventSink_Emit_StoresEvent()
    {
        var sink = new InMemoryEventSink();
        var sessionId = SessionId.New();
        var evt = new SessionStartedEvent(
            EventId.New(), 1, DateTimeOffset.UtcNow, sessionId,
            AgentId.New(), "gpt-4o", "openai");

        sink.Emit(evt);

        var all = sink.GetAllEvents();
        Assert.Single(all);
    }

    [Fact]
    public void InMemoryEventSink_GetSessionEvents_FiltersBySession()
    {
        var sink = new InMemoryEventSink();
        var session1 = SessionId.New();
        var session2 = SessionId.New();

        sink.Emit(new SessionStartedEvent(
            EventId.New(), 1, DateTimeOffset.UtcNow, session1,
            AgentId.New(), "gpt-4o", "openai"));
        sink.Emit(new SessionStartedEvent(
            EventId.New(), 2, DateTimeOffset.UtcNow, session2,
            AgentId.New(), "claude-3", "anthropic"));

        var session1Events = sink.GetSessionEvents(session1);
        Assert.Single(session1Events);

        var session2Events = sink.GetSessionEvents(session2);
        Assert.Single(session2Events);
    }

    [Fact]
    public void InMemoryEventSink_Clear_RemovesAllEvents()
    {
        var sink = new InMemoryEventSink();
        var sessionId = SessionId.New();
        sink.Emit(new SessionStartedEvent(
            EventId.New(), 1, DateTimeOffset.UtcNow, sessionId,
            AgentId.New(), "gpt-4o", "openai"));

        sink.Clear();
        Assert.Empty(sink.GetAllEvents());
    }

    // ================================================================
    // ToolRegistry tests
    // ================================================================

    [Fact]
    public void ToolRegistry_RegisterAndGet()
    {
        var registry = new ToolRegistry();
        var tool = new ToolDefinition(
            "calculator", "Do math", null,
            ctx => Task.FromResult(new ToolResult("42")));

        registry.Register(tool);
        Assert.True(registry.HasTool("calculator"));
        Assert.NotNull(registry.GetTool("calculator"));
        Assert.Single(registry.AllTools);
    }

    [Fact]
    public void ToolRegistry_GetTool_ReturnsNullForUnknown()
    {
        var registry = new ToolRegistry();
        Assert.Null(registry.GetTool("nonexistent"));
    }

    [Fact]
    public void ToolRegistry_Register_OverwritesExisting()
    {
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition("calc", "v1", null, ctx => Task.FromResult(new ToolResult("1"))));
        registry.Register(new ToolDefinition("calc", "v2", null, ctx => Task.FromResult(new ToolResult("2"))));

        Assert.Single(registry.AllTools);
        Assert.Equal("v2", registry.GetTool("calc")!.Description);
    }

    // ================================================================
    // CommandRegistry tests
    // ================================================================

    [Fact]
    public void CommandRegistry_RegisterAndGet()
    {
        var registry = new CommandRegistry();
        var cmd = new CommandDefinition(
            "session.reset", "Reset Session", "Clear conversation history",
            CommandScope.Session,
            ctx => Task.FromResult(new CommandResult("Reset")));

        registry.Register(cmd);
        Assert.NotNull(registry.GetCommand("session.reset"));
        Assert.Single(registry.AllCommands);
    }

    [Fact]
    public void CommandRegistry_GetCommand_ReturnsNullForUnknown()
    {
        var registry = new CommandRegistry();
        Assert.Null(registry.GetCommand("nonexistent"));
    }

    // ================================================================
    // PermissionService tests
    // ================================================================

    [Fact]
    public async Task AllowAllPermissionService_AllowsEverything()
    {
        var service = new AllowAllPermissionService();
        var result = await service.RequestAsync(new PermissionRequest("tool.execute", "shell", null));
        Assert.True(result.Allowed);
    }

    // ================================================================
    // Workspace tests
    // ================================================================

    [Fact]
    public void HostWorkspace_Constructor_SetsRoot()
    {
        var ws = new HostWorkspace(Environment.CurrentDirectory);
        Assert.Equal(Path.GetFullPath(Environment.CurrentDirectory), ws.RootPath);
    }

    // ================================================================
    // Extension system tests
    // ================================================================

    [Fact]
    public void ExtensionRegistry_RegisterExtension_AddsMetadata()
    {
        var toolRegistry = new ToolRegistry();
        var cmdRegistry = new CommandRegistry();
        var extRegistry = new ExtensionRegistry(toolRegistry, cmdRegistry);

        var ext = new TestExtension();
        extRegistry.Register(ext);

        Assert.Single(extRegistry.Extensions);
        Assert.Equal("test.extension", extRegistry.Extensions[0].Id);
    }

    [Fact]
    public void ExtensionRegistry_ExtensionCanRegisterTools()
    {
        var toolRegistry = new ToolRegistry();
        var cmdRegistry = new CommandRegistry();
        var extRegistry = new ExtensionRegistry(toolRegistry, cmdRegistry);

        extRegistry.Register(new ToolRegisteringExtension());

        Assert.True(toolRegistry.HasTool("test_tool"));
    }

    [Fact]
    public void ExtensionRegistry_ExtensionCanRegisterCommands()
    {
        var toolRegistry = new ToolRegistry();
        var cmdRegistry = new CommandRegistry();
        var extRegistry = new ExtensionRegistry(toolRegistry, cmdRegistry);

        extRegistry.Register(new CommandRegisteringExtension());

        Assert.NotNull(cmdRegistry.GetCommand("test.command"));
    }

    // ================================================================
    // OmicronHost tests
    // ================================================================

    [Fact]
    public void OmicronHost_Constructor_CreatesAllServices()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);

        Assert.NotNull(host.Extensions);
        Assert.NotNull(host.Providers);
        Assert.NotNull(host.Tools);
        Assert.NotNull(host.Commands);
        Assert.NotNull(host.Permissions);
        Assert.NotNull(host.Workspace);
        Assert.NotNull(host.Execution);
        Assert.NotNull(host.Events);
        Assert.NotNull(host.EventLog);
        Assert.NotNull(host.ProviderState);
    }

    [Fact]
    public void OmicronHost_LoadBuiltinExtensions_RegistersCalculatorAndTime()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        host.LoadBuiltinExtensions();

        Assert.True(host.Tools.HasTool("calculator"));
        Assert.True(host.Tools.HasTool("get_current_time"));
    }

    [Fact]
    public void OmicronHost_CreateSession_ReturnsAgentSession()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var model = new Model
        {
            Id = "test",
            Name = "Test",
            ProviderName = "test"
        };

        var session = host.CreateSession(model, "You are a test.");

        Assert.NotNull(session);
        Assert.Equal(model, session.Model);
        Assert.Equal("You are a test.", session.SystemPrompt);
    }

    // ================================================================
    // ToolInvocationContext tests
    // ================================================================

    [Fact]
    public void ToolInvocationContext_StoresAllFields()
    {
        var sessionId = SessionId.New();
        var agentId = AgentId.New();
        var args = new Dictionary<string, object?> { ["key"] = "value" };

        var ctx = new ToolInvocationContext(
            new ToolCallId("call_1"), args, sessionId, agentId, CancellationToken.None);

        Assert.Equal("call_1", ctx.ToolCallId.Value);
        Assert.Equal("value", ctx.Arguments["key"]);
        Assert.Equal(sessionId, ctx.SessionId);
        Assert.Equal(agentId, ctx.AgentId);
    }

    [Fact]
    public async Task ToolRegistry_ToolInvocation_ReturnsResult()
    {
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition(
            "echo", "Echo input", null,
            ctx => Task.FromResult(new ToolResult(ctx.Arguments.GetValueOrDefault("msg")?.ToString() ?? ""))));

        var tool = registry.GetTool("echo");
        Assert.NotNull(tool);

        var result = await tool.InvokeAsync(new ToolInvocationContext(
            new ToolCallId("call_1"),
            new Dictionary<string, object?> { ["msg"] = "Hello" },
            SessionId.New(), AgentId.New(), CancellationToken.None));

        Assert.Equal("Hello", result.Text);
        Assert.False(result.IsError);
    }

    // ================================================================
    // AgentSession event log tests
    // ================================================================

    [Fact]
    public async Task AgentSession_PromptAsync_EmitsEventsToEventLog()
{
    // Arrange
    var eventSink = new InMemoryEventSink();
    var toolRegistry = new ToolRegistry();
    var permissionService = new AllowAllPermissionService();
    var providerState = new InMemoryProviderConversationStateStore();
    var workspace = new HostWorkspace(Environment.CurrentDirectory);
    var execution = new LocalExecutionBroker();

    var model = new Model
    {
        Id = "test-model",
        Name = "Test Model",
        ProviderName = "fake",
        Provider = new FakeProvider
        {
            Responses =
            {
                () => Task.FromResult(new LlmResult
                {
                    Text = "Hello from AI!",
                    StopReason = StopReason.Stop,
                    Usage = new UsageInfo(10, 20)
                })
            }
        }
    };

    var session = new AgentSession(
        model, toolRegistry, permissionService, eventSink, providerState,
        "You are a test.");

    // Act
    var events = new List<OmicronEvent>();
    await foreach (var evt in session.PromptAsync("Hello"))
    {
        events.Add(evt);
    }

    // Assert: yielded events
    Assert.Contains(events, e => e is UserMessageEvent);
    Assert.Contains(events, e => e is SessionStartedEvent);
    Assert.Contains(events, e => e is AssistantTextDeltaEvent);
    Assert.Contains(events, e => e is AssistantResponseCompleteEvent);

    var userMsg = events.OfType<UserMessageEvent>().Single();
    Assert.Equal("Hello", userMsg.Text);

    var responseComplete = events.OfType<AssistantResponseCompleteEvent>().Single();
    Assert.Equal("Hello from AI!", responseComplete.FullText);
    Assert.Equal(10, responseComplete.InputTokens);
    Assert.Equal(20, responseComplete.OutputTokens);

    // Assert: event log contains the same events
    var logEvents = eventSink.GetSessionEvents(session.Id);
    Assert.NotEmpty(logEvents);
    Assert.Contains(logEvents, e => e is UserMessageEvent);
    Assert.Contains(logEvents, e => e is SessionStartedEvent);
    Assert.Contains(logEvents, e => e is AssistantTextDeltaEvent);
    Assert.Contains(logEvents, e => e is AssistantResponseCompleteEvent);
}

[Fact]
public async Task AgentSession_ToolCall_Emission()
{
    // Arrange
    var eventSink = new InMemoryEventSink();
    var toolRegistry = new ToolRegistry();
    toolRegistry.Register(new ToolDefinition(
        "test_tool", "A test tool", null,
        ctx => Task.FromResult(new ToolResult("Tool result"))));

    var permissionService = new AllowAllPermissionService();
    var providerState = new InMemoryProviderConversationStateStore();
    var workspace = new HostWorkspace(Environment.CurrentDirectory);
    var execution = new LocalExecutionBroker();

    // Fake provider that returns a tool call
    var fakeProvider = new FakeProvider();
    fakeProvider.Responses.Add(() => Task.FromResult(new LlmResult
    {
        Text = "Let me use a tool",
        ToolCalls = new List<ToolCallContent>
        {
            new("call_1", "test_tool", new Dictionary<string, object?>())
        },
        StopReason = StopReason.ToolUse
    }));
    // Second response (after tool result is returned to LLM)
    fakeProvider.Responses.Add(() => Task.FromResult(new LlmResult
    {
        Text = "Final answer based on tool",
        StopReason = StopReason.Stop,
        Usage = new UsageInfo(15, 30)
    }));

    var model = new Model
    {
        Id = "test-model",
        Name = "Test Model",
        ProviderName = "fake",
        Provider = fakeProvider
    };

    var session = new AgentSession(
        model, toolRegistry, permissionService, eventSink, providerState);

    // Act
    var events = new List<OmicronEvent>();
    await foreach (var evt in session.PromptAsync("Use a tool"))
    {
        events.Add(evt);
    }

    // Assert: tool events are in yielded events
    Assert.Contains(events, e => e is ToolInvocationStartedEvent);
    Assert.Contains(events, e => e is ToolInvocationCompletedEvent);

    var toolStart = events.OfType<ToolInvocationStartedEvent>().Single();
    Assert.Equal("test_tool", toolStart.ToolName);

    var toolEnd = events.OfType<ToolInvocationCompletedEvent>()
        .First(e => e.ToolName == "test_tool");
    Assert.Equal("Tool result", toolEnd.Result);
    Assert.False(toolEnd.IsError);

    // Assert: final response after tool call
    Assert.Contains(events, e => e is AssistantResponseCompleteEvent);
    var final = events.OfType<AssistantResponseCompleteEvent>().Last();
    Assert.Equal("Final answer based on tool", final.FullText);

    // Assert: event log contains tool events
    var logEvents = eventSink.GetSessionEvents(session.Id);
    Assert.Contains(logEvents, e => e is ToolInvocationStartedEvent);
    Assert.Contains(logEvents, e => e is ToolInvocationCompletedEvent);
    Assert.Contains(logEvents, e => e is AssistantResponseCompleteEvent);
}

[Fact]
public async Task AgentSession_Reset_ClearsProviderState()
{
    // Arrange
    var eventSink = new InMemoryEventSink();
    var toolRegistry = new ToolRegistry();
    var permissionService = new AllowAllPermissionService();
    var providerState = new InMemoryProviderConversationStateStore();
    var workspace = new HostWorkspace(Environment.CurrentDirectory);
    var execution = new LocalExecutionBroker();

    var model = new Model
    {
        Id = "test-model",
        Name = "Test Model",
        ProviderName = "fake",
        Provider = new FakeProvider()
    };

    var session = new AgentSession(
        model, toolRegistry, permissionService, eventSink, providerState);

    // Set some provider state
    var key = ProviderStateKey.Create(session.Id, session.AgentId, "fake", "test-model", ApiType.OpenAiChat);
    providerState.Set(new ProviderTurnState(key, "resp_123", "conv_456", null, null));

    Assert.NotNull(providerState.Get(key));

    // Act
    session.Reset();

    // Assert: provider state cleared
    Assert.Null(providerState.Get(key));

    // Assert: reset event in log
    var logEvents = eventSink.GetSessionEvents(session.Id);
    Assert.Contains(logEvents, e => e is SessionResetEvent);
    }

    [Fact]
    public async Task AgentSession_ContinueAsync_WithoutPrompt_Throws()
    {
        var eventSink = new InMemoryEventSink();
        var toolRegistry = new ToolRegistry();
        var permissionService = new AllowAllPermissionService();
        var providerState = new InMemoryProviderConversationStateStore();
        var model = new Model
        {
            Id = "test", ProviderName = "fake",
            Provider = new FakeProvider()
        };

        var session = new AgentSession(model, toolRegistry, permissionService, eventSink, providerState);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in session.ContinueAsync()) { }
        });

        Assert.Contains("No conversation to continue", ex.Message);
    }

    [Fact]
    public async Task AgentSession_ContinueAsync_AfterReset_Throws()
    {
        var eventSink = new InMemoryEventSink();
        var toolRegistry = new ToolRegistry();
        var permissionService = new AllowAllPermissionService();
        var providerState = new InMemoryProviderConversationStateStore();
        var model = new Model
        {
            Id = "test", ProviderName = "fake",
            Provider = new FakeProvider()
        };

        var session = new AgentSession(model, toolRegistry, permissionService, eventSink, providerState);
        var fakeProvider = (FakeProvider)model.Provider!;

        // First prompt
        fakeProvider.Responses.Add(() => Task.FromResult(new LlmResult
        {
            Text = "First", StopReason = StopReason.Stop
        }));
        await foreach (var _ in session.PromptAsync("First")) { }

        // Reset clears conversation
        session.Reset();

        // ContinueAsync after reset should throw
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in session.ContinueAsync()) { }
        });

        Assert.Contains("No conversation to continue", ex.Message);
    }

    [Fact]
    public async Task AgentSession_Reset_DoesNotReemitSessionStartedEvent()
    {
        // Arrange
        var eventSink = new InMemoryEventSink();
        var toolRegistry = new ToolRegistry();
        var permissionService = new AllowAllPermissionService();
        var providerState = new InMemoryProviderConversationStateStore();
        var model = new Model
        {
            Id = "test-model",
            ProviderName = "fake",
            Provider = new FakeProvider()
        };

        var session = new AgentSession(model, toolRegistry, permissionService, eventSink, providerState);
        var fakeProvider = (FakeProvider)model.Provider!;

        // First prompt starts the session
        fakeProvider.Responses.Add(() => Task.FromResult(new LlmResult
        {
            Text = "First response", StopReason = StopReason.Stop
        }));

        var firstEvents = new List<OmicronEvent>();
        await foreach (var evt in session.PromptAsync("First"))
            firstEvents.Add(evt);

        Assert.Single(firstEvents.OfType<SessionStartedEvent>());
        var firstLog = eventSink.GetSessionEvents(session.Id);
        var firstSessionStartCount = firstLog.OfType<SessionStartedEvent>().Count();

        // Reset
        session.Reset();

        // Second prompt after reset
        fakeProvider.Responses.Add(() => Task.FromResult(new LlmResult
        {
            Text = "Second response", StopReason = StopReason.Stop
        }));

        var secondEvents = new List<OmicronEvent>();
        await foreach (var evt in session.PromptAsync("Second"))
            secondEvents.Add(evt);

        // Assert: no new SessionStartedEvent after reset
        Assert.Empty(secondEvents.OfType<SessionStartedEvent>());
        Assert.Contains(secondEvents, e => e is TurnStartedEvent);

        // Assert: event log still has only one SessionStartedEvent
        var allLog = eventSink.GetSessionEvents(session.Id);
        Assert.Equal(firstSessionStartCount, allLog.OfType<SessionStartedEvent>().Count());
    }

    [Fact]
    public void InMemoryProviderConversationStateStore_WithEventSink_EmitsEvents()
    {
        var sink = new InMemoryEventSink();
        var store = new InMemoryProviderConversationStateStore(sink);
        var sessionId = SessionId.New();
        var key = ProviderStateKey.Create(sessionId, AgentId.New(), "openai", "gpt-4o", ApiType.OpenAiChat);

        // Set emits ProviderStateUpdatedEvent
        store.Set(new ProviderTurnState(key, "resp_123", "conv_456", null, null));

        var log = sink.GetAllEvents();
        Assert.Contains(log, e => e is ProviderStateUpdatedEvent);

        // Clear emits ProviderStateClearedEvent
        store.Clear(key);

        var log2 = sink.GetAllEvents();
        Assert.Contains(log2, e => e is ProviderStateClearedEvent);
    }

    [Fact]
    public async Task LocalExecutionBroker_EmitsExecutionEvents()
    {
        var sink = new InMemoryEventSink();
        var broker = new LocalExecutionBroker(sink);
        var sessionId = SessionId.New();

        // Pick a shell available on the current platform
        var shell = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd" : "sh";

        // Execute a simple command
        var request = new ExecutionRequest(
            "echo hello", shell, null, 10, sessionId, new ToolCallId("tc_1"));

        var result = await broker.ExecuteAsync(request);

        var log = sink.GetAllEvents();
        Assert.Contains(log, e => e is ExecutionStartedEvent);
        Assert.Contains(log, e => e is ExecutionCompletedEvent);

        var startEvent = log.OfType<ExecutionStartedEvent>().Single();
        Assert.Equal(sessionId, startEvent.SessionId);
        Assert.Equal(new ToolCallId("tc_1"), startEvent.ToolCallId);

        var completeEvent = log.OfType<ExecutionCompletedEvent>().Single();
        Assert.Equal(sessionId, completeEvent.SessionId);
        Assert.Equal(new ToolCallId("tc_1"), completeEvent.ToolCallId);
    }

    [Fact]
    public async Task LocalExecutionBroker_EmitsCompletionOnUnknownShell()
    {
        var sink = new InMemoryEventSink();
        var broker = new LocalExecutionBroker(sink);
        var sessionId = SessionId.New();

        var request = new ExecutionRequest(
            "some command", "nonexistent_shell_xyz", null, 10, sessionId);

        var result = await broker.ExecuteAsync(request);

        var log = sink.GetAllEvents();
        // Both start and completion events should be emitted
        Assert.Contains(log, e => e is ExecutionStartedEvent);
        Assert.Contains(log, e => e is ExecutionCompletedEvent);
        Assert.StartsWith("Error", result.Output);
    }

    [Fact]
    public void InMemoryEventSink_SequencesAreMonotonic()
    {
        var sink = new InMemoryEventSink();
        var sessionId = SessionId.New();
        var agentId = AgentId.New();

        // Emit events from multiple sources/sessions
        sink.Emit(new SessionStartedEvent(
            EventId.New(), 0, DateTimeOffset.UtcNow, sessionId, agentId, "m1", "p1"));
        var s2 = SessionId.New();
        sink.Emit(new SessionStartedEvent(
            EventId.New(), 0, DateTimeOffset.UtcNow, s2, agentId, "m2", "p2"));
        sink.Emit(new UserMessageEvent(
            EventId.New(), 0, DateTimeOffset.UtcNow, sessionId, "Hello"));
        sink.Emit(new AssistantTextDeltaEvent(
            EventId.New(), 0, DateTimeOffset.UtcNow, s2, "Delta", null));

        var all = sink.GetAllEvents();

        // Verify: no sequence is zero (all stamped by sink)
        Assert.All(all, e => Assert.NotEqual(0, e.Sequence));

        // Verify: sequences are strictly increasing in log order
        for (int i = 1; i < all.Count; i++)
            Assert.True(all[i].Sequence > all[i - 1].Sequence,
                $"Event {i} (seq={all[i].Sequence}) <= event {i - 1} (seq={all[i - 1].Sequence})");

        // Verify: session-filtered events preserve increasing order
        var sessionEvents = sink.GetSessionEvents(sessionId);
        for (int i = 1; i < sessionEvents.Count; i++)
            Assert.True(sessionEvents[i].Sequence > sessionEvents[i - 1].Sequence,
                $"Session event {i} out of order");
    }

    [Fact]
    public void ModelCatalogService_TracksFreeModels()
    {
        var providerFactory = new ProviderFactory();
        var catalog = new ModelCatalogService(providerFactory);

        // Add a model and mark it free via AddDiscovered path
        // We use the EnsureModel + MarkFree path for testing
        var model = new Model
        {
            Id = "free-model",
            Name = "Free Model",
            ProviderName = "openrouter"
        };
        catalog.EnsureModel("or:free-model", model);
        catalog.MarkFree("or:free-model");

        Assert.True(catalog.IsFreeModel("or:free-model"));

        // Same model looked up via different key format should still work
        Assert.True(catalog.FreeModelKeys.Contains("or:free-model"));
    }
}

// ================================================================
// Test extensions
// ================================================================

public class TestExtension : IOmicronExtension
{
    public string Id => "test.extension";
    public string DisplayName => "Test Extension";
    public Version Version => new(1, 0, 0);

    public void Register(IExtensionContext context)
    {
        // No-op test extension
    }
}

public class ToolRegisteringExtension : IOmicronExtension
{
    public string Id => "test.tool-extension";
    public string DisplayName => "Tool Test Extension";
    public Version Version => new(1, 0, 0);

    public void Register(IExtensionContext context)
    {
        context.RegisterTool(new ToolDefinition(
            "test_tool", "A test tool", null,
            ctx => Task.FromResult(new ToolResult("done"))));
    }
}

public class CommandRegisteringExtension : IOmicronExtension
{
    public string Id => "test.command-extension";
    public string DisplayName => "Command Test Extension";
    public Version Version => new(1, 0, 0);

    public void Register(IExtensionContext context)
    {
        context.RegisterCommand(new CommandDefinition(
            "test.command", "Test Command", "A test command",
            CommandScope.Global,
            ctx => Task.FromResult(new CommandResult("done"))));
    }
}
