using System.Globalization;
using System.Text;

namespace Omicron.Core.IO;

/// <summary>
/// Produces a standard hex dump of binary data: offset, 16 hex bytes per row,
/// ASCII representation. Handles offset/limit with 0x hex string support,
/// partial rows, and continuation hints.
/// </summary>
public sealed class HexDumpProcessor : IContentProcessor
{
    public string Id => "hex_dump";
    public IReadOnlySet<DetectedFileType> SupportedTypes { get; }
        = new HashSet<DetectedFileType> { DetectedFileType.UnknownBinary };
    public OutputModality OutputModality => OutputModality.HexDump;

    /// <summary>Maximum rows shown per call.</summary>
    private const int MaxRows = 32;
    private const int BytesPerRow = 16;

    public ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var bytes = context.Bytes;
        var fileSize = bytes.Length;

        // Parse offset
        long offset;
        if (context.Offset.HasValue)
        {
            offset = context.Offset.Value;
            if (offset < 0)
            {
                return ValueTask.FromResult(new ContentProcessorResult(
                    "Error: offset must be non-negative.", OutputModality.HexDump));
            }
        }
        else
        {
            offset = 0;
        }

        // Parse limit - default 256 bytes (16 rows)
        long limit;
        if (context.Limit.HasValue)
        {
            limit = context.Limit.Value;
            if (limit < 0)
            {
                return ValueTask.FromResult(new ContentProcessorResult(
                    "Error: limit must be non-negative.", OutputModality.HexDump));
            }
            if (limit == 0)
            {
                // Empty dump (header only)
                var emptySb = new StringBuilder();
                AppendHeader(emptySb, fileSize, 0, 0);
                return ValueTask.FromResult(new ContentProcessorResult(
                    emptySb.ToString().TrimEnd(), OutputModality.HexDump));
            }
        }
        else
        {
            limit = 256; // 16 rows by default
        }

        // Validate hex string values (offset/limit arrive as resolved integers;
        // the hex string parsing is done upstream.)

        // Handle empty file
        if (fileSize == 0)
        {
            return ValueTask.FromResult(new ContentProcessorResult(
                $"[HEX] {context.RelativePath}  (0 bytes)", OutputModality.HexDump));
        }

        // Round offset down to nearest 16-byte row boundary
        long startOffset = offset & ~0xF;

        if (startOffset >= fileSize)
        {
            return ValueTask.FromResult(new ContentProcessorResult(
                $"Requested offset 0x{offset:X} ({(int)offset}) is past end of file ({fileSize} bytes).",
                OutputModality.HexDump));
        }

        // Clamp end position
        long requestedEnd = startOffset + limit;
        long endOffset = Math.Min(requestedEnd, fileSize);

        // Calculate rows
        long totalBytes = endOffset - startOffset;
        int rowCount = (int)((totalBytes + BytesPerRow - 1) / BytesPerRow);
        rowCount = Math.Min(rowCount, MaxRows);
        long actualEnd = Math.Min(startOffset + rowCount * BytesPerRow, fileSize);

        var sb = new StringBuilder();

        // Header
        AppendHeader(sb, fileSize, startOffset, actualEnd);

        // Separator
        sb.AppendLine("--------  -------------------------------------------------  ----------------");

        // Rows
        for (int r = 0; r < rowCount; r++)
        {
            long rowStart = startOffset + r * BytesPerRow;
            var rowSpan = bytes.Span.Slice((int)rowStart, (int)Math.Min(BytesPerRow, actualEnd - rowStart));

            // Offset column
            sb.Append($"{rowStart:X8}  ");

            // Hex bytes — split into two groups of 8
            for (int b = 0; b < BytesPerRow; b++)
            {
                if (b < rowSpan.Length)
                {
                    sb.Append($"{rowSpan[b]:X2} ");
                }
                else
                {
                    sb.Append("   "); // blank for missing bytes
                }

                if (b == 7) sb.Append(' '); // extra space between groups
            }

            sb.Append(" |");

            // ASCII column
            for (int b = 0; b < BytesPerRow; b++)
            {
                if (b < rowSpan.Length)
                {
                    var val = rowSpan[b];
                    sb.Append(val >= 0x20 && val <= 0x7E ? (char)val : '.');
                }
                // Missing bytes are left blank (not padded)
            }

            sb.AppendLine("|");
        }

        // Continuation hint
        bool truncated = actualEnd < fileSize;
        long nextOffset = actualEnd;

        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine(
                $"... (showing bytes 0x{startOffset:X}–0x{actualEnd:X} of 0x{fileSize:X} total). " +
                $"Use offset=0x{nextOffset:X} to continue.");
        }

        return ValueTask.FromResult(new ContentProcessorResult(
            sb.ToString().TrimEnd(),
            OutputModality.HexDump,
            IsTruncated: truncated,
            NextOffset: truncated ? nextOffset : null));
    }

    private static void AppendHeader(StringBuilder sb, long fileSize, long startOffset, long endOffset)
    {
        sb.AppendLine($"[HEX] bytes 0x{startOffset:X}–0x{endOffset:X} of 0x{fileSize:X} ({(int)fileSize} bytes)");
    }
}
