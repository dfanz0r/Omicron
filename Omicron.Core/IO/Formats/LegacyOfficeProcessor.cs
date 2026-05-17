using System.Text;
using Omicron.Core.Content;

namespace Omicron.Core.IO;

/// <summary>
/// Processes legacy Office documents (.doc, .xls, .ppt) by extracting plain text.
/// Converts Office 97-2003 binary files to OpenXML via DocSharp, then delegates
/// text extraction to <see cref="OpenXmlProcessor"/>. Falls back to best-effort
/// OLE2 binary text scanning and finally hex dump when conversion/extraction is unavailable.
/// </summary>
public sealed class LegacyOfficeProcessor : IContentProcessor
{
    public string Id => "legacy_office";
    public IReadOnlySet<DetectedFileType> SupportedTypes { get; }
        = new HashSet<DetectedFileType> { DetectedFileType.LegacyDocument };
    public OutputModality OutputModality => OutputModality.Text;

    private const long MaxLegacyOfficeBytes = 100 * 1024 * 1024; // 100 MB

    /// <summary>
    /// Result of a DocSharp conversion attempt.
    /// </summary>
    private sealed record LegacyConversionResult(
        byte[] ConvertedBytes,
        string ConvertedExtension,
        string Method,
        string? Warning = null);

    public async ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var bytes = context.Bytes;
        var ext = Path.GetExtension(context.RelativePath)?.ToLowerInvariant() ?? "";
        var docType = GetLegacyDocType(ext);

        if (bytes.Length > MaxLegacyOfficeBytes)
        {
            return await HexDumpFallbackAsync(context, bytes, docType, "file too large", ct);
        }

        // Stage 1: Try DocSharp conversion to OpenXML
        var conversion = await TryConvertLegacyToOpenXmlAsync(bytes, ext, ct);

        if (conversion is not null)
        {
            try
            {
                var openXmlProcessor = new OpenXmlProcessor();
                var convertedRelativePath = Path.ChangeExtension(context.RelativePath, conversion.ConvertedExtension);
                var openXmlContext = new ContentProcessorContext(
                    context.AbsolutePath,
                    convertedRelativePath,
                    conversion.ConvertedBytes,
                    conversion.ConvertedBytes.Length,
                    context.SessionId,
                    context.ModelMetadata,
                    "openxml",
                    null,
                    null,
                    context.EventSink,
                    context.CancellationToken);

                var openXmlResult = await openXmlProcessor.ProcessAsync(openXmlContext, ct);

                var sb = new StringBuilder();
                sb.AppendLine($"[FILE] {context.RelativePath}  ({FormatSize.Format(bytes.Length)}, {docType})");
                sb.AppendLine($"[CONVERTED] {conversion.Method} -> {conversion.ConvertedExtension}");
                sb.AppendLine();
                sb.Append(openXmlResult.Text);

                return new ContentProcessorResult(
                    sb.ToString().TrimEnd(),
                    openXmlResult.ActualModality,
                    Warning: conversion.Warning ?? openXmlResult.Warning);
            }
            catch
            {
                // Conversion succeeded but OpenXML extraction failed — fall through
            }
        }

        // Stage 2: Best-effort OLE2 binary text scan
        var oleText = TryExtractOle2Text(bytes.Span);
        if (!string.IsNullOrWhiteSpace(oleText))
        {
            var warning = "Legacy Office extraction used best-effort OLE2 binary text scanning." +
                          " Formatting, tables, slides, and embedded objects may be missing or noisy.";
            return new ContentProcessorResult(
                BuildTextResult(context.RelativePath, bytes.Length, docType, "OLE2 text scan", oleText, warning),
                OutputModality.Text,
                Warning: warning);
        }

