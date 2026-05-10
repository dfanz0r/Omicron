using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Omicron.Core.Events;
using Omicron.Core.Models;

namespace Omicron.Core.Sessions;

// ============================================================
// Session catalog records
// ============================================================

/// <summary>
/// Metadata record for a persisted session.
/// </summary>
public sealed record SessionRecord(
    SessionId SessionId,
    string ModelId,
    string ProviderName,
    ApiType ApiType,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastActivityAt,
    string? SystemPrompt,
    string? Label,
    SessionStatus Status)
{
    public static SessionRecord Create(
        SessionId sessionId,
        string modelId,
        string providerName,
        ApiType apiType,
        string? systemPrompt = null,
        string? label = null)
        => new(
            sessionId, modelId, providerName, apiType,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            systemPrompt, label, SessionStatus.Active);
}

public enum SessionStatus
{
    Active,
    Archived,
    Error
}

/// <summary>
/// Request to create a new session.
/// If SessionId is null, the store generates one.
/// AgentSession passes its own Id so events are stored under the correct key.
/// </summary>
public sealed record SessionCreateRequest(
    string ModelId,
    string ProviderName,
    ApiType ApiType,
    SessionId? SessionId = null,
    string? SystemPrompt = null,
    string? Label = null);

/// <summary>
/// Query for listing sessions.
/// </summary>
public sealed record SessionListQuery(
    int? Limit = 100,
    int? Offset = 0,
    SessionStatus? StatusFilter = null,
    string? ProviderFilter = null);

/// <summary>
/// Range of event sequences for reading events.
/// Both ends are optional; null means unbounded.
/// </summary>
public readonly record struct EventSequenceRange(long? FromExclusive, long? ToInclusive)
{
    public static readonly EventSequenceRange All = new(null, null);
}

// ============================================================
// Session store interface
// ============================================================

/// <summary>
/// Persistence abstraction for sessions and their event streams.
/// </summary>
public interface ISessionStore
{
    /// <summary>Create a new session record and return it.</summary>
    ValueTask<SessionRecord> CreateSessionAsync(SessionCreateRequest request, CancellationToken ct = default);

    /// <summary>List sessions matching the query.</summary>
    ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(SessionListQuery query, CancellationToken ct = default);

    /// <summary>Get a single session by ID, or null if not found.</summary>
    ValueTask<SessionRecord?> GetSessionAsync(SessionId sessionId, CancellationToken ct = default);

    /// <summary>Update session metadata (label, status, etc.). Returns updated record or null if not found.</summary>
    ValueTask<SessionRecord?> UpdateSessionAsync(SessionId sessionId, Func<SessionRecord, SessionRecord> update, CancellationToken ct = default);

    /// <summary>Append events to a session's event stream. Throws if session not found.</summary>
    ValueTask AppendEventsAsync(SessionId sessionId, IReadOnlyList<OmicronEvent> events, CancellationToken ct = default);

    /// <summary>Read events from a session's event stream in sequence order.</summary>
    IAsyncEnumerable<OmicronEvent> ReadEventsAsync(SessionId sessionId, EventSequenceRange range, CancellationToken ct = default);

    /// <summary>Get the total event count for a session.</summary>
    ValueTask<long> GetEventCountAsync(SessionId sessionId, CancellationToken ct = default);
}

// ============================================================
// In-memory session store (for tests and default)
// ============================================================

