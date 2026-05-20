using System.Collections.Concurrent;
using System.Text.Json;
using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Providers;

namespace Omicron.Core.Sessions;

/// <summary>
///     Key that scopes provider turn state to a specific session, agent, provider, model, and API type.
///     Provider name is normalized to lowercase for case-insensitive comparison.
/// </summary>
public readonly record struct ProviderStateKey(
    SessionId SessionId,
    AgentId AgentId,
    string ProviderName,
    string ModelId,
    ApiType ApiType)
{
    /// <summary>
    ///     Create a key with a case-normalized provider name.
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
        return new ProviderStateKey(sessionId,
            agentId,
            providerName.ToLowerInvariant(),
            modelId,
            apiType);
    }

    /// <summary>
    ///     Returns a copy of this key with a case-normalized provider name.
    /// </summary>
    public ProviderStateKey Normalize()
    {
        return Create(SessionId, AgentId, ProviderName, ModelId, ApiType);
    }
}

/// <summary>
///     Provider continuation state for stateful APIs such as OpenAI Responses.
///     Allows the provider to carry conversation/response IDs between turns without
///     the CLI or core session needing to know the wire-level details.
///     StoragePolicy controls whether Omicron requests server-side storage and whether
///     it uses provider continuation tokens (previous_response_id).
/// </summary>
public sealed record ProviderTurnState(
    ProviderStateKey Key,
    string? PreviousResponseId,
    string? ConversationId,
    string? SessionAffinityKey,
    JsonElement? ProviderMetadata)
{
    /// <summary>
    ///     Storage policy for this provider turn state.
    ///     Controls whether previous_response_id and store flags are used.
    /// </summary>
    public ProviderStoragePolicy StoragePolicy { get; init; } =
        ProviderStoragePolicy.AllowProviderStateNoStore;

    /// <summary>
    ///     Whether this state represents a stateful continuation (has a PreviousResponseId).
    /// </summary>
    public bool IsStateful => !string.IsNullOrEmpty(PreviousResponseId);

    /// <summary>
    ///     Clear the continuation state, keeping only the session identity.
    ///     Used for stateless fallback or fork semantics.
    /// </summary>
    public ProviderTurnState ClearContinuation()
    {
        return this with
        {
            PreviousResponseId = null,
            ConversationId = null,
            ProviderMetadata = null
        };
    }

    /// <summary>
    ///     Return true if this state is compatible with the given fork policy.
    ///     By default, fork clears provider continuation unless explicitly continued.
    /// </summary>
    public bool CanFork()
    {
        return false;
    }
}

/// <summary>
///     Stores and retrieves provider continuation state scoped by session + provider + model.
///     In-memory implementation is the default until persistence is added.
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
///     Default in-memory implementation of IProviderConversationStateStore.
///     Thread-safe via a concurrent dictionary.
///     This is a pure storage primitive. Event emission is handled by
///     ProviderStateManager, which wraps this store and an IEventSink.
/// </summary>
public sealed class InMemoryProviderConversationStateStore : IProviderConversationStateStore
{
    private readonly ConcurrentDictionary<ProviderStateKey, ProviderTurnState> _states = new();

    public ProviderTurnState? Get(ProviderStateKey key)
    {
        _states.TryGetValue(Normalize(key), out ProviderTurnState? state);
        return state;
    }

    public void Set(ProviderTurnState state)
    {
        ProviderStateKey normalizedKey = Normalize(state.Key);
        // Use 'with' to preserve all init-only properties (StoragePolicy, etc.)
        ProviderTurnState normalizedState = state with
        {
            Key = normalizedKey
        };
        _states[normalizedKey] = normalizedState;
    }

    public bool Clear(ProviderStateKey key)
    {
        ProviderStateKey normalizedKey = Normalize(key);
        return _states.TryRemove(normalizedKey, out _);
    }

    public IReadOnlyList<ProviderStateKey> ClearSession(SessionId sessionId)
    {
        var keysToRemove = _states.Keys.Where(k => k.SessionId == sessionId).ToList();

        var removed = new List<ProviderStateKey>(keysToRemove.Count);
        foreach (ProviderStateKey key in keysToRemove)
        {
            if (_states.TryRemove(key, out _))
            {
                removed.Add(key);
            }
        }

        return removed;
    }

    public IReadOnlyList<ProviderStateKey> GetSessionKeys(SessionId sessionId)
    {
        return _states.Keys.Where(k => k.SessionId == sessionId).ToList();
    }

    /// <summary>
    ///     Normalize provider name to lowercase to ensure case-insensitive key matching.
    /// </summary>
    private static ProviderStateKey Normalize(ProviderStateKey key)
    {
        return key.Normalize();
    }
}
