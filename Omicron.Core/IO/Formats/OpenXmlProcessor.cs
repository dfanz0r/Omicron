using System.Text;
using System.Text.RegularExpressions;
namespace Omicron.Core.IO;

/// <summary>
/// Processes Open XML documents (DOCX, XLSX, PPTX) by extracting plain text.
/// Uses DocumentFormat.OpenXml when available; falls back to ZIP text scan.
/// </summary>
public sealed class OpenXmlProcessor : IContentProcessor
{
    private static readonly Regex WhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);
    public string Id => "open_xml";

    public IReadOnlySet<DetectedFileType> SupportedTypes { get; }
        = new HashSet<DetectedFileType> { DetectedFileType.OpenXmlDocument };
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
            ".docx" => "Word",
            ".xlsx" => "Excel",
            ".pptx" => "PowerPoint",
            _ => "Open XML"
        };

        // Try to extract text
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
            Warning: "Open XML text extraction unavailable.");
    }

    /// <summary>
    /// Extract text from Open XML documents using DocumentFormat.OpenXml,
    /// with fallback to manual ZIP+XML traversal.
    /// </summary>
    private static string? TryExtractText(ReadOnlySpan<byte> bytes)
    {
        // Try DocumentFormat.OpenXml first (handles DOCX, XLSX, PPTX)
        try
        {
            using var ms = new MemoryStream(bytes.ToArray());
            // Try DOCX
            try
            {
                using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(ms, false);
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body is not null)
                {
                    var text = string.Concat(body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(t => t.Text));
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
            catch
            {
                ms.Position = 0;
                // Try XLSX
                try
                {
                    using var xls = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(ms, false);
                    var sb = new System.Text.StringBuilder();
                    foreach (var sheet in xls.WorkbookPart.WorksheetParts)
                    {
                        var sheetData = sheet.Worksheet.GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.SheetData>();
                        if (sheetData is null) continue;
                        foreach (var row in sheetData.Elements<DocumentFormat.OpenXml.Spreadsheet.Row>())
                        {
                            foreach (var cell in row.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>())
                            {
                                var cellText = cell.CellValue?.Text ?? cell.InnerText ?? "";
                                sb.Append(cellText).Append('\t');
                            }
                            sb.AppendLine();
                        }
                    }
                    var xlsText = sb.ToString().Trim();
                    if (xlsText.Length > 0) return xlsText;
                }
                catch
                {
                    ms.Position = 0;
                    // Try PPTX
                    try
                    {
                        using var ppt = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(ms, false);
                        var sb = new System.Text.StringBuilder();
                        foreach (var slide in ppt.PresentationPart.SlideParts)
                        {
                            foreach (var slideShape in slide.Slide.Descendants<DocumentFormat.OpenXml.Presentation.Shape>())
                            {
                                var text = slideShape.TextBody?.Descendants<DocumentFormat.OpenXml.Drawing.Text>()?.FirstOrDefault()?.Text ?? "";
                                if (!string.IsNullOrWhiteSpace(text))
                                    sb.AppendLine(text);
                            }
                        }
                        var pptText = sb.ToString().Trim();
                        if (pptText.Length > 0) return pptText;
                    }
                    catch { }
                }
            }
        }
        catch
        {
            // DocumentFormat.OpenXml failed — fall back to manual ZIP traversal
        }

        // Fallback: manual ZIP entry scanning
        try
        {
            using var ms = new MemoryStream(bytes.ToArray());
            using var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);

            // Try DOCX: word/document.xml
            var docEntry = archive.GetEntry("word/document.xml");
            if (docEntry is not null)
            {
                using var reader = new System.IO.StreamReader(docEntry.Open(), Encoding.UTF8);
                var xml = reader.ReadToEnd();
                return ExtractTextFromXml(xml);
            }

            // Try XLSX: xl/sharedStrings.xml
            var stringsEntry = archive.GetEntry("xl/sharedStrings.xml");
            if (stringsEntry is not null)
            {
                using var reader = new System.IO.StreamReader(stringsEntry.Open(), Encoding.UTF8);
                var xml = reader.ReadToEnd();
                return ExtractTextFromXml(xml);
            }

            // Try XLSX: xl/worksheets/sheet*.xml
            for (int i = 1; i <= 20; i++)
            {
                var sheetEntry = archive.GetEntry($"xl/worksheets/sheet{i}.xml");
                if (sheetEntry is not null)
                {
                    using var reader = new System.IO.StreamReader(sheetEntry.Open(), Encoding.UTF8);
                    var xml = reader.ReadToEnd();
                    var text = ExtractTextFromXml(xml);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }

            // Try PPTX: ppt/slides/slide*.xml
            for (int i = 1; i <= 50; i++)
            {
                var slideEntry = archive.GetEntry($"ppt/slides/slide{i}.xml");
                if (slideEntry is not null)
                {
                    using var reader = new System.IO.StreamReader(slideEntry.Open(), Encoding.UTF8);
                    var xml = reader.ReadToEnd();
                    var slideText = ExtractTextFromXml(xml);
                    if (!string.IsNullOrWhiteSpace(slideText))
                        return slideText;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Very simple XML text extraction: strip tags, decode entities.
    /// </summary>
    private static string ExtractTextFromXml(string xml)
    {
        if (xml.Length > 10 * 1024 * 1024) // 10 MB cap
            return "(document too large)";

        var sb = new StringBuilder(xml.Length);
        bool inTag = false;
        bool inEntity = false;
        var entityBuf = new StringBuilder();

        for (int i = 0; i < xml.Length; i++)
        {
            var c = xml[i];
            if (c == '<')
            {
                inTag = true;
                continue;
            }
            if (c == '>')
            {
                inTag = false;
                continue;
            }
            if (inTag) continue;

            if (c == '&')
            {
                inEntity = true;
                entityBuf.Clear();
                continue;
            }
            if (c == ';' && inEntity)
            {
                inEntity = false;
                var entity = entityBuf.ToString();
                var decoded = entity switch
                {
                    "amp" => '&',
                    "lt" => '<',
                    "gt" => '>',
                    "quot" => '"',
                    "apos" => '\'',
                    _ => '\0'
                };
                if (decoded != '\0')
                    sb.Append(decoded);
                else if (entity.StartsWith("#x"))
                    sb.Append((char)Convert.ToInt32(entity[2..], 16));
                else if (entity.StartsWith("#"))
                    sb.Append((char)Convert.ToInt32(entity[1..]));
                continue;
            }
            if (inEntity)
            {
                entityBuf.Append(c);
                continue;
            }

            sb.Append(c);
        }

        // Collapse whitespace runs for cleaner output
        var result = sb.ToString();
        return WhitespaceRegex.Replace(result, " ").Trim();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
