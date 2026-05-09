using System.Text.Json;
using Omicron.Core.Models;
using Xunit;

namespace Omicron.Core.Tests;

public class ConversationConverterTests
{
    [Fact]
    public void ToCanonical_UserMessage_CreatesTextContent()
    {
        var msg = Message.UserMessage("Hello, world!");

        var turn = ConversationConverter.ToCanonical(msg);

        Assert.Equal(MessageRole.User, turn.Role);
        Assert.Single(turn.Content);
        var textItem = Assert.IsType<TextContentItem>(turn.Content[0]);
        Assert.Equal("Hello, world!", textItem.Text);
    }

    [Fact]
    public void ToCanonical_AssistantMessage_CreatesTextAndReasoning()
    {
        var msg = new Message
        {
            Role = MessageRole.Assistant,
            Text = "Hello back!",
            Reasoning = "Thinking..."
        };

        var turn = ConversationConverter.ToCanonical(msg);

        Assert.Equal(MessageRole.Assistant, turn.Role);
        Assert.Equal(2, turn.Content.Count);
        Assert.IsType<TextContentItem>(turn.Content[0]);
        Assert.IsType<ReasoningContentItem>(turn.Content[1]);
    }

    [Fact]
    public void ToCanonical_AssistantWithToolCall_CreatesToolCallContent()
    {
        var msg = Message.AssistantToolCallMessage(
            new ToolCallContent("call_1", "get_weather", new Dictionary<string, object?>
            {
                ["location"] = "New York"
            }));

        var turn = ConversationConverter.ToCanonical(msg);

        Assert.Equal(MessageRole.Assistant, turn.Role);
        var toolCallItem = Assert.IsType<ToolCallContentItem>(turn.Content[0]);
        Assert.Equal("call_1", toolCallItem.Id);
        Assert.Equal("get_weather", toolCallItem.Name);
    }

    [Fact]
    public void ToCanonical_ToolResult_CreatesToolResultContent()
    {
        var msg = Message.ToolResultMessage("call_1", "get_weather", "Sunny, 25°C");

        var turn = ConversationConverter.ToCanonical(msg);

        Assert.Equal(MessageRole.ToolResult, turn.Role);
        var resultItem = Assert.IsType<ToolResultContentItem>(turn.Content[0]);
        Assert.Equal("call_1", resultItem.ToolCallId);
        Assert.Equal("get_weather", resultItem.ToolName);
        Assert.Equal("Sunny, 25°C", resultItem.Text);
        Assert.False(resultItem.IsError);
    }

    [Fact]
    public void ToCanonical_UserWithImage_CreatesImageContent()
    {
        var msg = new Message
        {
            Role = MessageRole.User,
            Text = "What's in this image?",
            Images = new List<ImageContent>
            {
                new("/9j/4AAQ==", "image/jpeg")
            }
        };

        var turn = ConversationConverter.ToCanonical(msg);

        Assert.Equal(2, turn.Content.Count);
        Assert.IsType<TextContentItem>(turn.Content[0]);
        var imageItem = Assert.IsType<ImageContentItem>(turn.Content[1]);
        Assert.Equal("/9j/4AAQ==", imageItem.Data);
        Assert.Equal("image/jpeg", imageItem.MimeType);
    }

    [Fact]
    public void RoundTrip_UserMessage()
    {
        var original = Message.UserMessage("Hello, world!");

        var turn = ConversationConverter.ToCanonical(original);
        var result = ConversationConverter.FromCanonical(turn);

        Assert.Equal(original.Role, result.Role);
        Assert.Equal(original.Text, result.Text);
    }

    [Fact]
    public void RoundTrip_AssistantWithToolCalls()
    {
        var original = Message.AssistantToolCallsMessage(new List<ToolCallContent>
        {
            new("call_1", "get_weather", new Dictionary<string, object?> { ["location"] = "NYC" }),
            new("call_2", "get_time", new Dictionary<string, object?> { ["timezone"] = "EST" })
        });

        var turn = ConversationConverter.ToCanonical(original);
        var result = ConversationConverter.FromCanonical(turn);

        Assert.Equal(original.Role, result.Role);
        Assert.NotNull(result.ToolCalls);
        Assert.Equal(2, result.ToolCalls.Count);
        Assert.Equal("get_weather", result.ToolCalls[0].Name);
        Assert.Equal("get_time", result.ToolCalls[1].Name);
    }

    [Fact]
    public void RoundTrip_ToolResult()
    {
        var original = Message.ToolResultMessage("call_1", "get_weather", "Sunny", true);

        var turn = ConversationConverter.ToCanonical(original);
        var result = ConversationConverter.FromCanonical(turn);

        Assert.Equal(original.Role, result.Role);
        Assert.Equal(original.ToolCallId, result.ToolCallId);
        Assert.Equal(original.ToolName, result.ToolName);
        Assert.Equal(original.IsError, result.IsError);
    }

    [Fact]
    public void ToCanonical_EmptyMessages_ReturnsEmptyList()
    {
        var turns = ConversationConverter.ToCanonical(new List<Message>());
        Assert.Empty(turns);
    }

    [Fact]
    public void FromCanonical_EmptyTurns_ReturnsEmptyList()
    {
        var messages = ConversationConverter.FromCanonical(new List<ConversationTurn>());
        Assert.Empty(messages);
    }

    [Fact]
    public void ToCanonical_MultipleMessages_AllConverted()
    {
        var messages = new List<Message>
        {
            Message.UserMessage("Hello"),
            Message.AssistantMessage("Hi there!"),
            Message.UserMessage("How are you?")
        };

        var turns = ConversationConverter.ToCanonical(messages);

        Assert.Equal(3, turns.Count);
        Assert.Equal(MessageRole.User, turns[0].Role);
        Assert.Equal(MessageRole.Assistant, turns[1].Role);
        Assert.Equal(MessageRole.User, turns[2].Role);
    }

    [Fact]
    public void FromCanonical_MultipleTurns_ConvertedRoundTrip()
    {
        var original = new List<Message>
        {
            Message.UserMessage("Hello"),
            Message.AssistantMessage("Hi!"),
            Message.UserMessage("What's 2+2?"),
            Message.AssistantToolCallMessage(
                new ToolCallContent("calc_1", "calculator", new Dictionary<string, object?> { ["expr"] = "2+2" })),
            Message.ToolResultMessage("calc_1", "calculator", "4"),
            Message.AssistantMessage("The answer is 4.")
        };

        var turns = ConversationConverter.ToCanonical(original);
        var result = ConversationConverter.FromCanonical(turns);

        Assert.Equal(original.Count, result.Count);
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i].Role, result[i].Role);
            Assert.Equal(original[i].Text, result[i].Text);
        }
    }
}
