using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Omicron.Core.Events;
using Omicron.Core.Models;

namespace Omicron.Core.Sessions;

// ============================================================
// Session catalog records
// ============================================================

/// <summary>
///     Metadata record for a persisted session.
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
    {
        return new SessionRecord(sessionId,
            modelId,
            providerName,
            apiType,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            systemPrompt,
            label,
            SessionStatus.Active);
    }
}

public enum SessionStatus
{
    Active,
    Archived,
    Error
}

/// <summary>
///     Request to create a new session.
///     If SessionId is null, the store generates one.
///     AgentSession passes its own Id so events are stored under the correct key.
/// </summary>
public sealed record SessionCreateRequest(
    string ModelId,
    string ProviderName,
    ApiType ApiType,
    SessionId? SessionId = null,
    string? SystemPrompt = null,
    string? Label = null);

/// <summary>
///     Query for listing sessions.
/// </summary>
public sealed record SessionListQuery(
    int? Limit = 100,
    int? Offset = 0,
    SessionStatus? StatusFilter = null,
    string? ProviderFilter = null);

/// <summary>
///     Range of event sequences for reading events.
///     Both ends are optional; null means unbounded.
/// </summary>
public readonly record struct EventSequenceRange(long? FromExclusive, long? ToInclusive)
{
    public static readonly EventSequenceRange All = new(null, null);
}

// ============================================================
// Session store interface
// ============================================================

/// <summary>
///     Persistence abstraction for sessions and their event streams.
/// </summary>
public interface ISessionStore
{
    /// <summary>Create a new session record and return it.</summary>
    ValueTask<SessionRecord> CreateSessionAsync(
        SessionCreateRequest request,
        CancellationToken ct = default);

    /// <summary>List sessions matching the query.</summary>
    ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(
        SessionListQuery query,
        CancellationToken ct = default);

    /// <summary>Get a single session by ID, or null if not found.</summary>
    ValueTask<SessionRecord?> GetSessionAsync(SessionId sessionId, CancellationToken ct = default);

    /// <summary>Update session metadata (label, status, etc.). Returns updated record or null if not found.</summary>
    ValueTask<SessionRecord?> UpdateSessionAsync(
        SessionId sessionId,
        Func<SessionRecord, SessionRecord> update,
        CancellationToken ct = default);

    /// <summary>Append events to a session's event stream. Throws if session not found.</summary>
    ValueTask AppendEventsAsync(
        SessionId sessionId,
        IReadOnlyList<OmicronEvent> events,
        CancellationToken ct = default);

    /// <summary>Read events from a session's event stream in sequence order.</summary>
    IAsyncEnumerable<OmicronEvent> ReadEventsAsync(
        SessionId sessionId,
        EventSequenceRange range,
        CancellationToken ct = default);

    /// <summary>Get the total event count for a session.</summary>
    ValueTask<long> GetEventCountAsync(SessionId sessionId, CancellationToken ct = default);
}

// ============================================================
// In-memory session store (for tests and default)
// ============================================================

