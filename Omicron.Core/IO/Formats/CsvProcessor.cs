using System.Buffers;
using System.Text;
using Cysharp.Text;
using Omicron.Core.Text;

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
        var delimiterStr = new string(delimiter, 1);

        if (bytes.IsEmpty)
        {
            return ValueTask.FromResult(new ContentProcessorResult(
                $"[FILE] {context.RelativePath}  (empty)", OutputModality.Text));
        }

        var output = ZString.CreateUtf8StringBuilder();
        try
        {

            // Try CsvHelper first for robust parsing
            try
            {
                using var ms = new MemoryStream(bytes.ToArray());
                using var reader = new System.IO.StreamReader(ms, Encoding.UTF8);
                using var csv = new CsvHelper.CsvReader(reader, new CsvHelper.Configuration.CsvConfiguration(
                    System.Globalization.CultureInfo.InvariantCulture)
                {
                    Delimiter = delimiterStr,
                    HasHeaderRecord = true,
                    DetectColumnCountChanges = false
                });

                csv.Read();
                csv.ReadHeader();
                var headers = csv.HeaderRecord ?? [];
                Utf8CompositeFormat.AppendFormatUtf8Slow(
                    ref output, "[FILE] {0}  (CSV, {1}-delimited)"u8, context.RelativePath, delimiter);
                output.AppendLine();
                output.AppendLine();
                Utf8CompositeFormat.AppendFormatUtf8Slow(ref output, "Columns ({0}): "u8, headers.Length);
                // Join headers without allocating a string
                for (int h = 0; h < headers.Length; h++)
                {
                    if (h > 0) output.AppendLiteral(", "u8);
                    output.Append(headers[h]);
                }
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
                    // Build "{rowCount + 1,6}| " directly without allocating a string
                    var rowNum = rowCount + 1;
                    // "{0,6}| " always produces exactly 8 bytes for 0-999999
                    const int RowPrefixBytes = 8;
                    var totalBytes = RowPrefixBytes + rowBytes;

                    if (totalBytes > byteBudget)
                    {
                        output.AppendLiteral("... (output truncated)"u8);
                        output.AppendLine();
                        stoppedDueToOutputLimit = true;
                        hasMoreRows = true;
                        break;
                    }

                    // Write right-justified row number: "     1| " style
                    int digits = rowNum < 10 ? 1 : rowNum < 100 ? 2 : rowNum < 1000 ? 3 : rowNum < 10000 ? 4 : 5;
                    for (int s = 0; s < 6 - digits; s++) output.Append(' ');
                    output.Append(rowNum);
                    output.AppendLiteral("| "u8);
                    output.AppendLiteral(rowBuilder.AsSpan());
                    byteBudget -= totalBytes;
                    rowCount++;
                }

                if (!stoppedDueToOutputLimit && rowCount >= MaxPreviewRows)
                    hasMoreRows = csv.Read();

                if (hasMoreRows)
                {
                    Utf8CompositeFormat.AppendFormatUtf8Slow(
                        ref output, "... ({0}+ total rows)"u8, rowCount);
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

            Utf8CompositeFormat.AppendFormatUtf8Slow(
                ref output, "[FILE] {0}  ({1} rows, {2}-delimited)"u8, context.RelativePath, totalLines, delimiter);
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
                Utf8CompositeFormat.AppendFormatUtf8Slow(ref output, "Columns ({0}): "u8, columns.Length);
                for (int c = 0; c < columns.Length; c++)
                {
                    if (c > 0) output.AppendLiteral(", "u8);
                    output.Append(columns[c]);
                }
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

                // Build prefix: "{rowNum,6}| " — always 8 bytes for 0-999999
                const int RowPrefixBytes = 8;
                int rowBytes = lineLen + 1;

                if (RowPrefixBytes + rowBytes > remainingByteBudget)
                {
                    Utf8CompositeFormat.AppendFormatUtf8Slow(
                        ref output, "... ({0} total rows, showing {1})"u8, totalLines - 1, shownRows);
                    output.AppendLine();
                    truncatedByBudget = true;
                    break;
                }

                // Write right-justified row number: "     1| "
                int digits = i < 10 ? 1 : i < 100 ? 2 : i < 1000 ? 3 : i < 10000 ? 4 : 5;
                for (int s = 0; s < 6 - digits; s++) output.Append(' ');
                output.Append(i);
                output.AppendLiteral("| "u8);
                // Write row bytes directly, avoiding string decode
                output.AppendLiteral(span.Slice(lineStart, lineLen));
                output.AppendLine();
                remainingByteBudget -= RowPrefixBytes + rowBytes;
                shownRows++;
            }

            if (!truncatedByBudget && shownRows < totalLines - 1)
            {
                Utf8CompositeFormat.AppendFormatUtf8Slow(
                    ref output, "... ({0} total rows, showing {1})"u8, totalLines - 1, shownRows);
                output.AppendLine();
            }

            return ValueTask.FromResult(new ContentProcessorResult(
                output.ToString().TrimEnd(), OutputModality.Text,
                Utf8Data: output.AsSpan().ToArray()));
        }
        finally { output.Dispose(); }
    }
}
