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
    /// <exception cref="ArgumentException">Thrown if providerName or modelId is null or whitespace.</exception>
    public static ProviderStateKey Create(
        SessionId sessionId,
        AgentId agentId,
        string providerName,
        string modelId,
        ApiType apiType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
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
    /// <summary>Clear provider state for a key. Returns true if state was actually removed.</summary>
    bool Clear(ProviderStateKey key);
    /// <summary>Clear all provider state for a session. Returns the keys that were removed.</summary>
    IReadOnlyList<ProviderStateKey> ClearSession(SessionId sessionId);

    /// <summary>Get all keys stored for a given session.</summary>
    IReadOnlyList<ProviderStateKey> GetSessionKeys(SessionId sessionId);
}

/// <summary>
/// Default in-memory implementation of IProviderConversationStateStore.
/// Thread-safe via a concurrent dictionary.
///
/// This is a pure storage primitive. Event emission is handled by
/// ProviderStateManager, which wraps this store and an IEventSink.
/// </summary>
public sealed class InMemoryProviderConversationStateStore : IProviderConversationStateStore
{
    private readonly ConcurrentDictionary<ProviderStateKey, ProviderTurnState> _states = new();

    public InMemoryProviderConversationStateStore()
    {
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
    }

    public bool Clear(ProviderStateKey key)
    {
        var normalizedKey = Normalize(key);
        return _states.TryRemove(normalizedKey, out _);
    }

    public IReadOnlyList<ProviderStateKey> ClearSession(SessionId sessionId)
    {
        var keysToRemove = _states.Keys
            .Where(k => k.SessionId == sessionId)
            .ToList();

        var removed = new List<ProviderStateKey>(keysToRemove.Count);
        foreach (var key in keysToRemove)
        {
            if (_states.TryRemove(key, out _))
                removed.Add(key);
        }

        return removed;
    }

    public IReadOnlyList<ProviderStateKey> GetSessionKeys(SessionId sessionId)
    {
        return _states.Keys.Where(k => k.SessionId == sessionId).ToList();
    }
}
