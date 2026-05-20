using System.Buffers;
using System.Text.Json;
using Omicron.Core.Models;
using Omicron.Core.Providers;
using Omicron.Core.Sessions;
using Xunit;

namespace Omicron.Core.Tests;

public class OpenAiResponsesShapeTests
{
    private readonly OpenAiResponsesShape _shape = new();

    /// <summary>
    ///     Helper that serializes via <see cref="IApiShape.WriteRequestBody" /> and returns
    ///     a <see cref="JsonDocument" /> for assertion.  Replaces legacy BuildRequestBody tests.
    /// </summary>
    private JsonDocument WriteAndParse(
        Model model,
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        IReadOnlyList<Tool>? tools,
        ChatOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            _shape.WriteRequestBody(writer, model, messages, systemPrompt, tools, options);
        }

        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    [Fact]
    public void WriteRequestBody_BasicRequest_HasRequiredFields()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses
        };
        var messages = new List<Message>
        {
            Message.UserMessage("Hello")
        };
        var options = new ChatOptions();

        using JsonDocument doc = WriteAndParse(model, messages, null, null, options);
        JsonElement root = doc.RootElement;

        Assert.Equal("gpt-5.5", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.TryGetProperty("input", out _));
        Assert.True(root.TryGetProperty("metadata", out _));
    }

    [Fact]
    public void WriteRequestBody_WithSystemPrompt_SetsInstructions()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses
        };
        var messages = new List<Message>
        {
            Message.UserMessage("Hello")
        };
        var options = new ChatOptions();

        using JsonDocument doc = WriteAndParse(model,
            messages,
            "You are a helpful assistant.",
            null,
            options);
        JsonElement root = doc.RootElement;

        Assert.Equal("You are a helpful assistant.", root.GetProperty("instructions").GetString());
    }

    [Fact]
    public void WriteRequestBody_WithTools_IncludesToolArray()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses
        };
        var messages = new List<Message>
        {
            Message.UserMessage("Hello")
        };
        var options = new ChatOptions();
        var tools = new List<Tool>
        {
            new()
            {
                Name = "get_weather",
                Description = "Get the weather"
            }
        };

        using JsonDocument doc = WriteAndParse(model, messages, null, tools, options);
        JsonElement root = doc.RootElement;

        JsonElement[] toolArray = root.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Single(toolArray);
        Assert.Equal("get_weather", toolArray[0].GetProperty("name").GetString());
    }

    [Fact]
    public void WriteRequestBody_WithMaxTokens_SetsMaxOutputTokens()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses
        };
        var messages = new List<Message>
        {
            Message.UserMessage("Hello")
        };
        var options = new ChatOptions
        {
            MaxTokens = 4096
        };

        using JsonDocument doc = WriteAndParse(model, messages, null, null, options);
        JsonElement root = doc.RootElement;

        Assert.Equal(4096, root.GetProperty("max_output_tokens").GetInt32());
    }

    [Fact]
    public void WriteRequestBody_WithReasoningEffort()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses
        };
        var messages = new List<Message>
        {
            Message.UserMessage("Hello")
        };
        var options = new ChatOptions
        {
            ReasoningEffort = "high"
        };

        using JsonDocument doc = WriteAndParse(model, messages, null, null, options);
        JsonElement root = doc.RootElement;

        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void WriteRequestBody_StoragePolicy_AllowProviderStoredState_SetsStoreTrue()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses,
            StoragePolicy = ProviderStoragePolicy.AllowProviderStoredState
        };
        var messages = new List<Message>
        {
            Message.UserMessage("Hello")
        };
        var options = new ChatOptions();

        using JsonDocument doc = WriteAndParse(model, messages, null, null, options);
        JsonElement root = doc.RootElement;

        Assert.True(root.GetProperty("store").GetBoolean());
    }

    [Fact]
    public void WriteRequestBody_StoragePolicy_AllowProviderStateNoStore_SetsStoreFalse()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses,
            StoragePolicy = ProviderStoragePolicy.AllowProviderStateNoStore
        };
        var messages = new List<Message>
        {
            Message.UserMessage("Hello")
        };
        var options = new ChatOptions();

        using JsonDocument doc = WriteAndParse(model, messages, null, null, options);
        JsonElement root = doc.RootElement;

        Assert.False(root.GetProperty("store").GetBoolean());
    }

    [Fact]
    public void WriteRequestBody_StoragePolicy_PreferStateless_SetsStoreFalse()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses,
            StoragePolicy = ProviderStoragePolicy.PreferStateless
        };
        var messages = new List<Message>
        {
            Message.UserMessage("Hello")
        };
        var options = new ChatOptions();

        using JsonDocument doc = WriteAndParse(model, messages, null, null, options);
        JsonElement root = doc.RootElement;

        Assert.False(root.GetProperty("store").GetBoolean());
    }

    [Fact]
    public void WriteRequestBody_WithStatefulContext_IncludesPreviousResponseId()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses,
            StoragePolicy = ProviderStoragePolicy.AllowProviderStateNoStore
        };
        var messages = new List<Message>
        {
            Message.UserMessage("Hello")
        };
        var options = new ChatOptions
        {
            CurrentProviderState = new ProviderTurnState(default, "resp_123", null, null, null)
        };

        using JsonDocument doc = WriteAndParse(model, messages, null, null, options);
        JsonElement root = doc.RootElement;

        Assert.Equal("resp_123", root.GetProperty("previous_response_id").GetString());
    }

    [Fact]
    public void ParseSseChunk_OutputTextDelta_ReturnsTextEvent()
    {
        byte[] data = """{"type":"response.output_text.delta","delta":"Hello"}"""u8.ToArray();
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        StreamEvent? result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.TextDelta, result.Type);
        Assert.Equal("Hello", result.Delta);
    }

    [Fact]
    public void ParseSseChunk_OutputTextDelta_EmptyDelta_ReturnsNull()
    {
        byte[] data = """{"type":"response.output_text.delta","delta":""}"""u8.ToArray();
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        StreamEvent? result = _shape.ParseSseChunk(data, accumulators);

        Assert.Null(result);
    }

    [Fact]
    public void ParseSseChunk_FunctionCallArgumentsDelta_ReturnsToolCallDelta()
    {
        // JSON: {"type":"response.function_call_arguments.delta","delta":"partial","item_id":"item_1"}
        byte[] data =
            """{"type":"response.function_call_arguments.delta","delta":"partial","item_id":"item_1"}"""u8.ToArray();
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        StreamEvent? result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.ToolCallDelta, result.Type);
        Assert.Equal("partial", result.Delta);
    }

    [Fact]
    public void ParseSseChunk_FunctionCallArgumentsDone_ReturnsToolCallEnd()
    {
        // Use a regular string to properly escape JSON quotes within arguments value
        byte[] data =
            "{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"item_1\",\"call_id\":\"call_abc\",\"name\":\"get_weather\",\"arguments\":\"{\\\"location\\\":\\\"NYC\\\"}\"}"u8
                .ToArray();
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        StreamEvent? result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.ToolCallEnd, result.Type);
        Assert.NotNull(result.ToolCall);
        Assert.Equal("get_weather", result.ToolCall.Name);
        Assert.Equal("call_abc", result.ToolCall.Id);
    }

    [Fact]
    public void ParseSseChunk_OutputItemAdded_FunctionCall_ReturnsToolCallStart()
    {
        byte[] data =
            """{"type":"response.output_item.added","item":{"id":"item_1","type":"function_call","call_id":"call_abc","name":"get_weather"}}"""u8
                .ToArray();
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        StreamEvent? result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.ToolCallStart, result.Type);
        Assert.Equal("call_abc", result.ToolCallId);
        Assert.Equal("get_weather", result.ToolName);
    }

    [Fact]
    public void ParseSseChunk_Completed_ReturnsDoneEvent()
    {
        byte[] data =
            """{"type":"response.completed","response":{"id":"resp_123","status":"completed","usage":{"input_tokens":10,"output_tokens":20}}}"""u8
                .ToArray();
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        StreamEvent? result = _shape.ParseSseChunk(data, accumulators);

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
        byte[] data =
            """{"type":"response.completed","response":{"id":"resp_123","status":"failed","error":{"message":"Something went wrong"}}}"""u8
                .ToArray();
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        StreamEvent? result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.Error, result.Type);
        Assert.Contains("Something went wrong", result.ErrorMessage);
    }

    [Fact]
    public void ParseSseChunk_Completed_Incomplete_ReturnsLengthStopReason()
    {
        byte[] data =
            """{"type":"response.completed","response":{"id":"resp_123","status":"incomplete","usage":{"input_tokens":10,"output_tokens":100}}}"""u8
                .ToArray();
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        StreamEvent? result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.Done, result.Type);
        Assert.Equal(StopReason.Length, result.StopReason);
    }

    [Fact]
    public void ParseSseChunk_RefusalDelta_ReturnsTextWithReasoning()
    {
        byte[] data =
            """{"type":"response.refusal.delta","delta":"I cannot answer that."}"""u8.ToArray();
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        StreamEvent? result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.TextDelta, result.Type);
        Assert.Equal("I cannot answer that.", result.Delta);
        Assert.Equal("I cannot answer that.", result.ReasoningText);
    }

    [Fact]
    public void ParseSseChunk_Error_ReturnsErrorEvent()
    {
        byte[] data = """{"type":"error","message":"API rate limit exceeded"}"""u8.ToArray();
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        StreamEvent? result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.Error, result.Type);
        Assert.Contains("rate limit", result.ErrorMessage);
    }

    [Fact]
    public void ParseSseChunk_UnknownEventType_ReturnsNull()
    {
        byte[] data = """{"type":"unknown.event.type","data":"something"}"""u8.ToArray();
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        StreamEvent? result = _shape.ParseSseChunk(data, accumulators);

        Assert.Null(result);
    }

    [Fact]
    public void ParseSseChunk_InvalidJson_ReturnsError()
    {
        byte[] data = "this is not json"u8.ToArray();
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        StreamEvent? result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.Error, result.Type);
    }

    [Fact]
    public void ParseSseChunk_TopLevelError_ReturnsError()
    {
        byte[] data = """{"error":{"message":"Authentication failed"}}"""u8.ToArray();
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        StreamEvent? result = _shape.ParseSseChunk(data, accumulators);

        Assert.NotNull(result);
        Assert.Equal(StreamEventType.Error, result.Type);
        Assert.Contains("Authentication failed", result.ErrorMessage);
    }

    [Fact]
    public void WriteRequestBody_AssistantWithToolCallInput()
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
            Message.AssistantToolCallMessage(new ToolCallContent("call_1",
                "get_weather",
                new Dictionary<string, object?>
                {
                    ["location"] = "NYC"
                })),
            Message.ToolResultMessage("call_1", "get_weather", "Sunny, 25°C")
        };

        var options = new ChatOptions();
        using JsonDocument doc = WriteAndParse(model, messages, null, null, options);
        JsonElement root = doc.RootElement;

        JsonElement[] input = root.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(3, input.Length);

        Assert.Equal("user", input[0].GetProperty("role").GetString());
        Assert.Equal("What's the weather in NYC?", input[0].GetProperty("content").GetString());

        // Assistant tool calls are now top-level function_call items
        Assert.Equal("function_call", input[1].GetProperty("type").GetString());
        Assert.Equal("call_1", input[1].GetProperty("call_id").GetString());
        Assert.Equal("get_weather", input[1].GetProperty("name").GetString());

        // Tool result — Responses API uses function_call_output items
        Assert.Equal("function_call_output", input[2].GetProperty("type").GetString());
        Assert.Equal("call_1", input[2].GetProperty("call_id").GetString());
        Assert.Equal("Sunny, 25°C", input[2].GetProperty("output").GetString());
    }

    [Fact]
    public void WriteRequestBody_AssistantTextUsesResponsesMessageItemAndOmitsReasoning()
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
                TextData = "Hi there"u8,
                ReasoningData = "internal reasoning"u8
            }
        };

        using JsonDocument doc = WriteAndParse(model, messages, null, null, new ChatOptions());
        JsonElement root = doc.RootElement;
        JsonElement[] input = root.GetProperty("input").EnumerateArray().ToArray();

        Assert.Equal("message", input[1].GetProperty("type").GetString());
        Assert.Equal("assistant", input[1].GetProperty("role").GetString());
        Assert.Equal("output_text",
            input[1].GetProperty("content")[0].GetProperty("type").GetString());
        // Verify no reasoning content type appears
        foreach (JsonElement item in input[1].GetProperty("content").EnumerateArray())
        {
            Assert.NotEqual("reasoning", item.GetProperty("type").GetString());
        }
    }

    [Fact]
    public void ParseSseChunk_MultipleToolCallsInStream_UsesDistinctAccumulatorIndices()
    {
        var accumulators = new Dictionary<int, ToolCallAccumulator>();

        // First function call: output_item.added
        byte[] chunk1 =
            """{"type":"response.output_item.added","item":{"id":"item_1","type":"function_call","call_id":"call_abc","name":"get_weather"}}"""u8
                .ToArray();
        StreamEvent? r1 = _shape.ParseSseChunk(chunk1, accumulators);
        Assert.NotNull(r1);
        Assert.Equal(StreamEventType.ToolCallStart, r1.Type);
        Assert.Equal("get_weather", r1.ToolName);
        Assert.Single(accumulators);
        Assert.Equal(0, accumulators.Keys.First());

        // First function call: arguments.delta
        byte[] chunk2 =
            "{\"type\":\"response.function_call_arguments.delta\",\"delta\":\"partial\",\"item_id\":\"item_1\"}"u8
                .ToArray();
        StreamEvent? r2 = _shape.ParseSseChunk(chunk2, accumulators);
        Assert.NotNull(r2);
        Assert.Equal(StreamEventType.ToolCallDelta, r2.Type);

        // First function call: arguments.done
        byte[] chunk3 =
            "{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"item_1\",\"arguments\":\"{\\\"loc\\\":\\\"NYC\\\"}\"}"u8
                .ToArray();
        StreamEvent? r3 = _shape.ParseSseChunk(chunk3, accumulators);
        Assert.NotNull(r3);
        Assert.Equal(StreamEventType.ToolCallEnd, r3.Type);
        Assert.Equal("get_weather", r3.ToolCall?.Name);
        Assert.Empty(accumulators);

        // Second function call starts — must not collide with cleared index 0
        byte[] chunk4 =
            """{"type":"response.output_item.added","item":{"id":"item_2","type":"function_call","call_id":"call_def","name":"get_time"}}"""u8
                .ToArray();
        StreamEvent? r4 = _shape.ParseSseChunk(chunk4, accumulators);
        Assert.NotNull(r4);
        Assert.Equal(StreamEventType.ToolCallStart, r4.Type);
        Assert.Equal("get_time", r4.ToolName);
        Assert.Single(accumulators);
        // After clearing, Keys.Count == 0, so index 0 is used
        Assert.Contains(accumulators, kv => kv.Value.ItemId == "item_2");
        Assert.Equal("call_def", accumulators.Values.First().Id);
    }
}
