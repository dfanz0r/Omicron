using System.Text;
using Omicron.Core.Content;

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

        if (bytes.Length > MaxSvgBytes)
        {
            return ValueTask.FromResult(new ContentProcessorResult(
                $"[FILE] {context.RelativePath}  ({FormatSize.Format(bytes.Length)}, SVG — file too large, showing first {FormatSize.Format(MaxSvgBytes)})\n" +
                                Encoding.UTF8.GetString(bytes[..(int)MaxSvgBytes].Span),
                OutputModality.Text,
                IsTruncated: true));
        }

        var text = Encoding.UTF8.GetString(bytes.Span);
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var sb = new StringBuilder();
        sb.AppendLine($"[FILE] {context.RelativePath}  ({lines.Length} lines, SVG)");
        sb.AppendLine();

        // Show up to 2000 lines
        int showLines = Math.Min(lines.Length, 2000);
        for (int i = 0; i < showLines; i++)
        {
            sb.AppendLine($"{(i + 1),6}| {lines[i]}");
        }

        if (showLines < lines.Length)
        {
            sb.AppendLine($"... ({lines.Length} total lines, showing {showLines})");
        }

        return ValueTask.FromResult(new ContentProcessorResult(
            sb.ToString().TrimEnd(), OutputModality.Text));
    }


}
