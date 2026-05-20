using System.Text.Json;
using Omicron.Core.Content;
using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Providers;
using Omicron.Core.Sessions;
using Omicron.Core.Tools;
using Xunit;

namespace Omicron.Core.Tests;

public class SessionProjectionTests
{
    private static readonly SessionId TestSessionId = SessionId.New();
    private static readonly AgentId TestAgentId = AgentId.New();

    private static SessionStartedEvent Started()
    {
        return new SessionStartedEvent(new EventEnvelope(EventId.New(), 1, DateTimeOffset.UtcNow, TestSessionId),
            TestAgentId,
            "gpt-4o",
            "openai");
    }

    private static UserMessageEvent UserMsg(string text, long seq = 2)
    {
        return new UserMessageEvent(new EventEnvelope(EventId.New(), seq, DateTimeOffset.UtcNow, TestSessionId),
            Utf8String.FromString(text));
    }

    private static AssistantResponseCompleteEvent AssistantResp(
        string text,
        int input = 10,
        int output = 20,
        long seq = 3)
    {
        return new AssistantResponseCompleteEvent(
            new EventEnvelope(EventId.New(), seq, DateTimeOffset.UtcNow, TestSessionId),
            Utf8String.FromString(text),
            null,
            new TokenUsage(input, output));
    }

    private static ToolInvocationStartedEvent ToolStart(string id, string name, long seq = 4)
    {
        return new ToolInvocationStartedEvent(
            new EventEnvelope(EventId.New(), seq, DateTimeOffset.UtcNow, TestSessionId),
            new ToolCallId(id),
            name,
            new Dictionary<string, object?>());
    }

    private static ToolInvocationCompletedEvent ToolEnd(
        string id,
        string name,
        string result,
        bool isError = false,
        long seq = 5)
    {
        return new ToolInvocationCompletedEvent(
            new EventEnvelope(EventId.New(), seq, DateTimeOffset.UtcNow, TestSessionId),
            new ToolCallId(id),
            name,
            Utf8String.FromString(result),
            isError);
    }

    private static ProviderStateUpdatedEvent ProvUpdate(
        ProviderStateKey key,
        string? prevRespId,
        long seq = 6)
    {
        return new ProviderStateUpdatedEvent(
            new EventEnvelope(EventId.New(), seq, DateTimeOffset.UtcNow, TestSessionId),
            new ProviderStateSnapshot(key, prevRespId, null, null, null),
            "test");
    }

    private static ProviderStateClearedEvent ProvClear(ProviderStateKey key, long seq = 7)
    {
        return new ProviderStateClearedEvent(
            new EventEnvelope(EventId.New(), seq, DateTimeOffset.UtcNow, TestSessionId),
            key,
            "test");
    }

    private static SessionErrorEvent Err(string msg, long seq = 8)
    {
        return new SessionErrorEvent(new EventEnvelope(EventId.New(), seq, DateTimeOffset.UtcNow, TestSessionId),
            msg,
            "test_error");
    }

    [Fact]
    public void SimplePrompt_ReconstructsMessages()
    {
        var projector = new SessionProjector();
        var events = new OmicronEvent[]
        {
            Started(), UserMsg("Hello"), AssistantResp("Hi there!")
        };

        SessionProjection projection = projector.Project(events);

        Assert.Equal(TestSessionId, projection.SessionId);
        Assert.Equal(2, projection.Messages.Count);
        Assert.Equal(MessageRole.User, projection.Messages[0].Role);
        Assert.True(projection.Messages[0].TextUtf8.SequenceEqual("Hello"u8));
        Assert.Equal(MessageRole.Assistant, projection.Messages[1].Role);
        Assert.True(projection.Messages[1].TextUtf8.SequenceEqual("Hi there!"u8));
        Assert.Equal((10, 20), projection.TotalUsage);
        Assert.Empty(projection.Errors);
        Assert.False(projection.IsReset);
    }