/// <summary>
///     In-memory implementation of ISessionStore for testing and default use.
///     Not durable — sessions and events are lost on process exit.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<SessionId, List<OmicronEvent>> _events = new();
    private readonly object _lock = new();
    private readonly List<SessionRecord> _sessions = new();

    public ValueTask<SessionRecord> CreateSessionAsync(
        SessionCreateRequest request,
        CancellationToken ct = default)
    {
        SessionId sessionId = request.SessionId ?? SessionId.New();

        lock (_lock)
        {
            if (_sessions.Any(s => s.SessionId == sessionId))
            {
                throw new InvalidOperationException($"Session {sessionId} already exists.");
            }

            var record = SessionRecord.Create(sessionId,
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

    public ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(
        SessionListQuery query,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            IEnumerable<SessionRecord> filtered = _sessions.AsEnumerable();

            if (query.StatusFilter.HasValue)
            {
                filtered = filtered.Where(s => s.Status == query.StatusFilter.Value);
            }

            if (query.ProviderFilter is not null)
            {
                filtered = filtered.Where(s =>
                    s.ProviderName.Contains(query.ProviderFilter,
                        StringComparison.OrdinalIgnoreCase));
            }

            var result = filtered.Skip(query.Offset ?? 0).Take(query.Limit ?? 100).ToList();

            return ValueTask.FromResult<IReadOnlyList<SessionRecord>>(result);
        }
    }

    public ValueTask<SessionRecord?> GetSessionAsync(
        SessionId sessionId,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            SessionRecord? record = _sessions.FirstOrDefault(s => s.SessionId == sessionId);
            return ValueTask.FromResult(record);
        }
    }

    public ValueTask<SessionRecord?> UpdateSessionAsync(
        SessionId sessionId,
        Func<SessionRecord, SessionRecord> update,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            int index = _sessions.FindIndex(s => s.SessionId == sessionId);
            if (index >= 0)
            {
                SessionRecord record = _sessions[index];
                SessionRecord updated = update(record);
                _sessions[index] = updated;
                return ValueTask.FromResult<SessionRecord?>(updated);
            }

            return ValueTask.FromResult<SessionRecord?>(null);
        }
    }

    public ValueTask AppendEventsAsync(
        SessionId sessionId,
        IReadOnlyList<OmicronEvent> events,
        CancellationToken ct = default)
    {
        if (events.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        lock (_lock)
        {
            if (!_events.TryGetValue(sessionId, out List<OmicronEvent>? list))
            {
                throw new InvalidOperationException(
                    $"Session {sessionId} not found in store. Create the session before appending events.");
            }

            list.AddRange(events);

            // Update LastActivityAt on the session record
            int index = _sessions.FindIndex(s => s.SessionId == sessionId);
            if (index >= 0)
            {
                _sessions[index] = _sessions[index] with
                {
                    LastActivityAt = DateTimeOffset.UtcNow
                };
            }
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<OmicronEvent> ReadEventsAsync(
        SessionId sessionId,
        EventSequenceRange range,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<OmicronEvent> snapshot;
        lock (_lock)
        {
            if (!_events.TryGetValue(sessionId, out List<OmicronEvent>? list))
            {
                yield break;
            }

            snapshot = list.ToList();
        }

        foreach (OmicronEvent evt in snapshot)
        {
            ct.ThrowIfCancellationRequested();

            if (range.FromExclusive.HasValue && evt.Sequence <= range.FromExclusive.Value)
            {
                continue;
            }

            if (range.ToInclusive.HasValue && evt.Sequence > range.ToInclusive.Value)
            {
                yield break;
            }

            yield return evt;
        }
    }

    public ValueTask<long> GetEventCountAsync(SessionId sessionId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_events.TryGetValue(sessionId, out List<OmicronEvent>? list))
            {
                return ValueTask.FromResult((long)list.Count);
            }

            return ValueTask.FromResult(0L);
        }
    }
}

// ============================================================
// JSONL file-backed session store (MVP durability)
// ============================================================

/// <summary>
///     File-backed session store that persists events as newline-delimited JSON.
///     Each session gets its own .jsonl file in the configured directory.
///     Session metadata is stored in a sessions.json index file.
///     This store owns its data directly so LoadIndex() can properly restore state on reopen.
///     Sync writes ensure event ordering is preserved on disk.
/// </summary>
public sealed class JsonlSessionStore : ISessionStore, IDisposable
{
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<SessionId, SemaphoreSlim> _sessionLocks = new();
    private readonly List<SessionRecord> _sessions = new();

    private bool _disposed;

    /// <summary>
    ///     Create a JSONL session store at the given directory.
    ///     Creates the directory if it doesn't exist, restores sessions from index.
    /// </summary>
    public JsonlSessionStore(string storeDir)
    {
        StoreDirectory = storeDir;
        _jsonOptions = OmicronEventJson.CreateOptions();
        Directory.CreateDirectory(StoreDirectory);
        LoadIndex();
    }

