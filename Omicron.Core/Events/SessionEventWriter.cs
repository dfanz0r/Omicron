namespace Omicron.Core.Events;

/// <summary>
/// Lightweight event writer scoped to a session.
/// Delegates to the shared IEventSink for sequence stamping.
/// Consumers still supply EventId.New() and DateTimeOffset.UtcNow
/// for each event; the sink stamps only the sequence number.
///
/// Helper factory methods (NewEventId, Now) are provided for
/// callers that do not need custom IDs/timestamps.
/// </summary>
public sealed class SessionEventWriter
{
    private readonly IEventSink _sink;

    /// <summary>Session identity.</summary>
    public SessionId SessionId { get; }

    /// <summary>Agent identity.</summary>
    public AgentId AgentId { get; }

    public SessionEventWriter(IEventSink sink, SessionId sessionId, AgentId agentId)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        SessionId = sessionId;
        AgentId = agentId;
    }

    /// <summary>
    /// Emit an event. The sink stamps the global sequence number;
    /// producers pass sequence=0. Returns the stamped copy.
    /// </summary>
    public T Emit<T>(T evt) where T : OmicronEvent
        => (T)_sink.Emit(evt);

    /// <summary>Shortcut for EventId.New().</summary>
    public static EventId NewEventId() => EventId.New();

    /// <summary>Shortcut for DateTimeOffset.UtcNow.</summary>
    public static DateTimeOffset Now() => DateTimeOffset.UtcNow;
}