    [Fact]
    public void ToolCallTranscript_ReconstructsToolCallAndResult()
    {
        // This matches real AgentSession event flow:
        //   UserMessage → ToolStart (creates assistant msg w/ ToolCalls) → ToolEnd → AssistantResponse
        var projector = new SessionProjector();
        var events = new OmicronEvent[]
        {
            Started(), UserMsg("What's the weather?"), ToolStart("call_1", "get_weather"),
            ToolEnd("call_1", "get_weather", "Sunny, 25°C"), AssistantResp("It's sunny!", 5, 10)
        };

        SessionProjection projection = projector.Project(events);

        Assert.Equal(4, projection.Messages.Count);

        // Message[0]: user
        Assert.Equal(MessageRole.User, projection.Messages[0].Role);
        Assert.True(projection.Messages[0].TextUtf8.SequenceEqual("What's the weather?"u8));

        // Message[1]: assistant with tool call (created by ToolStart)
        Assert.Equal(MessageRole.Assistant, projection.Messages[1].Role);
        Assert.NotNull(projection.Messages[1].ToolCalls);
        ToolCallContent tc1 = Assert.Single(projection.Messages[1].ToolCalls!);
        Assert.Equal("call_1", tc1!.Id);
        Assert.Equal("get_weather", tc1!.Name);

        // Message[2]: tool result
        Assert.Equal(MessageRole.ToolResult, projection.Messages[2].Role);
        Assert.Equal("call_1", projection.Messages[2].ToolCallId);
        Assert.Equal("get_weather", projection.Messages[2].ToolName);
        Assert.True(projection.Messages[2].TextUtf8.SequenceEqual("Sunny, 25°C"u8));
        Assert.False(projection.Messages[2].IsError);

        // Message[3]: assistant final
        Assert.Equal(MessageRole.Assistant, projection.Messages[3].Role);
        Assert.True(projection.Messages[3].TextUtf8.SequenceEqual("It's sunny!"u8));

        // Token usage comes only from AssistantResponseCompleteEvent
        Assert.Equal((5, 10), projection.TotalUsage);
    }

    [Fact]
    public void ProviderState_UpdatesAndClears()
    {
        var projector = new SessionProjector();
        var key = ProviderStateKey.Create(TestSessionId,
            TestAgentId,
            "openai",
            "gpt-5.5",
            ApiType.OpenAiResponses);

        var events = new OmicronEvent[]
        {
            Started(), ProvUpdate(key, "resp_123"), ProvClear(key)
        };

        SessionProjection projection = projector.Project(events);

        Assert.Empty(projection.ProviderStates);
    }

    [Fact]
    public void ProviderState_PreservesAllFields()
    {
        var projector = new SessionProjector();
        var key = ProviderStateKey.Create(TestSessionId,
            TestAgentId,
            "openai",
            "gpt-5.5",
            ApiType.OpenAiResponses);
        JsonElement metadata = JsonSerializer.SerializeToElement(new
        {
            foo = "bar"
        });

        var events = new OmicronEvent[]
        {
            Started(), new ProviderStateUpdatedEvent(
                new EventEnvelope(EventId.New(), 2, DateTimeOffset.UtcNow, TestSessionId),
                new ProviderStateSnapshot(key, "resp_123", "conv_456", "affinity_key", metadata),
                "test")
        };

        SessionProjection projection = projector.Project(events);

        Assert.Single(projection.ProviderStates);
        ProviderTurnState state = projection.ProviderStates[key];
        Assert.Equal("resp_123", state.PreviousResponseId);
        Assert.Equal("conv_456", state.ConversationId);
        Assert.Equal("affinity_key", state.SessionAffinityKey);
        Assert.NotNull(state.ProviderMetadata);
        Assert.Equal("bar", state.ProviderMetadata?.GetProperty("foo").GetString());
    }

    [Fact]
    public void ProviderState_UpdatePersists()
    {
        var projector = new SessionProjector();
        var key = ProviderStateKey.Create(TestSessionId,
            TestAgentId,
            "openai",
            "gpt-5.5",
            ApiType.OpenAiResponses);

        var events = new OmicronEvent[]
        {
            Started(), ProvUpdate(key, "resp_123")
        };

        SessionProjection projection = projector.Project(events);

        Assert.Single(projection.ProviderStates);
        Assert.True(projection.ProviderStates.ContainsKey(key));
        Assert.Equal("resp_123", projection.ProviderStates[key].PreviousResponseId);
    }

