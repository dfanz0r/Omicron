using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Permissions;
using Omicron.Core.Providers;
using Omicron.Core.Sessions;
using Omicron.Core.Tools;
using Xunit;

namespace Omicron.Core.Tests;

public class AgentSessionTests
{
    private static AgentSession CreateSession(Model model, IEventSink sink, IToolRegistry? tools = null, IPermissionService? perms = null, string? systemPrompt = null, IProviderStateManager? providerState = null)
    {
        var ts = tools ?? new ToolRegistry();
        var ps = perms ?? new AllowAllPermissionService();
        var psm = providerState ?? new ProviderStateManager(new InMemoryProviderConversationStateStore(), sink);
        var config = SessionConfig.Create(model, systemPrompt);
        return new AgentSession(config, ts, ps, sink, psm);
    }

    [Fact]
    public async Task AgentSession_PromptAsync_EmitsEventsToEventLog()
    {
        var eventSink = new InMemoryEventSink();
        var toolRegistry = new ToolRegistry();
        var permissionService = new AllowAllPermissionService();
        var providerState = new ProviderStateManager(new InMemoryProviderConversationStateStore(), eventSink);

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

        var session = CreateSession(model, eventSink, toolRegistry, permissionService, "You are a test.");

        var events = new List<OmicronEvent>();
        await foreach (var evt in session.PromptAsync("Hello"))
            events.Add(evt);

        Assert.Contains(events, e => e is UserMessageEvent);
        Assert.Contains(events, e => e is SessionStartedEvent);
        Assert.Contains(events, e => e is AssistantTextDeltaEvent);
        Assert.Contains(events, e => e is AssistantResponseCompleteEvent);

        var userMsg = events.OfType<UserMessageEvent>().Single();
        Assert.True(userMsg.Text.Utf8Span.SequenceEqual("Hello"u8));

        var responseComplete = events.OfType<AssistantResponseCompleteEvent>().Single();
        Assert.True(responseComplete.FullText.Utf8Span.SequenceEqual("Hello from AI!"u8));
        Assert.Equal(10, responseComplete.Usage.InputTokens);
        Assert.Equal(20, responseComplete.Usage.OutputTokens);

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
        var eventSink = new InMemoryEventSink();
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new ToolDefinition(
            "test_tool", "A test tool", null,
            ctx => Task.FromResult(new ToolResult("Tool result"))));

        var permissionService = new AllowAllPermissionService();
        var providerState = new ProviderStateManager(new InMemoryProviderConversationStateStore(), eventSink);

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

        var session = CreateSession(model, eventSink, toolRegistry, permissionService, providerState: providerState);

        var events = new List<OmicronEvent>();
        await foreach (var evt in session.PromptAsync("Use a tool"))
            events.Add(evt);

        Assert.Contains(events, e => e is ToolInvocationStartedEvent);
        Assert.Contains(events, e => e is ToolInvocationCompletedEvent);

        var toolStart = events.OfType<ToolInvocationStartedEvent>().Single();
        Assert.Equal("test_tool", toolStart.ToolName);

        var toolEnd = events.OfType<ToolInvocationCompletedEvent>()
            .First(e => e.ToolName == "test_tool");
        Assert.True(toolEnd.Result.Utf8Span.SequenceEqual("Tool result"u8));
        Assert.False(toolEnd.IsError);

        Assert.Contains(events, e => e is AssistantResponseCompleteEvent);
        var final = events.OfType<AssistantResponseCompleteEvent>().Last();
        Assert.True(final.FullText.Utf8Span.SequenceEqual("Final answer based on tool"u8));

