using System.Runtime.InteropServices;
using Omicron.Core.Events;
using Omicron.Core.Execution;
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

        string shell = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd" : "sh";

        var request = new ExecutionRequest(sessionId,
            "echo hello",
            shell,
            null,
            10,
            new ToolCallId("tc_1"));

        ExecutionResult result = await broker.ExecuteAsync(request);

        IReadOnlyList<OmicronEvent> log = sink.GetAllEvents();
        Assert.Contains(log, e => e is ExecutionStartedEvent);
        Assert.Contains(log, e => e is ExecutionCompletedEvent);

        ExecutionStartedEvent startEvent = log.OfType<ExecutionStartedEvent>().Single();
        Assert.Equal(sessionId, startEvent.SessionId);
        Assert.Equal(new ToolCallId("tc_1"), startEvent.ToolCallId);

        ExecutionCompletedEvent completeEvent = log.OfType<ExecutionCompletedEvent>().Single();
        Assert.Equal(sessionId, completeEvent.SessionId);
        Assert.Equal(new ToolCallId("tc_1"), completeEvent.ToolCallId);
    }

    [Fact]
    public async Task LocalExecutionBroker_EmitsCompletionOnUnknownShell()
    {
        var sink = new InMemoryEventSink();
        var broker = new LocalExecutionBroker(sink);
        var sessionId = SessionId.New();

        var request = new ExecutionRequest(sessionId,
            "some command",
            "nonexistent_shell_xyz",
            null,
            10);

        ExecutionResult result = await broker.ExecuteAsync(request);

        IReadOnlyList<OmicronEvent> log = sink.GetAllEvents();
        Assert.Contains(log, e => e is ExecutionStartedEvent);
        Assert.Contains(log, e => e is ExecutionCompletedEvent);
        Assert.StartsWith("Error", result.Output);
    }
}
