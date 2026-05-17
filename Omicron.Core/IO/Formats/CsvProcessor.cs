using System.Buffers;
using System.Text;
using Cysharp.Text;

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

    private const int MaxOutputBytes = 50 * 1024;
    private const int MaxPreviewRows = 50;

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

        using var output = ZString.CreateUtf8StringBuilder();

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
            output.Append($"[FILE] {context.RelativePath}  (CSV, {delimiter}-delimited)");
            output.AppendLine();
            output.AppendLine();
            output.Append($"Columns ({headers.Length}): ");
            output.Append(string.Join(", ", headers));
            output.AppendLine();
            output.AppendLine();

            int rowCount = 0;
            int byteBudget = MaxOutputBytes - output.Length;
            bool stoppedDueToOutputLimit = false;
            bool hasMoreRows = false;

            while (rowCount < MaxPreviewRows && byteBudget > 0)
            {
                if (!csv.Read())
                    break;

                using var rowBuilder = ZString.CreateUtf8StringBuilder();
                for (int i = 0; i < csv.ColumnCount; i++)
                {
                    if (i > 0) rowBuilder.Append(delimiter);
                    rowBuilder.Append(csv.GetField(i) ?? "");
                }
                rowBuilder.AppendLine();

                var rowBytes = rowBuilder.Length;
                var rowPrefix = $"{rowCount + 1,6}| ";
                var prefixBytes = Encoding.UTF8.GetByteCount(rowPrefix);
                var totalBytes = prefixBytes + rowBytes;

                if (totalBytes > byteBudget)
                {
                    output.Append("... (output truncated)");
                    output.AppendLine();
                    stoppedDueToOutputLimit = true;
                    hasMoreRows = true; // current row exists but was not emitted
                    break;
                }

                output.Append(rowPrefix);
                output.AppendLiteral(rowBuilder.AsSpan());
                byteBudget -= totalBytes;
                rowCount++;
            }

            if (!stoppedDueToOutputLimit && rowCount >= MaxPreviewRows)
                hasMoreRows = csv.Read();

            if (hasMoreRows)
            {
                output.Append("... (");
                output.Append(rowCount);
                output.Append("+ total rows)");
                output.AppendLine();
            }

            return ValueTask.FromResult(new ContentProcessorResult(
                output.ToString().TrimEnd(), OutputModality.Text,
                Utf8Data: output.AsSpan().ToArray()));
        }
        catch
        {
            // CsvHelper failed — fall back to manual splitting
        }

        // Fallback: scan raw bytes for line boundaries without allocating a full string.
        // Also pre-compute line start positions so we can track the byte budget accurately.
        var span = bytes.Span;
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == (byte)'\n' && i + 1 < span.Length)
                lineStarts.Add(i + 1);
        }

        var totalLines = lineStarts.Count;

        output.Append($"[FILE] {context.RelativePath}  ({totalLines} rows, {delimiter}-delimited)");
        output.AppendLine();
        output.AppendLine();

        // Decode header line to extract column names
        if (totalLines > 0)
        {
            int headerStart = lineStarts[0];
            int headerEnd = totalLines > 1 ? lineStarts[1] : span.Length;
            int headerLen = headerEnd - headerStart;
            // Strip trailing newline characters from header
            if (headerLen > 0 && span[headerStart + headerLen - 1] == (byte)'\n') headerLen--;
            if (headerLen > 0 && span[headerStart + headerLen - 1] == (byte)'\r') headerLen--;

            var headerText = Encoding.UTF8.GetString(span.Slice(headerStart, headerLen));
            var columns = headerText.Split(delimiter);
            output.Append($"Columns ({columns.Length}): ");
            output.Append(string.Join(", ", columns));
            output.AppendLine();
            output.AppendLine();
        }

        int remainingByteBudget = MaxOutputBytes - output.Length;
        int showRows = Math.Min(totalLines - 1, MaxPreviewRows);
        int shownRows = 0;
        bool truncatedByBudget = false;
        for (int i = 1; i <= showRows; i++)
        {
            int lineStart = lineStarts[i];
            int lineEnd = (i + 1 < lineStarts.Count) ? lineStarts[i + 1] : span.Length;
            int lineLen = lineEnd - lineStart;
            // Strip trailing newline
            if (lineLen > 0 && span[lineStart + lineLen - 1] == (byte)'\n') lineLen--;
            if (lineLen > 0 && span[lineStart + lineLen - 1] == (byte)'\r') lineLen--;

            var rowText = Encoding.UTF8.GetString(span.Slice(lineStart, lineLen));
            var rowPrefix = $"{i,6}| ";
            var prefixBytes = Encoding.UTF8.GetByteCount(rowPrefix);
            var rowBytes = lineLen + 1; // +1 for newline we'll append

            if (prefixBytes + rowBytes > remainingByteBudget)
            {
                output.Append("... (");
                output.Append(totalLines - 1);
                output.Append(" total rows, showing ");
                output.Append(shownRows);
                output.Append(")");
                output.AppendLine();
                truncatedByBudget = true;
                break;
            }

            output.Append(rowPrefix);
            output.Append(rowText);
            output.AppendLine();
            remainingByteBudget -= prefixBytes + rowBytes;
            shownRows++;
        }

        if (!truncatedByBudget && shownRows < totalLines - 1)
        {
            output.Append("... (");
            output.Append(totalLines - 1);
            output.Append(" total rows, showing ");
            output.Append(shownRows);
            output.Append(")");
            output.AppendLine();
        }

        return ValueTask.FromResult(new ContentProcessorResult(
            output.ToString().TrimEnd(), OutputModality.Text,
            Utf8Data: output.AsSpan().ToArray()));
    }
}
