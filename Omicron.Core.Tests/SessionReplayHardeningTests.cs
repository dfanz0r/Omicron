using Omicron.Core.Events;
using Omicron.Core.IO;
using Omicron.Core.Models;
using Omicron.Core.Permissions;
using Omicron.Core.Providers;
using Omicron.Core.Sessions;
using Omicron.Core.Tools;
using Omicron.Core.Workspace;
using Xunit;

namespace Omicron.Core.Tests;

public class SessionReplayHardeningTests
{
    private static readonly SessionId TestSessionId = SessionId.New();
    private static readonly AgentId TestAgentId = AgentId.New();

    // ============================================================
    // Work Item 1: Projection completeness audit
    // ============================================================

    /// <summary>
    /// Every registered event type must be either handled by the projector
    /// or explicitly tagged as intentionally ignored.
    /// </summary>
    [Fact]
    public void Projection_CoversAllRegisteredEventTypes()
    {
        var handled = new HashSet<Type>
        {
            typeof(SessionStartedEvent),
            typeof(SessionResetEvent),
            typeof(SessionErrorEvent),
            typeof(UserMessageEvent),
            typeof(AssistantTextDeltaEvent),
            typeof(AssistantResponseCompleteEvent),
            typeof(ToolInvocationStartedEvent),
            typeof(ToolInvocationCompletedEvent),
            typeof(ProviderStateUpdatedEvent),
            typeof(ProviderStateClearedEvent),
        };

        // Event types that are intentionally ignored by projection
        // (metadata, execution, or lifecycle events with no conversational content)
        var intentionallyIgnored = new HashSet<Type>
        {
            typeof(ModalityUsedEvent),
            typeof(TransactionStartedEvent),
            typeof(TransactionStagedEvent),
            typeof(TransactionCommittedEvent),
            typeof(TransactionRolledBackEvent),
            typeof(TurnStartedEvent),
            typeof(SessionEndedEvent),
            typeof(PermissionRequestedEvent),
            typeof(ExecutionStartedEvent),
            typeof(ExecutionCompletedEvent),
        };

        var registered = OmicronEventRegistry.AllTypes.ToHashSet();

        // Every registered type must be either handled or intentionally ignored
        var uncovered = registered
            .Where(t => !handled.Contains(t) && !intentionallyIgnored.Contains(t))
            .ToList();

        Assert.False(uncovered.Count > 0,
            $"Unhandled event types in projector: {string.Join(", ", uncovered.Select(t => t.Name))}");
    }

    // ============================================================
    // Work Item 1b: Unknown/ignored events do not crash projection
    // ============================================================

    [Fact]
    public void Projection_ModalityUsedEvent_IsIgnored()
    {
        var projector = new SessionProjector();
        var events = new OmicronEvent[]
        {
            new SessionStartedEvent(Envelope(), TestAgentId, "gpt-4", "openai"),
            new UserMessageEvent(Envelope(), "hello"),
            new ModalityUsedEvent(Envelope(), "image", "photo.png", OutputModality.ImageBase64),
            new AssistantResponseCompleteEvent(Envelope(), "response", null, new TokenUsage(10, 20)),
        };

        var projection = projector.Project(events);
        Assert.Equal(2, projection.Messages.Count); // User + Assistant; ModalityUsedEvent ignored
        Assert.Equal(MessageRole.User, projection.Messages[0].Role);
        Assert.Equal("hello", projection.Messages[0].Text);
    }

    [Fact]
    public void Projection_TransactionEvents_AreIgnored()
    {
        var projector = new SessionProjector();
        var txId = new WorkspaceTransactionId(Guid.NewGuid());
        var events = new OmicronEvent[]
        {
            new SessionStartedEvent(Envelope(), TestAgentId, "gpt-4", "openai"),
            new TransactionStartedEvent(Envelope(), txId),
            new TransactionStagedEvent(Envelope(), txId, "file.txt", "write"),
            new TransactionCommittedEvent(Envelope(), txId),
            new TransactionRolledBackEvent(Envelope(), txId),
            new UserMessageEvent(Envelope(), "hello"),
        };

        var projection = projector.Project(events);
        var userMsg = Assert.Single(projection.Messages);
        Assert.Equal(MessageRole.User, userMsg.Role);
    }

