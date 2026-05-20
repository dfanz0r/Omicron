using DocSharp.Binary.DocFileFormat;
using DocSharp.Binary.OpenXmlLib;
using DocSharp.Binary.OpenXmlLib.PresentationML;
using DocSharp.Binary.OpenXmlLib.SpreadsheetML;
using DocSharp.Binary.OpenXmlLib.WordprocessingML;
using DocSharp.Binary.PptFileFormat;
using DocSharp.Binary.Spreadsheet.XlsFileFormat;
using DocSharp.Binary.StructuredStorage.Reader;
using DocSharp.Binary.WordprocessingMLMapping;
using Omicron.Core.Content;
using Omicron.Core.Text;

namespace Omicron.Core.IO;

/// <summary>
///     Processes legacy Office documents (.doc, .xls, .ppt) by extracting plain text.
///     Converts Office 97-2003 binary files to OpenXML via DocSharp, then delegates
///     text extraction to <see cref="OpenXmlProcessor" />. Falls back to best-effort
///     OLE2 binary text scanning and finally hex dump when conversion/extraction is unavailable.
/// </summary>
public sealed class LegacyOfficeProcessor : IContentProcessor
{
    private const long MaxLegacyOfficeBytes = 100 * 1024 * 1024; // 100 MB
    public string Id => "legacy_office";

    public IReadOnlySet<DetectedFileType> SupportedTypes { get; } =
        new HashSet<DetectedFileType>
        {
            DetectedFileType.LegacyDocument
        };

    public OutputModality OutputModality => OutputModality.Text;

    public async ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        ReadOnlyMemory<byte> bytes = context.Bytes;
        string ext = Path.GetExtension(context.RelativePath)?.ToLowerInvariant() ?? "";
        string docType = GetLegacyDocType(ext);

        if (bytes.Length > MaxLegacyOfficeBytes)
        {
            return await HexDumpFallbackAsync(context, bytes, docType, "file too large", ct);
        }

        // Stage 1: Try DocSharp conversion to OpenXML
        LegacyConversionResult? conversion = await TryConvertLegacyToOpenXmlAsync(bytes, ext, ct);

