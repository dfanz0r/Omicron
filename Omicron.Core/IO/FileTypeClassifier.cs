using System.IO.Compression;

namespace Omicron.Core.IO;

/// <summary>
/// High-level file type classification based on magic bytes and extension.
/// Classification runs before the text/binary check so that structured
/// formats like CSV, IPYNB, SVG, and EML that are technically valid UTF-8
/// get routed to their proper processors.
/// </summary>
public enum DetectedFileType
{
    /// <summary>UTF-8 or BOM-family text (source code, config, Markdown, etc.)</summary>
    Text,
    /// <summary>PNG, JPG, GIF, WebP, BMP, ICO</summary>
    Image,
    /// <summary>application/pdf</summary>
    Pdf,
    /// <summary>MP3, WAV, FLAC, OGG, AAC</summary>
    Audio,
    /// <summary>MP4, AVI, MOV, WebM</summary>
    Video,
    /// <summary>DOCX, XLSX, PPTX (ZIP container with Open XML content)</summary>
    OpenXmlDocument,
    /// <summary>.doc, .xls, .ppt (binary Office formats)</summary>
    LegacyDocument,
    /// <summary>CSV, TSV (text but parseable as structured data)</summary>
    Csv,
    /// <summary>.eml email messages</summary>
    Email,
    /// <summary>EPUB ebook</summary>
    Ebook,
    /// <summary>IPYNB Jupyter notebook</summary>
    Notebook,
    /// <summary>ZIP, TAR, 7z, GZip, BZip2</summary>
    Archive,
    /// <summary>SVG (text/xml, treated as text with size cap)</summary>
    Svg,
    /// <summary>Everything else</summary>
    UnknownBinary
}

/// <summary>
/// Classifies files by examining magic bytes (first bytes of the file)
/// and falling back to extension when magic bytes are not definitive.
/// </summary>
public static class FileTypeClassifier
{
    /// <summary>
    /// Classify a file by its path (extension) and content (full bytes).
    /// The full byte span is passed so ZIP-based formats (DOCX, EPUB) can
    /// be inspected by opening the archive in memory.
    /// </summary>
    public static DetectedFileType Classify(string path, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2)
        {
            // Too small for magic bytes — rely on extension
            var ext = GetExtension(path);
            return ClassifyByExtension(ext) ?? DetectedFileType.Text;
        }

        // ---- Magic byte detection (first bytes) ----

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return DetectedFileType.Image;

        // JPEG: FF D8 FF
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return DetectedFileType.Image;

        // GIF: 47 49 46 (GIF8)
        if (bytes.Length >= 3 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
            return DetectedFileType.Image;

        // WebP: 52 49 46 46 .... 57 45 42 50 (RIFF .... WEBP)
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return DetectedFileType.Image;

        // BMP: 42 4D
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
            return DetectedFileType.Image;

        // ICO: 00 00 01 00
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00)
            return DetectedFileType.Image;

        // PDF: 25 50 44 46 (%PDF)
        if (bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46)
            return DetectedFileType.Pdf;