        // Stage 3: Hex dump last resort
        return await HexDumpFallbackAsync(context, bytes, docType, null, ct);
    }

    // ============================================================
    // DocSharp conversion wrappers
    // ============================================================

    private static async ValueTask<LegacyConversionResult?> TryConvertLegacyToOpenXmlAsync(
        ReadOnlyMemory<byte> bytes,
        string ext,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            return ext switch
            {
                ".doc" => await ConvertDocToDocxAsync(bytes, ct),
                ".xls" => await ConvertXlsToXlsxAsync(bytes, ct),
                ".ppt" => await ConvertPptToPptxAsync(bytes, ct),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static async ValueTask<LegacyConversionResult?> ConvertDocToDocxAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tempInput = GetTempFilePath();
        var tempOutput = GetTempFilePath();
        try
        {
            await File.WriteAllBytesAsync(tempInput, bytes.ToArray(), ct);
            using (var reader = new DocSharp.Binary.StructuredStorage.Reader.StructuredStorageReader(tempInput))
            {
                var doc = new DocSharp.Binary.DocFileFormat.WordDocument(reader);
                using (var docx = DocSharp.Binary.OpenXmlLib.WordprocessingML.WordprocessingDocument.Create(
                    tempOutput, DocSharp.Binary.OpenXmlLib.WordprocessingDocumentType.Document))
                {
                    DocSharp.Binary.WordprocessingMLMapping.Converter.Convert(doc, docx);
                }
            }
            var converted = await File.ReadAllBytesAsync(tempOutput, ct);
            return new LegacyConversionResult(converted, ".docx", "DocSharp.Binary.Doc");
        }
        catch
        {
            return null;
        }
        finally
        {
            TryDeleteTempFile(tempInput);
            TryDeleteTempFile(tempOutput);
        }
    }

    private static async ValueTask<LegacyConversionResult?> ConvertXlsToXlsxAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tempInput = GetTempFilePath();
        var tempOutput = GetTempFilePath();
        try
        {
            await File.WriteAllBytesAsync(tempInput, bytes.ToArray(), ct);
            using (var reader = new DocSharp.Binary.StructuredStorage.Reader.StructuredStorageReader(tempInput))
            {
                var xls = new DocSharp.Binary.Spreadsheet.XlsFileFormat.XlsDocument(reader);
                using (var xlsx = DocSharp.Binary.OpenXmlLib.SpreadsheetML.SpreadsheetDocument.Create(
                    tempOutput, DocSharp.Binary.OpenXmlLib.SpreadsheetDocumentType.Workbook))
                {
                    DocSharp.Binary.SpreadsheetMLMapping.Converter.Convert(xls, xlsx);
                }
            }
            var converted = await File.ReadAllBytesAsync(tempOutput, ct);
            return new LegacyConversionResult(converted, ".xlsx", "DocSharp.Binary.Xls");
        }
        catch
        {
            return null;
        }
        finally
        {
            TryDeleteTempFile(tempInput);
            TryDeleteTempFile(tempOutput);
        }
    }

    private static async ValueTask<LegacyConversionResult?> ConvertPptToPptxAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tempInput = GetTempFilePath();
        var tempOutput = GetTempFilePath();
        try
        {
            await File.WriteAllBytesAsync(tempInput, bytes.ToArray(), ct);
            using (var reader = new DocSharp.Binary.StructuredStorage.Reader.StructuredStorageReader(tempInput))
            {
                var ppt = new DocSharp.Binary.PptFileFormat.PowerpointDocument(reader);
                using (var pptx = DocSharp.Binary.OpenXmlLib.PresentationML.PresentationDocument.Create(
                    tempOutput, DocSharp.Binary.OpenXmlLib.PresentationDocumentType.Presentation))
                {
                    DocSharp.Binary.PresentationMLMapping.Converter.Convert(ppt, pptx);
                }
            }
            var converted = await File.ReadAllBytesAsync(tempOutput, ct);
            return new LegacyConversionResult(converted, ".pptx", "DocSharp.Binary.Ppt");
        }
        catch
        {
            return null;
        }
        finally
        {
            TryDeleteTempFile(tempInput);
            TryDeleteTempFile(tempOutput);
        }
    }

    // ============================================================
    // Temp file helpers (GUID-based, never throws on collision)
    // ============================================================

    private static string GetTempFilePath()
        => Path.Combine(Path.GetTempPath(), $"omicron_legacy_{Guid.NewGuid():N}.tmp");

    private static void TryDeleteTempFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    // ============================================================
    // OLE2 text scanner (fallback)
    // ============================================================

    /// <summary>
    /// Best-effort OLE2 binary text scanning. Looks for UTF-16LE text sequences
    /// in OLE2 compound document streams. Low-fidelity — formatting, tables,
    /// and non-text content are lost.
    /// </summary>
    private static string? TryExtractOle2Text(ReadOnlySpan<byte> bytes)
    {
        try
        {
            // OLE2 signature: D0 CF 11 E0 A1 B1 1A E1
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

    // ============================================================
    // Helpers
    // ============================================================

    private static string GetLegacyDocType(string ext) => ext switch
    {
        ".doc" => "Word (legacy)",
        ".xls" => "Excel (legacy)",
        ".ppt" => "PowerPoint (legacy)",
        _ => "Legacy Office"
    };

    private static string BuildTextResult(
        string relativePath, long size, string docType,
        string method, string text, string? warning)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[FILE] {relativePath}  ({FormatSize.Format(size)}, {docType})");
        sb.AppendLine($"[METHOD] {method}");
        if (warning is not null)
            sb.AppendLine($"[WARNING] {warning}");
        sb.AppendLine();
        sb.Append(text);
        return sb.ToString().TrimEnd();
    }

    private static async ValueTask<ContentProcessorResult> HexDumpFallbackAsync(
        ContentProcessorContext context,
        ReadOnlyMemory<byte> bytes,
        string docType,
        string? extraReason,
        CancellationToken ct)
    {
        var hexDumper = new HexDumpProcessor();
        var hexContext = new ContentProcessorContext(
            context.AbsolutePath, context.RelativePath, bytes, bytes.Length,
            context.SessionId, context.ModelMetadata, "hex", null, null,
            context.EventSink, ct);

        var hexResult = await hexDumper.ProcessAsync(hexContext, ct);

        var reason = extraReason is not null ? $" ({extraReason})" : "";
        var warning = $"Legacy Office text extraction unavailable{reason}; showing hex dump.";

        return new ContentProcessorResult(
            $"[FILE] {context.RelativePath}  ({FormatSize.Format(bytes.Length)}, {docType})\n" +
            $"[WARNING] {warning}\n{hexResult.Text}",
            OutputModality.HexDump,
            Warning: warning);
    }


}
