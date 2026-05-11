using Omicron.Core.Events;
using Omicron.Core.Text;

namespace Omicron.Core.Rendering.Transcript;

/// <summary>Status of a tool call within the transcript.</summary>
public enum ToolCallState
{
    Pending,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// Base record for all transcript blocks.
/// Each block represents a logical unit of conversation (user message,
/// assistant message, tool call, system notice).
/// </summary>
public abstract record TranscriptBlock(BlockId Id);

/// <summary>A user message block.</summary>
public sealed record UserMessageBlock(
    BlockId Id,
    TextPosition Position,
    int ByteLength) : TranscriptBlock(Id);

/// <summary>An assistant message block, possibly still streaming.</summary>
public sealed record AssistantMessageBlock(
    BlockId Id,
    TextPosition Position,
    int ByteLength,
    bool IsStreaming) : TranscriptBlock(Id);

/// <summary>A tool call block.</summary>
public sealed record ToolCallBlock(
    BlockId Id,
    TextPosition Position,
    int ByteLength,
    string ToolName,
    ToolCallState State) : TranscriptBlock(Id);

/// <summary>A system notice (errors, status messages).</summary>
public sealed record SystemNoticeBlock(
    BlockId Id,
    TextPosition Position,
    int ByteLength,
    string Text) : TranscriptBlock(Id);

/// <summary>A visual separator (blank line padding between messages).
/// Carries an optional background color so adjacent message types can
/// extend their background into the padding rows.</summary>
public sealed record SeparatorBlock(
    BlockId Id,
    TextPosition Position,
    int ByteLength,
    byte BgR = 0,
    byte BgG = 0,
    byte BgB = 0) : TranscriptBlock(Id);
