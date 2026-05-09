using System.Runtime.CompilerServices;
using System.Text;
using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Permissions;
using Omicron.Core.Providers;
using Omicron.Core.Tools;

namespace Omicron.Core.Sessions;

/// <summary>
/// A stateful agent session that owns conversation history, coordinates
/// tool calls, emits durable-shaped events, and manages provider state.
///
/// Event delivery model:
///   - PromptAsync / ContinueAsync yield session-scoped events:
///     turns, user messages, assistant deltas, tool invocations, errors.
///   - Reset() is synchronous and emits SessionResetEvent only to the
///     IEventSink; it is not yielded from the async stream.
///   - Nested service events (execution, provider-state) are emitted
///     directly to the shared IEventSink but are NOT yielded from
///     the async stream. Frontends that need the complete event log
///     should read from the IEventSink directly.
///   - This makes the async stream a convenient live UI feed, while
///     the IEventSink is the authoritative audit/durability stream.
/// </summary> 
public sealed class AgentSession
{
    private readonly List<Message> _messages = [];
    private readonly IToolRegistry _toolRegistry;
    private readonly IPermissionService _permissions;
    private readonly IProviderStateManager _providerState;
    private readonly SessionEventWriter _writer;
    private bool _sessionStarted;

    /// <summary>Session identity.</summary>
    public SessionId Id { get; }

    /// <summary>Agent identity.</summary>
    public AgentId AgentId { get; }

    /// <summary>The model in use.</summary>
    public Model Model { get; set; }

    /// <summary>System prompt.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>API key for the LLM provider.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Max tokens for each LLM call.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Temperature for generation.</summary>
    public double? Temperature { get; set; }

    /// <summary>Reasoning effort.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Maximum tool-call loop iterations.</summary>
    public int MaxIterations { get; set; } = 100;

    /// <summary>Conversation transcript (read-only).</summary>
    public IReadOnlyList<Message> Messages => _messages.AsReadOnly();

    public AgentSession(
        Model model,
        IToolRegistry toolRegistry,
        IPermissionService permissions,
        IEventSink eventSink,
        IProviderStateManager providerState,
        string? systemPrompt = null,
        string? apiKey = null)
    {
        Id = SessionId.New();
        AgentId = AgentId.New();
        Model = model;
        SystemPrompt = systemPrompt;
        ApiKey = apiKey;
        _toolRegistry = toolRegistry;
        _permissions = permissions;
        _writer = new SessionEventWriter(eventSink, Id, AgentId);
        _providerState = providerState;
    }

    /// <summary>
    /// Add a user message and stream the LLM response.
    /// Emits SessionStartedEvent once per session lifetime,
    /// then TurnStartedEvent for this prompt.
    /// </summary>
    public async IAsyncEnumerable<OmicronEvent> PromptAsync(
        string text,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // SessionStartedEvent fires only once per session lifecycle
        if (!_sessionStarted)
        {
            _sessionStarted = true;
            yield return Emit(new SessionStartedEvent(
                EventId.New(), 0, DateTimeOffset.UtcNow, Id, AgentId,
                Model.Id, Model.ProviderName));
        }

        // Add user message
        _messages.Add(Message.UserMessage(text));
        yield return Emit(new UserMessageEvent(
            EventId.New(), 0, DateTimeOffset.UtcNow, Id, text));

        // TurnStartedEvent marks the start of this model turn
        yield return Emit(new TurnStartedEvent(
            EventId.New(), 0, DateTimeOffset.UtcNow, Id, text));

        await foreach (var evt in RunLoopAsync(ct))
            yield return evt;
    }

