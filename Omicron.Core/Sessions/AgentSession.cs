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
/// Uses an immutable <see cref="SessionConfig"/> passed at construction.
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
    private readonly HashSet<string> _usedToolCallIds = new(StringComparer.Ordinal);
    private bool _sessionStarted;
    private readonly SessionConfig _config;

    /// <summary>Session identity.</summary>
    public SessionId Id { get; }

    /// <summary>Agent identity.</summary>
    public AgentId AgentId { get; }

    /// <summary>The model in use (read-only — from SessionConfig).</summary>
    public Model Model => _config.Model;

    /// <summary>System prompt (read-only — from SessionConfig).</summary>
    public string? SystemPrompt => _config.SystemPrompt;

    /// <summary>API key for the LLM provider (read-only — from SessionConfig).</summary>
    public string? ApiKey => _config.ApiKey;

    /// <summary>Max tokens for each LLM call (read-only — from SessionConfig).</summary>
    public int? MaxTokens => _config.MaxTokens;

    /// <summary>Temperature for generation (read-only — from SessionConfig).</summary>
    public double? Temperature => _config.Temperature;

    /// <summary>Reasoning effort (read-only — from SessionConfig).</summary>
    public string? ReasoningEffort => _config.ReasoningEffort;

    /// <summary>Maximum tool-call loop iterations (read-only — from SessionConfig).</summary>
    public int MaxIterations => _config.MaxIterations;

    /// <summary>Conversation transcript (read-only).</summary>
    public IReadOnlyList<Message> Messages => _messages.AsReadOnly();

    public AgentSession(
        SessionConfig config,
        IToolRegistry toolRegistry,
        IPermissionService permissions,
        IEventSink eventSink,
        IProviderStateManager providerState)
        : this(config, toolRegistry, permissions, eventSink, providerState, null)
    {
    }

    internal AgentSession(
        SessionConfig config,
        IToolRegistry toolRegistry,
        IPermissionService permissions,
        IEventSink eventSink,
        IProviderStateManager providerState,
        SessionId? existingSessionId)
    {
        Id = existingSessionId ?? SessionId.New();
        AgentId = AgentId.New();
        _config = config;
        _toolRegistry = toolRegistry;
        _permissions = permissions;
        _writer = new SessionEventWriter(eventSink, Id, AgentId);
        _providerState = providerState;
    }

    /// <summary>
    /// Create a session from a projection — hydrates transcript and optionally restores
    /// provider state (re-keyed to the new session's AgentId).
    /// </summary>
    public static AgentSession FromProjection(
        SessionProjection projection,
        SessionConfig config,
        SessionId? existingSessionId,
        IToolRegistry toolRegistry,
        IPermissionService permissions,
        IEventSink eventSink,
        IProviderStateManager providerState,
        bool restoreProviderState = true)
    {
        var session = new AgentSession(
            config, toolRegistry, permissions, eventSink, providerState, existingSessionId);

        session._messages.AddRange(projection.Messages);
        session._sessionStarted = true;

        if (restoreProviderState && projection.ProviderStates.Count > 0)
        {
            foreach (var (key, state) in projection.ProviderStates)
            {
                // Re-key to the hydrated session's AgentId so RunLoopAsync can find it
                var rekeyed = state with
                {
                    Key = new ProviderStateKey(
                        session.Id, session.AgentId,
                        key.ProviderName, key.ModelId, key.ApiType)
                };
                providerState.Set(rekeyed, reason: "session_hydration");
            }
        }

        return session;
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
                _writer.Envelope(),
                AgentId, Model.Id, Model.ProviderName));
        }

        // Add user message
        _messages.Add(Message.UserMessage(text));
        yield return Emit(new UserMessageEvent(
            _writer.Envelope(), text));

        // TurnStartedEvent marks the start of this model turn
        yield return Emit(new TurnStartedEvent(
                _writer.Envelope(), text));

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
                _writer.Envelope(), "(continuation)"));

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
        _usedToolCallIds.Clear();
        _providerState.ClearSession(Id);
        Emit(new SessionResetEvent(
                _writer.Envelope()));
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
                            _writer.Envelope(),
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
                    _writer.Envelope(),
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
                var normalizedToolCalls = toolCalls
                    .Select(tc => tc with { Id = ReserveUniqueToolCallId(tc.Id) })
                    .ToList();

                _messages.Add(new Message
                {
                    Role = MessageRole.Assistant,
                    Text = fullText,
                    Reasoning = fullReasoning,
                    ToolCalls = [.. normalizedToolCalls],
                    Timestamp = DateTime.UtcNow
                });

                // Emit an AssistantResponseCompleteEvent to mark the tool-call turn boundary.
                // This gives the session projector an unambiguous signal to flush the
                // tool-call batch before subsequent ToolInvocationStarted events arrive.
                yield return Emit(new AssistantResponseCompleteEvent(
                    _writer.Envelope(),
                    fullText, fullReasoning,
                    TokenUsage.From(usage)));

                foreach (var toolCall in normalizedToolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    var toolCallId = new ToolCallId(toolCall.Id);

                    yield return Emit(new ToolInvocationStartedEvent(
                        _writer.Envelope(),
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
                            _writer.Envelope(),
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
                                // Build model metadata from session config for tool context
                                var modelModalities = new HashSet<string> { "text" };
                                if (_config.Model.SupportsImages) modelModalities.Add("image");
                                var toolModelMeta = new ModelMetadata(
                                    _config.Model.Id, _config.Model.ProviderName,
                                    SupportsVision: _config.Model.SupportsImages ? true : null,
                                    Modalities: modelModalities);

                                var invokeResult = await toolDef.InvokeAsync(
                                    new ToolInvocationContext(
                                        toolCallId,
                                        toolCall.Arguments ?? new Dictionary<string, object?>(),
                                        Id, AgentId, ct, toolModelMeta));
                                resultText = invokeResult.Text;
                                isError = invokeResult.IsError;

                                // Multimodal bridge: if read_path returned base64 content,
                                // inject a user message with actual image/audio/video/PDF content
                                // so provider shapes can serialize it as image_url/input_image.
                                if (!isError && toolCall.Name == "read_path" && resultText is not null)
                                {
                                    var bridgeImages = TryExtractBase64Content(resultText);
                                    if (bridgeImages.Count > 0)
                                    {
                                        var bridgeMsg = Message.UserMessage(
                                            $"read_path returned base64 content for the tool result below", bridgeImages);
                                        _messages.Add(bridgeMsg);
                                    }
                                }
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
                        _writer.Envelope(),
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
                _writer.Envelope(),
                fullText, fullReasoning,
                TokenUsage.From(usage)));

            yield break;
        }

        yield return Emit(new SessionErrorEvent(
            _writer.Envelope(),
            $"Agent reached maximum iteration limit ({MaxIterations}).",
            "max_iterations"));
    }

    // ================================================================
    // Helpers
    // ================================================================

    private string ReserveUniqueToolCallId(string? providerId)
    {
        var baseId = string.IsNullOrWhiteSpace(providerId)
            ? $"call_{Guid.NewGuid():N}"[..17]
            : providerId;

        if (_usedToolCallIds.Add(baseId))
            return baseId;

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseId}_{i}";
            if (_usedToolCallIds.Add(candidate))
                return candidate;
        }
    }

    private OmicronEvent Emit(OmicronEvent evt)
    {
        return _writer.Emit(evt);
    }

    /// <summary>
    /// Extract base64-encoded content from a read_path tool result.
    /// Looks for lines starting with "Data: " and extracts the base64 payload
    /// along with the MIME type from the header line.
    /// Returns an empty list if no base64 content is found.
    /// </summary>


    private static IReadOnlyList<ImageContent> TryExtractBase64Content(string resultText)
    {
        var lines = resultText.Replace("\r\n", "\n").Split('\n');
        string? mimeType = null;
        string? base64Data = null;

        foreach (var line in lines)
        {
            if (mimeType is null && line.Contains("[FILE]") && !line.Contains("Data:"))
            {
                var mimeStart = line.LastIndexOf('(');
                var mimeEnd = line.LastIndexOf(')');
                if (mimeStart >= 0 && mimeEnd > mimeStart)
                {
                    var inner = line.Substring(mimeStart + 1, mimeEnd - mimeStart - 1);
                    var comma = inner.LastIndexOf(',');
                    var candidate = comma >= 0 ? inner.Substring(comma + 1).Trim() : inner.Trim();
                    if (candidate.Contains('/'))
                        mimeType = candidate;
                }
            }

            if (line.StartsWith("Data: "))
                base64Data = line.Substring(6).Trim();
        }

        if (base64Data is not null && mimeType is not null)
            return new[] { new ImageContent(base64Data, mimeType) };

        return Array.Empty<ImageContent>();
    }
}
