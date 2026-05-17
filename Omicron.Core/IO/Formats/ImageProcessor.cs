using Omicron.Core.Content;
using Omicron.Core.Models;

namespace Omicron.Core.IO;

/// <summary>
/// Processes image files. Returns base64 for vision-capable models,
/// hex dump for text-only models. Uses ImageSharp when available,
/// falls back to hex dump gracefully.
/// </summary>
public sealed class ImageProcessor : IContentProcessor
{
    public string Id => "image";
    public IReadOnlySet<DetectedFileType> SupportedTypes { get; }
        = new HashSet<DetectedFileType> { DetectedFileType.Image };
    public OutputModality OutputModality => OutputModality.ImageBase64 | OutputModality.HexDump | OutputModality.Metadata;

    public async ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var bytes = context.Bytes;
        var mimeType = GuessMimeType(context.RelativePath, bytes.Span);
        var dim = TryGetDimensions(bytes.Span);

        // Determine if the model can accept images
        bool visionCapable = context.ModelMetadata?.SupportsImages() == true;

        if (visionCapable)
        {
            // Return base64 inline
            var b64 = Convert.ToBase64String(bytes.Span);

            var text = $"[FILE] {context.RelativePath}  ({FormatSize.Format(bytes.Length)}, {mimeType}";
            if (dim.HasValue)
                text += $", {dim.Value.Width}×{dim.Value.Height}px";
            text += ")\n";
            text += $"Data: {b64}";

            return new ContentProcessorResult(
                text,
                OutputModality.ImageBase64,
                MimeType: mimeType);
        }

        // Text-only model: fall back to hex dump with metadata header
        var imageInfo = $"[FILE] {context.RelativePath}  ({FormatSize.Format(bytes.Length)}, {mimeType}";
        if (dim.HasValue)
            imageInfo += $", {dim.Value.Width}×{dim.Value.Height}px";
        imageInfo += ")\n";

        var hexDumper = new HexDumpProcessor();
        var hexContext = new ContentProcessorContext(
            context.AbsolutePath, context.RelativePath, context.Bytes, context.FileSize,
            context.SessionId, context.ModelMetadata, "hex", null, null,
            context.EventSink, context.CancellationToken);

        var hexResult = await hexDumper.ProcessAsync(hexContext, ct);

