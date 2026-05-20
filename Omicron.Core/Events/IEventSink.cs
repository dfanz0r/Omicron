using Omicron.Core.Content;

namespace Omicron.Core.Events;

/// <summary>
///     Accepts events for dispatch, storage, or replay. The initial
///     InMemoryEventSink is sufficient for MVP; a persistent implementation
///     can be swapped in later without changing event producers.
///     The sink stamps a global monotonic sequence number on every event.
///     Producers should pass Envelope.Sequence = 0; the sink overwrites it
///     via stamped = evt with { Envelope = evt.Envelope with { Sequence = seq } }.
/// </summary>
public interface IEventSink
{
    /// <summary>
    ///     Emit a single event. Returns the event with a sequence number stamped by the sink.
    /// </summary>
    OmicronEvent Emit(OmicronEvent evt);

    /// <summary>
    ///     Emit a batch of events atomically where the implementation supports it.
    ///     Returns the stamped events with sequence numbers assigned by the sink.
    /// </summary>
    IReadOnlyList<OmicronEvent> EmitBatch(IReadOnlyList<OmicronEvent> events);

    /// <summary>
    ///     Emit a single event asynchronously. The default implementation calls the sync <see cref="Emit" />.
    ///     Override in implementations that can avoid sync-over-async (e.g., <see cref="PersistentEventSink" />).
    /// </summary>
    ValueTask<OmicronEvent> EmitAsync(OmicronEvent evt, CancellationToken ct = default)
    {
        OmicronEvent stamped = Emit(evt);
        return new ValueTask<OmicronEvent>(stamped);
    }

    /// <summary>
    ///     Emit a batch of events asynchronously. The default implementation calls the sync <see cref="EmitBatch" />.
    ///     Override in implementations that can avoid sync-over-async.
    /// </summary>
    ValueTask<IReadOnlyList<OmicronEvent>> EmitBatchAsync(
        IReadOnlyList<OmicronEvent> events,
        CancellationToken ct = default)
    {
        IReadOnlyList<OmicronEvent> stamped = EmitBatch(events);
        return new ValueTask<IReadOnlyList<OmicronEvent>>(stamped);
    }
}

/// <summary>
///     In-memory event log. Events are stored in order and can be queried
///     for testing or replay projection. Assigns global monotonic sequence numbers.
/// </summary>
public sealed class InMemoryEventSink : IEventSink
{
    private readonly List<OmicronEvent> _events = [];
    private readonly object _lock = new();
    private long _globalSequence;

    public OmicronEvent Emit(OmicronEvent evt)
    {
        lock (_lock)
        {
            long seq = ++_globalSequence;
            OmicronEvent stamped = evt with
            {
                Envelope = evt.Envelope with
                {
                    Sequence = seq
                }
            };
            _events.Add(stamped);
            return stamped;
        }
    }

    public IReadOnlyList<OmicronEvent> EmitBatch(IReadOnlyList<OmicronEvent> events)
    {
        var stamped = new List<OmicronEvent>(events.Count);
        lock (_lock)
        {
            foreach (OmicronEvent evt in events)
            {
                long seq = ++_globalSequence;
                OmicronEvent s = evt with
                {
                    Envelope = evt.Envelope with
                    {
                        Sequence = seq
                    }
                };
                _events.Add(s);
                stamped.Add(s);
            }
        }

        return stamped;
    }

    public ValueTask<OmicronEvent> EmitAsync(OmicronEvent evt, CancellationToken ct = default)
    {
        OmicronEvent stamped = Emit(evt);
        return new ValueTask<OmicronEvent>(stamped);
    }

    public ValueTask<IReadOnlyList<OmicronEvent>> EmitBatchAsync(
        IReadOnlyList<OmicronEvent> events,
        CancellationToken ct = default)
    {
        IReadOnlyList<OmicronEvent> stamped = EmitBatch(events);
        return new ValueTask<IReadOnlyList<OmicronEvent>>(stamped);
    }

    /// <summary>
    ///     Returns a snapshot of all events currently in the log.
    /// </summary>
    public IReadOnlyList<OmicronEvent> GetAllEvents()
    {
        lock (_lock)
        {
            return _events.ToList();
        }
    }

    /// <summary>
    ///     Returns events scoped to a specific session.
    /// </summary>
    public IReadOnlyList<OmicronEvent> GetSessionEvents(SessionId sessionId)
    {
        lock (_lock)
        {
            return _events.Where(e => e.SessionId == sessionId).ToList();
        }
    }

    /// <summary>
    ///     Clear all events (useful for testing). Disposes any owned content blocks.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (OmicronEvent evt in _events)
            {
                if (evt is ToolInvocationCompletedEvent tic && tic.Blocks is { Count: > 0 })
                {
                    ContentBlockHelper.DisposeBlocks(tic.Blocks);
                }
            }

            _events.Clear();
        }
    }
}
