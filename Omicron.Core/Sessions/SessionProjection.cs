using System.Text;
using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Providers;

namespace Omicron.Core.Sessions;

/// <summary>
/// Reconstructed session state from a replay of OmicronEvents.
/// Used for session resume, debug inspection, and UI reconstruction.
/// </summary>
public sealed record SessionProjection
{
    /// <summary>Session identity.</summary>
    public SessionId SessionId { get; init; }

    /// <summary>Reconstructed conversation messages.</summary>
    public IReadOnlyList<Message> Messages { get; init; } = Array.Empty<Message>();

    /// <summary>Reconstructed provider turn states.</summary>
    public IReadOnlyDictionary<ProviderStateKey, ProviderTurnState> ProviderStates { get; init; }
        = new Dictionary<ProviderStateKey, ProviderTurnState>();

    /// <summary>Whether the session was reset at any point.</summary>
    public bool IsReset { get; init; }

    /// <summary>Errors encountered during the session.</summary>
    public IReadOnlyList<SessionErrorEvent> Errors { get; init; } = Array.Empty<SessionErrorEvent>();

    /// <summary>Total token usage (input + output) across all responses.</summary>
    public (int InputTokens, int OutputTokens) TotalUsage { get; init; }
}

/// <summary>
/// Projects an ordered sequence of OmicronEvents into a SessionProjection,
/// reconstructing messages, provider state, and error state.
/// Events should be in sequence order (e.g., from ISessionStore.ReadEventsAsync).
/// </summary>
public interface ISessionProjector
{
    /// <summary>Project an ordered event sequence into session state.</summary>
    SessionProjection Project(IEnumerable<OmicronEvent> events);
}

