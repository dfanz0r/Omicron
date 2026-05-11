using System.Text;
using Omicron.Core.Text;

namespace Omicron.Core.Rendering.Transcript;

/// <summary>
/// Width-dependent layout cache that maps transcript blocks into wrapped terminal rows.
/// Supports incremental invalidation so only changed blocks are re-laid-out.
/// </summary>
public sealed class TranscriptLayoutCache
{
    private readonly List<WrappedLineInfo> _wrappedLines = [];
    private readonly Dictionary<BlockId, List<WrappedLineInfo>> _blockLines = [];
    private int _terminalWidth = 80;

    /// <summary>Current terminal width (used for wrapping).</summary>
    public int TerminalWidth => _terminalWidth;

    /// <summary>Total number of wrapped rows across all blocks.</summary>
    public int TotalWrappedRows => _wrappedLines.Count;

    /// <summary>All wrapped lines (read-only snapshot).</summary>
    public IReadOnlyList<WrappedLineInfo> WrappedLines => _wrappedLines;

    /// <summary>
    /// Reflow all blocks for the given terminal width.
    /// This fully rebuilds the layout cache.
    /// </summary>
    public void ReflowForWidth(int terminalWidth, TranscriptStore store)
    {
        _terminalWidth = terminalWidth;
        _wrappedLines.Clear();
        _blockLines.Clear();

        foreach (var block in store.Blocks)
        {
            var lines = WrapBlock(block, store, terminalWidth);
            _blockLines[block.Id] = lines;
            _wrappedLines.AddRange(lines);
        }
    }

    /// <summary>
    /// Invalidate a specific block, removing its cached wrapped lines.
    /// The next call to <see cref="GetVisibleLines"/> will trigger a re-layout
    /// of that block when it's encountered.
    /// For MVP, this marks the block for full reflow on next render.
    /// </summary>
    public void InvalidateBlock(BlockId blockId)
    {
        if (_blockLines.Remove(blockId, out var lines))
        {
            // Remove the lines from the flat list and rebuild
            foreach (var line in lines)
                _wrappedLines.Remove(line);
        }
    }

    /// <summary>
    /// Invalidate a block and all subsequent blocks.
    /// </summary>
    public void InvalidateFrom(BlockId blockId)
    {
        // Remove from the given block onwards
        int removeFrom = -1;
        for (int i = 0; i < _wrappedLines.Count; i++)
        {
            if (_wrappedLines[i].BlockId == blockId)
            {
                removeFrom = i;
                break;
            }
        }

        if (removeFrom >= 0)
        {
            // Collect all block IDs from removeFrom onwards
            var removedBlocks = new HashSet<BlockId>();
            for (int i = removeFrom; i < _wrappedLines.Count; i++)
                removedBlocks.Add(_wrappedLines[i].BlockId);

            _wrappedLines.RemoveRange(removeFrom, _wrappedLines.Count - removeFrom);

            foreach (var bid in removedBlocks)
                _blockLines.Remove(bid);
        }
    }

    /// <summary>
    /// Get visible wrapped lines for a window into the transcript.
    /// </summary>
    public IReadOnlyList<WrappedLineInfo> GetVisibleLines(int firstWrappedRow, int height)
    {
        if (firstWrappedRow < 0) firstWrappedRow = 0;
        if (firstWrappedRow >= _wrappedLines.Count)
            return Array.Empty<WrappedLineInfo>();

        int count = Math.Min(height, _wrappedLines.Count - firstWrappedRow);
        return _wrappedLines.GetRange(firstWrappedRow, count);
    }

    /// <summary>
    /// Insert cached lines back into the flat list after re-layout.
    /// Used by incremental invalidation.
    /// </summary>
    public void InsertBlockLines(BlockId blockId, List<WrappedLineInfo> lines)
    {
        _blockLines[blockId] = lines;

        // Find the position to insert
        int insertAt = 0;
        for (int i = 0; i < _wrappedLines.Count; i++)
        {
            if (_wrappedLines[i].BlockId == blockId)
            {
                // Replace existing lines for this block
                int end = i;
                while (end < _wrappedLines.Count && _wrappedLines[end].BlockId == blockId)
                    end++;
                int count = end - i;
                _wrappedLines.RemoveRange(i, count);
                _wrappedLines.InsertRange(i, lines);
                return;
            }
            if (CompareBlockIds(_wrappedLines[i].BlockId, blockId) < 0)
                insertAt = i + 1;
        }

        // Block not found in flat list — append
        _wrappedLines.AddRange(lines);
    }

    private static int CompareBlockIds(BlockId a, BlockId b) => a.Value.CompareTo(b.Value);

