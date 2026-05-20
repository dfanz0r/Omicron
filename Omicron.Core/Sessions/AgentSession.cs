using System.Runtime.CompilerServices;
using System.Text;
using Omicron.Core.Content;
using Omicron.Core.Diff;
using Omicron.Core.Events;
using Omicron.Core.Text;
using Omicron.Core.Models;
using Omicron.Core.Permissions;
using Omicron.Core.Providers;
using Omicron.Core.Tools;

namespace Omicron.Core.Sessions;

/// <summary>
///     A stateful agent session that owns conversation history, coordinates
///     tool calls, emits durable-shaped events, and manages provider state.
///     Uses an immutable <see cref="SessionConfig" /> passed at construction.
///     Event delivery model:
///     - PromptAsync / ContinueAsync yield session-scoped events:
///     turns, user messages, assistant deltas, tool invocations, errors.
///     - Reset() is synchronous and emits SessionResetEvent only to the
///     IEventSink; it is not yielded from the async stream.
///     - Nested service events (execution, provider-state) are emitted
///     directly to the shared IEventSink but are NOT yielded from
///     the async stream. Frontends that need the complete event log
///     should read from the IEventSink directly.
///     - This makes the async stream a convenient live UI feed, while
///     the IEventSink is the authoritative audit/durability stream.
/// </summary>
public sealed class AgentSession
{
    private readonly SessionConfig _config;
    private readonly List<Message> _messages = [];
    private readonly IPermissionService _permissions;
    private readonly IProviderStateManager _providerState;
    private readonly IToolRegistry _toolRegistry;
    private readonly HashSet<string> _usedToolCallIds = new(StringComparer.Ordinal);
    private readonly SessionEventWriter _writer;
    private bool _sessionStarted;

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

    /// <summary>
    ///     Create a session from a projection — hydrates transcript and optionally restores
    ///     provider state (re-keyed to the new session's AgentId).
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
        var session = new AgentSession(config,
            toolRegistry,
            permissions,
            eventSink,
            providerState,
            existingSessionId);

        session._messages.AddRange(projection.Messages);
        session._sessionStarted = true;

        if (restoreProviderState && projection.ProviderStates.Count > 0)
        {
            foreach ((ProviderStateKey key, ProviderTurnState state) in projection.ProviderStates)
            {
                // Re-key to the hydrated session's AgentId so RunLoopAsync can find it
                ProviderTurnState rekeyed = state with
                {
                    Key = new ProviderStateKey(session.Id,
                        session.AgentId,
                        key.ProviderName,
                        key.ModelId,
                        key.ApiType)
                };
                providerState.Set(rekeyed, "session_hydration");
            }
        }