    [Fact]
    public void Reset_ClearsMessagesAndProviderState()
    {
        var projector = new SessionProjector();
        var key = ProviderStateKey.Create(TestSessionId,
            TestAgentId,
            "openai",
            "gpt-5.5",
            ApiType.OpenAiResponses);

        var events = new OmicronEvent[]
        {
            Started(), UserMsg("Hello"), AssistantResp("Hi!"), ProvUpdate(key, "resp_123"),
            new SessionResetEvent(new EventEnvelope(EventId.New(), 10, DateTimeOffset.UtcNow, TestSessionId)),
            UserMsg("Again"), AssistantResp("Reset response")
        };

        SessionProjection projection = projector.Project(events);

        Assert.True(projection.IsReset);
        Assert.Equal(2, projection.Messages.Count); // Only messages after reset
        Assert.True(projection.Messages[0].TextUtf8.SequenceEqual("Again"u8));
        Assert.True(projection.Messages[1].TextUtf8.SequenceEqual("Reset response"u8));
        Assert.Empty(projection.ProviderStates);
    }

    [Fact]
    public void Errors_AreCaptured()
    {
        var projector = new SessionProjector();
        var events = new OmicronEvent[]
        {
            Started(), UserMsg("Hi"), Err("Something went wrong")
        };

        SessionProjection projection = projector.Project(events);

        Assert.Single(projection.Errors);
        Assert.Equal("Something went wrong", projection.Errors[0].Message);
        // Error also creates an assistant message
        Assert.Contains(projection.Messages,
            m =>
                m.Role == MessageRole.Assistant
                && m.GetTextString().Contains("Something went wrong"));
    }

    [Fact]
    public void EmptyEvents_ReturnsDefaultProjection()
    {
        var projector = new SessionProjector();
        SessionProjection projection = projector.Project(Array.Empty<OmicronEvent>());

        Assert.Equal(default, projection.SessionId);
        Assert.Empty(projection.Messages);
        Assert.Empty(projection.ProviderStates);
        Assert.Empty(projection.Errors);
        Assert.False(projection.IsReset);
        Assert.Equal((0, 0), projection.TotalUsage);
    }

    [Fact]
    public void TokenUsage_AccumulatesAcrossTurns()
    {
        var projector = new SessionProjector();
        var events = new OmicronEvent[]
        {
            Started(), UserMsg("Q1"), AssistantResp("A1"), UserMsg("Q2"), AssistantResp("A2", 30, 40), UserMsg("Q3"),
            AssistantResp("A3", 50, 60)
        };

        SessionProjection projection = projector.Project(events);

        Assert.Equal((90, 120), projection.TotalUsage);
    }

