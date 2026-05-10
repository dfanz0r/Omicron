using System.Text.Json;
using Omicron.Core.Events;

namespace Omicron.Core.Sessions;

/// <summary>
/// Manages provider state lifecycle — wraps an IProviderConversationStateStore
/// and emits ProviderStateUpdatedEvent / ProviderStateClearedEvent on mutations.
///
/// This is the Plan 2 handoff point for richer provider state management:
/// storage policy, previous-vs-new response IDs, fallback reasons, etc.
/// </summary>
public interface IProviderStateManager
{
    /// <summary>Get the current provider state for a given key.</summary>
    ProviderTurnState? Get(ProviderStateKey key);

    /// <summary>Set provider state and emit a ProviderStateUpdatedEvent.</summary>
    ProviderTurnState Set(ProviderTurnState state, string? reason = null);

    /// <summary>Clear provider state for a given key and emit a ProviderStateClearedEvent.</summary>
    void Clear(ProviderStateKey key, string? reason = null);

    /// <summary>Clear all provider state for a session and emit events.</summary>
    void ClearSession(SessionId sessionId, string? reason = null);

    /// <summary>Get all keys stored for a given session.</summary>
    IReadOnlyList<ProviderStateKey> GetSessionKeys(SessionId sessionId);
}

/// <summary>
/// Default implementation of IProviderStateManager.
/// Wraps an IProviderConversationStateStore and emits events via IEventSink.
/// </summary>
public sealed class ProviderStateManager : IProviderStateManager
{
    private readonly IProviderConversationStateStore _store;
    private readonly IEventSink? _eventSink;

    public ProviderStateManager(
        IProviderConversationStateStore store,
        IEventSink? eventSink = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _eventSink = eventSink;
    }

    public ProviderTurnState? Get(ProviderStateKey key)
        => _store.Get(key);

    public ProviderTurnState Set(ProviderTurnState state, string? reason = null)
    {
        // Normalize the key so storage and event identity are consistent
        // Use 'with' to preserve all init-only properties (StoragePolicy, etc.)
        var normalizedKey = state.Key.Normalize();
        var normalizedState = state with { Key = normalizedKey };
        _store.Set(normalizedState);
        _eventSink?.Emit(new ProviderStateUpdatedEvent(
                EventEnvelope.ForSession(normalizedKey.SessionId),
                new ProviderStateSnapshot(normalizedKey, normalizedState.PreviousResponseId, normalizedState.ConversationId, normalizedState.SessionAffinityKey, normalizedState.ProviderMetadata, normalizedState.StoragePolicy),
                reason));
        return normalizedState;
    }

    public void Clear(ProviderStateKey key, string? reason = null)
    {
        var removed = _store.Clear(key);
        if (removed)
        {
            var normalizedKey = key.Normalize();
            _eventSink?.Emit(new ProviderStateClearedEvent(
                EventEnvelope.ForSession(normalizedKey.SessionId), normalizedKey, reason));
        }
    }

    public void ClearSession(SessionId sessionId, string? reason = null)
    {
        var removedKeys = _store.ClearSession(sessionId);
        foreach (var key in removedKeys)
        {
            _eventSink?.Emit(new ProviderStateClearedEvent(
                EventEnvelope.ForSession(sessionId), key, reason));
        }
    }

    public IReadOnlyList<ProviderStateKey> GetSessionKeys(SessionId sessionId)
        => _store.GetSessionKeys(sessionId);
}

