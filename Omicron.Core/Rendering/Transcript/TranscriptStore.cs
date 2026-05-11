using Omicron.Core.Events;
using Omicron.Core.Text;

namespace Omicron.Core.Rendering.Transcript;

/// <summary>
/// Append-only store for conversation transcript content.
/// Holds a <see cref="Utf8TextStore"/> for raw text and a list of
/// <see cref="TranscriptBlock"/> records that reference byte ranges
/// within the store.
/// </summary>
public sealed class TranscriptStore
{
    private readonly List<TranscriptBlock> _blocks = [];
    private BlockId? _lastAssistantBlockId;
    private bool _lastAssistantCompleted;

    /// <summary>The underlying UTF-8 text store.</summary>
    public Utf8TextStore Text { get; } = new();

    /// <summary>All blocks in order.</summary>
    public IReadOnlyList<TranscriptBlock> Blocks => _blocks;

    /// <summary>
    /// Append a user message block. The text is appended to the store.
    /// </summary>
    public UserMessageBlock AppendUserMessage(ReadOnlySpan<byte> utf8Text)
    {
        var id = BlockId.New();
        var pos = Text.Append(utf8Text);
        var block = new UserMessageBlock(id, pos, utf8Text.Length);
        _blocks.Add(block);
        _lastAssistantBlockId = null;
        _lastAssistantCompleted = true;
        return block;
    }

    /// <summary>
    /// Append a delta to the current assistant message block.
    /// If the last block is not an assistant message, or is completed,
    /// starts a new block.
    /// </summary>
    public AssistantMessageBlock AppendAssistantDelta(ReadOnlySpan<byte> utf8Delta)
    {
        AssistantMessageBlock? block;

        if (_lastAssistantBlockId.HasValue && !_lastAssistantCompleted)
        {
            // Extend existing block
            var existing = _blocks[^1] as AssistantMessageBlock;
            if (existing is not null)
            {
                Text.Append(utf8Delta);
                // Update the block's byte length (text was appended at the end of the store)
                var updated = existing with
                {
                    ByteLength = existing.ByteLength + utf8Delta.Length
                };
                _blocks[^1] = updated;
                return updated;
            }
        }

        // Start a new block
        var pos = Text.Append(utf8Delta);
        block = new AssistantMessageBlock(BlockId.New(), pos, utf8Delta.Length, IsStreaming: true);
        _blocks.Add(block);
        _lastAssistantBlockId = block.Id;
        _lastAssistantCompleted = false;
        return block;
    }

    /// <summary>
    /// Start a tool call block (before any output is available).
    /// </summary>
    public ToolCallBlock AppendToolCall(string toolName)
    {
        var id = BlockId.New();
        // Write a placeholder text
        var textBytes = System.Text.Encoding.UTF8.GetBytes($"[tool: {toolName}]");
        var pos = Text.Append(textBytes);
        var block = new ToolCallBlock(id, pos, textBytes.Length, toolName, ToolCallState.Running);
        _blocks.Add(block);
        _lastAssistantBlockId = null;
        _lastAssistantCompleted = true;
        return block;
    }

    /// <summary>
    /// Update an existing tool call block with a new state and optional output text.
    /// </summary>
    public ToolCallBlock UpdateToolCall(BlockId blockId, ToolCallState newState, ReadOnlySpan<byte> outputText)
    {
        for (int i = _blocks.Count - 1; i >= 0; i--)
        {
            if (_blocks[i] is ToolCallBlock tcb && tcb.Id == blockId)
            {
                // Append output text
                Text.Append(outputText);
                var updated = tcb with
                {
                    State = newState,
                    ByteLength = tcb.ByteLength + outputText.Length
                };
                _blocks[i] = updated;
                return updated;
            }
        }

        // Block not found — create a new one
        var pos = Text.Append(outputText);
        var block = new ToolCallBlock(blockId, pos, outputText.Length, "unknown", newState);
        _blocks.Add(block);
        return block;
    }

    /// <summary>
    /// Append a system notice (error, status message).
    /// </summary>
    public SystemNoticeBlock AppendNotice(string text)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(text);
        var pos = Text.Append(utf8);
        var block = new SystemNoticeBlock(BlockId.New(), pos, utf8.Length, text);
        _blocks.Add(block);
        _lastAssistantBlockId = null;
        _lastAssistantCompleted = true;
        return block;
    }

    /// <summary>
    /// Append a blank visual separator (empty line padding between messages).
    /// Optionally tint the separator with a background color so adjacent message
    /// backgrounds can visually bleed into the padding.
    /// </summary>
    public void AppendSeparator(byte bgR = 0, byte bgG = 0, byte bgB = 0)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(" ");
        var pos = Text.Append(utf8);
        var block = new SeparatorBlock(BlockId.New(), pos, utf8.Length, bgR, bgG, bgB);
        _blocks.Add(block);
    }

    /// <summary>
    /// Mark the last assistant block as complete (sets IsStreaming = false).
    /// </summary>
    public void CompleteLastAssistantBlock()
    {
        if (_lastAssistantBlockId.HasValue && !_lastAssistantCompleted)
        {
            if (_blocks[^1] is AssistantMessageBlock existing)
            {
                _blocks[^1] = existing with { IsStreaming = false };
                _lastAssistantCompleted = true;
                _lastAssistantBlockId = null;
            }
        }
    }

    /// <summary>
    /// Remove all blocks and optionally clear the text store.
    /// </summary>
    public void Clear()
    {
        _blocks.Clear();
        _lastAssistantBlockId = null;
        _lastAssistantCompleted = true;
    }
}