        // ZIP-related (PK\x03\x04)
        if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B
            && bytes[2] == 0x03 && bytes[3] == 0x04)
        {
            return ClassifyZip(bytes);
        }

        // GZip: 1F 8B
        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            return DetectedFileType.Archive;

        // BZip2: 42 5A
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x5A)
            return DetectedFileType.Archive;

        // 7z: 37 7A BC AF 27 1C
        if (bytes.Length >= 6
            && bytes[0] == 0x37 && bytes[1] == 0x7A && bytes[2] == 0xBC
            && bytes[3] == 0xAF && bytes[4] == 0x27 && bytes[5] == 0x1C)
            return DetectedFileType.Archive;

        // TAR (old-style): first 100 bytes have file name, then various fields
        // No single reliable magic byte — rely on extension.
        // But check for ustar at offset 257
        if (bytes.Length >= 262
            && bytes[257] == 0x75 && bytes[258] == 0x73 && bytes[259] == 0x74 && bytes[260] == 0x61 && bytes[261] == 0x72)
            return DetectedFileType.Archive;

        // MP4 / MOV / 3GP: ftyp box (00 00 00 XX 66 74 79 70)
        if (bytes.Length >= 8
            && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
        {
            // ftyp present — check for common brands
            var ext = GetExtension(path);
            if (ext is ".mp4" or ".mov" or ".avi" or ".webm" or ".mkv")
                return DetectedFileType.Video;
            // Could also be audio (M4A), but default to video
            return DetectedFileType.Video;
        }

        // AVI: 52 49 46 46 .... 41 56 49 20 (RIFF .... AVI )
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x41 && bytes[9] == 0x56 && bytes[10] == 0x49 && bytes[11] == 0x20)
            return DetectedFileType.Video;

        // RIFF audio (WAV): 52 49 46 46 .... 57 41 56 45 (RIFF .... WAVE)
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x41 && bytes[10] == 0x56 && bytes[11] == 0x45)
            return DetectedFileType.Audio;

        // OGG: 4F 67 67 53 (OggS)
        if (bytes.Length >= 4 && bytes[0] == 0x4F && bytes[1] == 0x67 && bytes[2] == 0x67 && bytes[3] == 0x53)
            return DetectedFileType.Audio;

        // FLAC: 66 4C 61 43 (fLaC)
        if (bytes.Length >= 4 && bytes[0] == 0x66 && bytes[1] == 0x4C && bytes[2] == 0x61 && bytes[3] == 0x43)
            return DetectedFileType.Audio;

        // ---- Extension fallback for formats without reliable magic bytes ----
        var extension = GetExtension(path);
        var extResult = ClassifyByExtension(extension);
        if (extResult.HasValue)
            return extResult.Value;

        // ---- SVG check: XML with root <svg> ----
        if (LooksLikeSvg(bytes))
            return DetectedFileType.Svg;

        // ---- EML check: starts with a common email header pattern ----
        if (extension is ".eml" or ".msg" || LooksLikeEmail(bytes))
            return DetectedFileType.Email;

        // ---- Fallback: TextEncodingDetector will be called after classification ----
        return DetectedFileType.Text;
    }

    /// <summary>
    /// Get the lowercase extension including dot, or empty string.
    /// </summary>
    private static string GetExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return ext?.ToLowerInvariant() ?? "";
    }

    /// <summary>
    /// Classify by extension alone when magic bytes are unavailable
    /// or ambiguous. Returns null if extension is not definitive.
    /// </summary>
    private static DetectedFileType? ClassifyByExtension(string ext)
    {
        return ext switch
        {
            // Images
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp"
                or ".bmp" or ".ico" or ".tiff" or ".tif" => DetectedFileType.Image,

            // Audio
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".aac"
                or ".m4a" or ".wma" or ".opus" => DetectedFileType.Audio,

            // Video
            ".mp4" or ".avi" or ".mov" or ".webm" or ".mkv"
                or ".wmv" or ".flv" => DetectedFileType.Video,

            // Office Open XML
            ".docx" or ".xlsx" or ".pptx" => DetectedFileType.OpenXmlDocument,

            // Legacy Office
            ".doc" or ".xls" or ".ppt" => DetectedFileType.LegacyDocument,

            // CSV / TSV
            ".csv" or ".tsv" => DetectedFileType.Csv,

            // Email
            ".eml" or ".msg" => DetectedFileType.Email,

            // Ebook
            ".epub" => DetectedFileType.Ebook,

            // Notebook
            ".ipynb" => DetectedFileType.Notebook,

            // SVG
            ".svg" => DetectedFileType.Svg,

            // Archive (magic bytes preferred, but extension as fallback)
            ".zip" or ".tar" or ".gz" or ".tgz" or ".bz2"
                or ".xz" or ".7z" or ".rar" => DetectedFileType.Archive,

            // PDF (magic bytes preferred)
            ".pdf" => DetectedFileType.Pdf,

            _ => null
        };
    }

    /// <summary>
    /// Classify a ZIP container by inspecting its contents for
    /// signature entries that identify subtypes.
    /// </summary>
    private static DetectedFileType ClassifyZip(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes.ToArray());
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

            bool hasContentTypesXml = false;
            bool hasMetaInfContainerXml = false;

            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');

                if (name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
                    hasContentTypesXml = true;

                if (name.Equals("META-INF/container.xml", StringComparison.OrdinalIgnoreCase))
                    hasMetaInfContainerXml = true;

                // Short-circuit: if we found both or the deciding one, stop early
                if (hasContentTypesXml)
                    break;
            }

            if (hasContentTypesXml)
                return DetectedFileType.OpenXmlDocument;

            if (hasMetaInfContainerXml)
                return DetectedFileType.Ebook;

            return DetectedFileType.Archive;
        }
        catch
        {
            // Not a valid ZIP or can't inspect — treat as generic archive
            return DetectedFileType.Archive;
        }
    }

    /// <summary>
    /// Quick check if the content looks like an SVG (XML with &lt;svg root).
    /// </summary>
    private static bool LooksLikeSvg(ReadOnlySpan<byte> bytes)
    {
        // Check first 4096 bytes for "<svg" or "<!DOCTYPE svg" or "<svg:svg"
        int len = Math.Min(bytes.Length, 4096);
        // Look for "<svg" case-insensitively
        for (int i = 0; i < len - 4; i++)
        {
            if ((bytes[i] == '<' || bytes[i] == '<') // <
                && (bytes[i + 1] == 's' || bytes[i + 1] == 'S')
                && (bytes[i + 2] == 'v' || bytes[i + 2] == 'V')
                && (bytes[i + 3] == 'g' || bytes[i + 3] == 'G'))
            {
                // Check that it's an XML tag (preceded by < and followed by >, space, or /)
                if (i + 4 >= len) break;
                var next = (char)bytes[i + 4];
                if (next is '>' or ' ' or '/' or ':')
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Quick check if the content looks like an email (starts with common headers).
    /// </summary>
    private static bool LooksLikeEmail(ReadOnlySpan<byte> bytes)
    {
        // Check first 4096 bytes for common email header patterns at the start of lines
        int len = Math.Min(bytes.Length, 4096);
        bool atLineStart = true;
        for (int i = 0; i < len - 4; i++)
        {
            var c = bytes[i];
            if (c == (byte)'\n')
            {
                atLineStart = true;
                continue;
            }
            if (c == (byte)'\r') continue;

            if (atLineStart && i + 4 < len)
            {
                // Match bytes directly without string allocation
                if ((bytes[i] == 'F' && bytes[i + 1] == 'r' && bytes[i + 2] == 'o' && bytes[i + 3] == 'm' && bytes[i + 4] == ':') ||
                    (bytes[i] == 'D' && bytes[i + 1] == 'a' && bytes[i + 2] == 't' && bytes[i + 3] == 'e' && bytes[i + 4] == ':') ||
                    (bytes[i] == 'T' && bytes[i + 1] == 'o' && bytes[i + 2] == ':') ||
                    (bytes[i] == 'S' && bytes[i + 1] == 'u' && bytes[i + 2] == 'b' && bytes[i + 3] == 'j' && bytes[i + 4] == 'e') ||
                    (bytes[i] == 'M' && bytes[i + 1] == 'I' && bytes[i + 2] == 'M' && bytes[i + 3] == 'E' && bytes[i + 4] == '-') ||
                    (bytes[i] == 'M' && bytes[i + 1] == 'e' && bytes[i + 2] == 's' && bytes[i + 3] == 's'))
                    return true;
            }
            atLineStart = false;
        }
        return false;
    }
}