        return new ContentProcessorResult(
            imageInfo + hexResult.Text,
            OutputModality.HexDump,
            MimeType: mimeType,
            Warning: "Image displayed as hex dump; model does not support vision.");
    }

    /// <summary>
    /// Try to extract image dimensions. Prefers ImageSharp when available,
    /// falls back to manual magic-bytes parsing (PNG, JPEG, GIF, BMP, WebP).
    /// </summary>
    internal static (int Width, int Height)? TryGetDimensions(ReadOnlySpan<byte> bytes)
    {
        // Try ImageSharp first
        try
        {
            using var ms = new MemoryStream(bytes.ToArray());
            using var image = SixLabors.ImageSharp.Image.Load(ms);
            return (image.Width, image.Height);
        }
        catch
        {
            // ImageSharp failed — fall back to manual parsing
        }

        // Fallback: manual magic-bytes parsing
        try
        {
            // PNG: IHDR chunk at offset 16
            if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 0x50)
            {
                int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                int h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                if (w > 0 && h > 0) return (w, h);
            }

            // JPEG: SOF0 marker (0xFF 0xC0) or SOF2 (0xFF 0xC2)
            for (int i = 0; i < bytes.Length - 8; i++)
            {
                if (bytes[i] == 0xFF && (bytes[i + 1] == 0xC0 || bytes[i + 1] == 0xC2))
                {
                    int h = (bytes[i + 5] << 8) | bytes[i + 6];
                    int w = (bytes[i + 7] << 8) | bytes[i + 8];
                    if (w > 0 && h > 0) return (w, h);
                }
            }

            // GIF: width/height at offset 6 (little-endian)
            if (bytes.Length >= 10 && bytes[0] == 0x47 && bytes[1] == 0x49)
            {
                int w = bytes[6] | (bytes[7] << 8);
                int h = bytes[8] | (bytes[9] << 8);
                if (w > 0 && h > 0) return (w, h);
            }

            // BMP: width/height at offset 18 (little-endian 32-bit)
            if (bytes.Length >= 26 && bytes[0] == 0x42 && bytes[1] == 0x4D)
            {
                int w = bytes[18] | (bytes[19] << 8) | (bytes[20] << 16) | (bytes[21] << 24);
                int h = bytes[22] | (bytes[23] << 8) | (bytes[24] << 16) | (bytes[25] << 24);
                if (w > 0) return (Math.Abs(w), Math.Abs(h));
            }

            // TIFF: MM (big-endian) or II (little-endian) at offset 0, then 0x002A at offset 2
            if (bytes.Length >= 8 && ((bytes[0] == 0x4D && bytes[1] == 0x4D) || (bytes[0] == 0x49 && bytes[1] == 0x49)))
            {
                bool bigEndian = bytes[0] == 0x4D;
                if ((bigEndian && bytes[2] == 0x00 && bytes[3] == 0x2A) ||
                    (!bigEndian && bytes[2] == 0x2A && bytes[3] == 0x00))
                {
                    // Read first IFD entry for ImageWidth (0x0100) and ImageLength (0x0101)
                    int ifdOffset = bigEndian
                        ? (bytes[4] << 24) | (bytes[5] << 16) | (bytes[6] << 8) | bytes[7]
                        : bytes[4] | (bytes[5] << 8) | (bytes[6] << 16) | (bytes[7] << 24);
                    if (ifdOffset > 0 && ifdOffset + 12 < bytes.Length)
                    {
                        int? width = null, height = null;
                        int entryCount = bigEndian
                            ? (bytes[ifdOffset] << 8) | bytes[ifdOffset + 1]
                            : bytes[ifdOffset] | (bytes[ifdOffset + 1] << 8);
                        for (int e = 0; e < entryCount && e < 20; e++)
                        {
                            int tagOffset = ifdOffset + 2 + e * 12;
                            if (tagOffset + 12 > bytes.Length) break;
                            int tag = bigEndian
                                ? (bytes[tagOffset] << 8) | bytes[tagOffset + 1]
                                : bytes[tagOffset] | (bytes[tagOffset + 1] << 8);
                            if (tag == 0x0100) // ImageWidth
                            {
                                width = bigEndian
                                    ? (bytes[tagOffset + 8] << 24) | (bytes[tagOffset + 9] << 16) | (bytes[tagOffset + 10] << 8) | bytes[tagOffset + 11]
                                    : bytes[tagOffset + 8] | (bytes[tagOffset + 9] << 8) | (bytes[tagOffset + 10] << 16) | (bytes[tagOffset + 11] << 24);
                            }
                            else if (tag == 0x0101) // ImageLength
                            {
                                height = bigEndian
                                    ? (bytes[tagOffset + 8] << 24) | (bytes[tagOffset + 9] << 16) | (bytes[tagOffset + 10] << 8) | bytes[tagOffset + 11]
                                    : bytes[tagOffset + 8] | (bytes[tagOffset + 9] << 8) | (bytes[tagOffset + 10] << 16) | (bytes[tagOffset + 11] << 24);
                            }
                        }
                        if (width.HasValue && height.HasValue && width > 0 && height > 0)
                            return (width.Value, height.Value);
                    }
                }
            }

            // WebP: VP8X or VP8 frame
            if (bytes.Length >= 30 && bytes[0] == 0x52 && bytes[1] == 0x49) // RIFF
            {
                if (bytes[12] == 0x56 && bytes[13] == 0x50 && bytes[14] == 0x38 && bytes[15] == 0x58)
                {
                    int w = ((bytes[24] | ((bytes[25] & 0x0F) << 8)) + 1) << 2;
                    int h = ((((bytes[25] >> 4) & 0x0F) | (bytes[26] << 4)) + 1) << 2;
                    if (w > 0 && h > 0) return (w, h);
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return null;
    }

    private static string GuessMimeType(string path, ReadOnlySpan<byte> header)
    {
        var ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
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
            _ => MimeFromMagicBytes(header) ?? "application/octet-stream"
        };
    }

    private static string? MimeFromMagicBytes(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8
            && header[0] == 0x89 && header[1] == 0x50) return "image/png";
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8) return "image/jpeg";
        if (header.Length >= 3 && header[0] == 0x47 && header[1] == 0x49) return "image/gif";
        if (header.Length >= 12
            && header[0] == 0x52 && header[1] == 0x49
            && header[8] == 0x57 && header[9] == 0x45) return "image/webp";
        if (header.Length >= 2 && header[0] == 0x42 && header[1] == 0x4D) return "image/bmp";
        return null;
    }


}