    [Fact]
    public void Projection_SessionEndedEvent_IsIgnored()
    {
        var projector = new SessionProjector();
        var events = new OmicronEvent[]
        {
            new SessionStartedEvent(Envelope(), TestAgentId, "gpt-4", "openai"),
            new SessionEndedEvent(Envelope(), "done"),
            new UserMessageEvent(Envelope(), "ping"),
        };

        var projection = projector.Project(events);
        Assert.NotEmpty(projection.Messages);
    }

    [Fact]
    public void Projection_ExecutionEvents_AreIgnored()
    {
        var projector = new SessionProjector();
        var events = new OmicronEvent[]
        {
            new SessionStartedEvent(Envelope(), TestAgentId, "gpt-4", "openai"),
            new ExecutionStartedEvent(Envelope(), "ls -la", "/tmp"),
            new ExecutionCompletedEvent(Envelope(), "ls -la", 0, 100, false),
            new UserMessageEvent(Envelope(), "ok"),
        };

        var projection = projector.Project(events);
        Assert.Single(projection.Messages);
    }

    [Fact]
    public void Projection_PermissionRequestedEvent_IsIgnored()
    {
        var projector = new SessionProjector();
        var events = new OmicronEvent[]
        {
            new SessionStartedEvent(Envelope(), TestAgentId, "gpt-4", "openai"),
            new PermissionRequestedEvent(Envelope(), "read_path", true),
            new UserMessageEvent(Envelope(), "hello"),
        };

        var projection = projector.Project(events);
        Assert.Single(projection.Messages);
    }

    // ============================================================
    // Work Item 2: AgentSession hydration hardening
    // ============================================================

    private static (IToolRegistry, IPermissionService, IEventSink, IProviderStateManager) CreateMocks()
    {
        var tools = new ToolRegistry();
        var permissions = new AllowAllPermissionService();
        var events = new InMemoryEventSink();
        var providerState = new InMemoryProviderConversationStateStore();
        var stateManager = new ProviderStateManager(providerState, events);
        return (tools, permissions, events, stateManager);
    }

    [Fact]
    public void FromProjection_EmptyProjection_HydratesCleanSession()
    {
        var (tools, permissions, events, providerState) = CreateMocks();
        var config = SessionConfig.Create(
            new Model { Id = "gpt-4", ProviderName = "openai", ApiType = ApiType.OpenAiChat });

        var projection = new SessionProjection
        {
            SessionId = TestSessionId,
            Messages = Array.Empty<Message>(),
            ProviderStates = new Dictionary<ProviderStateKey, ProviderTurnState>()
        };

        var session = AgentSession.FromProjection(
            projection, config, TestSessionId, tools, permissions, events, providerState, restoreProviderState: true);

        Assert.Empty(session.Messages);
    }

    [Fact]
    public void FromProjection_SingleUserMessage_RestoresTranscript()
    {
        var (tools, permissions, events, providerState) = CreateMocks();
        var config = SessionConfig.Create(
            new Model { Id = "gpt-4", ProviderName = "openai", ApiType = ApiType.OpenAiChat });

        var projection = new SessionProjection
        {
            SessionId = TestSessionId,
            Messages = new[]
            {
                Message.UserMessage("hello"),
                new Message { Role = MessageRole.Assistant, Text = "world" }
            },
            ProviderStates = new Dictionary<ProviderStateKey, ProviderTurnState>()
        };

        var session = AgentSession.FromProjection(
            projection, config, TestSessionId, tools, permissions, events, providerState, restoreProviderState: true);

        Assert.Equal(2, session.Messages.Count);
        Assert.Equal(MessageRole.User, session.Messages[0].Role);
        Assert.Equal("hello", session.Messages[0].Text);
        Assert.Equal(MessageRole.Assistant, session.Messages[1].Role);
        Assert.Equal("world", session.Messages[1].Text);
    }

    // ============================================================
    // Work Item 3: Provider state transfer policy
    // ============================================================

