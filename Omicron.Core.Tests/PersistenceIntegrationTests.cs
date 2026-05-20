using System.Text.Json;
using Omicron.Core.Content;
using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Providers;
using Omicron.Core.Sessions;
using Omicron.Core.Workspace;
using Xunit;

namespace Omicron.Core.Tests;

public class PersistenceIntegrationTests
{
    [Fact]
    public async Task OmicronHost_CreateSession_CreatesSessionRecord()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var store = (InMemorySessionStore)host.SessionStore;

        var model = new Model
        {
            Id = "test-model",
            Name = "Test",
            ProviderName = "fake",
            ApiType = ApiType.OpenAiChat,
            Provider = new FakeProvider
            {
                Responses =
                {
                    () =>
                        Task.FromResult(new LlmResult
                        {
                            Text = "Hello",
                            StopReason = StopReason.Stop
                        })
                }
            }
        };

        AgentSession session = host.CreateSession(SessionConfig.Create(model, "You are a test."));

        SessionRecord? record = await store.GetSessionAsync(session.Id);
        Assert.NotNull(record);
        Assert.Equal(session.Id, record.SessionId);
        Assert.Equal("test-model", record.ModelId);
        Assert.Equal("fake", record.ProviderName);
        Assert.Equal(ApiType.OpenAiChat, record.ApiType);
        Assert.Equal("You are a test.", record.SystemPrompt);
        Assert.Equal(SessionStatus.Active, record.Status);
    }

    [Fact]
    public async Task SessionEvents_ArePersistedToStore()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var store = (InMemorySessionStore)host.SessionStore;

        var model = new Model
        {
            Id = "test-model",
            Name = "Test",
            ProviderName = "fake",
            ApiType = ApiType.OpenAiChat,
            Provider = new FakeProvider
            {
                Responses =
                {
                    () =>
                        Task.FromResult(new LlmResult
                        {
                            Text = "Response text",
                            StopReason = StopReason.Stop,
                            Usage = new UsageInfo(10, 20),
                            ResponseId = "test_resp_1"
                        })
                }
            }
        };

        AgentSession session = host.CreateSession(model);

        await foreach (OmicronEvent _ in session.PromptAsync("Hello")) { }

        // Read persisted events from the store
        var persisted = new List<OmicronEvent>();
        await foreach (OmicronEvent e in store.ReadEventsAsync(session.Id, EventSequenceRange.All))
        {
            persisted.Add(e);
        }

        // Should have at least: SessionStarted, UserMessage, TurnStarted, AssistantTextDelta, AssistantResponseComplete
        Assert.Contains(persisted, e => e is SessionStartedEvent);
        Assert.Contains(persisted, e => e is UserMessageEvent u && u.Text.ToString() == "Hello");
        Assert.Contains(persisted,
            e =>
                e is AssistantResponseCompleteEvent arc
                && arc.FullText.ToString() == "Response text");

        // Verify sequence order is preserved
        for (int i = 1; i < persisted.Count; i++)
        {
            Assert.True(persisted[i - 1].Sequence < persisted[i].Sequence);
        }
    }

    [Fact]
    public async Task ProviderStateEvents_ArePersistedViaHostSink()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var store = (InMemorySessionStore)host.SessionStore;

        var model = new Model
        {
            Id = "test-model",
            Name = "Test",
            ProviderName = "fake",
            StoragePolicy = ProviderStoragePolicy.AllowProviderStateNoStore,
            ApiType = ApiType.OpenAiResponses,
            Provider = new FakeProvider
            {
                Responses =
                {
                    () =>
                        Task.FromResult(new LlmResult
                        {
                            Text = "Hello",
                            StopReason = StopReason.Stop,
                            ResponseId = "resp_abc123"
                        })
                }
            }
        };

        AgentSession session = host.CreateSession(model);

        // ProviderStateManager is wired to Events which is the persistent sink
        var key = ProviderStateKey.Create(session.Id,
            session.AgentId,
            "fake",
            "test-model",
            ApiType.OpenAiResponses);

        // Manually set provider state (simulating what AgentSession does)
        host.ProviderStateManager.Set(new ProviderTurnState(key, "resp_abc123", null, null, null),
            "response_completed");

        // Read persisted events
        var persisted = new List<OmicronEvent>();
        await foreach (OmicronEvent e in store.ReadEventsAsync(session.Id, EventSequenceRange.All))
        {
            persisted.Add(e);
        }

        Assert.Contains(persisted, e => e is ProviderStateUpdatedEvent);
    }

    [Fact]
    public async Task ExecutionEvents_ArePersistedViaHostSink()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var store = (InMemorySessionStore)host.SessionStore;

        var model = new Model
        {
            Id = "test-model",
            Name = "Test",
            ProviderName = "fake",
            ApiType = ApiType.OpenAiChat,
            Provider = new FakeProvider()
        };

        AgentSession session = host.CreateSession(model);
        var key = ProviderStateKey.Create(session.Id,
            session.AgentId,
            "fake",
            "test-model",
            ApiType.OpenAiChat);

        // Emit an execution event via the host's event sink (which is PersistentEventSink)
        host.Events.Emit(new ExecutionStartedEvent(
            new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, session.Id),
            "echo test",
            "/tmp"));

        var persisted = new List<OmicronEvent>();
        await foreach (OmicronEvent e in store.ReadEventsAsync(session.Id, EventSequenceRange.All))
        {
            persisted.Add(e);
        }

        Assert.Contains(persisted, e => e is ExecutionStartedEvent);
    }

    [Fact]
    public async Task PromptAsyncRun_PersistsAllEventsInOrder()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var store = (InMemorySessionStore)host.SessionStore;

        var model = new Model
        {
            Id = "test-model",
            Name = "Test",
            ProviderName = "fake",
            ApiType = ApiType.OpenAiChat,
            Provider = new FakeProvider
            {
                Responses =
                {
                    () =>
                        Task.FromResult(new LlmResult
                        {
                            Text = "Hello!",
                            StopReason = StopReason.Stop,
                            Usage = new UsageInfo(5, 15)
                        })
                }
            }
        };

        AgentSession session = host.CreateSession(model);

        var yieldedEvents = new List<OmicronEvent>();
        await foreach (OmicronEvent evt in session.PromptAsync("Hi"))
        {
            yieldedEvents.Add(evt);
        }

        // Read persisted
        var persisted = new List<OmicronEvent>();
        await foreach (OmicronEvent e in store.ReadEventsAsync(session.Id, EventSequenceRange.All))
        {
            persisted.Add(e);
        }

        // Every yielded event should also be in the store (persisted)
        foreach (OmicronEvent yielded in yieldedEvents)
        {
            Assert.Contains(persisted, p => p.Id == yielded.Id);
        }

        // Persisted events should be exactly in sequence order
        for (int i = 1; i < persisted.Count; i++)
        {
            Assert.True(persisted[i - 1].Sequence < persisted[i].Sequence);
        }

        // Verify we have the full conversation lifecycle
        var types = persisted.Select(e => e.GetType().Name).Distinct().ToList();
        Assert.Contains("SessionStartedEvent", types);
        Assert.Contains("UserMessageEvent", types);
        Assert.Contains("TurnStartedEvent", types);
        Assert.Contains("AssistantTextDeltaEvent", types);
        Assert.Contains("AssistantResponseCompleteEvent", types);
    }

    [Fact]
    public async Task PersistentEventSink_ReportsFailuresViaCallback()
    {
        var innerSink = new InMemoryEventSink();
        var store = new InMemorySessionStore();
        var sink = new PersistentEventSink(innerSink, store);

        Exception? capturedEx = null;
        OmicronEvent? capturedEvent = null;
        string? capturedLabel = null;
        sink.OnError = (ex, evt, label) =>
        {
            capturedEx = ex;
            capturedEvent = evt;
            capturedLabel = label;
        };

        var evt = new SessionStartedEvent(new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, SessionId.New()),
            AgentId.New(),
            "test",
            "test");

        // No session record created yet — AppendEventsAsync should throw
        sink.Emit(evt);

        Assert.NotNull(capturedEx);
        Assert.NotNull(capturedEvent);
        Assert.Equal("Emit", capturedLabel);
        Assert.Equal(1, sink.FailureCount);
        Assert.Equal(0, sink.PersistedCount);
    }

    [Fact]
    public async Task PersistentEventSink_CountsSuccessfulPersists()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var persistentSink = (PersistentEventSink)host.Events;

        var model = new Model
        {
            Id = "test-model",
            Name = "Test",
            ProviderName = "fake",
            ApiType = ApiType.OpenAiChat,
            Provider = new FakeProvider
            {
                Responses =
                {
                    () =>
                        Task.FromResult(new LlmResult
                        {
                            Text = "Hi",
                            StopReason = StopReason.Stop
                        })
                }
            }
        };

        AgentSession session = host.CreateSession(model);
        await foreach (OmicronEvent _ in session.PromptAsync("Hello")) { }

        Assert.True(persistentSink.PersistedCount > 0);
        Assert.Equal(0, persistentSink.FailureCount);
    }

    /// <summary>
    ///     Regression test for F1: ensures ToolInvocationCompletedEvent with structured
    ///     IContentBlock blocks survives JSONL round-trip without serialization errors.
    /// </summary>
    [Fact]
    public async Task ToolInvocationCompletedEvent_WithContentBlocks_RoundTrip()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"omicron-persist-tic-{Guid.NewGuid():N}");

        {
            using var store = new JsonlSessionStore(tempDir);
            SessionRecord record = await store.CreateSessionAsync(
                new SessionCreateRequest("gpt-4o", "openai", ApiType.OpenAiChat));

            var toolCallId = new ToolCallId("call_abc123");

            var blocks = new List<IContentBlock>
            {
                new FilePreviewContentBlock("src/Foo.cs",
                    "public class Foo { }",
                    100,
                    1,
                    false),
                new CodeContentBlock("int x = 1;", "csharp", "src/Bar.cs"),
                new ErrorContentBlock("something went wrong", "at line 42"),
                new PlainTextContentBlock("plain message"),
                new MarkdownContentBlock("# Title\n\n**bold**"),
                new DiffContentBlock("@@ -1,1 +1,1 @@\n-old\n+new",
                    "src/file.cs"),
                new ToolCallContentBlock("call_nested",
                    "read_path",
                    new Dictionary<string, object?>
                    {
                        ["path"] = "test.txt"
                    })
            };

            var evt = new ToolInvocationCompletedEvent(
                new EventEnvelope(EventId.New(), 0, DateTimeOffset.UtcNow, record.SessionId),
                toolCallId,
                "read_path",
                "Result text"u8,
                false,
                blocks);

            // Persist
            await store.AppendEventsAsync(record.SessionId, new List<OmicronEvent>
            {
                evt
            });

            // Dispose store before reading back (forces flush, closes files)
        }

        // Read back from a fresh store instance (explicit scope for disposal before cleanup)
        {
            using var reopened = new JsonlSessionStore(tempDir);
            IReadOnlyList<SessionRecord> sessions = await reopened.ListSessionsAsync(new SessionListQuery());
            SessionId sessionId = Assert.Single(sessions).SessionId;

            var events = new List<OmicronEvent>();
            await foreach (OmicronEvent e in reopened.ReadEventsAsync(sessionId, EventSequenceRange.All))
            {
                events.Add(e);
            }

            OmicronEvent deserialized = Assert.Single(events);
            ToolInvocationCompletedEvent tic = Assert.IsType<ToolInvocationCompletedEvent>(deserialized);
            Assert.Equal("call_abc123", tic.ToolCallId.Value);
            Assert.Equal("read_path", tic.ToolName);
            Assert.NotNull(tic.Blocks);
            Assert.Equal(7, tic.Blocks.Count);

            // Verify first block: FilePreviewContentBlock
            FilePreviewContentBlock fp = Assert.IsType<FilePreviewContentBlock>(tic.Blocks[0]);
            Assert.Equal("src/Foo.cs", fp.Path);
            Assert.Equal("public class Foo { }", fp.Text);
            Assert.Equal(100, fp.Size);
            Assert.Equal(1, fp.LineCount);
            Assert.False(fp.IsBinary);

            // Verify second block: CodeContentBlock
            CodeContentBlock code = Assert.IsType<CodeContentBlock>(tic.Blocks[1]);
            Assert.Equal("int x = 1;", code.Text);
            Assert.Equal("csharp", code.Language);
            Assert.Equal("src/Bar.cs", code.Path);

            // Verify third block: ErrorContentBlock
            ErrorContentBlock err = Assert.IsType<ErrorContentBlock>(tic.Blocks[2]);
            Assert.Equal("something went wrong", err.Text);
            Assert.Equal("at line 42", err.Details);

            // Verify fourth block: PlainTextContentBlock
            PlainTextContentBlock pt = Assert.IsType<PlainTextContentBlock>(tic.Blocks[3]);
            Assert.Equal("plain message", pt.Text);

            // Verify fifth block: MarkdownContentBlock
            MarkdownContentBlock md = Assert.IsType<MarkdownContentBlock>(tic.Blocks[4]);
            Assert.Contains("Title", md.Text);

            // Verify sixth block: DiffContentBlock
            DiffContentBlock diff = Assert.IsType<DiffContentBlock>(tic.Blocks[5]);
            Assert.Contains("-old", diff.Text);
            Assert.Equal("src/file.cs", diff.Path);

            // Verify seventh block: ToolCallContentBlock
            ToolCallContentBlock tc = Assert.IsType<ToolCallContentBlock>(tic.Blocks[6]);
            Assert.Equal("call_nested", tc.ToolCallId);
            Assert.Equal("read_path", tc.ToolName);
            Assert.NotNull(tc.Arguments);
            Assert.True(tc.Arguments.ContainsKey("path"));
            // JsonElement round-trips as JsonElement, not native string
            object? pathValue = tc.Arguments["path"];
            Assert.NotNull(pathValue);
            // Accept either string or JsonElement (System.Text.Json deserialization)
            if (pathValue is JsonElement je)
            {
                Assert.Equal("test.txt", je.GetString());
            }
            else
            {
                Assert.Equal("test.txt", pathValue as string);
            }
        }

        // Cleanup after store is disposed
        try
        {
            Directory.Delete(tempDir, true);
        }
        catch { }
    }

    [Fact]
    public async Task OmicronHost_TransactionManager_CreatesWorkingTransaction()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        IWorkspaceTransaction tx = host.WorkspaceTransactions.BeginTransaction();

        Assert.NotNull(tx);
        Assert.IsAssignableFrom<IWorkspaceTransaction>(tx);

        // Write through overlay
        var path = new WorkspacePath("_tx_test.txt");
        await tx.Files.WriteFileAsync(path, "tx content"u8.ToArray());

        // Verify visible in overlay
        ReadOnlyMemory<byte> content = await tx.Files.ReadFileAsync(path);
        Assert.Equal("tx content"u8.ToArray(), content.ToArray());

        // Verify not in host yet
        FileStat? hostStat = await host.FileSystem.StatAsync(path);
        Assert.Null(hostStat);

        // Commit
        await tx.CommitAsync();

        // Verify in host after commit
        hostStat = await host.FileSystem.StatAsync(path);
        Assert.NotNull(hostStat);

        // Cleanup
        await host.FileSystem.DeleteAsync(path);
    }
}
