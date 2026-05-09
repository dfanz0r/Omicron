using System.Text.Json;
using Omicron.Core.Providers;
using Omicron.Core.Sessions;

namespace Omicron.Core.Events;

/// <summary>
/// Shared metadata carried by every OmicronEvent.
/// Replaces the repetitive Id/Sequence/Timestamp/SessionId constructor tail.
/// </summary>
public sealed record EventEnvelope(
    EventId Id,
    long Sequence,
    DateTimeOffset Timestamp,
    SessionId SessionId);

// ============================================================
// Event-specific payload records (reduce long constructors)
// ============================================================

/// <summary>
/// Snapshot of provider turn state, used by ProviderStateUpdatedEvent.
/// </summary>
public sealed record ProviderStateSnapshot(
    ProviderStateKey Key,
    string? PreviousResponseId,
    string? ConversationId,
    string? SessionAffinityKey,
    JsonElement? ProviderMetadata,
    ProviderStoragePolicy StoragePolicy = ProviderStoragePolicy.AllowProviderStateNoStore);

/// <summary>
/// Token usage summary, used by AssistantResponseCompleteEvent.
/// </summary>
public sealed record TokenUsage(
    int InputTokens,
    int OutputTokens)
{
    /// <summary>Create a TokenUsage from an optional UsageInfo (LLM provider response).</summary>
    public static TokenUsage From(Models.UsageInfo? usage) => new(
        usage?.InputTokens ?? 0,
        usage?.OutputTokens ?? 0);
}