    /// <summary>Directory where session data is stored.</summary>
    public string StoreDirectory { get; }

    private string IndexPath => Path.Combine(StoreDirectory, "sessions.json");

    public void Dispose()
    {
        if (!_disposed)
        {
            PruneEmptySessions();
            SaveIndex();
            _disposed = true;
        }
    }

    public ValueTask<SessionRecord> CreateSessionAsync(
        SessionCreateRequest request,
        CancellationToken ct = default)
    {
        SessionId sessionId = request.SessionId ?? SessionId.New();
        SessionRecord record;

        lock (_lock)
        {
            if (_sessions.Any(s => s.SessionId == sessionId))
            {
                throw new InvalidOperationException($"Session {sessionId} already exists.");
            }

            record = SessionRecord.Create(sessionId,
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

    public ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(
        SessionListQuery query,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            IEnumerable<SessionRecord> filtered = _sessions.AsEnumerable();

            if (query.StatusFilter.HasValue)
            {
                filtered = filtered.Where(s => s.Status == query.StatusFilter.Value);
            }

            if (query.ProviderFilter is not null)
            {
                filtered = filtered.Where(s =>
                    s.ProviderName.Contains(query.ProviderFilter,
                        StringComparison.OrdinalIgnoreCase));
            }

            var result = filtered.Skip(query.Offset ?? 0).Take(query.Limit ?? 100).ToList();

            return ValueTask.FromResult<IReadOnlyList<SessionRecord>>(result);
        }
    }

    public ValueTask<SessionRecord?> GetSessionAsync(
        SessionId sessionId,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            SessionRecord? record = _sessions.FirstOrDefault(s => s.SessionId == sessionId);
            return ValueTask.FromResult(record);
        }
    }

    public ValueTask<SessionRecord?> UpdateSessionAsync(
        SessionId sessionId,
        Func<SessionRecord, SessionRecord> update,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            int index = _sessions.FindIndex(s => s.SessionId == sessionId);
            if (index >= 0)
            {
                SessionRecord updated = update(_sessions[index]);
                _sessions[index] = updated;
                SaveIndex();
                return ValueTask.FromResult<SessionRecord?>(updated);
            }

            return ValueTask.FromResult<SessionRecord?>(null);
        }
    }

    public async ValueTask AppendEventsAsync(
        SessionId sessionId,
        IReadOnlyList<OmicronEvent> events,
        CancellationToken ct = default)
    {
        if (events.Count == 0)
        {
            return;
        }

        SemaphoreSlim semaphore = GetSessionLock(sessionId);
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Verify session exists
            lock (_lock)
            {
                if (!_sessions.Any(s => s.SessionId == sessionId))
                {
                    throw new InvalidOperationException($"Session {sessionId} not found in store.");
                }
            }

            // Write events to the JSONL file first (before metadata update) for atomicity
            string eventPath = EventPath(sessionId);
            try
            {
                using var stream = new FileStream(eventPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    4096);

                foreach (OmicronEvent evt in events)
                {
                    byte[] bytes = OmicronEventJson.SerializeEvent(evt);
                    stream.Write(bytes);
                    stream.WriteByte((byte)'\n');
                }

                await stream.FlushAsync(ct);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to write events to {eventPath}: {ex.Message}",
                    ex);
            }

            // Then update LastActivityAt and save index
            lock (_lock)
            {
                int index = _sessions.FindIndex(s => s.SessionId == sessionId);
                if (index >= 0)
                {
                    _sessions[index] = _sessions[index] with
                    {
                        LastActivityAt = DateTimeOffset.UtcNow
                    };
                }
            }

