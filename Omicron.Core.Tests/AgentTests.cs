using System.Text.Json;
using Omicron.Core.Models;
using Xunit;

namespace Omicron.Core.Tests;

/// <summary>
/// Tests for core model types: Message, ToolSchema, Model, LlmResult, UsageInfo.
/// </summary>
public class CoreModelTests
{
    [Fact]
    public void CreateMessage_UserMessage_HasCorrectRole()
    {
        var msg = Message.UserMessage("Hello");
        Assert.Equal(MessageRole.User, msg.Role);
        Assert.Equal("Hello", msg.GetTextString());
    }

    [Fact]
    public void CreateMessage_AssistantMessage_HasCorrectRole()
    {
        var msg = Message.AssistantMessage("Hi there");
        Assert.Equal(MessageRole.Assistant, msg.Role);
        Assert.Equal("Hi there", msg.GetTextString());
    }

    [Fact]
    public void CreateMessage_AssistantToolCallMessage_HasToolCall()
    {
        var toolCall = new ToolCallContent("call_1", "get_weather", new Dictionary<string, object?>
        {
            ["city"] = "London"
        });
        var msg = Message.AssistantToolCallMessage(toolCall);

        Assert.Equal(MessageRole.Assistant, msg.Role);
        Assert.NotNull(msg.ToolCall);
        Assert.Equal("call_1", msg.ToolCall.Id);
        Assert.Equal("get_weather", msg.ToolCall.Name);
    }

    [Fact]
    public void CreateMessage_ToolResultMessage_HasCorrectFields()
    {
        var msg = Message.ToolResultMessage("call_1", "get_weather", "Sunny, 22°C");

        Assert.Equal(MessageRole.ToolResult, msg.Role);
        Assert.Equal("call_1", msg.ToolCallId);
        Assert.Equal("get_weather", msg.ToolName);
        Assert.Equal("Sunny, 22°C", msg.GetTextString());
        Assert.False(msg.IsError);
    }

    [Fact]
    public void CreateMessage_ToolResultMessage_WithError()
    {
        var msg = Message.ToolResultMessage("call_1", "get_weather", "Failed", isError: true);

        Assert.True(msg.IsError);
        Assert.Equal("Failed", msg.GetTextString());
    }

    [Fact]
    public void ToolSchema_StringProperty_GeneratesValidJson()
    {
        var el = ToolSchema.StringProperty("A test string");
        var json = el.GetRawText();

        Assert.Contains("\"type\":\"string\"", json);
        Assert.Contains("\"description\":\"A test string\"", json);
    }

    [Fact]
    public void ToolSchema_Object_CreatesParameterSchema()
    {
        var schema = ToolSchema.Object(
            new Dictionary<string, JsonElement>
            {
                ["name"] = ToolSchema.StringProperty("The name"),
                ["age"] = ToolSchema.IntegerProperty("The age")
            },
            required: new[] { "name" }
        );

        var json = schema.GetRawText();
        Assert.Contains("\"type\":\"object\"", json);
        Assert.Contains("\"name\"", json);
        Assert.Contains("\"age\"", json);
        Assert.Contains("\"required\"", json);
    }

    [Fact]
    public void Message_Timestamps_AreSet()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var msg = Message.UserMessage("test");
        var after = DateTime.UtcNow.AddSeconds(1);

        Assert.InRange(msg.Timestamp, before, after);
    }

    [Fact]
    public void UsageInfo_RecordsTokens()
    {
        var usage = new UsageInfo(InputTokens: 10, OutputTokens: 20);
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(20, usage.OutputTokens);
        Assert.Null(usage.CacheReadTokens);
        Assert.Null(usage.CacheWriteTokens);
    }

    [Fact]
    public void UsageInfo_WithCache()
    {
        var usage = new UsageInfo(InputTokens: 10, OutputTokens: 20, CacheReadTokens: 5, CacheWriteTokens: 3);
        Assert.Equal(5, usage.CacheReadTokens);
        Assert.Equal(3, usage.CacheWriteTokens);
    }

    [Fact]
    public void LlmResult_Defaults()
    {
        var result = new LlmResult();
        Assert.Empty(result.Text);
        Assert.Empty(result.ToolCalls);
        Assert.Equal(StopReason.Stop, result.StopReason);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.Usage);
        Assert.Null(result.ResponseId);
    }

    [Fact]
    public void LlmResult_WithToolCalls()
    {
        var result = new LlmResult
        {
            Text = "Let me check",
            ToolCalls = new List<ToolCallContent>
            {
                new("call_1", "get_weather", new Dictionary<string, object?> { ["city"] = "Paris" })
            },
            StopReason = StopReason.ToolUse
        };

        Assert.Equal("Let me check", result.Text);
        Assert.Single(result.ToolCalls);
        Assert.Equal(StopReason.ToolUse, result.StopReason);
    }

    [Fact]
    public void Model_DefaultValues()
    {
        var model = new Model();
        Assert.Equal("", model.Id);
        Assert.Equal("", model.Name);
        Assert.False(model.SupportsReasoning);
        Assert.False(model.SupportsImages);
        Assert.Equal(0, model.ContextWindow);
        Assert.Equal(0, model.MaxTokens);
    }

    [Fact]
    public void Model_WithValues()
    {
        var model = new Model
        {
            Id = "gpt-4o",
            Name = "GPT-4o",
            ProviderName = "openai",
            BaseUrl = "https://api.openai.com/v1",
            SupportsReasoning = true,
            SupportsImages = true,
            ContextWindow = 128000,
            MaxTokens = 16384,
            Cost = (2.5, 10.0, 1.25, 5.0)
        };

        Assert.Equal("gpt-4o", model.Id);
        Assert.True(model.SupportsReasoning);
        Assert.True(model.SupportsImages);
        Assert.Equal(128000, model.ContextWindow);
        Assert.Equal((2.5, 10.0, 1.25, 5.0), model.Cost);
    }
}