        if (conversion is not null)
        {
            try
            {
                var openXmlProcessor = new OpenXmlProcessor();
                string convertedRelativePath = Path.ChangeExtension(context.RelativePath,
                    conversion.ConvertedExtension);
                var openXmlContext = new ContentProcessorContext(context.AbsolutePath,
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

                ContentProcessorResult openXmlResult = await openXmlProcessor.ProcessAsync(openXmlContext, ct);

                Utf8Builder output = Utf8Text.CreateBuilder();
                try
                {
                    Utf8CompositeFormat.AppendFormatUtf8Slow(ref output,
                        "[FILE] {0}  ({1}, {2})"u8,
                        context.RelativePath,
                        FormatSize.Format(bytes.Length),
                        docType);
                    output.AppendLine();
                    Utf8CompositeFormat.AppendFormatUtf8Slow(ref output,
                        "[CONVERTED] {0} -> {1}"u8,
                        conversion.Method,
                        conversion.ConvertedExtension);
                    output.AppendLine();
                    output.AppendLine();
                    output.Append(openXmlResult.Text);

                    return new ContentProcessorResult(output.ToString().TrimEnd(),
                        openXmlResult.ActualModality,
                        output.AsSpan().ToArray(),
                        Warning: conversion.Warning ?? openXmlResult.Warning);
                }
                finally
                {
                    output.Dispose();
                }
            }
            catch
            {
                // Conversion succeeded but OpenXML extraction failed — fall through
            }
        }

        // Stage 2: Best-effort OLE2 binary text scan
        string? oleText = TryExtractOle2Text(bytes.Span);
        if (!string.IsNullOrWhiteSpace(oleText))
        {
            string warning =
                "Legacy Office extraction used best-effort OLE2 binary text scanning."
                + " Formatting, tables, slides, and embedded objects may be missing or noisy.";
            return BuildUtf8Result(context.RelativePath,
                bytes.Length,
                docType,
                "OLE2 text scan",
                oleText,
                warning,
                OutputModality.Text);
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
        string tempInput = GetTempFilePath();
        string tempOutput = GetTempFilePath();
        try
        {
            await File.WriteAllBytesAsync(tempInput, bytes.ToArray(), ct);
            using (
                var reader = new StructuredStorageReader(tempInput)
            )
            {
                var doc = new WordDocument(reader);
                using (
                    var docx =
                    WordprocessingDocument.Create(tempOutput,
                        WordprocessingDocumentType.Document)
                )
                {
                    Converter.Convert(doc, docx);
                }
            }

            byte[] converted = await File.ReadAllBytesAsync(tempOutput, ct);
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
        string tempInput = GetTempFilePath();
        string tempOutput = GetTempFilePath();
        try
        {
            await File.WriteAllBytesAsync(tempInput, bytes.ToArray(), ct);
            using (
                var reader = new StructuredStorageReader(tempInput)
            )
            {
                var xls = new XlsDocument(reader);
                using (
                    var xlsx = SpreadsheetDocument.Create(tempOutput,
                        SpreadsheetDocumentType.Workbook)
                )
                {
                    DocSharp.Binary.SpreadsheetMLMapping.Converter.Convert(xls, xlsx);
                }
            }

            byte[] converted = await File.ReadAllBytesAsync(tempOutput, ct);
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
        string tempInput = GetTempFilePath();
        string tempOutput = GetTempFilePath();
        try
        {
            await File.WriteAllBytesAsync(tempInput, bytes.ToArray(), ct);
            using (
                var reader = new StructuredStorageReader(tempInput)
            )
            {
                var ppt = new PowerpointDocument(reader);
                using (
                    var pptx =
                    PresentationDocument.Create(tempOutput,
                        PresentationDocumentType.Presentation)
                )
                {
                    DocSharp.Binary.PresentationMLMapping.Converter.Convert(ppt, pptx);
                }
            }

            byte[] converted = await File.ReadAllBytesAsync(tempOutput, ct);
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
    {
        return Path.Combine(Path.GetTempPath(), $"omicron_legacy_{Guid.NewGuid():N}.tmp");
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch { }
    }

    // ============================================================
    // OLE2 text scanner (fallback)
    // ============================================================

    /// <summary>
    ///     Best-effort OLE2 binary text scanning. Looks for UTF-16LE text sequences
    ///     in OLE2 compound document streams. Low-fidelity — formatting, tables,
    ///     and non-text content are lost.
    /// </summary>
    private static string? TryExtractOle2Text(ReadOnlySpan<byte> bytes)
    {
        try
        {
            // OLE2 signature: D0 CF 11 E0 A1 B1 1A E1
            if (bytes.Length < 8)
            {
                return null;
            }

            if (bytes[0] != 0xD0 || bytes[1] != 0xCF)
            {
                return null;
            }

            using Utf8Builder output = Utf8Text.CreateBuilder();
            bool inText = false;

            for (int i = 0; i < bytes.Length - 2; i += 2)
            {
                char low = (char)bytes[i];
                char high = (char)bytes[i + 1];

                if (high == 0 && low >= 0x20 && low <= 0x7E)
                {
                    output.Append(low);
                    inText = true;
                }
                else if (high == 0 && (low == '\r' || low == '\n'))
                {
                    if (inText)
                    {
                        output.Append(low);
                    }
                }
                else
                {
                    if (inText && output.Length > 20)
                    {
                        byte lastChar = output.AsSpan()[^1];
                        if (lastChar != (byte)'\n')
                        {
                            output.Append('\n');
                        }
                    }

                    inText = false;
                }
            }

            string result = output.ToString().Trim();
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

    private static string GetLegacyDocType(string ext)
    {
        return ext switch
        {
            ".doc" => "Word (legacy)",
            ".xls" => "Excel (legacy)",
            ".ppt" => "PowerPoint (legacy)",
            _ => "Legacy Office"
        };
    }

    private static ContentProcessorResult BuildUtf8Result(
        string relativePath,
        long size,
        string docType,
        string method,
        string text,
        string? warning,
        OutputModality modality)
    {
        Utf8Builder output = Utf8Text.CreateBuilder();
        try
        {
            Utf8CompositeFormat.AppendFormatUtf8Slow(ref output,
                "[FILE] {0}  ({1}, {2})"u8,
                relativePath,
                FormatSize.Format(size),
                docType);
            output.AppendLine();
            Utf8CompositeFormat.AppendFormatUtf8Slow(ref output, "[METHOD] {0}"u8, method);
            output.AppendLine();
            if (warning is not null)
            {
                Utf8CompositeFormat.AppendFormatUtf8Slow(ref output, "[WARNING] {0}"u8, warning);
                output.AppendLine();
            }

            output.AppendLine();
            output.Append(text);
            return new ContentProcessorResult(output.ToString().TrimEnd(),
                modality,
                output.AsSpan().ToArray(),
                Warning: warning);
        }
        finally
        {
            output.Dispose();
        }
    }

    private static async ValueTask<ContentProcessorResult> HexDumpFallbackAsync(
        ContentProcessorContext context,
        ReadOnlyMemory<byte> bytes,
        string docType,
        string? extraReason,
        CancellationToken ct)
    {
        var hexDumper = new HexDumpProcessor();
        var hexContext = new ContentProcessorContext(context.AbsolutePath,
            context.RelativePath,
            bytes,
            bytes.Length,
            context.SessionId,
            context.ModelMetadata,
            "hex",
            null,
            null,
            context.EventSink,
            ct);

        ContentProcessorResult hexResult = await hexDumper.ProcessAsync(hexContext, ct);

        string reason = extraReason is not null ? $" ({extraReason})" : "";
        string warning = $"Legacy Office text extraction unavailable{reason}; showing hex dump.";

        Utf8Builder output2 = Utf8Text.CreateBuilder();
        try
        {
            Utf8CompositeFormat.AppendFormatUtf8Slow(ref output2,
                "[FILE] {0}  ({1}, {2})"u8,
                context.RelativePath,
                FormatSize.Format(bytes.Length),
                docType);
            output2.AppendLine();
            Utf8CompositeFormat.AppendFormatUtf8Slow(ref output2, "[WARNING] {0}"u8, warning);
            output2.AppendLine();
            output2.Append(hexResult.Text);
            return new ContentProcessorResult(output2.ToString().TrimEnd(),
                OutputModality.HexDump,
                output2.AsSpan().ToArray(),
                Warning: warning);
        }
        finally
        {
            output2.Dispose();
        }
    }

    /// <summary>
    ///     Result of a DocSharp conversion attempt.
    /// </summary>
    private sealed record LegacyConversionResult(
        byte[] ConvertedBytes,
        string ConvertedExtension,
        string Method,
        string? Warning = null);
}
