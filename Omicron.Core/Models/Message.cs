using System.Text;

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

public sealed record TextContent(string Text);

public sealed record ImageContent(string Data, string MimeType);

public sealed record ToolCallContent(string Id, string Name, Dictionary<string, object?>? Arguments);

public sealed record Message
{
    public MessageRole Role { get; init; }
    public string? Text { get; init; }
    public ReadOnlyMemory<byte>? Utf8Text { get; init; }
    public IReadOnlyList<ImageContent>? Images { get; init; }
    public ToolCallContent? ToolCall { get; init; }
    public IReadOnlyList<ToolCallContent>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public bool IsError { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? Reasoning { get; init; }

    public string EffectiveText => Utf8Text.HasValue
        ? Encoding.UTF8.GetString(Utf8Text.Value.Span)
        : Text ?? string.Empty;

    public static Message UserMessage(string text, IReadOnlyList<ImageContent>? images = null) =>
        new() { Role = MessageRole.User, Text = text, Images = images };

    public static Message AssistantMessage(string text) =>
        new() { Role = MessageRole.Assistant, Text = text };

    public static Message AssistantToolCallMessage(ToolCallContent toolCall) =>
        new() { Role = MessageRole.Assistant, ToolCall = toolCall, ToolCalls = [toolCall] };

    public static Message AssistantToolCallsMessage(IReadOnlyList<ToolCallContent> toolCalls) =>
        new() { Role = MessageRole.Assistant, ToolCalls = toolCalls };

    public static Message ToolResultMessage(string toolCallId, string toolName, string text, bool isError = false) =>
        new() { Role = MessageRole.ToolResult, ToolCallId = toolCallId, ToolName = toolName, Text = text, IsError = isError };

    public static Message ToolResultMessageUtf8(string toolCallId, string toolName, ReadOnlyMemory<byte> utf8Text, bool isError = false) =>
        new() { Role = MessageRole.ToolResult, ToolCallId = toolCallId, ToolName = toolName, Utf8Text = utf8Text, IsError = isError };
}

public sealed record UsageInfo(
    int InputTokens,
    int OutputTokens,
    int? CacheReadTokens = null,
    int? CacheWriteTokens = null);

public sealed record LlmResult
{
    public string Text { get; init; } = "";
    public IReadOnlyList<ToolCallContent> ToolCalls { get; init; } = [];
    public StopReason StopReason { get; init; } = StopReason.Stop;
    public string? ErrorMessage { get; init; }
    public UsageInfo? Usage { get; init; }
    public string? ResponseId { get; init; }
}
