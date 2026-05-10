using System.Text;

namespace Omicron.Core.Tools;

/// <summary>
/// Fast non-cryptographic line hashing for anchor-based editing.
/// Uses FNV-1a 32-bit over normalized UTF-8 line bytes,
/// mapped to a 2-letter lowercase anchor ID (a-z, no digits).
/// This avoids ambiguity between line numbers and hash digits
/// in the rendering format (e.g. "42sr|content").
/// </summary>
internal static class LineHash
{
    // FNV-1a 32-bit constants
    private const uint FnvPrime = 16777619;
    private const uint FnvOffsetBasis = 2166136261;

    /// <summary>
    /// Compute the 32-bit FNV-1a hash of a normalized line (no \r\n).
    /// </summary>
    public static uint ComputeHash(string line)
    {
        // Normalize: use UTF-8 bytes. Lines passed in are already
        // split with \r\n normalized to \n, so we just hash the text.
        var bytes = Encoding.UTF8.GetBytes(line);

        uint hash = FnvOffsetBasis;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= FnvPrime;
        }
        return hash;
    }

    /// <summary>
    /// Map a 32-bit hash to a 2-letter lowercase anchor (aa..zz).
    /// Collisions are acceptable because old_text serves as a second guard.
    /// 26×26 = 676 distinct anchors.
    /// </summary>
    public static string ToAnchor(uint hash)
    {
        // 676 = 26 * 26
        var index = (int)(hash % 676);
        var first = (char)('a' + index / 26);
        var second = (char)('a' + index % 26);
        return new string([first, second]);
    }

    /// <summary>
    /// Convenience: compute hash and return anchor in one step.
    /// </summary>
    public static string ComputeAnchor(string line)
    {
        return ToAnchor(ComputeHash(line));
    }

    /// <summary>
    /// Format a line with its anchor for display: "{line}{anchor}|{text}".
    /// Example: "42sr|    return a + b;"
    /// </summary>
    public static string FormatLine(int lineNumber, string anchor, string text)
    {
        return $"{lineNumber}{anchor}|{text}";
    }

    /// <summary>
    /// Format a line directly from its content.
    /// </summary>
    public static string FormatLine(int lineNumber, string text)
    {
        return FormatLine(lineNumber, ComputeAnchor(text), text);
    }
}
