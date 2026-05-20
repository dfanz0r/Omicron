using System.Text;

namespace Omicron.Core.IO;

/// <summary>
///     Content-based text/binary detection for workspace files.
///     Uses null-byte scan, BOM detection, and UTF-8 decode fallback
///     to decide whether a byte span represents human-readable text.
///     This is more reliable than extension-based detection because
///     extensionless files and misnamed files are common.
/// </summary>
public static class TextEncodingDetector
{
    /// <summary>
    ///     Maximum bytes examined for text detection. Files larger than this
    ///     are checked only on the prefix; binary content late in a large file
    ///     is rare and not worth the I/O cost.
    /// </summary>
    public const int MaxDetectBytes = 8192;

    // Encoding instances cached to avoid repeated constructor calls.
    // throwOnInvalidBytes = true so that GetString throws on bad input.
    private static readonly UTF8Encoding Utf8Strict = new(false, true);
    private static readonly UTF8Encoding Utf8Lenient = new(false, false);
    private static readonly UnicodeEncoding Utf16LE = new(false, false, true);
    private static readonly UnicodeEncoding Utf16BE = new(true, false, true);
    private static readonly UTF32Encoding Utf32LE = new(false, false, true);
    private static readonly UTF32Encoding Utf32BE = new(true, false, true);

    /// <summary>
    ///     Determines whether a byte span likely represents text content
    ///     (as opposed to binary data such as images, compiled code, archives).
    /// </summary>
    public static bool IsText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return true; // empty files are text
        }

        ReadOnlySpan<byte> sample = bytes.Length <= MaxDetectBytes ? bytes : bytes[..MaxDetectBytes];

        // Null bytes anywhere in the sample → binary.
        if (sample.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        // BOM present → text (we know the encoding).
        if (HasBom(sample))
        {
            return true;
        }

        // Try strict UTF-8 decode.
        try
        {
            Utf8Strict.GetCharCount(sample);
            return true;
        }
        catch (DecoderFallbackException)
        {
            // Not valid UTF-8 — check printable ratio.
        }

        // Try lenient UTF-8 (replaces invalid sequences with U+FFFD).
        string decoded = Utf8Lenient.GetString(sample);

        // If lenient decode produced no replacement characters, it's valid UTF-8.
        if (!decoded.Contains('\uFFFD'))
        {
            return true;
        }

        // Fallback: count printable vs non-printable characters.
        return IsHighPrintableRatio(decoded);
    }

    /// <summary>
    ///     Detect the encoding of a byte span by BOM inspection.
    ///     Returns the detected encoding and the number of BOM prefix bytes to skip.
    ///     Falls back to UTF-8 when no BOM is present.
    ///     Only supports UTF-8, UTF-16, and UTF-32.
    /// </summary>
    public static (Encoding Encoding, int bomLength) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        // UTF-32 BE: 00 00 FE FF
        if (
            bytes.Length >= 4
            && bytes[0] == 0x00
            && bytes[1] == 0x00
            && bytes[2] == 0xFE
            && bytes[3] == 0xFF
        )
        {
            return (Utf32BE, 4);
        }

        // UTF-32 LE: FF FE 00 00
        if (
            bytes.Length >= 4
            && bytes[0] == 0xFF
            && bytes[1] == 0xFE
            && bytes[2] == 0x00
            && bytes[3] == 0x00
        )
        {
            return (Utf32LE, 4);
        }

        // UTF-16 BE: FE FF
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Utf16BE, 2);
        }

        // UTF-16 LE: FF FE
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Utf16LE, 2);
        }

        // UTF-8 BOM: EF BB BF
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Utf8Lenient, 3);
        }

        // No BOM — default to UTF-8.
        return (Utf8Lenient, 0);
    }

    /// <summary>
    ///     Reads the entire file and returns whether it is text.
    ///     Convenience overload for file paths.
    /// </summary>
    public static bool IsTextFile(string absolutePath)
    {
        try
        {
            using var fs = new FileStream(absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                MaxDetectBytes);
            Span<byte> buffer = stackalloc byte[MaxDetectBytes];
            int read = fs.Read(buffer);
            return IsText(buffer[..read]);
        }
        catch
        {
            return false;
        }
    }

    // ---- private helpers ----

    private static bool HasBom(ReadOnlySpan<byte> bytes)
    {
        // UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return true;
        }

        // UTF-16 LE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return true;
        }

        // UTF-16 BE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return true;
        }

        // UTF-32 LE BOM
        if (
            bytes.Length >= 4
            && bytes[0] == 0xFF
            && bytes[1] == 0xFE
            && bytes[2] == 0x00
            && bytes[3] == 0x00
        )
        {
            return true;
        }

        // UTF-32 BE BOM
        if (
            bytes.Length >= 4
            && bytes[0] == 0x00
            && bytes[1] == 0x00
            && bytes[2] == 0xFE
            && bytes[3] == 0xFF
        )
        {
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Returns true if more than 85% of characters in the sample are
    ///     printable ASCII, whitespace, or common line-break / tab characters.
    ///     This catches Latin-1, Windows-1252, and other 8-bit encodings
    ///     that are mostly text.
    /// </summary>
    private static bool IsHighPrintableRatio(string sample)
    {
        if (sample.Length == 0)
        {
            return true;
        }

        int printable = 0;
        foreach (char ch in sample)
        {
            // Printable ASCII range + common control characters
            if (ch >= 0x20 && ch <= 0x7E) // printable ASCII
            {
                printable++;
            }
            else if (ch == '\t' || ch == '\n' || ch == '\r')
            {
                printable++;
            }
            else if (ch >= 0xA0 && ch <= 0xFF) // Latin-1 supplement (printable in 8859-1)
            {
                printable++;
            }
            // else: control character or replacement char → non-printable
        }

        return printable / (double)sample.Length >= 0.85;
    }
}
