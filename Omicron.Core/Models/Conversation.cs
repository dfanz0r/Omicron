using System.Text.Json;
using Omicron.Core.Content;
using Omicron.Core.Providers;
using Omicron.Core.Text;

namespace Omicron.Core.Models;

// ============================================================
// Canonical conversation format
// ============================================================

/// <summary>
/// A single turn in the canonical conversation format.
/// This is the internal representation that can be converted to/from
/// provider-specific wire formats (OpenAI Chat, OpenAI Responses,
/// Anthropic Messages, Google GenAI, etc.).
/// </summary>
public sealed record ConversationTurn(
    MessageId Id,
    MessageRole Role,
    IReadOnlyList<ConversationContent> Content,
    ProviderOrigin? Origin,
    DateTimeOffset Timestamp);

/// <summary>
/// A unique identifier for a conversation message.
/// </summary>
public readonly record struct MessageId(Guid Value)
{
    public static MessageId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Abstract base for content items within a conversation turn.
/// </summary>
public abstract record ConversationContent;

/// <summary>
/// Text content item backed by UTF-8 data.
/// </summary>
public sealed record TextContentItem(Utf8String Text) : ConversationContent;

/// <summary>
/// Image content item (base64-encoded data).
/// </summary>
public sealed record ImageContentItem(string Data, string MimeType) : ConversationContent;

/// <summary>
/// Reasoning / thinking content from the model.
/// <c>Summary</c> is <c>null</c> when no reasoning was produced, and
/// <c>Utf8String.Empty</c> when reasoning was explicitly empty.
/// ProviderMetadata carries provider-specific reasoning data.
/// </summary>
public sealed record ReasoningContentItem(
    Utf8String? Summary,
    string? EncryptedContent,
    JsonElement? ProviderMetadata) : ConversationContent;

/// <summary>
/// A tool call requested by the model.
/// ProviderMetadata carries provider-specific tool call metadata.
/// </summary>
public sealed record ToolCallContentItem(
    string Id,
    string Name,
    JsonElement Arguments,
    JsonElement? ProviderMetadata) : ConversationContent;

/// <summary>
/// A tool result returned to the model.
/// </summary>
public sealed record ToolResultContentItem(
    string ToolCallId,
    string ToolName,
    Utf8String Text,
    bool IsError) : ConversationContent;

/// <summary>
/// Origin metadata for a conversation turn, indicating which
/// provider/model generated it and the wire format used.
/// </summary>
public sealed record ProviderOrigin(
    string ProviderName,
    ApiType ApiType,
    string ModelId,
    JsonElement? ProviderMetadata);

// ============================================================
// Conversion helpers between Message and ConversationTurn
// ============================================================

/// <summary>
/// Conversion helpers between the existing flat Message model
/// and the richer canonical ConversationTurn representation.
/// </summary>
public static class ConversationConverter
{
    /// <summary>
    /// Convert a list of Messages to canonical ConversationTurns.
    /// </summary>
    public static IReadOnlyList<ConversationTurn> ToCanonical(IReadOnlyList<Message> messages)
    {
        var turns = new List<ConversationTurn>(messages.Count);
        foreach (var msg in messages)
        {
            turns.Add(ToCanonical(msg));
        }
        return turns;
    }

    /// <summary>
    /// Convert a single Message to a canonical ConversationTurn.
    /// Preserves UTF-8 text and reasoning data directly without
    /// materializing intermediate strings.
    /// </summary>
    public static ConversationTurn ToCanonical(Message msg)
    {
        var content = new List<ConversationContent>();

        // Handle tool result messages first (text goes inside ToolResultContentItem, not separately)
        if (msg.Role == MessageRole.ToolResult && msg.ToolCallId is not null)
        {
            content.Add(new ToolResultContentItem(
                msg.ToolCallId,
                msg.ToolName ?? "",
                msg.TextData ?? Utf8String.Empty,
                msg.IsError));

            ProviderOrigin? toolResultOrigin = null;

            return new ConversationTurn(
                MessageId.New(),
                msg.Role,
                content,
                toolResultOrigin,
                msg.Timestamp);
        }

        // Add text content — keep Utf8String throughout
        if (msg.HasText)
        {
            content.Add(new TextContentItem(msg.TextData!));
        }

        // Add reasoning content (preserve null-vs-empty distinction)
        if (msg.ReasoningData is not null)
        {
            content.Add(new ReasoningContentItem(msg.ReasoningData, null, null));
        }

        // Add images
        if (msg.Images is { Count: > 0 })
        {
            foreach (var img in msg.Images)
            {
                content.Add(new ImageContentItem(img.Data ?? "", img.MimeType ?? "image/png"));
            }
        }

        // Add tool calls
        var allToolCalls = msg.ToolCalls ?? (msg.ToolCall is not null ? new List<ToolCallContent> { msg.ToolCall } : null);
        if (allToolCalls is { Count: > 0 })
        {
            foreach (var tc in allToolCalls)
            {
                var argsJson = JsonSerializer.SerializeToElement(tc.Arguments ?? new Dictionary<string, object?>());
                content.Add(new ToolCallContentItem(tc.Id, tc.Name, argsJson, null));
            }
        }

        // Origin is null until real provider/session metadata is available.
        ProviderOrigin? origin = null;

        return new ConversationTurn(
            MessageId.New(),
            msg.Role,
            content,
            origin,
            msg.Timestamp);
    }

    /// <summary>
    /// Convert canonical ConversationTurns back to the flat Message list.
    /// This is lossy — the flat format cannot represent all canonical features.
    /// </summary>
    public static List<Message> FromCanonical(IReadOnlyList<ConversationTurn> turns)
    {
        var messages = new List<Message>(turns.Count);
        foreach (var turn in turns)
        {
            messages.Add(FromCanonical(turn));
        }
        return messages;
    }

    /// <summary>
    /// Convert a single ConversationTurn to a Message.
    /// Reconstructs Utf8String-backed fields directly from the
    /// content items, avoiding unnecessary string materialization.
    /// </summary>
    public static Message FromCanonical(ConversationTurn turn)
    {
        // Collect Utf8String text parts for joining
        var textParts = new List<Utf8String>();
        var toolCalls = new List<ToolCallContent>();
        var images = new List<ImageContent>();
        Utf8String? reasoningUtf8 = null;
        string? toolCallId = null;
        string? toolName = null;
        bool isError = false;

        foreach (var item in turn.Content)
        {
            switch (item)
            {
                case TextContentItem textItem:
                    textParts.Add(textItem.Text);
                    break;

                case ReasoningContentItem reasoningItem:
                    reasoningUtf8 = reasoningItem.Summary;
                    break;

                case ImageContentItem imageItem:
                    images.Add(new ImageContent(imageItem.Data, imageItem.MimeType));
                    break;

                case ToolCallContentItem toolCallItem:
                    var argsJson = toolCallItem.Arguments.GetRawText();
                    var argsDict = string.IsNullOrEmpty(argsJson)
                        ? new Dictionary<string, object?>()
                        : JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson);
                    toolCalls.Add(new ToolCallContent(toolCallItem.Id, toolCallItem.Name, argsDict));
                    break;

                case ToolResultContentItem toolResultItem:
                    toolCallId = toolResultItem.ToolCallId;
                    toolName = toolResultItem.ToolName;
                    textParts.Add(toolResultItem.Text);
                    isError = toolResultItem.IsError;
                    break;
            }
        }

        // Join text parts into a single Utf8String
        Utf8String? textData = JoinTextParts(textParts);

        bool hasToolCalls = toolCalls.Count > 0;

        if (turn.Role == MessageRole.ToolResult)
        {
            return new Message
            {
                Role = MessageRole.ToolResult,
                TextData = textData,
                ToolCallId = toolCallId,
                ToolName = toolName,
                IsError = isError,
                Timestamp = turn.Timestamp.DateTime
            };
        }

        if (hasToolCalls)
        {
            return new Message
            {
                Role = MessageRole.Assistant,
                TextData = textData,
                ReasoningData = reasoningUtf8,
                ToolCalls = toolCalls,
                ToolCall = toolCalls.Count == 1 ? toolCalls[0] : null,
                Timestamp = turn.Timestamp.DateTime
            };
        }

        return new Message
        {
            Role = turn.Role,
            TextData = textData,
            ReasoningData = reasoningUtf8,
            Images = images.Count > 0 ? images : null,
            Timestamp = turn.Timestamp.DateTime
        };
    }

    /// <summary>Join a list of Utf8String parts separated by newlines.</summary>
    private static Utf8String? JoinTextParts(List<Utf8String> parts)
    {
        if (parts.Count == 0)
            return null;

        if (parts.Count == 1)
            return parts[0];

        using var sb = Utf8Text.CreateBuilder();
        sb.AppendLiteral(parts[0].Utf8Span);
        for (int i = 1; i < parts.Count; i++)
        {
            sb.AppendLiteral("\n"u8);
            sb.AppendLiteral(parts[i].Utf8Span);
        }
        return Utf8String.FromUtf8(sb.AsSpan());
    }
}
