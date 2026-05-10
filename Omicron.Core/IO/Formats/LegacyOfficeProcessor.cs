using System.Text;

namespace Omicron.Core.IO;

/// <summary>
/// Processes legacy Office documents (.doc, .xls, .ppt) by extracting plain text.
/// Uses NPOI when available; falls back to basic text extraction from binary formats.
/// </summary>
public sealed class LegacyOfficeProcessor : IContentProcessor
{
    public string Id => "legacy_office";
    public IReadOnlySet<DetectedFileType> SupportedTypes { get; }
        = new HashSet<DetectedFileType> { DetectedFileType.LegacyDocument };
    public OutputModality OutputModality => OutputModality.Text;

    public async ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var bytes = context.Bytes;
        var ext = Path.GetExtension(context.RelativePath)?.ToLowerInvariant() ?? "";

        string docType = ext switch
        {
            ".doc" => "Word (legacy)",
            ".xls" => "Excel (legacy)",
            ".ppt" => "PowerPoint (legacy)",
            _ => "Legacy Office"
        };

        // Try simple text extraction from OLE2 compound document
        var text = TryExtractText(bytes.Span);

        if (text is not null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[FILE] {context.RelativePath}  ({FormatSize(bytes.Length)}, {docType})");
            sb.AppendLine();
            sb.Append(text);

            return new ContentProcessorResult(
                sb.ToString().TrimEnd(), OutputModality.Text);
        }

        // Fallback: hex dump
        var hexDumper = new HexDumpProcessor();
        var hexContext = new ContentProcessorContext(
            context.AbsolutePath, context.RelativePath, context.Bytes, context.FileSize,
            context.SessionId, context.ModelMetadata, "hex", null, null,
            context.EventSink, context.CancellationToken);

        var hexResult = await hexDumper.ProcessAsync(hexContext, ct);

        return new ContentProcessorResult(
            $"[FILE] {context.RelativePath}  ({FormatSize(bytes.Length)}, {docType} — text extraction unavailable, showing hex dump)\n{hexResult.Text}",
            OutputModality.HexDump,
            Warning: "Legacy Office text extraction unavailable.");
    }

    /// <summary>
    /// Extract text from legacy Office documents using NPOI,
    /// with fallback to OLE2 binary text scanning.
    /// </summary>
    private static string? TryExtractText(ReadOnlySpan<byte> bytes)
    {
        var ext = ""; // passed separately in a real implementation

        // Try NPOI first (HSSFWorkbook for .xls is available in NPOI.Core)
        try
        {
            using var ms = new MemoryStream(bytes.ToArray());
            // Try as .xls (HSSF is included in NPOI.Core)
            try
            {
                using var xls = new NPOI.HSSF.UserModel.HSSFWorkbook(ms);
                var sb = new System.Text.StringBuilder();
                for (int s = 0; s < xls.NumberOfSheets; s++)
                {
                    var sheet = xls.GetSheetAt(s);
                    for (int r = 0; r <= sheet.LastRowNum; r++)
                    {
                        var row = sheet.GetRow(r);
                        if (row is null) continue;
                        for (int c = 0; c < row.LastCellNum; c++)
                        {
                            var cell = row.GetCell(c);
                            if (cell is not null)
                                sb.Append(cell.ToString()).Append('\t');
                        }
                        sb.AppendLine();
                    }
                }
                var xlsText = sb.ToString().Trim();
                if (xlsText.Length > 0) return xlsText;
            }
            catch
            {
                // .doc (HWPF) and .ppt (HSLF) require NPOI.Scratchpad which is not available
                // Fall through to OLE2 scanning
            }
        }
        catch
        {
            // NPOI failed — fall back to OLE2 scanning
        }

        // Fallback: OLE2 binary text scanning
        try
        {
            if (bytes.Length < 8) return null;
            if (bytes[0] != 0xD0 || bytes[1] != 0xCF) return null;

            var sb = new StringBuilder();
            bool inText = false;

            for (int i = 0; i < bytes.Length - 2; i += 2)
            {
                char low = (char)bytes[i];
                char high = (char)bytes[i + 1];

                if (high == 0 && low >= 0x20 && low <= 0x7E)
                {
                    sb.Append(low);
                    inText = true;
                }
                else if (high == 0 && (low == '\r' || low == '\n'))
                {
                    if (inText) sb.Append(low);
                }
                else
                {
                    if (inText && sb.Length > 20)
                    {
                        if (sb[^1] != '\n') sb.Append('\n');
                    }
                    inText = false;
                }
            }

            var result = sb.ToString().Trim();
            return result.Length > 50 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
