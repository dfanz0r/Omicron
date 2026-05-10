using System.Text;

namespace Omicron.Core.IO;

/// <summary>
/// Processes CSV/TSV files by extracting structured text.
/// Uses CsvHelper when available; falls back to simple line-based reading.
/// </summary>
public sealed class CsvProcessor : IContentProcessor
{
    public string Id => "csv";
    public IReadOnlySet<DetectedFileType> SupportedTypes { get; }
        = new HashSet<DetectedFileType> { DetectedFileType.Csv };
    public OutputModality OutputModality => OutputModality.Text;

    public ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var bytes = context.Bytes;
        var ext = Path.GetExtension(context.RelativePath)?.ToLowerInvariant() ?? "";
        var delimiter = ext == ".tsv" ? '\t' : ',';

        if (bytes.IsEmpty)
        {
            return ValueTask.FromResult(new ContentProcessorResult(
                $"[FILE] {context.RelativePath}  (empty)", OutputModality.Text));
        }

        var sb = new StringBuilder();

        // Try CsvHelper first for robust parsing
        try
        {
            using var ms = new MemoryStream(bytes.ToArray());
            using var reader = new System.IO.StreamReader(ms, Encoding.UTF8);
            using var csv = new CsvHelper.CsvReader(reader, new CsvHelper.Configuration.CsvConfiguration(
                System.Globalization.CultureInfo.InvariantCulture)
            {
                Delimiter = delimiter.ToString(),
                HasHeaderRecord = true,
                DetectColumnCountChanges = false
            });

            csv.Read();
            csv.ReadHeader();
            var headers = csv.HeaderRecord ?? [];
            sb.AppendLine($"[FILE] {context.RelativePath}  (CSV, {delimiter}-delimited)");
            sb.AppendLine();
            sb.AppendLine($"Columns ({headers.Length}): {string.Join(", ", headers)}");
            sb.AppendLine();

            int rowCount = 0;
            const int maxRows = 50;
            while (csv.Read() && rowCount < maxRows)
            {
                var rowSb = new StringBuilder();
                for (int i = 0; i < csv.ColumnCount; i++)
                {
                    if (i > 0) rowSb.Append(delimiter);
                    rowSb.Append(csv.GetField(i) ?? "");
                }
                if (Encoding.UTF8.GetByteCount(sb.ToString() + rowSb + "\n") > 50 * 1024)
                {
                    sb.AppendLine($"... (output truncated)");
                    break;
                }
                sb.AppendLine($"{rowCount + 1,6}| {rowSb}");
                rowCount++;
            }

            if (csv.Read()) // more rows exist
                sb.AppendLine($"... ({rowCount}+ total rows)");

            return ValueTask.FromResult(new ContentProcessorResult(
                sb.ToString().TrimEnd(), OutputModality.Text));
        }
        catch
        {
            // CsvHelper failed — fall back to manual splitting
        }

        // Fallback: manual line splitting
        var text = Encoding.UTF8.GetString(bytes.Span).Replace("\r\n", "\n");
        var lines = text.Split('\n');

        sb.AppendLine($"[FILE] {context.RelativePath}  ({lines.Length} rows, {delimiter}-delimited)");
        sb.AppendLine();

        if (lines.Length > 0)
        {
            var columns = lines[0].Split(delimiter);
            sb.AppendLine($"Columns ({columns.Length}): {string.Join(", ", columns)}");
            sb.AppendLine();
        }

        int showRows = Math.Min(lines.Length - 1, 50);
        for (int i = 1; i <= showRows; i++)
        {
            var rowText = lines[i];
            if (Encoding.UTF8.GetByteCount(sb.ToString() + rowText + "\n") > 50 * 1024)
            {
                sb.AppendLine($"... ({lines.Length - 1} total rows, showing {i - 1})");
                break;
            }
            sb.AppendLine($"{i,6}| {rowText}");
        }

        if (showRows < lines.Length - 1)
            sb.AppendLine($"... ({lines.Length - 1} total rows, showing {showRows})");

        return ValueTask.FromResult(new ContentProcessorResult(
            sb.ToString().TrimEnd(), OutputModality.Text));
    }
}
