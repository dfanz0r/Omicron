namespace Omicron.Core.Models;

public enum MessageRole
{
    User,
    Assistant,
    ToolResult
}

public enum StopReason
{
    Stop,
    Length,
    ToolUse,
    Error,
    Aborted
}

public record TextContent(string Text);

public record ImageContent(string Data, string MimeType);

public record ToolCallContent(string Id, string Name, Dictionary<string, object?>? Arguments);

/// <summary>
/// A single message in the conversation.
/// </summary>
public class Message
{
    public MessageRole Role { get; init; }
    public string? Text { get; init; }
    public List<ImageContent>? Images { get; init; }
    public ToolCallContent? ToolCall { get; init; }
    public List<ToolCallContent>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public bool IsError { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Reasoning / thinking content from the model (required by DeepSeek and others for multi-turn).</summary>
    public string? Reasoning { get; init; }

    public static Message UserMessage(string text, List<ImageContent>? images = null) =>
        new() { Role = MessageRole.User, Text = text, Images = images };

    public static Message AssistantMessage(string text) =>
        new() { Role = MessageRole.Assistant, Text = text };

    public static Message AssistantToolCallMessage(ToolCallContent toolCall) =>
        new() { Role = MessageRole.Assistant, ToolCall = toolCall, ToolCalls = [toolCall] };

    public static Message AssistantToolCallsMessage(List<ToolCallContent> toolCalls) =>
        new() { Role = MessageRole.Assistant, ToolCalls = toolCalls };

    public static Message ToolResultMessage(string toolCallId, string toolName, string text, bool isError = false) =>
        new() { Role = MessageRole.ToolResult, ToolCallId = toolCallId, ToolName = toolName, Text = text, IsError = isError };
}

/// <summary>
/// Usage/cost information from a provider response.
/// </summary>
public record UsageInfo(
    int InputTokens,
    int OutputTokens,
    int? CacheReadTokens = null,
    int? CacheWriteTokens = null
);

/// <summary>
/// The result of a single LLM call.
/// </summary>
public class LlmResult
{
    public string Text { get; init; } = "";
    public List<ToolCallContent> ToolCalls { get; init; } = [];
    public StopReason StopReason { get; init; } = StopReason.Stop;
    public string? ErrorMessage { get; init; }
    public UsageInfo? Usage { get; init; }
    public string? ResponseId { get; init; }
}
