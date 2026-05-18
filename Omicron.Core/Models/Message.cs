using Omicron.Core.Content;

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

/// <summary>
/// A message in the conversation. Text is stored as owned UTF-8 via <see cref="Utf8String"/>.
/// Use <see cref="GetTextString()"/> or <see cref="GetReasoningString()"/> for explicit string materialization.
/// Use <see cref="TextUtf8"/> / <see cref="ReasoningUtf8"/> for zero-alloc span access.
/// </summary>
public sealed record Message
{
    public MessageRole Role { get; init; }
    public Utf8String? TextData { get; init; }
    public Utf8String? ReasoningData { get; init; }
    public IReadOnlyList<ImageContent>? Images { get; init; }
    public ToolCallContent? ToolCall { get; init; }
    public IReadOnlyList<ToolCallContent>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public bool IsError { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Whether this message has text content.</summary>
    public bool HasText => TextData is not null && !TextData.IsEmpty;

    /// <summary>Whether this message has reasoning data (including empty reasoning).
    /// Use <see cref="GetReasoningStringOrNull()"/> to distinguish null (no reasoning)
    /// from empty (explicit empty reasoning).</summary>
    public bool HasReasoning => ReasoningData is not null;

    /// <summary>UTF-8 span of the message text (empty if no text). Zero-alloc.</summary>
    public ReadOnlySpan<byte> TextUtf8 => TextData is not null ? TextData.Utf8Span : ReadOnlySpan<byte>.Empty;

    /// <summary>UTF-8 span of the reasoning text (empty if no reasoning). Zero-alloc.</summary>
    public ReadOnlySpan<byte> ReasoningUtf8 => ReasoningData is not null ? ReasoningData.Utf8Span : ReadOnlySpan<byte>.Empty;

    /// <summary>Explicit string materialization for the message text. Allocates.</summary>
    public string GetTextString() => TextData?.ToString() ?? string.Empty;

    /// <summary>Explicit string materialization for reasoning text. Allocates.</summary>
    public string GetReasoningString() => ReasoningData?.ToString() ?? string.Empty;

    /// <summary>Reasoning string that preserves null-vs-empty: null when no reasoning, string when present.</summary>
    public string? GetReasoningStringOrNull() => ReasoningData?.ToString();

    /// <summary>Create a user message. Prefer the <see cref="Utf8String"/> overload for UTF-8-native paths.</summary>
    public static Message UserMessage(string text, IReadOnlyList<ImageContent>? images = null) =>
        new() { Role = MessageRole.User, TextData = Utf8String.FromString(text), Images = images };

    /// <summary>Create a user message from owned UTF-8 text.</summary>
    public static Message UserMessage(Utf8String text, IReadOnlyList<ImageContent>? images = null) =>
        new() { Role = MessageRole.User, TextData = text, Images = images };

    /// <summary>Create an assistant message. Prefer the <see cref="Utf8String"/> overload for UTF-8-native paths.</summary>
    public static Message AssistantMessage(string text) =>
        new() { Role = MessageRole.Assistant, TextData = Utf8String.FromString(text) };

    /// <summary>Create an assistant message from owned UTF-8 text.</summary>
    public static Message AssistantMessage(Utf8String text) =>
        new() { Role = MessageRole.Assistant, TextData = text };

    public static Message AssistantToolCallMessage(ToolCallContent toolCall) =>
        new() { Role = MessageRole.Assistant, ToolCall = toolCall, ToolCalls = [toolCall] };

    public static Message AssistantToolCallsMessage(IReadOnlyList<ToolCallContent> toolCalls) =>
        new() { Role = MessageRole.Assistant, ToolCalls = toolCalls };

    /// <summary>Create a tool result message. Prefer the <see cref="Utf8String"/> overload for UTF-8-native paths.</summary>
    public static Message ToolResultMessage(string toolCallId, string toolName, string text, bool isError = false) =>
        new() { Role = MessageRole.ToolResult, ToolCallId = toolCallId, ToolName = toolName, TextData = Utf8String.FromString(text), IsError = isError };

    /// <summary>Create a tool result message from owned UTF-8 text.</summary>
    public static Message ToolResultMessage(string toolCallId, string toolName, Utf8String text, bool isError = false) =>
        new() { Role = MessageRole.ToolResult, ToolCallId = toolCallId, ToolName = toolName, TextData = text, IsError = isError };
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
