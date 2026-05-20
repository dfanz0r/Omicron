using System.Text;
using Omicron.Core.Text;

namespace Omicron.Core.Tools;

/// <summary>
///     Fast non-cryptographic line hashing for anchor-based editing.
///     Uses FNV-1a 32-bit over normalized UTF-8 line bytes,
///     mapped to a 2-letter lowercase anchor ID (a-z, no digits).
///     This avoids ambiguity between line numbers and hash digits
///     in the rendering format (e.g. "42sr|content").
/// </summary>
internal static class LineHash
{
    // FNV-1a 32-bit constants
    private const uint FnvPrime = 16777619;
    private const uint FnvOffsetBasis = 2166136261;

    /// <summary>
    ///     Compute the 32-bit FNV-1a hash of a string line.
    /// </summary>
    public static uint ComputeHash(string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        return ComputeHash(bytes.AsSpan());
    }

    /// <summary>
    ///     Compute the 32-bit FNV-1a hash of raw UTF-8 bytes directly,
    ///     avoiding a string allocation.
    /// </summary>
    public static uint ComputeHash(ReadOnlySpan<byte> utf8Bytes)
    {
        uint hash = FnvOffsetBasis;
        for (int i = 0; i < utf8Bytes.Length; i++)
        {
            hash ^= utf8Bytes[i];
            hash *= FnvPrime;
        }

        return hash;
    }

    /// <summary>
    ///     Map a 32-bit hash to a 2-letter lowercase anchor (aa..zz).
    ///     Collisions are acceptable because old_text serves as a second guard.
    ///     26×26 = 676 distinct anchors.
    /// </summary>
    public static string ToAnchor(uint hash)
    {
        // 676 = 26 * 26
        int index = (int)(hash % 676);
        char first = (char)('a' + index / 26);
        char second = (char)('a' + index % 26);
        return new string([first, second]);
    }

    /// <summary>
    ///     Convenience: compute hash and return anchor in one step.
    /// </summary>
    public static string ComputeAnchor(string line)
    {
        return ToAnchor(ComputeHash(line));
    }

    /// <summary>
    ///     Compute anchor directly from UTF-8 bytes without a string allocation.
    /// </summary>
    public static string ComputeAnchorUtf8(ReadOnlySpan<byte> utf8Bytes)
    {
        return ToAnchor(ComputeHash(utf8Bytes));
    }

    /// <summary>
    ///     Format a line with its anchor for display: "{line}{anchor}|{text}".
    ///     Example: "42sr|    return a + b;"
    /// </summary>
    public static string FormatLine(int lineNumber, string anchor, string text)
    {
        return $"{lineNumber}{anchor}|{text}";
    }

    /// <summary>
    ///     Append a formatted hashline directly to a UTF-8 builder.
    /// </summary>
    public static void FormatLineUtf8(
        ref Utf8Builder builder,
        int lineNumber,
        string anchor,
        ReadOnlySpan<byte> utf8Text)
    {
        builder.Append(lineNumber);
        builder.Append(anchor);
        builder.Append('|');
        builder.AppendLiteral(utf8Text);
        builder.AppendLine();
    }

    /// <summary>
    ///     Format a line directly from its content.
    /// </summary>
    public static string FormatLine(int lineNumber, string text)
    {
        return FormatLine(lineNumber, ComputeAnchor(text), text);
    }
}
