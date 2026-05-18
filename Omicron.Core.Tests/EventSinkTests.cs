using Omicron.Core.Content;
using Omicron.Core.Events;
using Omicron.Core.Sessions;
using Omicron.Core.Workspace;
using Xunit;

namespace Omicron.Core.Tests;

public class EventSinkTests
{
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

    [Fact]
    public void OmicronEvent_HasIdSequenceTimestampAndSession()
    {
        var sessionId = SessionId.New();
        var evt = new SessionStartedEvent(new EventEnvelope(EventId.New(), 7, DateTimeOffset.UtcNow, sessionId), AgentId.New(), "gpt-4o", "openai");

        Assert.NotEqual(default, evt.Id);
        Assert.Equal(7, evt.Sequence);
        Assert.Equal(sessionId, evt.SessionId);
    }

    [Fact]
    public void UserMessageEvent_StoresText()
    {
        var sessionId = SessionId.New();
        var evt = new UserMessageEvent(
                new EventEnvelope(EventId.New(), 1, DateTimeOffset.UtcNow, sessionId), "Hello"u8);

        Assert.True(evt.Text.Utf8Span.SequenceEqual("Hello"u8));
    }

    [Fact]
    public void AssistantTextDeltaEvent_StoresDelta()
    {
        var sessionId = SessionId.New();
        var evt = new AssistantTextDeltaEvent(
                new EventEnvelope(EventId.New(), 1, DateTimeOffset.UtcNow, sessionId), "Hello"u8, null);

        Assert.True(evt.Delta.Utf8Span.SequenceEqual("Hello"u8));
        Assert.Null(evt.ReasoningDelta);
    }

    [Fact]
    public void AssistantResponseCompleteEvent_StoresFullResponse()
    {
        var sessionId = SessionId.New();
        var evt = new AssistantResponseCompleteEvent(
                new EventEnvelope(EventId.New(), 1, DateTimeOffset.UtcNow, sessionId), "Full response"u8, "reasoning"u8,
                new TokenUsage(10, 20));

        Assert.True(evt.FullText.Utf8Span.SequenceEqual("Full response"u8));
        Assert.True(evt.ReasoningText!.Utf8Span.SequenceEqual("reasoning"u8));
        Assert.Equal(10, evt.Usage.InputTokens);
        Assert.Equal(20, evt.Usage.OutputTokens);
    }

    [Fact]
    public void ToolInvocationStartedEvent_StoresArguments()
    {
        var sessionId = SessionId.New();
        var args = new Dictionary<string, object?> { ["expr"] = "2+2" };
        var evt = new ToolInvocationStartedEvent(new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, sessionId), new ToolCallId("call_1"), "calculator", args);

        Assert.Equal("calculator", evt.ToolName);
        Assert.Equal("2+2", evt.Arguments["expr"]?.ToString());
    }

    [Fact]
    public void ToolInvocationCompletedEvent_StoresResult()
    {
        var sessionId = SessionId.New();
        var evt = new ToolInvocationCompletedEvent(
            new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, sessionId),
            new ToolCallId("call_1"), "calculator", "42"u8, false);

        Assert.True(evt.Result.Utf8Span.SequenceEqual("42"u8));
        Assert.False(evt.IsError);
    }

    [Fact]
    public void InMemoryEventSink_Emit_StoresEvent()
    {
        var sink = new InMemoryEventSink();
        var sessionId = SessionId.New();
        var evt = new SessionStartedEvent(new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, sessionId), AgentId.New(), "gpt-4o", "openai");

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
            new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, session1),
            AgentId.New(), "gpt-4o", "openai"));
        sink.Emit(new SessionStartedEvent(
            new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, session2),
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
            new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, sessionId),
            AgentId.New(), "gpt-4o", "openai"));

        sink.Clear();
        Assert.Empty(sink.GetAllEvents());
    }

    [Fact]
    public void InMemoryEventSink_EmitBatch_ReturnsStampedEvents()
    {
        var sink = new InMemoryEventSink();
        var sessionId = SessionId.New();
        var agentId = AgentId.New();

        var events = new List<OmicronEvent>
        {
            new SessionStartedEvent(new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, sessionId), agentId, "m1", "p1"),
            new UserMessageEvent(
                new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, sessionId), "Hello"u8),
            new TurnStartedEvent(
                new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, sessionId), "Hello")
        };

        var stamped = sink.EmitBatch(events);

        Assert.Equal(3, stamped.Count);
        Assert.All(stamped, e => Assert.NotEqual(0, e.Sequence));

        // Sequences must be strictly increasing
        for (int i = 1; i < stamped.Count; i++)
            Assert.True(stamped[i].Sequence > stamped[i - 1].Sequence);

        // Original events should not be modified
        Assert.Equal(0, events[0].Sequence);
    }

    [Fact]
    public void InMemoryEventSink_SequencesAreMonotonic()
    {
        var sink = new InMemoryEventSink();
        var sessionId = SessionId.New();
        var agentId = AgentId.New();

        sink.Emit(new SessionStartedEvent(
            new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, sessionId), agentId, "m1", "p1"));
        var s2 = SessionId.New();
        sink.Emit(new SessionStartedEvent(
            new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, s2), agentId, "m2", "p2"));
        sink.Emit(new UserMessageEvent(
                new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, sessionId), "Hello"u8));
        sink.Emit(new AssistantTextDeltaEvent(
                new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, s2), "Delta"u8, null));

        var all = sink.GetAllEvents();

        Assert.All(all, e => Assert.NotEqual(0, e.Sequence));

        for (int i = 1; i < all.Count; i++)
            Assert.True(all[i].Sequence > all[i - 1].Sequence,
                $"Event {i} (seq={all[i].Sequence}) <= event {i - 1} (seq={all[i - 1].Sequence})");

        var sessionEvents = sink.GetSessionEvents(sessionId);
        for (int i = 1; i < sessionEvents.Count; i++)
            Assert.True(sessionEvents[i].Sequence > sessionEvents[i - 1].Sequence,
                $"Session event {i} out of order");
    }
}




