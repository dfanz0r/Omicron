using Omicron.Core.Content;
using Omicron.Core.Sessions;
using Omicron.Core.Workspace;

namespace Omicron.Core.Events;

/// <summary>
///     Base record for all system events.
///     Each concrete event carries an EventEnvelope with shared metadata
///     (Id, Sequence, Timestamp, SessionId) and a type-specific payload.
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

public sealed record SessionEndedEvent(EventEnvelope Envelope, string? Reason)
    : OmicronEvent(Envelope);

public sealed record SessionResetEvent(EventEnvelope Envelope) : OmicronEvent(Envelope);

public sealed record TurnStartedEvent(EventEnvelope Envelope, string UserText)
    : OmicronEvent(Envelope);

public sealed record SessionErrorEvent(EventEnvelope Envelope, string Message, string? Code)
    : OmicronEvent(Envelope);

// ============================================================
// User message events
// ============================================================

public sealed record UserMessageEvent(EventEnvelope Envelope, Utf8String Text)
    : OmicronEvent(Envelope);

// ============================================================
// Assistant stream events
// ============================================================

public sealed record AssistantTextDeltaEvent(
    EventEnvelope Envelope,
    Utf8String Delta,
    Utf8String? ReasoningDelta) : OmicronEvent(Envelope);

public sealed record AssistantResponseCompleteEvent(
    EventEnvelope Envelope,
    Utf8String FullText,
    Utf8String? ReasoningText,
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
    Utf8String Result,
    bool IsError,
    List<IContentBlock>? Blocks = null) : OmicronEvent(Envelope);

// ============================================================
// Permission events
// ============================================================

public sealed record PermissionRequestedEvent(EventEnvelope Envelope, string Action, bool Allowed)
    : OmicronEvent(Envelope);

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

// ============================================================
// Transaction lifecycle events
// ============================================================

public sealed record TransactionStartedEvent(
    EventEnvelope Envelope,
    WorkspaceTransactionId TransactionId) : OmicronEvent(Envelope);

public sealed record TransactionStagedEvent(
    EventEnvelope Envelope,
    WorkspaceTransactionId TransactionId,
    string Path,
    string Action) : OmicronEvent(Envelope);

public sealed record TransactionCommittedEvent(
    EventEnvelope Envelope,
    WorkspaceTransactionId TransactionId) : OmicronEvent(Envelope);

public sealed record TransactionRolledBackEvent(
    EventEnvelope Envelope,
    WorkspaceTransactionId TransactionId) : OmicronEvent(Envelope);
