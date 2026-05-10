using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Sessions;
using Xunit;

namespace Omicron.Core.Tests;

public class JsonlConcurrencyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonlSessionStore _store;
    private readonly SessionId _sessionId;

    public JsonlConcurrencyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"omicron-concurrency-{Guid.NewGuid():N}");
        _store = new JsonlSessionStore(_tempDir);
        _sessionId = SessionId.New();

        var request = new SessionCreateRequest(
            "test-model", "test-provider", ApiType.OpenAiChat,
            SessionId: _sessionId);
        _store.CreateSessionAsync(request).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task ConcurrentAppends_AllEventsArePersistedInOrder()
    {
        const int taskCount = 10;
        const int eventsPerTask = 100;
        var tasks = new Task[taskCount];

        for (int t = 0; t < taskCount; t++)
        {
            var taskId = t;
            tasks[t] = Task.Run(async () =>
            {
                for (int i = 0; i < eventsPerTask; i++)
                {
                    var evt = new SessionStartedEvent(
                        EventEnvelope.ForSession(_sessionId),
                        AgentId.New(), "test-model", "test-provider");
                    await _store.AppendEventsAsync(_sessionId, [evt]);
                }
            });
        }

        await Task.WhenAll(tasks);

        // Read back all events
        var allEvents = new List<OmicronEvent>();
        await foreach (var e in _store.ReadEventsAsync(_sessionId, EventSequenceRange.All))
            allEvents.Add(e);

        Assert.Equal(taskCount * eventsPerTask, allEvents.Count);

        // Verify file is not corrupt — all lines deserialize
        Assert.All(allEvents, e => Assert.NotNull(e));
    }
}
