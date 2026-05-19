using System.Text;
using Cysharp.Text;
using Omicron.Core.Content;
using Omicron.Core.Text;

namespace Omicron.Core.IO;

/// <summary>
/// Processes SVG files by reading them as XML text with a 1 MB size cap.
/// SVG files are text but can be very large, so we limit the output size.
/// </summary>
public sealed class SvgProcessor : IContentProcessor
{
    public string Id => "svg";
    public IReadOnlySet<DetectedFileType> SupportedTypes { get; }
        = new HashSet<DetectedFileType> { DetectedFileType.Svg };
    public OutputModality OutputModality => OutputModality.Text;

    private const long MaxSvgBytes = 1 * 1024 * 1024; // 1 MB
    private const int MaxPreviewLines = 2000;

    public ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var bytes = context.Bytes;

        if (bytes.IsEmpty)
        {
            return ValueTask.FromResult(new ContentProcessorResult(
                $"[FILE] {context.RelativePath}  (empty SVG)", OutputModality.Text));
        }

        // Truncate to max size before scanning
        var slice = bytes.Length > MaxSvgBytes
            ? bytes[..(int)MaxSvgBytes]
            : bytes;
        bool isTruncated = bytes.Length > MaxSvgBytes;

        var span = slice.Span;

        // Scan bytes for line boundaries
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == (byte)'\n' && i + 1 < span.Length)
                lineStarts.Add(i + 1);
        }
        var totalLines = lineStarts.Count;

        var output = ZString.CreateUtf8StringBuilder();
        try
        {

            if (isTruncated)
            {
                Utf8CompositeFormat.AppendFormatUtf8Slow(ref output, "[FILE] {0}  ({1}, SVG — file too large, showing first {2})"u8, context.RelativePath, FormatSize.Format(bytes.Length), FormatSize.Format(MaxSvgBytes));
            }
            else
            {
                Utf8CompositeFormat.AppendFormatUtf8Slow(ref output, "[FILE] {0}  ({1} lines, SVG)"u8, context.RelativePath, totalLines);
            }
            output.AppendLine();
            output.AppendLine();

            // Show up to MaxPreviewLines lines
            int showLines = Math.Min(totalLines, MaxPreviewLines);
            for (int i = 0; i < showLines; i++)
            {
                int lineStart = lineStarts[i];
                int lineEnd = (i + 1 < lineStarts.Count) ? lineStarts[i + 1] : span.Length;
                int lineLen = lineEnd - lineStart;
                // Strip trailing newline
                if (lineLen > 0 && span[lineStart + lineLen - 1] == (byte)'\n') lineLen--;
                if (lineLen > 0 && span[lineStart + lineLen - 1] == (byte)'\r') lineLen--;

                // Format: "     1| <line>" — avoid string allocation
                int digits = (i + 1) < 10 ? 1 : (i + 1) < 100 ? 2 : (i + 1) < 1000 ? 3 : (i + 1) < 10000 ? 4 : 5;
                for (int s = 0; s < 6 - digits; s++) output.Append(' ');
                output.Append(i + 1);
                output.AppendLiteral("| "u8);
                output.AppendLiteral(span.Slice(lineStart, lineLen));
                output.AppendLine();
            }

            if (showLines < totalLines)
            {
                Utf8CompositeFormat.AppendFormatUtf8Slow(ref output, "... ({0} total lines, showing {1})"u8, totalLines, showLines);
                output.AppendLine();
            }

            return ValueTask.FromResult(new ContentProcessorResult(
                output.ToString().TrimEnd(), OutputModality.Text,
                Utf8Data: output.AsSpan().ToArray(),
                IsTruncated: isTruncated || showLines < totalLines));
        }
        finally { output.Dispose(); }
    }
}