        var logEvents = eventSink.GetSessionEvents(session.Id);
        Assert.Contains(logEvents, e => e is ToolInvocationStartedEvent);
        Assert.Contains(logEvents, e => e is ToolInvocationCompletedEvent);
        Assert.Contains(logEvents, e => e is AssistantResponseCompleteEvent);
    }

    [Fact]
    public async Task AgentSession_DeduplicatesRepeatedProviderToolCallIds()
    {
        var eventSink = new InMemoryEventSink();
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new ToolDefinition(
            "test_tool", "A test tool", null,
            ctx => Task.FromResult(new ToolResult($"result for {ctx.ToolCallId.Value}"))));

        var permissionService = new AllowAllPermissionService();
        var providerState = new ProviderStateManager(new InMemoryProviderConversationStateStore(), eventSink);

        var fakeProvider = new FakeProvider();
        fakeProvider.Responses.Add(() => Task.FromResult(new LlmResult
        {
            ToolCalls = [new ToolCallContent("dup_call", "test_tool", new Dictionary<string, object?>())],
            StopReason = StopReason.ToolUse
        }));
        fakeProvider.Responses.Add(() => Task.FromResult(new LlmResult
        {
            ToolCalls = [new ToolCallContent("dup_call", "test_tool", new Dictionary<string, object?>())],
            StopReason = StopReason.ToolUse
        }));
        fakeProvider.Responses.Add(() => Task.FromResult(new LlmResult
        {
            Text = "done",
            StopReason = StopReason.Stop
        }));

        var model = new Model
        {
            Id = "test-model",
            Name = "Test Model",
            ProviderName = "fake",
            Provider = fakeProvider
        };

        var session = CreateSession(model, eventSink, toolRegistry, permissionService, providerState: providerState);

        await foreach (var _ in session.PromptAsync("Use tools twice")) { }

        var assistantToolCallIds = session.Messages
            .Where(m => m.Role == MessageRole.Assistant)
            .SelectMany(m => m.ToolCalls ?? [])
            .Select(tc => tc.Id)
            .ToList();
        var toolResultIds = session.Messages
            .Where(m => m.Role == MessageRole.ToolResult)
            .Select(m => m.ToolCallId)
            .ToList();

        Assert.Equal(["dup_call", "dup_call_2"], assistantToolCallIds);
        Assert.Equal(["dup_call", "dup_call_2"], toolResultIds);
    }

    [Fact]
    public async Task AgentSession_Reset_ClearsProviderState()
    {
        var eventSink = new InMemoryEventSink();
        var toolRegistry = new ToolRegistry();
        var permissionService = new AllowAllPermissionService();
        var providerState = new ProviderStateManager(new InMemoryProviderConversationStateStore(), eventSink);

        var model = new Model
        {
            Id = "test-model",
            Name = "Test Model",
            ProviderName = "fake",
            Provider = new FakeProvider()
        };

        var session = CreateSession(model, eventSink, toolRegistry, permissionService, providerState: providerState);

        var key = ProviderStateKey.Create(session.Id, session.AgentId, "fake", "test-model", ApiType.OpenAiChat);
        providerState.Set(new ProviderTurnState(key, "resp_123", "conv_456", null, null));

        Assert.NotNull(providerState.Get(key));

        session.Reset();

        Assert.Null(providerState.Get(key));

        var logEvents = eventSink.GetSessionEvents(session.Id);
        Assert.Contains(logEvents, e => e is SessionResetEvent);
    }

    [Fact]
    public async Task AgentSession_ContinueAsync_WithoutPrompt_Throws()
    {
        var eventSink = new InMemoryEventSink();
        var toolRegistry = new ToolRegistry();
        var permissionService = new AllowAllPermissionService();
        var providerState = new ProviderStateManager(new InMemoryProviderConversationStateStore(), eventSink);
        var model = new Model
        {
            Id = "test", ProviderName = "fake",
            Provider = new FakeProvider()
        };

        var session = CreateSession(model, eventSink, toolRegistry, permissionService);

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
        var providerState = new ProviderStateManager(new InMemoryProviderConversationStateStore(), eventSink);
        var model = new Model
        {
            Id = "test", ProviderName = "fake",
            Provider = new FakeProvider()
        };

        var session = CreateSession(model, eventSink, toolRegistry, permissionService);
        var fakeProvider = (FakeProvider)model.Provider!;

        fakeProvider.Responses.Add(() => Task.FromResult(new LlmResult
        {
            Text = "First", StopReason = StopReason.Stop
        }));
        await foreach (var _ in session.PromptAsync("First")) { }

        session.Reset();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in session.ContinueAsync()) { }
        });

        Assert.Contains("No conversation to continue", ex.Message);
    }

    [Fact]
    public async Task AgentSession_Reset_DoesNotReemitSessionStartedEvent()
    {
        var eventSink = new InMemoryEventSink();
        var toolRegistry = new ToolRegistry();
        var permissionService = new AllowAllPermissionService();
        var providerState = new ProviderStateManager(new InMemoryProviderConversationStateStore(), eventSink);
        var model = new Model
        {
            Id = "test-model",
            ProviderName = "fake",
            Provider = new FakeProvider()
        };

        var session = CreateSession(model, eventSink, toolRegistry, permissionService);
        var fakeProvider = (FakeProvider)model.Provider!;

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

        session.Reset();

        fakeProvider.Responses.Add(() => Task.FromResult(new LlmResult
        {
            Text = "Second response", StopReason = StopReason.Stop
        }));

        var secondEvents = new List<OmicronEvent>();
        await foreach (var evt in session.PromptAsync("Second"))
            secondEvents.Add(evt);

        Assert.Empty(secondEvents.OfType<SessionStartedEvent>());
        Assert.Contains(secondEvents, e => e is TurnStartedEvent);

        var allLog = eventSink.GetSessionEvents(session.Id);
        Assert.Equal(firstSessionStartCount, allLog.OfType<SessionStartedEvent>().Count());
    }
}