        return session;
    }

    /// <summary>
    ///     Add a user message and stream the LLM response.
    ///     Emits SessionStartedEvent once per session lifetime,
    ///     then TurnStartedEvent for this prompt.
    /// </summary>
    public async IAsyncEnumerable<OmicronEvent> PromptAsync(
        string text,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // SessionStartedEvent fires only once per session lifecycle
        if (!_sessionStarted)
        {
            _sessionStarted = true;

            yield return Emit(new SessionStartedEvent(_writer.Envelope(), AgentId, Model.Id, Model.ProviderName));
        }

        // Add user message
        _messages.Add(Message.UserMessage(Utf8String.FromString(text)));
        yield return Emit(new UserMessageEvent(_writer.Envelope(), Utf8String.FromString(text)));

        // TurnStartedEvent marks the start of this model turn
        yield return Emit(new TurnStartedEvent(_writer.Envelope(), text));

        await foreach (OmicronEvent evt in RunLoopAsync(ct))
        {
            yield return evt;
        }
    }

    /// <summary>
    ///     Continue the conversation from the current transcript.
    ///     Requires that PromptAsync was called at least once and that the
    ///     conversation has not been reset to empty.
    /// </summary>
    public async IAsyncEnumerable<OmicronEvent> ContinueAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_sessionStarted || _messages.Count == 0)
        {
            throw new InvalidOperationException("No conversation to continue. Call PromptAsync first.");
        }

        yield return Emit(new TurnStartedEvent(_writer.Envelope(), "(continuation)"));

        await foreach (OmicronEvent evt in RunLoopAsync(ct))
        {
            yield return evt;
        }
    }

    /// <summary>
    ///     Reset the conversation (clear all messages and provider state).
    ///     Uses same-session policy: SessionStartedEvent is NOT re-emitted on next prompt.
    ///     SessionResetEvent marks the clearing point for replay.
    /// </summary>
    public void Reset()
    {
        _messages.Clear();
        _usedToolCallIds.Clear();
        _providerState.ClearSession(Id);
        Emit(new SessionResetEvent(_writer.Envelope()));
    }

    /// <summary>
    ///     Resolve any dangling tool calls — assistant messages with ToolCalls
    ///     that have no corresponding ToolResult message after them.
    ///     Injects dummy error tool results so the conversation history remains valid
    ///     for the next API call.
    /// </summary>
    public void ResolvePendingToolCalls()
    {
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            if (
                _messages[i]
                is not { Role: MessageRole.Assistant, ToolCalls: { Count: > 0 } toolCalls }
            )
            {
                continue;
            }

            bool allResolved = toolCalls.All(tc =>
                _messages
                    .Skip(i + 1)
                    .Any(m =>
                        m is { Role: MessageRole.ToolResult, ToolCallId: { } id } && id == tc.Id));

            if (!allResolved)
            {
                foreach (ToolCallContent tc in toolCalls)
                {
                    _messages.Insert(i + 1,
                        Message.ToolResultMessage(tc.Id,
                            tc.Name,
                            Utf8String.FromTrustedUtf8Literal("(cancelled)"u8),
                            true));
                }
            }
        }
    }

    // ================================================================
    // Core agent loop
    // ================================================================

    private async IAsyncEnumerable<OmicronEvent> RunLoopAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        IChatProvider provider =
            Model.Provider ?? throw new InvalidOperationException("Model has no provider set.");

        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            // Build provider state context for stateful API support
            var providerStateKey = ProviderStateKey.Create(Id,
                AgentId,
                Model.ProviderName,
                Model.Id,
                Model.ApiType);
            ProviderTurnState? currentProviderState = _providerState.Get(providerStateKey);

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
            List<Tool>? tools =
                _toolRegistry.AllTools.Count > 0
                    ? _toolRegistry
                        .AllTools.Select(t => new Tool
                        {
                            Name = t.Name,
                            Description = t.Description,
                            Parameters = t.Parameters
                        })
                        .ToList()
                    : null;

            using var responseText = new Utf8TextAccumulator();
            using var reasoningText = new Utf8TextAccumulator();
            var toolCalls = new List<ToolCallContent>();
            StopReason stopReason = StopReason.Stop;
            string? errorMessage = null;
            UsageInfo? usage = null;
            string? responseId = null;

            // Stream response
            await foreach (
                StreamEvent evt in provider.StreamAsync(Model,
                    _messages,
                    SystemPrompt,
                    tools?.Count > 0 ? tools : null,
                    options)
            )
            {
                switch (evt.Type)
                {
                    case StreamEventType.TextDelta:
                        responseText.Append(evt.Delta);
                        if (evt.ReasoningText is not null)
                        {
                            reasoningText.Append(evt.ReasoningText);
                        }

                        yield return Emit(new AssistantTextDeltaEvent(_writer.Envelope(),
                            Utf8String.FromString(evt.Delta ?? string.Empty),
                            evt.ReasoningText is not null
                                ? Utf8String.FromString(evt.ReasoningText)
                                : null));
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
                        bool supportsStateful = CompatibilityDetector.SupportsStatefulContinuation(Model.ApiType,
                            Model.StoragePolicy);
                        ProviderCompatibility compat = Model.GetEffectiveCompatibility();
                        if (
                            supportsStateful
                            && compat.SupportsPreviousResponseId
                            && responseId is not null
                        )
                        {
                            var newState = new ProviderTurnState(providerStateKey,
                                responseId,
                                null,
                                null,
                                null)
                            {
                                StoragePolicy = Model.StoragePolicy
                            };
                            _providerState.Set(newState, "response_completed");
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
                string errMsg = errorMessage ?? "Unknown error";
                yield return Emit(new SessionErrorEvent(_writer.Envelope(), errMsg, "provider_error"));
                _messages.Add(new Message
                {
                    Role = MessageRole.Assistant,
                    TextData = Utf8String.FromString($"[Error: {errMsg}]"),
                    Timestamp = DateTime.UtcNow
                });
                yield break;
            }

            Utf8String? fullTextData =
                responseText.Length > 0
                    ? Utf8String.FromBuffer(responseText.ToOwnedBuffer())
                    : null;
            Utf8String? fullReasoningData =
                reasoningText.Length > 0
                    ? Utf8String.FromBuffer(reasoningText.ToOwnedBuffer())
                    : null;
            if (fullReasoningData is null && _messages.Count > 0)
            {
                Message? lastAssistant = _messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
                if (lastAssistant?.ReasoningData is not null)
                {
                    fullReasoningData = Utf8String.Empty;
                }
            }

            // Handle tool calls
            if (toolCalls.Count > 0)
            {
                var normalizedToolCalls = toolCalls
                    .Select(tc => tc with
                    {
                        Id = ReserveUniqueToolCallId(tc.Id)
                    })
                    .ToList();

                _messages.Add(new Message
                {
                    Role = MessageRole.Assistant,
                    TextData = fullTextData,
                    ReasoningData = fullReasoningData,
                    ToolCalls = [.. normalizedToolCalls],
                    Timestamp = DateTime.UtcNow
                });

                // Emit an AssistantResponseCompleteEvent to mark the tool-call turn boundary.
                // This gives the session projector an unambiguous signal to flush the
                // tool-call batch before subsequent ToolInvocationStarted events arrive.
                yield return Emit(new AssistantResponseCompleteEvent(_writer.Envelope(),
                    fullTextData ?? Utf8String.Empty,
                    fullReasoningData,
                    TokenUsage.From(usage)));

                foreach (ToolCallContent toolCall in normalizedToolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    var toolCallId = new ToolCallId(toolCall.Id);

                    yield return Emit(new ToolInvocationStartedEvent(_writer.Envelope(),
                        toolCallId,
                        toolCall.Name,
                        toolCall.Arguments ?? new Dictionary<string, object?>()));

                    ToolDefinition? toolDef = _toolRegistry.GetTool(toolCall.Name);
                    Utf8String? resultUtf8 = null;
                    bool isError = false;
                    List<IContentBlock>? resultBlocks = null;

                    if (toolDef is null)
                    {
                        resultUtf8 = MakeToolErrorUtf8("Tool '"u8, toolCall.Name, "' not found."u8);
                        isError = true;
                    }
                    else
                    {
                        // Check permission
                        var permRequest = new PermissionRequest("tool.execute",
                            toolCall.Name,
                            $"Execute tool '{toolCall.Name}'");
                        PermissionDecision permResult = await _permissions.RequestAsync(permRequest, ct);

                        // Emit permission event
                        yield return Emit(new PermissionRequestedEvent(_writer.Envelope(),
                            permRequest.Action,
                            permResult.Allowed));

                        if (!permResult.Allowed)
                        {
                            resultUtf8 = MakeToolErrorUtf8(
                                "Permission denied for tool '"u8, toolCall.Name, "': "u8, permResult.Reason ?? string.Empty);
                            isError = true;
                        }
                        else
                        {
                            try
                            {
                                // Build model metadata from session config for tool context
                                var modelModalities = new HashSet<string>
                                {
                                    "text"
                                };
                                if (_config.Model.SupportsImages)
                                {
                                    modelModalities.Add("image");
                                }

                                var toolModelMeta = new ModelMetadata(_config.Model.Id,
                                    _config.Model.ProviderName,
                                    SupportsVision: _config.Model.SupportsImages ? true : null,
                                    Modalities: modelModalities);

                                ToolResult invokeResult = await toolDef.InvokeAsync(new ToolInvocationContext(
                                    toolCallId,
                                    toolCall.Arguments ?? new Dictionary<string, object?>(),
                                    Id,
                                    AgentId,
                                    ct,
                                    toolModelMeta));
                                resultUtf8 = invokeResult.TextData;
                                isError = invokeResult.IsError;
                                resultBlocks = invokeResult.Blocks;

                                // Multimodal bridge: if read_path returned base64 content,
                                // inject a user message with actual image/audio/video/PDF content
                                // so provider shapes can serialize it as image_url/input_image.
                                if (!isError && toolCall.Name == "read_path" && resultUtf8 is not null)
                                {
                                    IReadOnlyList<ImageContent> bridgeImages =
                                        TryExtractBase64ContentUtf8(resultUtf8.Utf8Span);
                                    if (bridgeImages.Count > 0)
                                    {
                                        _messages.Add(Message.UserMessage(
                                            Utf8String.FromTrustedUtf8Literal(
                                                "read_path returned base64 content for the tool result below"u8),
                                            bridgeImages));
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                resultUtf8 = MakeToolErrorUtf8(
                                    "executing tool '"u8, toolCall.Name, "': "u8, ex.Message);
                                isError = true;
                            }
                        }
                    }

                    _messages.Add(Message.ToolResultMessage(toolCall.Id,
                        toolCall.Name,
                        resultUtf8 ?? Utf8String.Empty,
                        isError));

                    yield return Emit(new ToolInvocationCompletedEvent(_writer.Envelope(),
                        toolCallId,
                        toolCall.Name,
                        resultUtf8 ?? Utf8String.Empty,
                        isError,
                        resultBlocks));
                }

                continue;
            }

            // No tool calls — final response
            _messages.Add(new Message
            {
                Role = MessageRole.Assistant,
                TextData = fullTextData,
                ReasoningData = fullReasoningData,
                Timestamp = DateTime.UtcNow
            });

            yield return Emit(new AssistantResponseCompleteEvent(_writer.Envelope(),
                fullTextData ?? Utf8String.Empty,
                fullReasoningData,
                TokenUsage.From(usage)));

            yield break;
        }

        yield return Emit(new SessionErrorEvent(_writer.Envelope(),
            $"Agent reached maximum iteration limit ({MaxIterations}).",
            "max_iterations"));
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static Utf8String MakeToolErrorUtf8(
        ReadOnlySpan<byte> prefix, string value, ReadOnlySpan<byte> suffix)
    {
        using var b = Utf8Text.CreateBuilder();
        b.AppendLiteral("Error: "u8);
        b.AppendLiteral(prefix);
        b.Append(value);
        b.AppendLiteral(suffix);
        return Utf8String.FromUtf8(b.AsSpan());
    }

    private static Utf8String MakeToolErrorUtf8(
        ReadOnlySpan<byte> prefix, string value1, ReadOnlySpan<byte> separator, string value2)
    {
        using var b = Utf8Text.CreateBuilder();
        b.AppendLiteral("Error: "u8);
        b.AppendLiteral(prefix);
        b.Append(value1);
        b.AppendLiteral(separator);
        b.Append(value2);
        return Utf8String.FromUtf8(b.AsSpan());
    }

    private string ReserveUniqueToolCallId(string? providerId)
    {
        string? baseId = string.IsNullOrWhiteSpace(providerId)
            ? $"call_{Guid.NewGuid():N}"[..17]
            : providerId;

        if (_usedToolCallIds.Add(baseId))
        {
            return baseId;
        }

        for (int i = 2;; i++)
        {
            string candidate = $"{baseId}_{i}";
            if (_usedToolCallIds.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private OmicronEvent Emit(OmicronEvent evt)
    {
        return _writer.Emit(evt);
    }

    /// <summary>
    ///     Extract base64-encoded content from a read_path tool result.
    ///     Looks for lines starting with "Data: " and extracts the base64 payload
    ///     along with the MIME type from the header line.
    ///     Returns an empty list if no base64 content is found.
    /// </summary>
    private static IReadOnlyList<ImageContent> TryExtractBase64Content(string resultText)
    {
        string? mimeType = null;
        string? base64Data = null;

        // Iterate lines via StringReader to avoid allocating a string array.
        using var reader = new StringReader(resultText);
        while (mimeType is null || base64Data is null)
        {
            string? line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            if (mimeType is null && line.Contains("[FILE]") && !line.Contains("Data:"))
            {
                int mimeStart = line.LastIndexOf('(');
                int mimeEnd = line.LastIndexOf(')');
                if (mimeStart >= 0 && mimeEnd > mimeStart)
                {
                    string inner = line.Substring(mimeStart + 1, mimeEnd - mimeStart - 1);
                    int comma = inner.LastIndexOf(',');
                    string candidate = comma >= 0 ? inner.Substring(comma + 1).Trim() : inner.Trim();
                    if (candidate.Contains('/'))
                    {
                        mimeType = candidate;
                    }
                }
            }

            if (base64Data is null && line.StartsWith("Data: "))
            {
                base64Data = line.Substring(6).Trim();
            }
        }

        if (base64Data is not null && mimeType is not null)
        {
            return new[]
            {
                new ImageContent(base64Data, mimeType)
            };
        }

        return Array.Empty<ImageContent>();
    }

    /// <summary>
    ///     UTF-8 byte variant of <see cref="TryExtractBase64Content" />.
    ///     Scans raw bytes for <c>[FILE]</c> and <c>Data: </c> markers
    ///     without decoding the entire buffer to a string.
    /// </summary>
    private static IReadOnlyList<ImageContent> TryExtractBase64ContentUtf8(ReadOnlySpan<byte> utf8Bytes)
    {
        string? mimeType = null;
        string? base64Data = null;
        int index = 0;

        while (
            (mimeType is null || base64Data is null)
            && TextLineSplitter.TryReadNextLine(utf8Bytes,
                ref index,
                out ReadOnlySpan<byte> line)
        )
        {
            if (mimeType is null)
            {
                // Look for "[FILE]" without "Data:" in the ASCII range
                bool hasFileMarker = false;
                bool hasDataMarker = false;
                int parenStart = -1,
                    parenEnd = -1;
                for (int i = 0; i < line.Length; i++)
                {
                    // Check for [FILE] (4+5 = 9 byte match from current position)
                    if (
                        !hasFileMarker
                        && i + 5 < line.Length
                        && line[i] == (byte)'['
                        && line[i + 1] == (byte)'F'
                        && line[i + 2] == (byte)'I'
                        && line[i + 3] == (byte)'L'
                        && line[i + 4] == (byte)'E'
                        && line[i + 5] == (byte)']'
                    )
                    {
                        hasFileMarker = true;
                    }

                    if (
                        !hasDataMarker
                        && i + 5 < line.Length
                        && line[i] == (byte)'D'
                        && line[i + 1] == (byte)'a'
                        && line[i + 2] == (byte)'t'
                        && line[i + 3] == (byte)'a'
                        && line[i + 4] == (byte)':'
                    )
                    {
                        hasDataMarker = true;
                    }

                    if (line[i] == (byte)'(')
                    {
                        parenStart = i;
                    }

                    if (line[i] == (byte)')')
                    {
                        parenEnd = i;
                    }
                }

                if (hasFileMarker && !hasDataMarker && parenStart >= 0 && parenEnd > parenStart)
                {
                    string inner = Encoding.UTF8.GetString(line.Slice(parenStart + 1, parenEnd - parenStart - 1));
                    int comma = inner.LastIndexOf(',');
                    string candidate = comma >= 0 ? inner.Substring(comma + 1).Trim() : inner.Trim();
                    if (candidate.Contains('/'))
                    {
                        mimeType = candidate;
                    }
                }
            }

            if (
                base64Data is null
                && line.Length >= 6
                && line[0] == (byte)'D'
                && line[1] == (byte)'a'
                && line[2] == (byte)'t'
                && line[3] == (byte)'a'
                && line[4] == (byte)' '
                && line[5] == (byte)':'
            )
            {
                ReadOnlySpan<byte> valueSlice = line.Slice(6);
                base64Data = Encoding.UTF8.GetString(valueSlice).Trim();
            }
        }

        if (base64Data is not null && mimeType is not null)
        {
            return new[]
            {
                new ImageContent(base64Data, mimeType)
            };
        }

        return Array.Empty<ImageContent>();
    }
}
