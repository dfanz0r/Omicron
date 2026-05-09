using System.Text.Json;
using System.Text.Json.Nodes;
using Omicron.Core.Models;
using Omicron.Core.Providers;
using Omicron.Core.Sessions;
using Xunit;

namespace Omicron.Core.Tests;

public class OpenAiResponsesShapeTests
{
    private readonly OpenAiResponsesShape _shape = new();

    [Fact]
    public void BuildRequestBody_BasicRequest_HasRequiredFields()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses
        };
        var messages = new List<Message> { Message.UserMessage("Hello") };
        var options = new ChatOptions();

        var body = _shape.BuildRequestBody(model, messages, null, null, options);

        Assert.Equal("gpt-5.5", body["model"]?.ToString());
        Assert.True((bool)body["stream"]!);
        Assert.NotNull(body["input"]);
        Assert.NotNull(body["metadata"]);
    }

    [Fact]
    public void BuildRequestBody_WithSystemPrompt_SetsInstructions()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses
        };
        var messages = new List<Message> { Message.UserMessage("Hello") };
        var options = new ChatOptions();

        var body = _shape.BuildRequestBody(model, messages, "You are a helpful assistant.", null, options);

        Assert.Equal("You are a helpful assistant.", body["instructions"]?.ToString());
    }

    [Fact]
    public void BuildRequestBody_WithTools_IncludesToolArray()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses
        };
        var messages = new List<Message> { Message.UserMessage("Hello") };
        var options = new ChatOptions();
        var tools = new List<Tool>
        {
            new() { Name = "get_weather", Description = "Get the weather" }
        };

        var body = _shape.BuildRequestBody(model, messages, null, tools, options);

        Assert.NotNull(body["tools"]);
        var toolArray = body["tools"]!.AsArray();
        Assert.Single(toolArray);
        Assert.Equal("get_weather", toolArray[0]!["name"]?.ToString());
    }

    [Fact]
    public void BuildRequestBody_WithMaxTokens_SetsMaxOutputTokens()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses
        };
        var messages = new List<Message> { Message.UserMessage("Hello") };
        var options = new ChatOptions { MaxTokens = 4096 };

        var body = _shape.BuildRequestBody(model, messages, null, null, options);

        Assert.Equal(4096, (int)body["max_output_tokens"]!);
    }

    [Fact]
    public void BuildRequestBody_WithReasoningEffort()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses
        };
        var messages = new List<Message> { Message.UserMessage("Hello") };
        var options = new ChatOptions { ReasoningEffort = "high" };

        var body = _shape.BuildRequestBody(model, messages, null, null, options);

        Assert.Equal("high", body["reasoning_effort"]?.ToString());
    }

    [Fact]
    public void BuildRequestBody_StoragePolicy_AllowProviderStoredState_SetsStoreTrue()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses,
            StoragePolicy = ProviderStoragePolicy.AllowProviderStoredState
        };
        var messages = new List<Message> { Message.UserMessage("Hello") };
        var options = new ChatOptions();

        var body = _shape.BuildRequestBody(model, messages, null, null, options);

        Assert.True((bool)body["store"]!);
    }

    [Fact]
    public void BuildRequestBody_StoragePolicy_AllowProviderStateNoStore_SetsStoreFalse()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses,
            StoragePolicy = ProviderStoragePolicy.AllowProviderStateNoStore
        };
        var messages = new List<Message> { Message.UserMessage("Hello") };
        var options = new ChatOptions();

        var body = _shape.BuildRequestBody(model, messages, null, null, options);

        Assert.False((bool)body["store"]!);
    }

    [Fact]
    public void BuildRequestBody_StoragePolicy_PreferStateless_SetsStoreFalse()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses,
            StoragePolicy = ProviderStoragePolicy.PreferStateless
        };
        var messages = new List<Message> { Message.UserMessage("Hello") };
        var options = new ChatOptions();

        var body = _shape.BuildRequestBody(model, messages, null, null, options);

        Assert.False((bool)body["store"]!);
    }

    [Fact]
    public void BuildRequestBody_WithStatefulContext_IncludesPreviousResponseId()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses,
            StoragePolicy = ProviderStoragePolicy.AllowProviderStateNoStore
        };
        var messages = new List<Message> { Message.UserMessage("Hello") };
        var options = new ChatOptions
        {
            CurrentProviderState = new ProviderTurnState(
                default, "resp_123", null, null, null)
        };

        var body = _shape.BuildRequestBody(model, messages, null, null, options);

        Assert.Equal("resp_123", body["previous_response_id"]?.ToString());
    }

    [Fact]
    public void ParseSseChunk_OutputTextDelta_ReturnsTextEvent()
    {
        var data = """{"type":"response.output_text.delta","delta":"Hello"}""";
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        var result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.TextDelta, result.Type);
        Assert.Equal("Hello", result.Delta);
    }

    [Fact]
    public void ParseSseChunk_OutputTextDelta_EmptyDelta_ReturnsNull()
    {
        var data = """{"type":"response.output_text.delta","delta":""}""";
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        var result = _shape.ParseSseChunk(data, accumulators);

        Assert.Null(result);
    }

    [Fact]
    public void ParseSseChunk_FunctionCallArgumentsDelta_ReturnsToolCallDelta()
    {
        // JSON: {"type":"response.function_call_arguments.delta","delta":"partial","item_id":"item_1"}
        var data = """{"type":"response.function_call_arguments.delta","delta":"partial","item_id":"item_1"}""";
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        var result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.ToolCallDelta, result.Type);
        Assert.Equal("partial", result.Delta);
    }

    [Fact]
    public void ParseSseChunk_FunctionCallArgumentsDone_ReturnsToolCallEnd()
    {
        // Use a regular string to properly escape JSON quotes within arguments value
        var data = "{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"item_1\",\"call_id\":\"call_abc\",\"name\":\"get_weather\",\"arguments\":\"{\\\"location\\\":\\\"NYC\\\"}\"}";
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        var result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.ToolCallEnd, result.Type);
        Assert.NotNull(result.ToolCall);
        Assert.Equal("get_weather", result.ToolCall.Name);
        Assert.Equal("call_abc", result.ToolCall.Id);
    }

    [Fact]
    public void ParseSseChunk_OutputItemAdded_FunctionCall_ReturnsToolCallStart()
    {
        var data = """{"type":"response.output_item.added","item":{"id":"item_1","type":"function_call","call_id":"call_abc","name":"get_weather"}}""";
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        var result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.ToolCallStart, result.Type);
        Assert.Equal("call_abc", result.ToolCallId);
        Assert.Equal("get_weather", result.ToolName);
    }

    [Fact]
    public void ParseSseChunk_Completed_ReturnsDoneEvent()
    {
        var data = """{"type":"response.completed","response":{"id":"resp_123","status":"completed","usage":{"input_tokens":10,"output_tokens":20}}}""";
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        var result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.Done, result.Type);
        Assert.Equal(StopReason.Stop, result.StopReason);
        Assert.Equal("resp_123", result.Delta);
        Assert.NotNull(result.Usage);
        Assert.Equal(10, result.Usage.InputTokens);
        Assert.Equal(20, result.Usage.OutputTokens);
    }

    [Fact]
    public void ParseSseChunk_Completed_Failed_ReturnsErrorEvent()
    {
        var data = """{"type":"response.completed","response":{"id":"resp_123","status":"failed","error":{"message":"Something went wrong"}}}""";
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        var result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.Error, result.Type);
        Assert.Contains("Something went wrong", result.ErrorMessage);
    }

    [Fact]
    public void ParseSseChunk_Completed_Incomplete_ReturnsLengthStopReason()
    {
        var data = """{"type":"response.completed","response":{"id":"resp_123","status":"incomplete","usage":{"input_tokens":10,"output_tokens":100}}}""";
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        var result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.Done, result.Type);
        Assert.Equal(StopReason.Length, result.StopReason);
    }

    [Fact]
    public void ParseSseChunk_RefusalDelta_ReturnsTextWithReasoning()
    {
        var data = """{"type":"response.refusal.delta","delta":"I cannot answer that."}""";
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        var result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.TextDelta, result.Type);
        Assert.Equal("I cannot answer that.", result.Delta);
        Assert.Equal("I cannot answer that.", result.ReasoningText);
    }

    [Fact]
    public void ParseSseChunk_Error_ReturnsErrorEvent()
    {
        var data = """{"type":"error","message":"API rate limit exceeded"}""";
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        var result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.Error, result.Type);
        Assert.Contains("rate limit", result.ErrorMessage);
    }

    [Fact]
    public void ParseSseChunk_UnknownEventType_ReturnsNull()
    {
        var data = """{"type":"unknown.event.type","data":"something"}""";
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        var result = _shape.ParseSseChunk(data, accumulators);

        Assert.Null(result);
    }

    [Fact]
    public void ParseSseChunk_InvalidJson_ReturnsError()
    {
        var data = "this is not json";
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        var result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.Error, result.Type);
    }

    [Fact]
    public void ParseSseChunk_TopLevelError_ReturnsError()
    {
        var data = """{"error":{"message":"Authentication failed"}}""";
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        var result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.Error, result.Type);
        Assert.Contains("Authentication failed", result.ErrorMessage);
    }

    [Fact]
    public void BuildRequestBody_AssistantWithToolCallInput()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses
        };

        var messages = new List<Message>
        {
            Message.UserMessage("What's the weather in NYC?"),
            Message.AssistantToolCallMessage(
                new ToolCallContent("call_1", "get_weather", new Dictionary<string, object?> { ["location"] = "NYC" })),
            Message.ToolResultMessage("call_1", "get_weather", "Sunny, 25°C")
        };

        var options = new ChatOptions();
        var body = _shape.BuildRequestBody(model, messages, null, null, options);

        var input = body["input"]!.AsArray();
        Assert.Equal(3, input.Count);

        Assert.Equal("user", input[0]!["role"]?.ToString());
        Assert.Equal("What's the weather in NYC?", input[0]!["content"]?.ToString());

        // Assistant tool calls are now top-level function_call items
        Assert.Equal("function_call", input[1]!["type"]?.ToString());
        Assert.Equal("call_1", input[1]!["id"]?.ToString());
        Assert.Equal("call_1", input[1]!["call_id"]?.ToString());
        Assert.Equal("get_weather", input[1]!["name"]?.ToString());

        // Tool result — Responses API uses function_call_output items
        Assert.Equal("function_call_output", input[2]!["type"]?.ToString());
        Assert.Equal("call_1", input[2]!["call_id"]?.ToString());
        Assert.Equal("Sunny, 25°C", input[2]!["output"]?.ToString());
    }

    [Fact]
    public void BuildRequestBody_AssistantTextUsesResponsesMessageItemAndOmitsReasoning()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses
        };
        var messages = new List<Message>
        {
            Message.UserMessage("Hello"),
            new()
            {
                Role = MessageRole.Assistant,
                Text = "Hi there",
                Reasoning = "internal reasoning"
            }
        };

        var body = _shape.BuildRequestBody(model, messages, null, null, new ChatOptions());

        var input = body["input"]!.AsArray();
        Assert.Equal("message", input[1]!["type"]?.ToString());
        Assert.Equal("assistant", input[1]!["role"]?.ToString());
        Assert.Equal("output_text", input[1]!["content"]!.AsArray()[0]!["type"]?.ToString());
        Assert.DoesNotContain(input[1]!["content"]!.AsArray(), item => item!["type"]?.ToString() == "reasoning");
    }

    [Fact]
    public void ParseSseChunk_MultipleToolCallsInStream_UsesDistinctAccumulatorIndices()
    {
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        // First function call: output_item.added
        var chunk1 = """{"type":"response.output_item.added","item":{"id":"item_1","type":"function_call","call_id":"call_abc","name":"get_weather"}}""";
        var r1 = _shape.ParseSseChunk(chunk1, accumulators);
        Assert.NotNull(r1);
        Assert.Equal(StreamEventType.ToolCallStart, r1.Type);
        Assert.Equal("get_weather", r1.ToolName);
        Assert.Single(accumulators);
        Assert.Equal(0, accumulators.Keys.First());

        // First function call: arguments.delta
        var chunk2 = "{\"type\":\"response.function_call_arguments.delta\",\"delta\":\"partial\",\"item_id\":\"item_1\"}";
        var r2 = _shape.ParseSseChunk(chunk2, accumulators);
        Assert.NotNull(r2);
        Assert.Equal(StreamEventType.ToolCallDelta, r2.Type);

        // First function call: arguments.done
        var chunk3 = "{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"item_1\",\"arguments\":\"{\\\"loc\\\":\\\"NYC\\\"}\"}";
        var r3 = _shape.ParseSseChunk(chunk3, accumulators);
        Assert.NotNull(r3);
        Assert.Equal(StreamEventType.ToolCallEnd, r3.Type);
        Assert.Equal("get_weather", r3.ToolCall?.Name);
        Assert.Empty(accumulators);

        // Second function call starts — must not collide with cleared index 0
        var chunk4 = """{"type":"response.output_item.added","item":{"id":"item_2","type":"function_call","call_id":"call_def","name":"get_time"}}""";
        var r4 = _shape.ParseSseChunk(chunk4, accumulators);
        Assert.NotNull(r4);
        Assert.Equal(StreamEventType.ToolCallStart, r4.Type);
        Assert.Equal("get_time", r4.ToolName);
        Assert.Single(accumulators);
        // After clearing, Keys.Count == 0, so index 0 is used
        Assert.Contains(accumulators, kv => kv.Value.ItemId == "item_2");
        Assert.Equal("call_def", accumulators.Values.First().Id);
    }
}
