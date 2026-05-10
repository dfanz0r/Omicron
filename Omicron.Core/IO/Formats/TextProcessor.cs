using System.Text;

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

        string text;
        try
        {
            // Try strict UTF-8 first (use cached encoder from TextEncodingDetector)
            var (encoding, _) = TextEncodingDetector.DetectEncoding(bytes.Span);
            text = encoding.GetString(bytes.Span);
        }
        catch (DecoderFallbackException)
        {
            // Fall back to lenient UTF-8
            var utf8Lenient = new UTF8Encoding(false, false);
            text = utf8Lenient.GetString(bytes.Span);
            isBinary = true;
        }

        // Normalize line endings
        text = text.Replace("\r\n", "\n");

        var lines = text.Split('\n');
        var totalLines = lines.Length;

        var sb = new StringBuilder();
        if (isBinary)
        {
            sb.AppendLine($"[WARNING: binary file] {context.RelativePath}");
        }
        else
        {
            sb.AppendLine($"[FILE] {context.RelativePath}  ({totalLines} lines)");
        }

        // Apply offset/limit for text mode (same as existing read_path behavior)
        const int maxOutputLines = 2000;
        const int maxOutputBytes = 50 * 1024;

        int startLine = 0;
        if (context.Offset.HasValue && context.Offset.Value > 0)
            startLine = context.Offset.Value - 1;

        int limit = context.Limit ?? int.MaxValue;
        if (limit <= 0) limit = int.MaxValue;

        int endLine = Math.Min(startLine + limit, lines.Length);

        int writtenLines = 0;
        int writtenBytes = 0;
        bool truncated = false;

        for (int i = startLine; i < endLine; i++)
        {
            var line = lines[i];
            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;

            if (writtenLines >= maxOutputLines ||
                (writtenBytes > 0 && writtenBytes + lineBytes > maxOutputBytes))
            {
                truncated = i < lines.Length - 1;
                break;
            }

            sb.AppendLine($"{(i + 1),6}| {line}");
            writtenLines++;
            writtenBytes += lineBytes;
        }

        if (truncated)
        {
            sb.AppendLine($"... output truncated ({totalLines} total lines, showing {writtenLines})");
        }

        return ValueTask.FromResult(new ContentProcessorResult(
            sb.ToString().TrimEnd(),
            OutputModality.Text));
    }
}
