using System.Text;
using Cysharp.Text;

namespace Omicron.Core.IO;

/// <summary>
/// Processes text files using the existing text reading path.
/// Handles the "text" format override and is the default for Text files.
/// </summary>
public sealed class TextProcessor : IContentProcessor
{
    public string Id => "text";
    public IReadOnlySet<DetectedFileType> SupportedTypes { get; }
        = new HashSet<DetectedFileType> { DetectedFileType.Text, DetectedFileType.Svg };
    public OutputModality OutputModality => OutputModality.Text;

    public ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var bytes = context.Bytes;
        var isBinary = false;

        if (bytes.IsEmpty)
        {
            return ValueTask.FromResult(new ContentProcessorResult(
                $"[FILE] {context.RelativePath}  (empty file)",
                OutputModality.Text));
        }

        // Safety check: if classification was bypassed (format: "text"), verify content
        if (!TextEncodingDetector.IsText(bytes.Span))
        {
            isBinary = true;
        }

        // Detect encoding without decoding the whole file
        Encoding encoding;
        try
        {
            (encoding, _) = TextEncodingDetector.DetectEncoding(bytes.Span);
        }
        catch
        {
            encoding = new UTF8Encoding(false, false);
            isBinary = true;
        }

        var span = bytes.Span;
        bool isUtf8Compatible = encoding is UTF8Encoding || encoding.WebName == "utf-8" ||
                                encoding.CodePage == 65001 || encoding.CodePage == 20127;

        if (isUtf8Compatible)
        {
            // UTF-8/ASCII: scan raw bytes for line boundaries without allocating a full-file string.
            var builder = ZString.CreateUtf8StringBuilder();
            try
            {
                if (isBinary)
                {
                    builder.AppendFormat("[WARNING: binary file] {0}", context.RelativePath);
                    builder.AppendLine();
                }
                BuildTextOutputUtf8(context, span, ref builder, isBinary);
                var bytesOut = builder.AsSpan().ToArray();
                return ValueTask.FromResult(new ContentProcessorResult(
                    Encoding.UTF8.GetString(bytesOut).TrimEnd(),
                    OutputModality.Text,
                    Utf8Data: bytesOut));
            }
            finally { builder.Dispose(); }
        }
        else
        {
            // Non-UTF-8 encoding (e.g. UTF-16, UTF-32): decode the whole file, then split.
            // Byte scanning for \n is not safe here because \n is multi-byte.
            var builder = ZString.CreateUtf8StringBuilder();
            try
            {
                if (isBinary)
                {
                    builder.AppendFormat("[WARNING: binary file] {0}", context.RelativePath);
                    builder.AppendLine();
                }
                BuildTextOutputFallback(context, span, encoding, ref builder, isBinary);
                var bytesOut = builder.AsSpan().ToArray();
                return ValueTask.FromResult(new ContentProcessorResult(
                    Encoding.UTF8.GetString(bytesOut).TrimEnd(),
                    OutputModality.Text,
                    Utf8Data: bytesOut));
            }
            finally { builder.Dispose(); }
        }
    }

    private static void BuildTextOutputUtf8(
        ContentProcessorContext context,
        ReadOnlySpan<byte> span,
        ref Utf8ValueStringBuilder builder,
        bool isBinary)
    {
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == (byte)'\n')
                lineStarts.Add(i + 1);
        }
        var totalLines = lineStarts.Count;

        if (!isBinary)
        {
            builder.AppendFormat("[FILE] {0}  ({1} lines)", context.RelativePath, totalLines);
            builder.AppendLine();
        }

        const int maxOutputLines = 2000;
        const int maxOutputBytes = 50 * 1024;

        int startLine = 0;
        if (context.Offset.HasValue && context.Offset.Value > 0)
            startLine = context.Offset.Value - 1;

        int limit = context.Limit ?? int.MaxValue;
        if (limit <= 0) limit = int.MaxValue;

        int endLine = Math.Min(startLine + limit, totalLines);

        int writtenLines = 0;
        int writtenBytes = builder.Length; // count header bytes
        bool truncated = false;

        for (int i = startLine; i < endLine; i++)
        {
            int lineByteStart = lineStarts[i];
            int lineByteEnd = (i + 1 < lineStarts.Count) ? lineStarts[i + 1] : span.Length;

            // Strip trailing \r and \n
            int lineLength = lineByteEnd - lineByteStart;
            if (lineLength > 0 && span[lineByteStart + lineLength - 1] == (byte)'\n')
                lineLength--;
            if (lineLength > 0 && span[lineByteStart + lineLength - 1] == (byte)'\r')
                lineLength--;

            // Format: "     6| <line content>"
            var prefix = $"{i + 1,6}| ";
            var prefixBytes = Encoding.UTF8.GetByteCount(prefix);
            int lineBytes = prefixBytes + lineLength + 1; // prefix + content + newline

            if (writtenLines >= maxOutputLines ||
                writtenBytes + lineBytes > maxOutputBytes)
            {
                truncated = true;
                break;
            }

            builder.Append(prefix);
            builder.AppendLiteral(span.Slice(lineByteStart, lineLength));
            builder.AppendLine();
            writtenLines++;
            writtenBytes += lineBytes;
        }

        if (truncated)
        {
            builder.AppendFormat("... output truncated ({0} total lines, showing {1})", totalLines, writtenLines);
            builder.AppendLine();
        }
    }

    private static void BuildTextOutputFallback(
        ContentProcessorContext context,
        ReadOnlySpan<byte> span,
        Encoding encoding,
        ref Utf8ValueStringBuilder builder,
        bool isBinary)
    {
        string text;
        try
        {
            text = encoding.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            // Invalid bytes for the detected encoding — fall back to lenient UTF-8
            text = Encoding.UTF8.GetString(span);
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var totalLines = lines.Length;

        if (!isBinary)
        {
            builder.AppendFormat("[FILE] {0}  ({1} lines)", context.RelativePath, totalLines);
            builder.AppendLine();
        }

        const int maxOutputLines = 2000;
        const int maxOutputBytes = 50 * 1024;

        int startLine = 0;
        if (context.Offset.HasValue && context.Offset.Value > 0)
            startLine = context.Offset.Value - 1;

        int limit = context.Limit ?? int.MaxValue;
        if (limit <= 0) limit = int.MaxValue;

        int endLine = Math.Min(startLine + limit, totalLines);

        int writtenLines = 0;
        int writtenBytes = builder.Length; // count header bytes
        bool truncated = false;

        for (int i = startLine; i < endLine; i++)
        {
            var lineText = lines[i];
            var prefix = $"{i + 1,6}| ";
            var prefixBytes = Encoding.UTF8.GetByteCount(prefix);
            int lineBytes = prefixBytes + Encoding.UTF8.GetByteCount(lineText) + 1;

            if (writtenLines >= maxOutputLines ||
                writtenBytes + lineBytes > maxOutputBytes)
            {
                truncated = true;
                break;
            }

            builder.Append(prefix);
            builder.Append(lineText);
            builder.AppendLine();
            writtenLines++;
            writtenBytes += lineBytes;
        }

        if (truncated)
        {
            builder.AppendFormat("... output truncated ({0} total lines, showing {1})", totalLines, writtenLines);
            builder.AppendLine();
        }
    }
}