/// <summary>
/// Default implementation of ISessionProjector.
///
/// Handles real AgentSession tool-call patterns correctly:
/// - Accumulates text/reasoning from AssistantTextDeltaEvent.
/// - Queues tool calls from ToolInvocationStartedEvent.
/// - Buffers tool results from ToolInvocationCompletedEvent.
/// - Flushes the queued assistant message (with all tool calls from the batch)
///   and all buffered tool results when the tool-call round ends (detected by
///   a non-tool event or a new tool start after completion).
/// - Uses contributing event timestamps for message reconstruction.
/// </summary>
public sealed class SessionProjector : ISessionProjector
{
    public SessionProjection Project(IEnumerable<OmicronEvent> events)
    {
        SessionId? sessionId = null;
        var messages = new List<Message>();
        var providerStates = new Dictionary<ProviderStateKey, ProviderTurnState>();
        var errors = new List<SessionErrorEvent>();
        var isReset = false;
        var inputTokens = 0;
        var outputTokens = 0;
        DateTimeOffset? lastTimestamp = null;

        // Accumulator state for assistant text deltas
        var accumText = new StringBuilder();
        var accumReasoning = new StringBuilder();

        // Tool-call run tracking
        var pendingToolCalls = new List<ToolCallContent>();
        var bufferedToolResults = new List<Message>();
        int toolStartCount = 0;
        int toolEndCount = 0;

        void FlushAssistantText()
        {
            if (accumText.Length > 0 || accumReasoning.Length > 0)
            {
                messages.Add(new Message
                {
                    Role = MessageRole.Assistant,
                    Text = accumText.ToString(),
                    Reasoning = accumReasoning.Length > 0 ? accumReasoning.ToString() : null,
                    Timestamp = lastTimestamp?.DateTime ?? DateTime.UtcNow
                });
                accumText.Clear();
                accumReasoning.Clear();
            }
        }

        void FlushToolCallRun()
        {
            if (pendingToolCalls.Count == 0) return;

            // Emit the assistant message with all tool calls from this round
            messages.Add(new Message
            {
                Role = MessageRole.Assistant,
                Text = accumText.ToString(),
                Reasoning = accumReasoning.Length > 0 ? accumReasoning.ToString() : null,
                ToolCalls = [.. pendingToolCalls],
                ToolCall = pendingToolCalls.Count == 1 ? pendingToolCalls[0] : null,
                Timestamp = lastTimestamp?.DateTime ?? DateTime.UtcNow
            });
            accumText.Clear();
            accumReasoning.Clear();
            pendingToolCalls.Clear();

            // Emit all buffered tool results
            foreach (var tr in bufferedToolResults)
                messages.Add(tr);
            bufferedToolResults.Clear();

            toolStartCount = 0;
            toolEndCount = 0;
        }

        void HandleEvent(OmicronEvent evt)
        {
            lastTimestamp = evt.Timestamp;
            sessionId ??= evt.SessionId;

            switch (evt)
            {
                case SessionStartedEvent sse:
                    sessionId = sse.SessionId;
                    break;

                case SessionResetEvent:
                    isReset = true;
                    messages.Clear();
                    providerStates.Clear();
                    errors.Clear();
                    inputTokens = 0;
                    outputTokens = 0;
                    accumText.Clear();
                    accumReasoning.Clear();
                    pendingToolCalls.Clear();
                    bufferedToolResults.Clear();
                    toolStartCount = 0;
                    toolEndCount = 0;
                    break;

                case SessionErrorEvent err:
                    errors.Add(err);
                    FlushToolCallRun();
                    FlushAssistantText();
                    messages.Add(new Message
                    {
                        Role = MessageRole.Assistant,
                        Text = $"[Error: {err.Message}]",
                        Timestamp = err.Timestamp.DateTime
                    });
                    break;

                case UserMessageEvent ue:
                    FlushToolCallRun();
                    FlushAssistantText();
                    messages.Add(new Message
                    {
                        Role = MessageRole.User,
                        Text = ue.Text,
                        Timestamp = lastTimestamp?.DateTime ?? ue.Timestamp.DateTime
                    });
                    break;

                case AssistantTextDeltaEvent atd:
                    // A delta after a tool-call round means the next iteration started
                    if (toolStartCount > 0 && toolStartCount == toolEndCount)
                        FlushToolCallRun();
                    accumText.Append(atd.Delta);
                    if (atd.ReasoningDelta is not null)
                        accumReasoning.Append(atd.ReasoningDelta);
                    break;

                case AssistantResponseCompleteEvent arc:
                    inputTokens += arc.Usage.InputTokens;
                    outputTokens += arc.Usage.OutputTokens;
                    // End any active tool-call round
                    FlushToolCallRun();
                    // Accumulate the full text, discarding delta previews.
                    // Don't flush immediately — if tool calls follow (tool-call turn),
                    // the text will be included in the next FlushToolCallRun() combined
                    // with the tool calls. If no tool calls follow, FlushAssistantText()
                    // in the next non-tool handler or final flush will emit it.
                    accumText.Clear();
                    accumReasoning.Clear();
                    accumText.Append(arc.FullText);
                    if (arc.ReasoningText is not null)
                        accumReasoning.Append(arc.ReasoningText);
                    break;

                case ToolInvocationStartedEvent tis:
                    // Don't flush here — AgentSession emits interleaved Start/End pairs
                    // for the same batch. Flushing on a new Start after a completed End
                    // would break multi-tool batches. Instead, flush happens on the
                    // next non-tool event (AssistantTextDelta, ARC, UserMessage).
                    toolStartCount++;
                    pendingToolCalls.Add(new ToolCallContent(
                        tis.ToolCallId.Value,
                        tis.ToolName,
                        tis.Arguments is { Count: > 0 }
                            ? new Dictionary<string, object?>(tis.Arguments)
                            : new Dictionary<string, object?>()));
                    break;

                case ToolInvocationCompletedEvent tic:
                    toolEndCount++;
                    bufferedToolResults.Add(new Message
                    {
                        Role = MessageRole.ToolResult,
                        Text = tic.Result,
                        ToolCallId = tic.ToolCallId.Value,
                        ToolName = tic.ToolName,
                        IsError = tic.IsError,
                        Timestamp = lastTimestamp?.DateTime ?? tic.Timestamp.DateTime
                    });
                    // Don't flush here — AgentSession emits interleaved:
                    //   Start(1), End(1), Start(2), End(2)
                    // Flushing on first End would break the batch.
                    // Instead, flush happens on the next non-tool event
                    // (AssistantTextDelta, AssistantResponseComplete, UserMessage)
                    // or when a new tool start arrives after a completed batch.
                    break;

                case ProviderStateUpdatedEvent psu:
                    {
                        var state = new ProviderTurnState(
                            psu.State.Key,
                            psu.State.PreviousResponseId,
                            psu.State.ConversationId,
                            psu.State.SessionAffinityKey,
                            psu.State.ProviderMetadata)
                        {
                            StoragePolicy = psu.State.StoragePolicy
                        };
                        providerStates[psu.State.Key] = state;
                        break;
                    }

                case ProviderStateClearedEvent psc:
                    providerStates.Remove(psc.Key);
                    break;
            }
        }

        foreach (var evt in events)
            HandleEvent(evt);

        // Final flush
        FlushToolCallRun();
        FlushAssistantText();

        return new SessionProjection
        {
            SessionId = sessionId ?? default,
            Messages = messages,
            ProviderStates = providerStates,
            IsReset = isReset,
            Errors = errors,
            TotalUsage = (inputTokens, outputTokens)
        };
    }
}