            SaveIndex();
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async IAsyncEnumerable<OmicronEvent> ReadEventsAsync(
        SessionId sessionId,
        EventSequenceRange range,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string eventPath = EventPath(sessionId);
        if (!File.Exists(eventPath))
        {
            yield break;
        }

        byte[] fileBytes;
        try
        {
            fileBytes = await File.ReadAllBytesAsync(eventPath, ct);
        }
        catch
        {
            yield break;
        }

        int offset = 0;
        while (offset < fileBytes.Length)
        {
            ct.ThrowIfCancellationRequested();

            // Find the next newline (or end of file for the last line)
            int remaining = fileBytes.Length - offset;
            int newlinePos = Array.IndexOf(fileBytes, (byte)'\n', offset, remaining);

            ReadOnlyMemory<byte> lineMem;
            if (newlinePos >= 0)
            {
                lineMem = fileBytes.AsMemory(offset, newlinePos - offset);
                offset = newlinePos + 1;
            }
            else
            {
                lineMem = fileBytes.AsMemory(offset);
                offset = fileBytes.Length;
            }

            // Skip empty lines
            if (lineMem.IsEmpty)
            {
                continue;
            }

            OmicronEvent? evt = OmicronEventJson.DeserializeEvent(lineMem);
            if (evt is null)
            {
                continue;
            }

            if (range.FromExclusive.HasValue && evt.Sequence <= range.FromExclusive.Value)
            {
                continue;
            }

            if (range.ToInclusive.HasValue && evt.Sequence > range.ToInclusive.Value)
            {
                yield break;
            }

            yield return evt;
        }
    }

    public async ValueTask<long> GetEventCountAsync(
        SessionId sessionId,
        CancellationToken ct = default)
    {
        string eventPath = EventPath(sessionId);
        if (!File.Exists(eventPath))
        {
            return 0L;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(eventPath, ct);
            return CountNonEmptyLines(bytes);
        }
        catch
        {
            return 0L;
        }
    }

    private string EventPath(SessionId sessionId)
    {
        return Path.Combine(StoreDirectory, $"{sessionId}.jsonl");
    }

    private void LoadIndex()
    {
        string indexPath = IndexPath;
        if (!File.Exists(indexPath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(indexPath);
            List<SessionRecord>? records = JsonSerializer.Deserialize<List<SessionRecord>>(json, _jsonOptions);
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
            string json = JsonSerializer.Serialize(_sessions, _jsonOptions);
            File.WriteAllText(IndexPath, json);
        }
    }

    private SemaphoreSlim GetSessionLock(SessionId sessionId)
    {
        return _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
    }

    private static int CountNonEmptyLines(byte[] bytes)
    {
        int count = 0;
        Span<byte> span = bytes.AsSpan();
        int start = 0;
        while (start < span.Length)
        {
            int end = span.Slice(start).IndexOf((byte)'\n');
            if (end < 0)
            {
                if (start < span.Length)
                {
                    count++;
                }

                break;
            }

            if (end > 0)
            {
                count++; // non-empty line
            }

            start = start + end + 1;
        }

        return count;
    }

    private void PruneEmptySessions()
    {
        lock (_lock)
        {
            var emptySessionIds = _sessions
                .Where(s => GetEventCountUnsafe(s.SessionId) == 0)
                .Select(s => s.SessionId)
                .ToList();

            if (emptySessionIds.Count == 0)
            {
                return;
            }

            foreach (SessionId sessionId in emptySessionIds)
            {
                _sessions.RemoveAll(s => s.SessionId == sessionId);
                string eventPath = EventPath(sessionId);
                try
                {
                    if (File.Exists(eventPath))
                    {
                        File.Delete(eventPath);
                    }
                }
                catch { }
            }
        }
    }

    private long GetEventCountUnsafe(SessionId sessionId)
    {
        string eventPath = EventPath(sessionId);
        if (!File.Exists(eventPath))
        {
            return 0;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(eventPath);
            return CountNonEmptyLines(bytes);
        }
        catch
        {
            return 0;
        }
    }
}
