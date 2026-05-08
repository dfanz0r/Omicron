using System.Runtime.InteropServices;
using Omicron.Core.Events;
using Omicron.Core.Execution;
using Omicron.Core.Sessions;
using Xunit;

namespace Omicron.Core.Tests;

public class ExecutionBrokerTests
{
    [Fact]
    public async Task LocalExecutionBroker_EmitsExecutionEvents()
    {
        var sink = new InMemoryEventSink();
        var broker = new LocalExecutionBroker(sink);
        var sessionId = SessionId.New();

        var shell = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd" : "sh";

        var request = new ExecutionRequest(
            sessionId, "echo hello", shell, null, 10, new ToolCallId("tc_1"));

        var result = await broker.ExecuteAsync(request);

        var log = sink.GetAllEvents();
        Assert.Contains(log, e => e is ExecutionStartedEvent);
        Assert.Contains(log, e => e is ExecutionCompletedEvent);

        var startEvent = log.OfType<ExecutionStartedEvent>().Single();
        Assert.Equal(sessionId, startEvent.SessionId);
        Assert.Equal(new ToolCallId("tc_1"), startEvent.ToolCallId);

        var completeEvent = log.OfType<ExecutionCompletedEvent>().Single();
        Assert.Equal(sessionId, completeEvent.SessionId);
        Assert.Equal(new ToolCallId("tc_1"), completeEvent.ToolCallId);
    }

    [Fact]
    public async Task LocalExecutionBroker_EmitsCompletionOnUnknownShell()
    {
        var sink = new InMemoryEventSink();
        var broker = new LocalExecutionBroker(sink);
        var sessionId = SessionId.New();

        var request = new ExecutionRequest(
            sessionId, "some command", "nonexistent_shell_xyz", null, 10);

        var result = await broker.ExecuteAsync(request);

        var log = sink.GetAllEvents();
        Assert.Contains(log, e => e is ExecutionStartedEvent);
        Assert.Contains(log, e => e is ExecutionCompletedEvent);
        Assert.StartsWith("Error", result.Output);
    }
}