    /// <summary>
    /// Continue the conversation from the current transcript.
    /// Requires that PromptAsync was called at least once and that the
    /// conversation has not been reset to empty.
    /// </summary>
    public async IAsyncEnumerable<OmicronEvent> ContinueAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_sessionStarted || _messages.Count == 0)
            throw new InvalidOperationException(
                "No conversation to continue. Call PromptAsync first.");

        yield return Emit(new TurnStartedEvent(
            EventId.New(), 0, DateTimeOffset.UtcNow, Id, "(continuation)"));

        await foreach (var evt in RunLoopAsync(ct))
            yield return evt;
    }

    /// <summary>
    /// Reset the conversation (clear all messages and provider state).
    /// Uses same-session policy: SessionStartedEvent is NOT re-emitted on next prompt.
    /// SessionResetEvent marks the clearing point for replay.
    /// </summary>
    public void Reset()
    {
        _messages.Clear();
        _providerState.ClearSession(Id);
        Emit(new SessionResetEvent(
            EventId.New(), 0, DateTimeOffset.UtcNow, Id));
    }

    // ================================================================
    // Core agent loop
    // ================================================================

    private async IAsyncEnumerable<OmicronEvent> RunLoopAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var provider = Model.Provider ?? throw new InvalidOperationException("Model has no provider set.");

        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            // Build provider state context for stateful API support
            var providerStateKey = ProviderStateKey.Create(
                Id, AgentId, Model.ProviderName, Model.Id, Model.ApiType);
            var currentProviderState = _providerState.Get(providerStateKey);

            var options = new ChatOptions
            {
                ApiKey = ApiKey,
                Temperature = Temperature,
                MaxTokens = MaxTokens,
                ReasoningEffort = ReasoningEffort,
                CancellationToken = ct,
                ProviderStateKey = providerStateKey,
                CurrentProviderState = currentProviderState,
                StoragePolicy = Model.StoragePolicy
            };

            // Build tool list from registry
            var tools = _toolRegistry.AllTools.Count > 0
                ? _toolRegistry.AllTools
                    .Select(t => new Tool
                    {
                        Name = t.Name,
                        Description = t.Description,
                        Parameters = t.Parameters
                    })
                    .ToList()
                : null;

            var responseText = new StringBuilder();
            var reasoningText = new StringBuilder();
            var toolCalls = new List<ToolCallContent>();
            StopReason stopReason = StopReason.Stop;
            string? errorMessage = null;
            UsageInfo? usage = null;
            string? responseId = null;

            // Stream response
            await foreach (var evt in provider.StreamAsync(
                Model, _messages, SystemPrompt,
                tools?.Count > 0 ? tools : null, options))
            {
                switch (evt.Type)
                {
                    case StreamEventType.TextDelta:
                        responseText.Append(evt.Delta);
                        if (evt.ReasoningText is not null)
                            reasoningText.Append(evt.ReasoningText);
                        yield return Emit(new AssistantTextDeltaEvent(
                            EventId.New(), 0, DateTimeOffset.UtcNow, Id,
                            evt.Delta ?? string.Empty, evt.ReasoningText));
                        break;

                    case StreamEventType.ToolCallEnd when evt.ToolCall is not null:
                        toolCalls.Add(evt.ToolCall);
                        break;

                    case StreamEventType.Done:
                        stopReason = evt.StopReason ?? StopReason.Stop;
                        usage = evt.Usage;
                        responseId = evt.Delta; // Capture response ID for stateful APIs

                        // Only persist provider state for APIs that support stateful continuation.
                        // Stateless APIs (Chat, Anthropic) should not create continuation state.
                        var supportsStateful = CompatibilityDetector.SupportsStatefulContinuation(
                            Model.ApiType, Model.StoragePolicy);
                        var compat = Model.GetEffectiveCompatibility();
                        if (supportsStateful && compat.SupportsPreviousResponseId && responseId is not null)
                        {
                            var newState = new ProviderTurnState(
                                providerStateKey,
                                responseId,
                                null,
                                null,
                                null)
                            {
                                StoragePolicy = Model.StoragePolicy
                            };
                            _providerState.Set(newState, reason: "response_completed");
                        }
                        break;

                    case StreamEventType.Error:
                        stopReason = StopReason.Error;
                        errorMessage = evt.ErrorMessage;
                        break;
                }
            }

            // Handle errors
            if (stopReason == StopReason.Error)
            {
                var errMsg = errorMessage ?? "Unknown error";
                yield return Emit(new SessionErrorEvent(
                    EventId.New(), 0, DateTimeOffset.UtcNow, Id,
                    errMsg, "provider_error"));
                _messages.Add(new Message
                {
                    Role = MessageRole.Assistant,
                    Text = $"[Error: {errMsg}]",
                    Timestamp = DateTime.UtcNow
                });
                yield break;
            }

            var fullText = responseText.ToString();
            var fullReasoning = reasoningText.Length > 0 ? reasoningText.ToString() : null;
            if (reasoningText.Length == 0 && _messages.Count > 0)
            {
                var lastAssistant = _messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
                if (lastAssistant?.Reasoning is not null)
                    fullReasoning = "";
            }

            // Handle tool calls
            if (toolCalls.Count > 0)
            {
                _messages.Add(new Message
                {
                    Role = MessageRole.Assistant,
                    Text = fullText,
                    Reasoning = fullReasoning,
                    ToolCalls = [.. toolCalls],
                    Timestamp = DateTime.UtcNow
                });

                foreach (var toolCall in toolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    var toolCallId = new ToolCallId(toolCall.Id);

                    yield return Emit(new ToolInvocationStartedEvent(
                        EventId.New(), 0, DateTimeOffset.UtcNow, Id,
                        toolCallId, toolCall.Name,
                        toolCall.Arguments ?? new Dictionary<string, object?>()));

                    var toolDef = _toolRegistry.GetTool(toolCall.Name);
                    string resultText;
                    bool isError = false;

                    if (toolDef is null)
                    {
                        resultText = $"Error: Tool '{toolCall.Name}' not found.";
                        isError = true;
                    }
                    else
                    {
                        // Check permission
                        var permRequest = new PermissionRequest(
                            "tool.execute", toolCall.Name,
                            $"Execute tool '{toolCall.Name}'");
                        var permResult = await _permissions.RequestAsync(permRequest, ct);

                        // Emit permission event
                        yield return Emit(new PermissionRequestedEvent(
                            EventId.New(), 0, DateTimeOffset.UtcNow, Id,
                            permRequest.Action, permResult.Allowed));

                        if (!permResult.Allowed)
                        {
                            resultText = $"Error: Permission denied for tool '{toolCall.Name}': {permResult.Reason}";
                            isError = true;
                        }
                        else
                        {
                            try
                            {
                                var invokeResult = await toolDef.InvokeAsync(
                                    new ToolInvocationContext(
                                        toolCallId,
                                        toolCall.Arguments ?? new Dictionary<string, object?>(),
                                        Id, AgentId, ct));
                                resultText = invokeResult.Text;
                                isError = invokeResult.IsError;
                            }
                            catch (Exception ex)
                            {
                                resultText = $"Error executing tool '{toolCall.Name}': {ex.Message}";
                                isError = true;
                            }
                        }
                    }

                    _messages.Add(Message.ToolResultMessage(
                        toolCall.Id, toolCall.Name, resultText, isError));

                    yield return Emit(new ToolInvocationCompletedEvent(
                        EventId.New(), 0, DateTimeOffset.UtcNow, Id,
                        toolCallId, toolCall.Name, resultText, isError));
                }

                continue;
            }

            // No tool calls — final response
            _messages.Add(new Message
            {
                Role = MessageRole.Assistant,
                Text = fullText,
                Reasoning = fullReasoning,
                Timestamp = DateTime.UtcNow
            });

            yield return Emit(new AssistantResponseCompleteEvent(
                EventId.New(), 0, DateTimeOffset.UtcNow, Id,
                fullText, fullReasoning,
                usage?.InputTokens ?? 0, usage?.OutputTokens ?? 0));

            yield break;
        }

        yield return Emit(new SessionErrorEvent(
            EventId.New(), 0, DateTimeOffset.UtcNow, Id,
            $"Agent reached maximum iteration limit ({MaxIterations}).",
            "max_iterations"));
    }

    // ================================================================
    // Helpers
    // ================================================================

    private OmicronEvent Emit(OmicronEvent evt)
    {
        return _writer.Emit(evt);
    }
}
