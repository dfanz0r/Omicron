using System.Collections.Concurrent;
using System.Text.Json;
using Omicron.Core.Events;
using Omicron.Core.Models;

namespace Omicron.Core.Sessions;

/// <summary>
/// Key that scopes provider turn state to a specific session, agent, provider, model, and API type.
/// Provider name is normalized to lowercase for case-insensitive comparison.
/// </summary>
public readonly record struct ProviderStateKey(
    SessionId SessionId,
    AgentId AgentId,
    string ProviderName,
    string ModelId,
    ApiType ApiType)
{
    /// <summary>
    /// Create a key with a case-normalized provider name.
    /// </summary>
    public static ProviderStateKey Create(
        SessionId sessionId,
        AgentId agentId,
        string providerName,
        string modelId,
        ApiType apiType)
    {
        return new ProviderStateKey(
            sessionId,
            agentId,
            providerName.ToLowerInvariant(),
            modelId,
            apiType);
    }

    /// <summary>
    /// Returns a copy of this key with a case-normalized provider name.
    /// </summary>
    public ProviderStateKey Normalize() => Create(SessionId, AgentId, ProviderName, ModelId, ApiType);
}

/// <summary>
/// Minimal provider continuation state for stateful APIs such as OpenAI Responses.
/// Allows the provider to carry conversation/response IDs between turns without
/// the CLI or core session needing to know the wire-level details.
/// </summary>
public sealed record ProviderTurnState(
    ProviderStateKey Key,
    string? PreviousResponseId,
    string? ConversationId,
    string? SessionAffinityKey,
    JsonElement? ProviderMetadata);

/// <summary>
/// Stores and retrieves provider continuation state scoped by session + provider + model.
/// In-memory implementation is the default until persistence is added.
/// </summary>
public interface IProviderConversationStateStore
{
    ProviderTurnState? Get(ProviderStateKey key);
    void Set(ProviderTurnState state);
    void Clear(ProviderStateKey key);
    void ClearSession(SessionId sessionId);

    /// <summary>Get all keys stored for a given session.</summary>
    IReadOnlyList<ProviderStateKey> GetSessionKeys(SessionId sessionId);
}

/// <summary>
/// Default in-memory implementation of IProviderConversationStateStore.
/// Thread-safe via a concurrent dictionary.
///
/// Optionally accepts an IEventSink to emit ProviderStateUpdatedEvent
/// and ProviderStateClearedEvent on mutations.
///
/// NOTE: Event emission from the raw store is MVP-only. Plan 2 should
/// introduce a ProviderStateManager that wraps the store and emits
/// richer events (including provider/model display names, reason for
/// update, previous-vs-new response IDs, storage policy, and whether
/// the update came from a stateful Responses continuation, fallback,
/// reset, or fork). At that point the raw store should become a pure
/// persistence primitive without event emission responsibility.
/// </summary>
public sealed class InMemoryProviderConversationStateStore : IProviderConversationStateStore
{
    private readonly ConcurrentDictionary<ProviderStateKey, ProviderTurnState> _states = new();
    private readonly IEventSink? _eventSink;

    public InMemoryProviderConversationStateStore(IEventSink? eventSink = null)
    {
        _eventSink = eventSink;
    }

    /// <summary>
    /// Normalize provider name to lowercase to ensure case-insensitive key matching.
    /// </summary>
    private static ProviderStateKey Normalize(ProviderStateKey key)
        => key.Normalize();

    public ProviderTurnState? Get(ProviderStateKey key)
    {
        _states.TryGetValue(Normalize(key), out var state);
        return state;
    }

    public void Set(ProviderTurnState state)
    {
        var normalizedKey = Normalize(state.Key);
        var normalizedState = new ProviderTurnState(normalizedKey,
            state.PreviousResponseId, state.ConversationId,
            state.SessionAffinityKey, state.ProviderMetadata);
        _states[normalizedKey] = normalizedState;

        _eventSink?.Emit(new ProviderStateUpdatedEvent(
            EventId.New(), 0, DateTimeOffset.UtcNow, normalizedKey.SessionId,
            normalizedKey, normalizedState.PreviousResponseId, normalizedState.ConversationId));
    }

    public void Clear(ProviderStateKey key)
    {
        var normalizedKey = Normalize(key);

        if (_states.TryRemove(normalizedKey, out var removed))
        {
            _eventSink?.Emit(new ProviderStateClearedEvent(
                EventId.New(), 0, DateTimeOffset.UtcNow, normalizedKey.SessionId,
                normalizedKey));
        }
    }

    public void ClearSession(SessionId sessionId)
    {
        var keysToRemove = _states.Keys
            .Where(k => k.SessionId == sessionId)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _states.TryRemove(key, out _);
            _eventSink?.Emit(new ProviderStateClearedEvent(
                EventId.New(), 0, DateTimeOffset.UtcNow, sessionId, key));
        }
    }

    public IReadOnlyList<ProviderStateKey> GetSessionKeys(SessionId sessionId)
    {
        return _states.Keys.Where(k => k.SessionId == sessionId).ToList();
    }
}