    [Fact]
    public void FromProjection_SameModelProviderState_Restored()
    {
        var (tools, permissions, events, providerState) = CreateMocks();
        var config = SessionConfig.Create(
            new Model { Id = "gpt-4", ProviderName = "openai", ApiType = ApiType.OpenAiChat });

        var key = new ProviderStateKey(TestSessionId, TestAgentId, "openai", "gpt-4", ApiType.OpenAiChat);
        var turnState = new ProviderTurnState(key, "resp-123", "conv-456", null, null);

        var projection = new SessionProjection
        {
            SessionId = TestSessionId,
            Messages = Array.Empty<Message>(),
            ProviderStates = new Dictionary<ProviderStateKey, ProviderTurnState>
            {
                [key] = turnState
            }
        };

        var session = AgentSession.FromProjection(
            projection, config, TestSessionId, tools, permissions, events, providerState, restoreProviderState: true);

        // The state should be re-keyed and stored
        Assert.NotNull(session);
        // Verify via IProviderStateManager that state was set
        var storedKeys = providerState.GetSessionKeys(TestSessionId);
        Assert.Contains(storedKeys, k => k.ProviderName == "openai" && k.ModelId == "gpt-4");
    }

    [Fact]
    public void FromProjection_RestoreProviderStateFalse_ClearsState()
    {
        var (tools, permissions, events, providerState) = CreateMocks();
        var config = SessionConfig.Create(
            new Model { Id = "gpt-4", ProviderName = "openai", ApiType = ApiType.OpenAiChat });

        var key = new ProviderStateKey(TestSessionId, TestAgentId, "openai", "gpt-4", ApiType.OpenAiChat);
        var turnState = new ProviderTurnState(key, "resp-123", "conv-456", null, null);

        var projection = new SessionProjection
        {
            SessionId = TestSessionId,
            Messages = Array.Empty<Message>(),
            ProviderStates = new Dictionary<ProviderStateKey, ProviderTurnState>
            {
                [key] = turnState
            }
        };

        var session = AgentSession.FromProjection(
            projection, config, TestSessionId, tools, permissions, events, providerState, restoreProviderState: false);

        // State should NOT be restored
        var storedKeys = providerState.GetSessionKeys(TestSessionId);
        Assert.DoesNotContain(storedKeys, k => k.ProviderName == "openai" && k.ModelId == "gpt-4");
    }

    // ============================================================
    // Work Item 5: Tool-call transcript replay
    // ============================================================

    [Fact]
    public void Projection_ToolCallTurn_ReconstructsCorrectly()
    {
        var projector = new SessionProjector();
        var events = new OmicronEvent[]
        {
            new SessionStartedEvent(Envelope(), TestAgentId, "gpt-4", "openai"),
            new UserMessageEvent(Envelope(), "read the file"),
            new AssistantResponseCompleteEvent(Envelope(), "I'll read it", null, new TokenUsage(10, 20)),
            new ToolInvocationStartedEvent(Envelope(), new ToolCallId("tc1"), "read_path",
                new Dictionary<string, object?> { ["path"] = "test.txt" }),
            new ToolInvocationCompletedEvent(Envelope(), new ToolCallId("tc1"), "read_path",
                "[FILE] test.txt (content)", false),
            new AssistantResponseCompleteEvent(Envelope(), "Here's the content", null, new TokenUsage(5, 10)),
        };

        var projection = projector.Project(events);

        // Messages: User, Assistant (with tool call), ToolResult, Assistant (final)
        Assert.Equal(4, projection.Messages.Count);
        Assert.Equal(MessageRole.User, projection.Messages[0].Role);
        Assert.Equal(MessageRole.Assistant, projection.Messages[1].Role);
        Assert.Contains("I'll read it", projection.Messages[1].Text);
        Assert.NotNull(projection.Messages[1].ToolCalls);
        var toolCall = Assert.Single(projection.Messages[1].ToolCalls!);
        Assert.Equal("read_path", toolCall.Name);

        Assert.Equal(MessageRole.ToolResult, projection.Messages[2].Role);
        Assert.Equal("tc1", projection.Messages[2].ToolCallId);
        Assert.Equal("read_path", projection.Messages[2].ToolName);
        Assert.Contains("[FILE] test.txt", projection.Messages[2].Text);

        Assert.Equal(MessageRole.Assistant, projection.Messages[3].Role);
        Assert.Equal("Here's the content", projection.Messages[3].Text);
    }

