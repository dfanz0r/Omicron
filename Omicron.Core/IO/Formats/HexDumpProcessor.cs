using System.Globalization;
using System.Text;
using Cysharp.Text;

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
                var emptyBuilder = ZString.CreateUtf8StringBuilder();
                try
                {
                    AppendHeaderUtf8(ref emptyBuilder, fileSize, 0, 0);
                    emptyBuilder.AppendLine();
                    return ValueTask.FromResult(new ContentProcessorResult(
                        emptyBuilder.ToString().TrimEnd(),
                        OutputModality.HexDump,
                        Utf8Data: emptyBuilder.AsSpan().ToArray()));
                }
                finally { emptyBuilder.Dispose(); }
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

        var builder = ZString.CreateUtf8StringBuilder();
        try
        {
            // Header
            AppendHeaderUtf8(ref builder, fileSize, startOffset, actualEnd);

            // Separator
            builder.Append("--------  -------------------------------------------------  ----------------");
            builder.AppendLine();

            // Rows
            for (int r = 0; r < rowCount; r++)
            {
                long rowStart = startOffset + r * BytesPerRow;
                var rowSpan = bytes.Span.Slice((int)rowStart, (int)Math.Min(BytesPerRow, actualEnd - rowStart));

                // Offset column
                builder.AppendFormat("{0:X8}  ", rowStart);

                // Hex bytes — split into two groups of 8
                for (int b = 0; b < BytesPerRow; b++)
                {
                    if (b < rowSpan.Length)
                    {
                        builder.AppendFormat("{0:X2} ", rowSpan[b]);
                    }
                    else
                    {
                        builder.Append("   "); // blank for missing bytes
                    }

                    if (b == 7) builder.Append(' '); // extra space between groups
                }

                builder.Append(" |");

                // ASCII column
                for (int b = 0; b < BytesPerRow; b++)
                {
                    if (b < rowSpan.Length)
                    {
                        var val = rowSpan[b];
                        builder.Append(val >= 0x20 && val <= 0x7E ? (char)val : '.');
                    }
                    // Missing bytes are left blank (not padded)
                }

                builder.Append("|");
                builder.AppendLine();
            }

            // Continuation hint
            bool truncated = actualEnd < fileSize;
            long nextOffset = actualEnd;

            if (truncated)
            {
                builder.AppendLine();
                builder.AppendFormat("... (showing bytes 0x{0:X}–0x{1:X} of 0x{2:X} total). Use offset=0x{3:X} to continue.", startOffset, actualEnd, fileSize, nextOffset);
                builder.AppendLine();
            }

            var bytesOut = builder.AsSpan().ToArray();
            return ValueTask.FromResult(new ContentProcessorResult(
                Encoding.UTF8.GetString(bytesOut).TrimEnd(),
                OutputModality.HexDump,
                Utf8Data: bytesOut,
                IsTruncated: truncated,
                NextOffset: truncated ? nextOffset : null));
        }
        finally { builder.Dispose(); }
    }

    private static void AppendHeaderUtf8(ref Utf8ValueStringBuilder builder, long fileSize, long startOffset, long endOffset)
    {
        builder.AppendFormat("[HEX] bytes 0x{0:X}–0x{1:X} of 0x{2:X} ({3} bytes)", startOffset, endOffset, fileSize, (int)fileSize);
        builder.AppendLine();
    }
}
