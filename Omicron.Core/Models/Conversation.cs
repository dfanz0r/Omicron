using System.Text.Json;
using Omicron.Core.Providers;

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
/// Text content item.
/// </summary>
public sealed record TextContentItem(string Text) : ConversationContent;

/// <summary>
/// Image content item (base64-encoded data).
/// </summary>
public sealed record ImageContentItem(string Data, string MimeType) : ConversationContent;

/// <summary>
/// Reasoning / thinking content from the model.
/// ProviderMetadata carries provider-specific reasoning data.
/// </summary>
public sealed record ReasoningContentItem(
    string? Summary,
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
    string Text,
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
                msg.Text ?? "",
                msg.IsError));

            ProviderOrigin? toolResultOrigin = null;

            return new ConversationTurn(
                MessageId.New(),
                msg.Role,
                content,
                toolResultOrigin,
                msg.Timestamp);
        }

        // Add text content
        if (!string.IsNullOrEmpty(msg.Text))
        {
            content.Add(new TextContentItem(msg.Text));
        }

        // Add reasoning content
        if (!string.IsNullOrEmpty(msg.Reasoning))
        {
            content.Add(new ReasoningContentItem(msg.Reasoning, null, null));
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
    /// </summary>
    public static Message FromCanonical(ConversationTurn turn)
    {
        var textParts = new List<string>();
        var toolCalls = new List<ToolCallContent>();
        var images = new List<ImageContent>();
        string? reasoning = null;
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
                    reasoning = reasoningItem.Summary;
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

        var text = string.Join("\n", textParts);
        // Normalize: empty string becomes null to match Message default
        if (text.Length == 0) text = null!;
        bool hasToolCalls = toolCalls.Count > 0;

        if (turn.Role == MessageRole.ToolResult)
        {
            return new Message
            {
                Role = MessageRole.ToolResult,
                Text = string.IsNullOrEmpty(text) ? null : text,
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
                Text = string.IsNullOrEmpty(text) ? null : text,
                Reasoning = reasoning,
                ToolCalls = toolCalls,
                ToolCall = toolCalls.Count == 1 ? toolCalls[0] : null,
                Timestamp = turn.Timestamp.DateTime
            };
        }

        return new Message
        {
            Role = turn.Role,
            Text = string.IsNullOrEmpty(text) ? null : text,
            Reasoning = reasoning,
            Images = images.Count > 0 ? images : null,
            Timestamp = turn.Timestamp.DateTime
        };
    }
}
