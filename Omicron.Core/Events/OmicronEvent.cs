using Omicron.Core.Sessions;

namespace Omicron.Core.Events;

/// <summary>
/// Base record for all system events.
/// Designed to be durable-shaped from the start, with an ID, sequence number,
/// timestamp, and session scoping — even before persistence is implemented.
/// </summary>
public abstract record OmicronEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId);

// ============================================================
// Session events
// ============================================================

public sealed record SessionStartedEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId,
    AgentId AgentId,
    string ModelId,
    string ProviderName) : OmicronEvent(Id, Sequence, Timestamp, SessionId);

public sealed record SessionEndedEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId,
    string? Reason) : OmicronEvent(Id, Sequence, Timestamp, SessionId);

public sealed record SessionResetEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId) : OmicronEvent(Id, Sequence, Timestamp, SessionId);

/// <summary>
/// Emitted at the start of each user turn / model prompt.
/// Unlike SessionStartedEvent (which fires once per session lifetime),
/// TurnStartedEvent fires for every user message that triggers a model response.
/// </summary>
public sealed record TurnStartedEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId,
    string UserText) : OmicronEvent(Id, Sequence, Timestamp, SessionId);

/// <summary>
/// Emitted when a session encounters a non-recoverable error
/// (provider failure, max iteration limit, etc.)
/// </summary>
public sealed record SessionErrorEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId,
    string Message,
    string? Code) : OmicronEvent(Id, Sequence, Timestamp, SessionId);

// ============================================================
// User message events
// ============================================================

public sealed record UserMessageEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId,
    string Text) : OmicronEvent(Id, Sequence, Timestamp, SessionId);

// ============================================================
// Assistant stream events
// ============================================================

public sealed record AssistantTextDeltaEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId,
    string Delta,
    string? ReasoningDelta) : OmicronEvent(Id, Sequence, Timestamp, SessionId);

public sealed record AssistantResponseCompleteEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId,
    string FullText,
    string? ReasoningText,
    int InputTokens,
    int OutputTokens) : OmicronEvent(Id, Sequence, Timestamp, SessionId);

// ============================================================
// Tool invocation events
// ============================================================

public sealed record ToolInvocationStartedEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId,
    ToolCallId ToolCallId,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments) : OmicronEvent(Id, Sequence, Timestamp, SessionId);

public sealed record ToolInvocationCompletedEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId,
    ToolCallId ToolCallId,
    string ToolName,
    string Result,
    bool IsError) : OmicronEvent(Id, Sequence, Timestamp, SessionId);

// ============================================================
// Permission events
// ============================================================

public sealed record PermissionRequestedEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId,
    string Action,
    bool Allowed) : OmicronEvent(Id, Sequence, Timestamp, SessionId);

// ============================================================
// Execution events
// ============================================================

public sealed record ExecutionStartedEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId,
    string Command,
    string WorkingDirectory,
    ToolCallId? ToolCallId = null) : OmicronEvent(Id, Sequence, Timestamp, SessionId);

public sealed record ExecutionCompletedEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId,
    string Command,
    int ExitCode,
    long DurationMs,
    bool TimedOut,
    ToolCallId? ToolCallId = null,
    bool Cancelled = false,
    string? Error = null) : OmicronEvent(Id, Sequence, Timestamp, SessionId);

// ============================================================
// Provider state events
// ============================================================

public sealed record ProviderStateUpdatedEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId,
    ProviderStateKey Key,
    string? PreviousResponseId,
    string? ConversationId,
    string? Reason = null) : OmicronEvent(Id, Sequence, Timestamp, SessionId);

public sealed record ProviderStateClearedEvent(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId,
    ProviderStateKey Key,
    string? Reason = null) : OmicronEvent(Id, Sequence, Timestamp, SessionId);
