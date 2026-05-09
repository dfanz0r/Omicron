namespace Omicron.Core.Events;

/// <summary>
/// Lightweight event writer scoped to a session.
/// Delegates to the shared IEventSink for sequence stamping.
/// Provides a convenience Envelope() factory so producers can write:
///
///   yield return Emit(new UserMessageEvent(_writer.Envelope(), text));
///
/// instead of repeating:
///
///   new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, Id)
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
    /// Emit an event. The sink stamps the global sequence number
    /// (replacing Envelope.Sequence = 0 with the stamped value).
    /// Returns the stamped copy.
    /// </summary>
    public T Emit<T>(T evt) where T : OmicronEvent
        => (T)_sink.Emit(evt);

    /// <summary>
    /// Create a standard EventEnvelope for this session.
    /// Producers should use this when constructing events:
    ///
    ///   _writer.Emit(new UserMessageEvent(_writer.Envelope(), text));
    ///
    /// The sink stamps the sequence number; pass 0.
    /// </summary>
    public EventEnvelope Envelope()
        => new(EventId.New(), 0, DateTimeOffset.UtcNow, SessionId);
}
