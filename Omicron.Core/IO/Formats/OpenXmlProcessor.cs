using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Omicron.Core.Content;
using Omicron.Core.Text;

namespace Omicron.Core.IO;

/// <summary>
///     Processes Open XML documents (DOCX, XLSX, PPTX) by extracting plain text.
///     Uses DocumentFormat.OpenXml when available; falls back to ZIP text scan.
/// </summary>
public sealed class OpenXmlProcessor : IContentProcessor
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    public string Id => "open_xml";

    public IReadOnlySet<DetectedFileType> SupportedTypes { get; } =
        new HashSet<DetectedFileType>
        {
            DetectedFileType.OpenXmlDocument
        };

    public OutputModality OutputModality => OutputModality.Text;

    public async ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        ReadOnlyMemory<byte> bytes = context.Bytes;
        string ext = Path.GetExtension(context.RelativePath)?.ToLowerInvariant() ?? "";

        string docType = ext switch
        {
            ".docx" => "Word",
            ".xlsx" => "Excel",
            ".pptx" => "PowerPoint",
            _ => "Open XML"
        };

        // Try to extract text
        string? text = TryExtractText(bytes.Span);

        if (text is not null)
        {
            Utf8Builder output = Utf8Text.CreateBuilder();
            try
            {
                Utf8CompositeFormat.AppendFormatUtf8Slow(ref output,
                    "[FILE] {0}  ({1}, {2})"u8,
                    context.RelativePath,
                    FormatSize.Format(bytes.Length),
                    docType);
                output.AppendLine();
                output.AppendLine();
                output.Append(text);

                return new ContentProcessorResult(output.ToString().TrimEnd(),
                    OutputModality.Text,
                    output.AsSpan().ToArray());
            }
            finally
            {
                output.Dispose();
            }
        }

        // Fallback: hex dump
        var hexDumper = new HexDumpProcessor();
        var hexContext = new ContentProcessorContext(context.AbsolutePath,
            context.RelativePath,
            context.Bytes,
            context.FileSize,
            context.SessionId,
            context.ModelMetadata,
            "hex",
            null,
            null,
            context.EventSink,
            context.CancellationToken);

        ContentProcessorResult hexResult = await hexDumper.ProcessAsync(hexContext, ct);

        Utf8Builder fallback = Utf8Text.CreateBuilder();
        try
        {
            Utf8CompositeFormat.AppendFormatUtf8Slow(ref fallback,
                "[FILE] {0}  ({1}, {2} — text extraction unavailable, showing hex dump)"u8,
                context.RelativePath,
                FormatSize.Format(bytes.Length),
                docType);
            fallback.AppendLine();
            fallback.Append(hexResult.Text);
            return new ContentProcessorResult(fallback.ToString().TrimEnd(),
                OutputModality.HexDump,
                fallback.AsSpan().ToArray(),
                Warning: "Open XML text extraction unavailable.");
        }
        finally
        {
            fallback.Dispose();
        }
    }

    /// <summary>
    ///     Extract text from Open XML documents using DocumentFormat.OpenXml,
    ///     with fallback to manual ZIP+XML traversal.
    /// </summary>
    private static string? TryExtractText(ReadOnlySpan<byte> bytes)
    {
        // Try DocumentFormat.OpenXml first (handles DOCX, XLSX, PPTX)
        using var ms = new MemoryStream(bytes.ToArray());
        try
        {
            using var doc = WordprocessingDocument.Open(ms, false);
            Body? body = doc.MainDocumentPart?.Document?.Body;
            if (body is not null)
            {
                string text = string.Concat(body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>()
                    .Select(t => t.Text));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch
        {
            ms.Position = 0;
            // Try XLSX
            try
            {
                using var xls = SpreadsheetDocument.Open(ms,
                    false);
                var sb = new StringBuilder();
                foreach (WorksheetPart sheet in xls.WorkbookPart?.WorksheetParts ?? [])
                {
                    SheetData? sheetData =
                        sheet.Worksheet?.GetFirstChild<SheetData>();
                    if (sheetData is null)
                    {
                        continue;
                    }

                    foreach (
                        Row row in sheetData.Elements<Row>()
                    )
                    {
                        foreach (
                            Cell cell in row.Elements<Cell>()
                        )
                        {
                            string cellText = cell.CellValue?.Text ?? cell.InnerText ?? "";
                            sb.Append(cellText).Append('\t');
                        }

                        sb.AppendLine();
                    }
                }

                string xlsText = sb.ToString().Trim();
                if (xlsText.Length > 0)
                {
                    return xlsText;
                }
            }
            catch
            {
                ms.Position = 0;
                // Try PPTX
                try
                {
                    using var ppt = PresentationDocument.Open(ms,
                        false);
                    var sb = new StringBuilder();
                    foreach (SlidePart slide in ppt.PresentationPart?.SlideParts ?? [])
                    {
                        foreach (
                            Shape slideShape in slide.Slide?.Descendants<Shape>()
                                                ?? []
                        )
                        {
                            string text =
                                slideShape
                                    .TextBody?.Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                                    ?.FirstOrDefault()
                                    ?.Text
                                ?? "";
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                sb.AppendLine(text);
                            }
                        }
                    }

                    string pptText = sb.ToString().Trim();
                    if (pptText.Length > 0)
                    {
                        return pptText;
                    }
                }
                catch { }
            }
        }

        // Fallback: manual ZIP entry scanning
        try
        {
            using var fallbackMs = new MemoryStream(bytes.ToArray());
            using var archive = new ZipArchive(fallbackMs,
                ZipArchiveMode.Read);

            // Try DOCX: word/document.xml
            ZipArchiveEntry? docEntry = archive.GetEntry("word/document.xml");
            if (docEntry is not null)
            {
                using var reader = new StreamReader(docEntry.Open(), Encoding.UTF8);
                string xml = reader.ReadToEnd();
                return ExtractTextFromXml(xml);
            }

            // Try XLSX: xl/sharedStrings.xml
            ZipArchiveEntry? stringsEntry = archive.GetEntry("xl/sharedStrings.xml");
            if (stringsEntry is not null)
            {
                using var reader = new StreamReader(stringsEntry.Open(), Encoding.UTF8);
                string xml = reader.ReadToEnd();
                return ExtractTextFromXml(xml);
            }

            // Try XLSX: xl/worksheets/sheet*.xml
            for (int i = 1; i <= 20; i++)
            {
                ZipArchiveEntry? sheetEntry = archive.GetEntry($"xl/worksheets/sheet{i}.xml");
                if (sheetEntry is not null)
                {
                    using var reader = new StreamReader(sheetEntry.Open(), Encoding.UTF8);
                    string xml = reader.ReadToEnd();
                    string text = ExtractTextFromXml(xml);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }

            // Try PPTX: ppt/slides/slide*.xml
            for (int i = 1; i <= 50; i++)
            {
                ZipArchiveEntry? slideEntry = archive.GetEntry($"ppt/slides/slide{i}.xml");
                if (slideEntry is not null)
                {
                    using var reader = new StreamReader(slideEntry.Open(), Encoding.UTF8);
                    string xml = reader.ReadToEnd();
                    string slideText = ExtractTextFromXml(xml);
                    if (!string.IsNullOrWhiteSpace(slideText))
                    {
                        return slideText;
                    }
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
    ///     Very simple XML text extraction: strip tags, decode entities.
    /// </summary>
    private static string ExtractTextFromXml(string xml)
    {
        if (xml.Length > 10 * 1024 * 1024) // 10 MB cap
        {
            return "(document too large)";
        }

        using Utf8Builder output = Utf8Text.CreateBuilder();
        bool inTag = false;
        bool inEntity = false;
        var entityBuf = new StringBuilder();

        for (int i = 0; i < xml.Length; i++)
        {
            char c = xml[i];
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

            if (inTag)
            {
                continue;
            }

            if (c == '&')
            {
                inEntity = true;
                entityBuf.Clear();
                continue;
            }

            if (c == ';' && inEntity)
            {
                inEntity = false;
                string entity = entityBuf.ToString();
                char decoded = entity switch
                {
                    "amp" => '&',
                    "lt" => '<',
                    "gt" => '>',
                    "quot" => '"',
                    "apos" => '\'',
                    _ => '\0'
                };
                if (decoded != '\0')
                {
                    output.Append(decoded);
                }
                else if (entity.StartsWith("#x"))
                {
                    output.Append((char)Convert.ToInt32(entity[2..], 16));
                }
                else if (entity.StartsWith("#"))
                {
                    output.Append((char)Convert.ToInt32(entity[1..]));
                }

                continue;
            }

            if (inEntity)
            {
                entityBuf.Append(c);
                continue;
            }

            output.Append(c);
        }

        // Collapse whitespace runs for cleaner output
        string result = output.ToString();
        return WhitespaceRegex.Replace(result, " ").Trim();
    }
}