/// <summary>
/// In-memory implementation of ISessionStore for testing and default use.
/// Not durable — sessions and events are lost on process exit.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly List<SessionRecord> _sessions = new();
    private readonly Dictionary<SessionId, List<OmicronEvent>> _events = new();
    private readonly object _lock = new();

    public ValueTask<SessionRecord> CreateSessionAsync(SessionCreateRequest request, CancellationToken ct = default)
    {
        var sessionId = request.SessionId ?? SessionId.New();

        lock (_lock)
        {
            if (_sessions.Any(s => s.SessionId == sessionId))
                throw new InvalidOperationException($"Session {sessionId} already exists.");

            var record = SessionRecord.Create(
                sessionId,
                request.ModelId,
                request.ProviderName,
                request.ApiType,
                request.SystemPrompt,
                request.Label);

            _sessions.Add(record);
            _events[sessionId] = new List<OmicronEvent>();

            return ValueTask.FromResult(record);
        }
    }

    public ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(SessionListQuery query, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var filtered = _sessions.AsEnumerable();

            if (query.StatusFilter.HasValue)
                filtered = filtered.Where(s => s.Status == query.StatusFilter.Value);

            if (query.ProviderFilter is not null)
                filtered = filtered.Where(s =>
                    s.ProviderName.Contains(query.ProviderFilter, StringComparison.OrdinalIgnoreCase));

            var result = filtered
                .Skip(query.Offset ?? 0)
                .Take(query.Limit ?? 100)
                .ToList();

            return ValueTask.FromResult<IReadOnlyList<SessionRecord>>(result);
        }
    }

    public ValueTask<SessionRecord?> GetSessionAsync(SessionId sessionId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var record = _sessions.FirstOrDefault(s => s.SessionId == sessionId);
            return ValueTask.FromResult(record);
        }
    }

    public ValueTask<SessionRecord?> UpdateSessionAsync(SessionId sessionId, Func<SessionRecord, SessionRecord> update, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var index = _sessions.FindIndex(s => s.SessionId == sessionId);
            if (index >= 0)
            {
                var record = _sessions[index];
                var updated = update(record);
                _sessions[index] = updated;
                return ValueTask.FromResult<SessionRecord?>(updated);
            }
            return ValueTask.FromResult<SessionRecord?>(null);
        }
    }

    public ValueTask AppendEventsAsync(SessionId sessionId, IReadOnlyList<OmicronEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return ValueTask.CompletedTask;

        lock (_lock)
        {
            if (!_events.TryGetValue(sessionId, out var list))
                throw new InvalidOperationException(
                    $"Session {sessionId} not found in store. Create the session before appending events.");

            list.AddRange(events);

            // Update LastActivityAt on the session record
            var index = _sessions.FindIndex(s => s.SessionId == sessionId);
            if (index >= 0)
            {
                _sessions[index] = _sessions[index] with { LastActivityAt = DateTimeOffset.UtcNow };
            }
        }
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<OmicronEvent> ReadEventsAsync(SessionId sessionId, EventSequenceRange range, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<OmicronEvent> snapshot;
        lock (_lock)
        {
            if (!_events.TryGetValue(sessionId, out var list))
                yield break;

            snapshot = list.ToList();
        }

        foreach (var evt in snapshot)
        {
            ct.ThrowIfCancellationRequested();

            if (range.FromExclusive.HasValue && evt.Sequence <= range.FromExclusive.Value)
                continue;
            if (range.ToInclusive.HasValue && evt.Sequence > range.ToInclusive.Value)
                yield break;

            yield return evt;
        }
    }

    public ValueTask<long> GetEventCountAsync(SessionId sessionId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_events.TryGetValue(sessionId, out var list))
                return ValueTask.FromResult((long)list.Count);
            return ValueTask.FromResult(0L);
        }
    }
}

// ============================================================
// JSONL file-backed session store (MVP durability)
// ============================================================

/// <summary>
/// File-backed session store that persists events as newline-delimited JSON.
/// Each session gets its own .jsonl file in the configured directory.
/// Session metadata is stored in a sessions.json index file.
///
/// This store owns its data directly so LoadIndex() can properly restore state on reopen.
/// Sync writes ensure event ordering is preserved on disk.
/// </summary>
public sealed class JsonlSessionStore : ISessionStore, IDisposable
{
    /// <summary>Directory where session data is stored.</summary>
    public string StoreDirectory => _storeDir;