    [Fact]
    public async Task ReplayFromJsonlStore_ReconstructsProjection()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"omicron-proj-test-{Guid.NewGuid():N}");
        try
        {
            SessionId sessionId;
            using (var store = new JsonlSessionStore(tempDir))
            {
                var request = new SessionCreateRequest("gpt-4o",
                    "openai",
                    ApiType.OpenAiChat,
                    SystemPrompt: "You are helpful.");
                SessionRecord record = await store.CreateSessionAsync(request);
                sessionId = record.SessionId;

                var events = new List<OmicronEvent>
                {
                    new SessionStartedEvent(new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, sessionId),
                        TestAgentId,
                        "gpt-4o",
                        "openai"),
                    new UserMessageEvent(new EventEnvelope(EventId.New(), 2, DateTimeOffset.UtcNow, sessionId),
                        "Hello"u8),
                    new AssistantResponseCompleteEvent(
                        new EventEnvelope(EventId.New(), 3, DateTimeOffset.UtcNow, sessionId),
                        "Hi back!"u8,
                        null,
                        new TokenUsage(5, 15))
                };
                await store.AppendEventsAsync(sessionId, events);
            }

            // Reopen and replay
            using var reopened = new JsonlSessionStore(tempDir);
            var readEvents = new List<OmicronEvent>();
            await foreach (OmicronEvent e in reopened.ReadEventsAsync(sessionId, EventSequenceRange.All))
            {
                readEvents.Add(e);
            }

            var projector = new SessionProjector();
            SessionProjection projection = projector.Project(readEvents);

            Assert.Equal(sessionId, projection.SessionId);
            Assert.Equal(2, projection.Messages.Count);
            Assert.True(projection.Messages[0].TextUtf8.SequenceEqual("Hello"u8));
            Assert.True(projection.Messages[1].TextUtf8.SequenceEqual("Hi back!"u8));
            Assert.Equal((5, 15), projection.TotalUsage);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch { }
        }
    }

    [Fact]
    public async Task ReplayFromInMemoryStore_ReconstructsFullConversation()
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
                    () =>
                        Task.FromResult(new LlmResult
                        {
                            Text = "Response 1",
                            StopReason = StopReason.Stop,
                            Usage = new UsageInfo(10, 20)
                        }),
                    () =>
                        Task.FromResult(new LlmResult
                        {
                            Text = "Response 2",
                            StopReason = StopReason.Stop,
                            Usage = new UsageInfo(30, 40)
                        })
                }
            }
        };

        AgentSession session = host.CreateSession(model);

        // First turn
        await foreach (OmicronEvent _ in session.PromptAsync("First message")) { }

        // Second turn
        await foreach (OmicronEvent _ in session.PromptAsync("Second message")) { }

        // Read all events from store and project
        var allEvents = new List<OmicronEvent>();
        await foreach (OmicronEvent e in store.ReadEventsAsync(session.Id, EventSequenceRange.All))
        {
            allEvents.Add(e);
        }

        var projector = new SessionProjector();
        SessionProjection projection = projector.Project(allEvents);

        Assert.Equal(session.Id, projection.SessionId);
        Assert.Equal(4, projection.Messages.Count);
        Assert.True(projection.Messages[0].TextUtf8.SequenceEqual("First message"u8));
        Assert.True(projection.Messages[1].TextUtf8.SequenceEqual("Response 1"u8));
        Assert.True(projection.Messages[2].TextUtf8.SequenceEqual("Second message"u8));
        Assert.True(projection.Messages[3].TextUtf8.SequenceEqual("Response 2"u8));
        Assert.Equal((40, 60), projection.TotalUsage);
    }

    [Fact]
    public async Task RealToolCallFlow_ReconstructsFullTranscript()
    {
        // Use a real AgentSession + FakeProvider that returns a tool call
        var host = new OmicronHost(Environment.CurrentDirectory);
        var store = (InMemorySessionStore)host.SessionStore;
        var toolRegistry = (ToolRegistry)host.Tools;

        // Register a test tool so the session loop processes tool calls
        var toolDef = new ToolDefinition("test_tool",
            "A test tool",
            null,
            _ => Task.FromResult(new ToolResult(TextData: Utf8String.FromUtf8("Tool executed!"u8))));
        toolRegistry.Register(toolDef);

        var fakeProvider = new FakeProvider();
        // First response: tool call
        fakeProvider.Responses.Add(() =>
            Task.FromResult(new LlmResult
            {
                Text = "",
                ToolCalls = new List<ToolCallContent>
                {
                    new("call_abc", "test_tool", new Dictionary<string, object?>())
                },
                StopReason = StopReason.ToolUse,
                Usage = new UsageInfo(10, 5)
            }));
        // Second response: final answer after tool result
        fakeProvider.Responses.Add(() =>
            Task.FromResult(new LlmResult
            {
                Text = "Here is the answer.",
                StopReason = StopReason.Stop,
                Usage = new UsageInfo(15, 25)
            }));

        var model = new Model
        {
            Id = "test-tool-model",
            Name = "Tool Test Model",
            ProviderName = "fake",
            ApiType = ApiType.OpenAiChat,
            Provider = fakeProvider
        };

        AgentSession session = host.CreateSession(model);

        await foreach (OmicronEvent _ in session.PromptAsync("Use a tool")) { }

        // Read all events from store and project
        var allEvents = new List<OmicronEvent>();
        await foreach (OmicronEvent e in store.ReadEventsAsync(session.Id, EventSequenceRange.All))
        {
            allEvents.Add(e);
        }

        var projector = new SessionProjector();
        SessionProjection projection = projector.Project(allEvents);

        Assert.Equal(session.Id, projection.SessionId);

        // Messages should be:
        // [0] User "Use a tool"
        // [1] Assistant with ToolCalls (from ToolInvocationStartedEvent)
        // [2] ToolResult (from ToolInvocationCompletedEvent)
        // [3] Assistant "Here is the answer."
        Assert.Equal(4, projection.Messages.Count);

        // User message
        Assert.Equal(MessageRole.User, projection.Messages[0].Role);
        Assert.True(projection.Messages[0].TextUtf8.SequenceEqual("Use a tool"u8));

        // Assistant tool-call message
        Assert.Equal(MessageRole.Assistant, projection.Messages[1].Role);
        Assert.NotNull(projection.Messages[1].ToolCalls);
        ToolCallContent tc2 = Assert.Single(projection.Messages[1].ToolCalls!);
        Assert.Equal("test_tool", tc2!.Name);

        // Tool result
        Assert.Equal(MessageRole.ToolResult, projection.Messages[2].Role);
        Assert.Equal("test_tool", projection.Messages[2].ToolName);
        Assert.True(projection.Messages[2].TextUtf8.SequenceEqual("Tool executed!"u8));

        // Final assistant answer
        Assert.Equal(MessageRole.Assistant, projection.Messages[3].Role);
        Assert.True(projection.Messages[3].TextUtf8.SequenceEqual("Here is the answer."u8));

        // Tool-call turn now emits usage via AssistantResponseCompleteEvent
        Assert.Equal((25, 30), projection.TotalUsage);
        Assert.False(projection.IsReset);
    }

    [Fact]
    public void MultipleToolCallsFromOneTurn_AreGroupedIntoSingleAssistantMessage()
    {
        // Real AgentSession now emits AssistantResponseCompleteEvent for tool-call turns.
        // The projector must group the ARC text with the subsequent tool calls.
        var projector = new SessionProjector();
        var events = new OmicronEvent[]
        {
            Started(), UserMsg("Use multiple tools"),
            // Tool-call turn: ARC marks the boundary, then interleaved Start/End
            AssistantResp("Let me check", 5, 3), ToolStart("call_1", "tool_a"), ToolEnd("call_1", "tool_a", "Result A"),
            ToolStart("call_2", "tool_b"), ToolEnd("call_2", "tool_b", "Result B"),
            // Final response after tool round completes
            AssistantResp("Done with tools")
        };

        SessionProjection projection = projector.Project(events);

        // Messages: [User, Assistant(Text="Let me check", ToolCalls=[a,b]), ToolResult(a), ToolResult(b), Assistant(Done)]
        Assert.Equal(5, projection.Messages.Count);

        // [0] User
        Assert.Equal(MessageRole.User, projection.Messages[0].Role);

        // [1] Assistant with text AND both tool calls in one message
        Assert.Equal(MessageRole.Assistant, projection.Messages[1].Role);
        Assert.True(projection.Messages[1].TextUtf8.SequenceEqual("Let me check"u8));
        Assert.NotNull(projection.Messages[1].ToolCalls);
        IReadOnlyList<ToolCallContent>? toolCalls = projection.Messages[1].ToolCalls!;
        Assert.Equal(2, toolCalls.Count);
        Assert.Equal("tool_a", toolCalls[0].Name);
        Assert.Equal("call_1", toolCalls[0].Id);
        Assert.Equal("tool_b", toolCalls[1].Name);
        Assert.Equal("call_2", toolCalls[1].Id);

        // [2] ToolResult for call_1
        Assert.Equal(MessageRole.ToolResult, projection.Messages[2].Role);
        Assert.Equal("call_1", projection.Messages[2].ToolCallId);
        Assert.True(projection.Messages[2].TextUtf8.SequenceEqual("Result A"u8));

        // [3] ToolResult for call_2
        Assert.Equal(MessageRole.ToolResult, projection.Messages[3].Role);
        Assert.Equal("call_2", projection.Messages[3].ToolCallId);
        Assert.True(projection.Messages[3].TextUtf8.SequenceEqual("Result B"u8));

        // [4] Final assistant
        Assert.Equal(MessageRole.Assistant, projection.Messages[4].Role);
        Assert.True(projection.Messages[4].TextUtf8.SequenceEqual("Done with tools"u8));
    }

    [Fact]
    public void ProviderState_PreservesStoragePolicy()
    {
        var projector = new SessionProjector();
        var key = ProviderStateKey.Create(TestSessionId,
            TestAgentId,
            "openai",
            "gpt-5.5",
            ApiType.OpenAiResponses);

        var events = new OmicronEvent[]
        {
            Started(), new ProviderStateUpdatedEvent(
                new EventEnvelope(EventId.New(), 2, DateTimeOffset.UtcNow, TestSessionId),
                new ProviderStateSnapshot(key,
                    "resp_1",
                    null,
                    null,
                    null,
                    ProviderStoragePolicy.AllowProviderStoredState),
                "test")
        };

        SessionProjection projection = projector.Project(events);

        Assert.Single(projection.ProviderStates);
        ProviderTurnState state = projection.ProviderStates[key];
        Assert.Equal("resp_1", state.PreviousResponseId);
        Assert.Equal(ProviderStoragePolicy.AllowProviderStoredState, state.StoragePolicy);
    }

    [Fact]
    public void Projection_UsesEventTimestamps()
    {
        var projector = new SessionProjector();
        var ts1 = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ts2 = new DateTimeOffset(2025, 1, 1, 0, 0, 1, TimeSpan.Zero);

        var events = new OmicronEvent[]
        {
            new SessionStartedEvent(new EventEnvelope(EventId.New(), 1, ts1, TestSessionId),
                TestAgentId,
                "gpt-4o",
                "openai"),
            new UserMessageEvent(new EventEnvelope(EventId.New(), 2, ts2, TestSessionId),
                "Hello"u8)
        };

        SessionProjection projection = projector.Project(events);

        Message msg = Assert.Single(projection.Messages);
        Assert.Equal(ts2.DateTime, msg.Timestamp);
    }

    [Fact]
    public void ConsecutiveToolUseIterations_EachProduceSeparateAssistantMessages()
    {
        // Real AgentSession can produce multiple tool-use turns in sequence.
        // Each tool-use ARC marks a separate turn boundary.
        var projector = new SessionProjector();
        var events = new OmicronEvent[]
        {
            Started(), UserMsg("Do two things"),
            // Tool-call turn 1: one tool
            AssistantResp("", 5, 3), ToolStart("call_1", "first_tool"), ToolEnd("call_1", "first_tool", "First result"),
            // Tool-call turn 2: one tool
            AssistantResp("", 7, 4), ToolStart("call_2", "second_tool"),
            ToolEnd("call_2", "second_tool", "Second result"),
            // Final response
            AssistantResp("All done")
        };

        SessionProjection projection = projector.Project(events);

        // Should produce:
        // [0] User "Do two things"
        // [1] Assistant(ToolCalls=[call_1])
        // [2] ToolResult(call_1)
        // [3] Assistant(ToolCalls=[call_2])
        // [4] ToolResult(call_2)
        // [5] Assistant "All done"
        Assert.Equal(6, projection.Messages.Count);

        // [1] First tool-call assistant
        Assert.Equal(MessageRole.Assistant, projection.Messages[1].Role);
        ToolCallContent tc1 = Assert.Single(projection.Messages[1].ToolCalls!);
        Assert.Equal("first_tool", tc1.Name);

        // [2] First tool result
        Assert.Equal(MessageRole.ToolResult, projection.Messages[2].Role);
        Assert.True(projection.Messages[2].TextUtf8.SequenceEqual("First result"u8));

        // [3] Second tool-call assistant (separate turn)
        Assert.Equal(MessageRole.Assistant, projection.Messages[3].Role);
        ToolCallContent tc2 = Assert.Single(projection.Messages[3].ToolCalls!);
        Assert.Equal("second_tool", tc2.Name);

        // [4] Second tool result
        Assert.Equal(MessageRole.ToolResult, projection.Messages[4].Role);
        Assert.True(projection.Messages[4].TextUtf8.SequenceEqual("Second result"u8));

        // [5] Final assistant
        Assert.Equal(MessageRole.Assistant, projection.Messages[5].Role);
        Assert.True(projection.Messages[5].TextUtf8.SequenceEqual("All done"u8));

        // Token usage from both tool turns + final
        Assert.Equal((22, 27), projection.TotalUsage);
    }
}