    [Fact]
    public void Projection_TokenUsage_AggregatesCorrectly()
    {
        var projector = new SessionProjector();
        var events = new OmicronEvent[]
        {
            new SessionStartedEvent(Envelope(), TestAgentId, "gpt-4", "openai"),
            new UserMessageEvent(Envelope(), "hello"),
            new AssistantResponseCompleteEvent(Envelope(), "hi", null, new TokenUsage(10, 20)),
            new UserMessageEvent(Envelope(), "again"),
            new AssistantResponseCompleteEvent(Envelope(), "ok", null, new TokenUsage(5, 15)),
        };

        var projection = projector.Project(events);
        Assert.Equal(15, projection.TotalUsage.InputTokens);
        Assert.Equal(35, projection.TotalUsage.OutputTokens);
    }

    [Fact]
    public void Projection_Reset_ClearsState()
    {
        var projector = new SessionProjector();
        var events = new OmicronEvent[]
        {
            new SessionStartedEvent(Envelope(), TestAgentId, "gpt-4", "openai"),
            new UserMessageEvent(Envelope(), "hello"),
            new AssistantResponseCompleteEvent(Envelope(), "hi", null, new TokenUsage(5, 10)),
            new SessionResetEvent(Envelope()),
            new UserMessageEvent(Envelope(), "fresh start"),
            new AssistantResponseCompleteEvent(Envelope(), "ok", null, new TokenUsage(1, 2)),
        };

        var projection = projector.Project(events);
        // Only messages after reset should exist
        Assert.Equal(2, projection.Messages.Count); // User + Assistant
        Assert.Equal("fresh start", projection.Messages[0].Text);
        Assert.True(projection.IsReset);
        // Token usage should be reset too
        Assert.Equal(1, projection.TotalUsage.InputTokens);
        Assert.Equal(2, projection.TotalUsage.OutputTokens);
    }

    [Fact]
    public void Projection_MultiToolBatch_ReconstructsCorrectly()
    {
        var projector = new SessionProjector();
        var events = new OmicronEvent[]
        {
            new SessionStartedEvent(Envelope(), TestAgentId, "gpt-4", "openai"),
            new UserMessageEvent(Envelope(), "do both"),
            // Batch: 2 tool calls in parallel
            new ToolInvocationStartedEvent(Envelope(), new ToolCallId("tc1"), "tool_a",
                new Dictionary<string, object?>()),
            new ToolInvocationStartedEvent(Envelope(), new ToolCallId("tc2"), "tool_b",
                new Dictionary<string, object?>()),
            new ToolInvocationCompletedEvent(Envelope(), new ToolCallId("tc1"), "tool_a", "result_a", false),
            new ToolInvocationCompletedEvent(Envelope(), new ToolCallId("tc2"), "tool_b", "result_b", false),
            new AssistantResponseCompleteEvent(Envelope(), "done", null, new TokenUsage(10, 20)),
        };

        var projection = projector.Project(events);
        // Messages: User, Assistant (with 2 tool calls), ToolResult x2, Assistant (final)
        // Note: due to how flushing works, the empty text + tool calls is one assistant msg,
        // then 2 tool results, then final assistant
        Assert.Equal(5, projection.Messages.Count); // User + Assistant(ToolCalls) + TR1 + TR2 + Assistant(final)

        // First assistant message has the tool calls
        Assert.Equal(MessageRole.Assistant, projection.Messages[1].Role);
        Assert.Equal(2, projection.Messages[1].ToolCalls?.Count ?? 0);

        // Tool results follow
        Assert.Equal(MessageRole.ToolResult, projection.Messages[2].Role);
        Assert.Equal(MessageRole.ToolResult, projection.Messages[3].Role);
        Assert.Equal("tool_a", projection.Messages[2].ToolName);
        Assert.Equal("tool_b", projection.Messages[3].ToolName);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static EventEnvelope Envelope()
        => EventEnvelope.ForSession(TestSessionId);
}
