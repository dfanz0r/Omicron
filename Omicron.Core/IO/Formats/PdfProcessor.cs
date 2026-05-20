using System.Text;
using Omicron.Core.Content;
using Omicron.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Omicron.Core.IO;

/// <summary>
///     Processes PDF files. Returns base64 for PDF-capable models,
///     extracted text for text-only models (via PdfPig when available).
///     Falls back to hex dump if PdfPig is not available.
/// </summary>
public sealed class PdfProcessor : IContentProcessor
{
    public string Id => "pdf";

    public IReadOnlySet<DetectedFileType> SupportedTypes { get; } =
        new HashSet<DetectedFileType>
        {
            DetectedFileType.Pdf
        };

    public OutputModality OutputModality =>
        OutputModality.PdfBase64 | OutputModality.Text | OutputModality.HexDump;

    public async ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        ReadOnlyMemory<byte> bytes = context.Bytes;
        bool pdfCapable = context.ModelMetadata?.SupportsModality("pdf") == true;

        if (pdfCapable)
        {
            // Return base64 for models that natively handle PDF
            string b64 = Convert.ToBase64String(bytes.Span);
            string text =
                $"[FILE] {context.RelativePath}  ({FormatSize.Format(bytes.Length)}, application/pdf)\n";
            text += $"Data: {b64}";

            return new ContentProcessorResult(text,
                OutputModality.PdfBase64,
                MimeType: "application/pdf");
        }

        // Text-only model: try local text extraction, fall back to hex dump
        string? extracted = TryExtractText(bytes.Span);
        if (extracted is not null)
        {
            return new ContentProcessorResult(
                $"[FILE] {context.RelativePath}  ({FormatSize.Format(bytes.Length)}, PDF text extracted locally)\n\n{extracted}",
                OutputModality.Text,
                Warning: "PDF text extracted locally.");
        }

        // Fall back to hex dump
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

        return new ContentProcessorResult(
            $"[FILE] {context.RelativePath}  ({FormatSize.Format(bytes.Length)}, PDF — text extraction unavailable, showing hex dump)\n{hexResult.Text}",
            OutputModality.HexDump,
            Warning: "PDF text extraction unavailable.");
    }

    /// <summary>
    ///     Attempt to extract text from PDF using PdfPig, with fallback
    ///     to simple BT/ET text operator scanning.
    /// </summary>
    private static string? TryExtractText(ReadOnlySpan<byte> bytes)
    {
        // Try PdfPig first
        try
        {
            using var ms = new MemoryStream(bytes.ToArray());
            using var doc = PdfDocument.Open(ms);
            var sb = new StringBuilder();
            foreach (Page page in doc.GetPages())
            {
                foreach (Word word in page.GetWords())
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(' ');
                    }

                    sb.Append(word.Text);
                }

                sb.AppendLine();
            }

            string result = sb.ToString().Trim();
            return result.Length > 0 ? result : null;
        }
        catch
        {
            // PdfPig failed — fall back to heuristic
        }

        // Fallback: scan raw bytes for BT/ET markers without UTF-8 decode
        try
        {
            var sb = new StringBuilder();
            bool inText = false;
            bool inParen = false;
            int parenDepth = 0;

            for (int i = 0; i < bytes.Length - 4; i++)
            {
                // Detect BT (Begin Text) marker
                if (
                    bytes[i] == (byte)'\n'
                    && bytes[i + 1] == (byte)'B'
                    && bytes[i + 2] == (byte)'T'
                    && (bytes[i + 3] == (byte)'\n' || bytes[i + 3] == (byte)' ')
                )
                {
                    inText = true;
                    i += 3;
                    continue;
                }

                // Detect ET (End Text) marker
                if (
                    bytes[i] == (byte)'\n'
                    && bytes[i + 1] == (byte)'E'
                    && bytes[i + 2] == (byte)'T'
                    && (bytes[i + 3] == (byte)'\n' || bytes[i + 3] == (byte)' ')
                )
                {
                    inText = false;
                    if (sb.Length > 0 && sb[^1] != '\n')
                    {
                        sb.AppendLine();
                    }

                    i += 3;
                    continue;
                }

                if (inText)
                {
                    if (bytes[i] == (byte)'(' && !inParen)
                    {
                        inParen = true;
                        parenDepth = 1;
                        continue;
                    }

                    if (inParen)
                    {
                        if (bytes[i] == (byte)'(' && (i == 0 || bytes[i - 1] != (byte)'\\'))
                        {
                            parenDepth++;
                        }
                        else if (bytes[i] == (byte)')' && (i == 0 || bytes[i - 1] != (byte)'\\'))
                        {
                            parenDepth--;
                            if (parenDepth == 0)
                            {
                                inParen = false;
                                continue;
                            }
                        }

                        if (parenDepth > 0)
                        {
                            sb.Append((char)bytes[i]);
                        }
                    }
                }
            }

            string result = sb.ToString().Trim();
            return result.Length > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }
}