    /// <summary>
    /// Wrap a single block's text into lines fitting the terminal width.
    /// </summary>
    public static List<WrappedLineInfo> WrapBlock(TranscriptBlock block, TranscriptStore store, int terminalWidth)
    {
        var lines = new List<WrappedLineInfo>();

        // Read the block's bytes from the store
        long byteStart = block switch
        {
            UserMessageBlock umb => umb.Position.GlobalByteOffset,
            AssistantMessageBlock amb => amb.Position.GlobalByteOffset,
            ToolCallBlock tcb => tcb.Position.GlobalByteOffset,
            SystemNoticeBlock snb => snb.Position.GlobalByteOffset,
            _ => 0
        };

        int byteLength = block switch
        {
            UserMessageBlock umb => umb.ByteLength,
            AssistantMessageBlock amb => amb.ByteLength,
            ToolCallBlock tcb => tcb.ByteLength,
            SystemNoticeBlock snb => snb.ByteLength,
            _ => 0
        };

        if (byteLength <= 0)
            return lines;

        var bytes = store.Text.Slice(byteStart, byteLength);

        // Split into logical lines by scanning for newlines
        int logicalLineIndex = 0;
        int lineStart = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes.Span[i] == (byte)'\n')
            {
                var logicalBytes = bytes.Slice(lineStart, i - lineStart);
                WrapLogicalLine(logicalBytes.Span, block.Id, byteStart + lineStart, logicalLineIndex, terminalWidth, lines);
                lineStart = i + 1;
                logicalLineIndex++;
            }
            else if (bytes.Span[i] == (byte)'\r')
            {
                if (i + 1 < bytes.Length && bytes.Span[i + 1] == (byte)'\n')
                {
                    var logicalBytes = bytes.Slice(lineStart, i - lineStart);
                    WrapLogicalLine(logicalBytes.Span, block.Id, byteStart + lineStart, logicalLineIndex, terminalWidth, lines);
                    i++; // skip LF
                    lineStart = i + 1;
                    logicalLineIndex++;
                }
                else
                {
                    var logicalBytes = bytes.Slice(lineStart, i - lineStart);
                    WrapLogicalLine(logicalBytes.Span, block.Id, byteStart + lineStart, logicalLineIndex, terminalWidth, lines);
                    lineStart = i + 1;
                    logicalLineIndex++;
                }
            }
        }

        // Handle trailing content without newline
        if (lineStart < bytes.Length)
        {
            var logicalBytes = bytes.Slice(lineStart, bytes.Length - lineStart);
            WrapLogicalLine(logicalBytes.Span, block.Id, byteStart + lineStart, logicalLineIndex, terminalWidth, lines);
        }
        else if (lineStart == bytes.Length && bytes.Length > 0)
        {
            // Input ends with a newline — the logical line after it is empty, add one
            WrapLogicalLine(ReadOnlySpan<byte>.Empty, block.Id, byteStart + lineStart, logicalLineIndex, terminalWidth, lines);
        }

        return lines;
    }

    /// <summary>
    /// Wrap a single logical line into physical rows of <paramref name="terminalWidth"/> columns.
    /// </summary>
    private static void WrapLogicalLine(
        ReadOnlySpan<byte> utf8,
        BlockId blockId,
        long globalByteStart,
        int logicalLineIndex,
        int terminalWidth,
        List<WrappedLineInfo> lines)
    {
        if (utf8.IsEmpty)
        {
            // Empty line — still emit one wrapped line (blank row)
            lines.Add(new WrappedLineInfo(blockId, globalByteStart, 0, 0, 0, false, logicalLineIndex));
            return;
        }

        // Get all grapheme clusters for this line
        var clusters = GraphemeSegmenter.SegmentUtf8(utf8);
        if (clusters.Count == 0)
        {
            lines.Add(new WrappedLineInfo(blockId, globalByteStart, 0, 0, 0, false, logicalLineIndex));
            return;
        }

        int rowStartCluster = 0;
        int cellPos = 0;
        bool firstRow = true;

        for (int i = 0; i < clusters.Count; i++)
        {
            var cluster = clusters[i];
            int w = CellWidthCalculator.GetWidth(utf8.Slice((int)cluster.ByteOffset, cluster.ByteLength));

            // Check if this grapheme fits on the current row
            if (cellPos + w > terminalWidth && cellPos > 0)
            {
                // Emit the current row (from rowStartCluster to i-1)
                long rowStart = globalByteStart + clusters[rowStartCluster].ByteOffset;
                int rowEnd = i;
                int rowByteLen = (int)(clusters[rowEnd - 1].ByteOffset + clusters[rowEnd - 1].ByteLength - clusters[rowStartCluster].ByteOffset);

                lines.Add(new WrappedLineInfo(
                    blockId,
                    rowStart,
                    rowByteLen,
                    0,
                    cellPos,
                    !firstRow,
                    logicalLineIndex));

                // Start new row
                rowStartCluster = i;
                cellPos = 0;
                firstRow = false;

                // If this grapheme still doesn't fit on the new row, skip it (truncation)
                if (w > terminalWidth)
                {
                    rowStartCluster = i + 1;
                    continue;
                }
            }

            cellPos += w;
        }

        // Emit the last row
        if (rowStartCluster < clusters.Count)
        {
            long rowStart = globalByteStart + clusters[rowStartCluster].ByteOffset;
            int lastCluster = clusters.Count - 1;
            int rowByteLen = (int)(clusters[lastCluster].ByteOffset + clusters[lastCluster].ByteLength - clusters[rowStartCluster].ByteOffset);

            lines.Add(new WrappedLineInfo(
                blockId,
                rowStart,
                rowByteLen,
                0,
                cellPos,
                !firstRow,
                logicalLineIndex));
        }
        else if (lines.Count == 0)
        {
            // No rows emitted yet — emit at least one empty row
            lines.Add(new WrappedLineInfo(blockId, globalByteStart, 0, 0, 0, false, logicalLineIndex));
        }
    }
}
