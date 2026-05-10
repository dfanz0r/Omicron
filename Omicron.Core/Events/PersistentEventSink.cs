using System.Threading;
using Omicron.Core.Sessions;

namespace Omicron.Core.Events;

/// <summary>
/// An IEventSink wrapper that also writes stamped events to an ISessionStore.
/// Events are persisted synchronously in sequence order after the inner sink
/// stamps them. This ensures persistence ordering matches event sequence.
///
/// By default persists all sessions. Optionally filter to a single session.
/// Persistence failures are non-fatal for runtime; an optional OnError callback
/// can be set for observability (logging, testing, etc.).
/// </summary>
public sealed class PersistentEventSink : IEventSink
{
    private readonly IEventSink _inner;
    private ISessionStore _store;
    private readonly SessionId? _sessionId;

    /// <summary>
    /// Optional callback invoked when a persistence operation fails.
    /// Parameters: the exception, the event that failed, and a descriptive label.
    /// </summary>
    public Action<Exception, OmicronEvent, string>? OnError { get; set; }

    /// <summary>
    /// Total number of events successfully persisted (atomically updated).
    /// </summary>
    public long PersistedCount => Volatile.Read(ref _persistedCount);
    private long _persistedCount;

    /// <summary>
    /// Total number of persistence failures (atomically updated).
    /// </summary>
    public long FailureCount => Volatile.Read(ref _failureCount);
    private long _failureCount;

    /// <summary>
    /// Create a PersistentEventSink.
    /// </summary>
    /// <param name="inner">The primary event sink (e.g., InMemoryEventSink).</param>
    /// <param name="store">Session store to persist events to.</param>
    /// <param name="sessionId">If set, only events for this session are persisted. If null, all events are persisted.</param>
    public PersistentEventSink(IEventSink inner, ISessionStore store, SessionId? sessionId = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessionId = sessionId;
    }

    public OmicronEvent Emit(OmicronEvent evt)
    {
        // Let the inner sink stamp the event first
        var stamped = _inner.Emit(evt);

        // Persist if session matches (or if no filter)
        if (_sessionId is null || stamped.SessionId == _sessionId.Value)
        {
            try
            {
                _store.AppendEventsAsync(stamped.SessionId, [stamped]).GetAwaiter().GetResult();
                Interlocked.Increment(ref _persistedCount);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failureCount);
                OnError?.Invoke(ex, stamped, "Emit");
            }
        }

        return stamped;
    }

    public IReadOnlyList<OmicronEvent> EmitBatch(IReadOnlyList<OmicronEvent> events)
    {
        var stamped = _inner.EmitBatch(events);

        // Group by session and persist
        var bySession = stamped
            .Where(e => _sessionId is null || e.SessionId == _sessionId.Value)
            .GroupBy(e => e.SessionId);

        foreach (var group in bySession)
        {
            try
            {
                var list = group.ToList();
                _store.AppendEventsAsync(group.Key, list).GetAwaiter().GetResult();
                Interlocked.Add(ref _persistedCount, list.Count);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failureCount);
                OnError?.Invoke(ex, group.First(), "EmitBatch");
            }
        }

        return stamped;
    }
}