    private readonly string _storeDir;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly List<SessionRecord> _sessions = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Create a JSONL session store at the given directory.
    /// Creates the directory if it doesn't exist, restores sessions from index.
    /// </summary>
    public JsonlSessionStore(string storeDir)
    {
        _storeDir = storeDir;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        };
        Directory.CreateDirectory(_storeDir);
        LoadIndex();
    }

    private string IndexPath => Path.Combine(_storeDir, "sessions.json");
    private string EventPath(SessionId sessionId) => Path.Combine(_storeDir, $"{sessionId}.jsonl");

    private void LoadIndex()
    {
        var indexPath = IndexPath;
        if (!File.Exists(indexPath)) return;

        try
        {
            var json = File.ReadAllText(indexPath);
            var records = JsonSerializer.Deserialize<List<SessionRecord>>(json, _jsonOptions);
            if (records is not null)
            {
                lock (_lock)
                {
                    _sessions.Clear();
                    _sessions.AddRange(records);
                }
            }
        }
        catch
        {
            // Corrupt index file — start fresh
        }
    }

    private void SaveIndex()
    {
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(_sessions, _jsonOptions);
            File.WriteAllText(IndexPath, json);
        }
    }

    public ValueTask<SessionRecord> CreateSessionAsync(SessionCreateRequest request, CancellationToken ct = default)
    {
        var sessionId = request.SessionId ?? SessionId.New();
        SessionRecord record;

        lock (_lock)
        {
            if (_sessions.Any(s => s.SessionId == sessionId))
                throw new InvalidOperationException($"Session {sessionId} already exists.");

            record = SessionRecord.Create(
                sessionId,
                request.ModelId,
                request.ProviderName,
                request.ApiType,
                request.SystemPrompt,
                request.Label);

            _sessions.Add(record);
        }
        SaveIndex();
        return ValueTask.FromResult(record);
    }

    public ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(SessionListQuery query, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var filtered = _sessions.AsEnumerable();

            if (query.StatusFilter.HasValue)
                filtered = filtered.Where(s => s.Status == query.StatusFilter.Value);

            if (query.ProviderFilter is not null)
                filtered = filtered.Where(s =>
                    s.ProviderName.Contains(query.ProviderFilter, StringComparison.OrdinalIgnoreCase));

            var result = filtered
                .Skip(query.Offset ?? 0)
                .Take(query.Limit ?? 100)
                .ToList();

            return ValueTask.FromResult<IReadOnlyList<SessionRecord>>(result);
        }
    }

    public ValueTask<SessionRecord?> GetSessionAsync(SessionId sessionId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var record = _sessions.FirstOrDefault(s => s.SessionId == sessionId);
            return ValueTask.FromResult(record);
        }
    }

    public ValueTask<SessionRecord?> UpdateSessionAsync(SessionId sessionId, Func<SessionRecord, SessionRecord> update, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var index = _sessions.FindIndex(s => s.SessionId == sessionId);
            if (index >= 0)
            {
                var updated = update(_sessions[index]);
                _sessions[index] = updated;
                SaveIndex();
                return ValueTask.FromResult<SessionRecord?>(updated);
            }
            return ValueTask.FromResult<SessionRecord?>(null);
        }
    }

    public ValueTask AppendEventsAsync(SessionId sessionId, IReadOnlyList<OmicronEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return ValueTask.CompletedTask;

        // Verify session exists
        lock (_lock)
        {
            if (!_sessions.Any(s => s.SessionId == sessionId))
                throw new InvalidOperationException($"Session {sessionId} not found in store.");
        }

        // Write events to the JSONL file first (before metadata update) for atomicity
        var eventPath = EventPath(sessionId);
        try
        {
            using var stream = new FileStream(eventPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096);
            using var writer = new StreamWriter(stream);

            foreach (var evt in events)
            {
                var json = SerializeEvent(evt);
                writer.WriteLine(json);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to write events to {eventPath}: {ex.Message}", ex);
        }

        // Then update LastActivityAt and save index
        lock (_lock)
        {
            var index = _sessions.FindIndex(s => s.SessionId == sessionId);
            if (index >= 0)
                _sessions[index] = _sessions[index] with { LastActivityAt = DateTimeOffset.UtcNow };
        }
        SaveIndex();

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<OmicronEvent> ReadEventsAsync(SessionId sessionId, EventSequenceRange range, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var eventPath = EventPath(sessionId);
        if (!File.Exists(eventPath))
            yield break;

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(eventPath, ct);
        }
        catch
        {
            yield break;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            ct.ThrowIfCancellationRequested();

            var evt = DeserializeEvent(line);
            if (evt is null) continue;

            if (range.FromExclusive.HasValue && evt.Sequence <= range.FromExclusive.Value)
                continue;
            if (range.ToInclusive.HasValue && evt.Sequence > range.ToInclusive.Value)
                yield break;

            yield return evt;
        }
    }

    public ValueTask<long> GetEventCountAsync(SessionId sessionId, CancellationToken ct = default)
    {
        var eventPath = EventPath(sessionId);
        if (!File.Exists(eventPath))
            return ValueTask.FromResult(0L);

        try
        {
            var count = File.ReadLines(eventPath).Count(l => !string.IsNullOrWhiteSpace(l));
            return ValueTask.FromResult((long)count);
        }
        catch
        {
            return ValueTask.FromResult(0L);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            SaveIndex();
            _disposed = true;
        }
    }

    // ============================================================
    // JSONL serialization helpers
    // ============================================================

    /// <summary>Serialize an OmicronEvent to JSON with a $type discriminator.</summary>
    private static string SerializeEvent(OmicronEvent evt)
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var json = JsonSerializer.Serialize((object)evt, evt.GetType(), opts);
        var node = JsonNode.Parse(json)!;
        node["$type"] = evt.GetType().Name;
        return node.ToJsonString(opts);
    }

    /// <summary>Deserialize a JSON line to an OmicronEvent using the $type discriminator.</summary>
    private static OmicronEvent? DeserializeEvent(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("$type", out var typeEl))
                return null;

            var typeName = typeEl.GetString();
            var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            return typeName switch
            {
                nameof(SessionStartedEvent) => JsonSerializer.Deserialize<SessionStartedEvent>(line, opts),
                nameof(SessionResetEvent) => JsonSerializer.Deserialize<SessionResetEvent>(line, opts),
                nameof(SessionErrorEvent) => JsonSerializer.Deserialize<SessionErrorEvent>(line, opts),
                nameof(TurnStartedEvent) => JsonSerializer.Deserialize<TurnStartedEvent>(line, opts),
                nameof(UserMessageEvent) => JsonSerializer.Deserialize<UserMessageEvent>(line, opts),
                nameof(AssistantTextDeltaEvent) => JsonSerializer.Deserialize<AssistantTextDeltaEvent>(line, opts),
                nameof(AssistantResponseCompleteEvent) => JsonSerializer.Deserialize<AssistantResponseCompleteEvent>(line, opts),
                nameof(ToolInvocationStartedEvent) => JsonSerializer.Deserialize<ToolInvocationStartedEvent>(line, opts),
                nameof(ToolInvocationCompletedEvent) => JsonSerializer.Deserialize<ToolInvocationCompletedEvent>(line, opts),
                nameof(PermissionRequestedEvent) => JsonSerializer.Deserialize<PermissionRequestedEvent>(line, opts),
                nameof(ExecutionStartedEvent) => JsonSerializer.Deserialize<ExecutionStartedEvent>(line, opts),
                nameof(ExecutionCompletedEvent) => JsonSerializer.Deserialize<ExecutionCompletedEvent>(line, opts),
                nameof(ProviderStateUpdatedEvent) => JsonSerializer.Deserialize<ProviderStateUpdatedEvent>(line, opts),
                nameof(SessionEndedEvent) => JsonSerializer.Deserialize<SessionEndedEvent>(line, opts),
                nameof(ProviderStateClearedEvent) => JsonSerializer.Deserialize<ProviderStateClearedEvent>(line, opts),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
