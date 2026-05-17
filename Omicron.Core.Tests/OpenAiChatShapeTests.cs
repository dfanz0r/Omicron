using Omicron.Core.Providers;
using Xunit;

namespace Omicron.Core.Tests;

public sealed class OpenAiChatShapeTests
{
    [Fact]
    public void ParseSseChunk_RepeatedToolFinishChunk_DoesNotEmitSameToolCallTwice()
    {
        var shape = new OpenAiChatShape();
        var accumulators = new Dictionary<int, ToolCallAccumulator>
        {
            [0] = new() { Id = "call_1", Name = "read_path" }
        };
        accumulators[0].Args.Append("{\"path\":\"test_lines.txt\"}");

        const string finishChunk = "{\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}";

        var first = shape.ParseSseChunk(finishChunk, accumulators);
        var second = shape.ParseSseChunk(finishChunk, accumulators);

        Assert.NotNull(first);
        Assert.Equal(StreamEventType.ToolCallEnd, first.Type);
        Assert.Equal("call_1", first.ToolCall?.Id);
        Assert.Null(second?.ToolCall);
        Assert.NotEqual(StreamEventType.ToolCallEnd, second?.Type);
    }

    [Fact]
    public void ParseSseChunk_FinishChunk_EmitsOnlyFirstToolCall_LeavesRemainingForDrain()
    {
        var shape = new OpenAiChatShape();
        var accumulators = new Dictionary<int, ToolCallAccumulator>
        {
            [0] = new() { Id = "call_1", Name = "read_path" },
            [1] = new() { Id = "call_2", Name = "write_file" }
        };
        accumulators[0].Args.Append("{\"path\":\"a.txt\"}");
        accumulators[1].Args.Append("{\"content\":\"hello\"}");

        const string finishChunk = "{\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}";

        // First call emits only call_1
        var first = shape.ParseSseChunk(finishChunk, accumulators);
        Assert.NotNull(first);
        Assert.Equal(StreamEventType.ToolCallEnd, first.Type);
        Assert.Equal("call_1", first.ToolCall?.Id);
        Assert.Equal("read_path", first.ToolCall?.Name);

        // call_1 was removed, call_2 remains for the post-stream drain
        Assert.Single(accumulators);
        Assert.Contains(1, accumulators.Keys);
        Assert.Equal("call_2", accumulators[1].Id);

        // Second call emits call_2 (simulating the drain loop)
        var second = shape.ParseSseChunk(finishChunk, accumulators);
        Assert.NotNull(second);
        Assert.Equal(StreamEventType.ToolCallEnd, second.Type);
        Assert.Equal("call_2", second.ToolCall?.Id);
        Assert.Equal("write_file", second.ToolCall?.Name);

        // Now empty — third call emits Done
        Assert.Empty(accumulators);
        var third = shape.ParseSseChunk(finishChunk, accumulators);
        Assert.NotNull(third);
        Assert.Equal(StreamEventType.Done, third.Type);
    }
}
