using Omicron.Core.Content;

namespace Omicron.Core.IO;

/// <summary>
///     Produces raw base64-encoded file content with MIME type header.
///     Used only when <c>format: "base64"</c> is specified — never resolved
///     from the registry by file type.
///     No line wrapping on the base64 payload.
/// </summary>
public sealed class Base64Processor
{
    /// <summary>
    ///     Process a file as raw base64. Determines MIME type from
    ///     magic bytes + extension.
    /// </summary>
    public ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        string mimeType = GuessMimeType(context.RelativePath, context.Bytes.Span);
        string b64 = Convert.ToBase64String(context.Bytes.Span);

        string text =
            $"[FILE] {context.RelativePath}  ({mimeType}, {FormatSize.Format(context.Bytes.Length)})\nData: {b64}";

        // Determine appropriate modality based on MIME type
        OutputModality modality = mimeType switch
        {
            string m when m.StartsWith("image/") => OutputModality.ImageBase64,
            string m when m == "application/pdf" => OutputModality.PdfBase64,
            string m when m.StartsWith("audio/") => OutputModality.AudioBase64,
            string m when m.StartsWith("video/") => OutputModality.VideoBase64,
            _ => OutputModality.Metadata
        };

        return ValueTask.FromResult(new ContentProcessorResult(text, modality, MimeType: mimeType));
    }

    private static string GuessMimeType(string path, ReadOnlySpan<byte> header)
    {
        string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";

        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".ico" => "image/x-icon",
            ".tiff" or ".tif" => "image/tiff",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/opus",
            ".aac" => "audio/aac",
            ".m4a" => "audio/mp4",
            ".mp4" => "video/mp4",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html",
            ".txt" or ".md" => "text/plain",
            ".csv" => "text/csv",
            _ => GuessByMagic(header) ?? "application/octet-stream"
        };
    }

    private static string? GuessByMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4 && header[0] == 0x25 && header[1] == 0x50)
        {
            return "application/pdf";
        }

        if (header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50)
        {
            return "image/png";
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8)
        {
            return "image/jpeg";
        }

        if (header.Length >= 3 && header[0] == 0x47 && header[1] == 0x49)
        {
            return "image/gif";
        }

        if (header.Length >= 4 && header[0] == 0x52 && header[1] == 0x49)
        {
            return GuessRiffType(header);
        }

        if (header.Length >= 4 && header[0] == 0x50 && header[1] == 0x4B)
        {
            return "application/zip";
        }

        return null;
    }

    private static string GuessRiffType(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 12)
        {
            if (header[8] == 0x57 && header[9] == 0x45)
            {
                return "image/webp"; // WEBP
            }

            if (header[8] == 0x57 && header[9] == 0x41)
            {
                return "audio/wav"; // WAVE
            }

            if (header[8] == 0x41 && header[9] == 0x56)
            {
                return "video/x-msvideo"; // AVI
            }
        }

        return "application/octet-stream";
    }
}
