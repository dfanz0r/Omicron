using System.Text.Json;
using Omicron.Core.Providers;
using Omicron.Core.Sessions;

namespace Omicron.Core.Events;

/// <summary>
/// Base record for all system events.
/// Each concrete event carries an EventEnvelope with shared metadata
/// (Id, Sequence, Timestamp, SessionId) and a type-specific payload.
/// </summary>
public abstract record OmicronEvent(EventEnvelope Envelope)
{
    public EventId Id => Envelope.Id;
    public long Sequence => Envelope.Sequence;
    public DateTimeOffset Timestamp => Envelope.Timestamp;
    public SessionId SessionId => Envelope.SessionId;
}

// ============================================================
// Session events
// ============================================================

public sealed record SessionStartedEvent(
    EventEnvelope Envelope,
    AgentId AgentId,
    string ModelId,
    string ProviderName) : OmicronEvent(Envelope);

public sealed record SessionEndedEvent(
    EventEnvelope Envelope,
    string? Reason) : OmicronEvent(Envelope);

public sealed record SessionResetEvent(
    EventEnvelope Envelope) : OmicronEvent(Envelope);

public sealed record TurnStartedEvent(
    EventEnvelope Envelope,
    string UserText) : OmicronEvent(Envelope);

public sealed record SessionErrorEvent(
    EventEnvelope Envelope,
    string Message,
    string? Code) : OmicronEvent(Envelope);

// ============================================================
// User message events
// ============================================================

public sealed record UserMessageEvent(
    EventEnvelope Envelope,
    string Text) : OmicronEvent(Envelope);

// ============================================================
// Assistant stream events
// ============================================================

public sealed record AssistantTextDeltaEvent(
    EventEnvelope Envelope,
    string Delta,
    string? ReasoningDelta) : OmicronEvent(Envelope);

public sealed record AssistantResponseCompleteEvent(
    EventEnvelope Envelope,
    string FullText,
    string? ReasoningText,
    TokenUsage Usage) : OmicronEvent(Envelope);

// ============================================================
// Tool invocation events
// ============================================================

public sealed record ToolInvocationStartedEvent(
    EventEnvelope Envelope,
    ToolCallId ToolCallId,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments) : OmicronEvent(Envelope);

public sealed record ToolInvocationCompletedEvent(
    EventEnvelope Envelope,
    ToolCallId ToolCallId,
    string ToolName,
    string Result,
    bool IsError) : OmicronEvent(Envelope);

// ============================================================
// Permission events
// ============================================================

public sealed record PermissionRequestedEvent(
    EventEnvelope Envelope,
    string Action,
    bool Allowed) : OmicronEvent(Envelope);

// ============================================================
// Execution events
// ============================================================

public sealed record ExecutionStartedEvent(
    EventEnvelope Envelope,
    string Command,
    string WorkingDirectory,
    ToolCallId? ToolCallId = null) : OmicronEvent(Envelope);

public sealed record ExecutionCompletedEvent(
    EventEnvelope Envelope,
    string Command,
    int ExitCode,
    long DurationMs,
    bool TimedOut,
    ToolCallId? ToolCallId = null,
    bool Cancelled = false,
    string? Error = null) : OmicronEvent(Envelope);

// ============================================================
// Provider state events
// ============================================================

public sealed record ProviderStateUpdatedEvent(
    EventEnvelope Envelope,
    ProviderStateSnapshot State,
    string? Reason = null) : OmicronEvent(Envelope);

public sealed record ProviderStateClearedEvent(
    EventEnvelope Envelope,
    ProviderStateKey Key,
    string? Reason = null) : OmicronEvent(Envelope);
