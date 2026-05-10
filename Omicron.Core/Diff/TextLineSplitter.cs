namespace Omicron.Core.Diff;

/// <summary>
/// Consistent line splitting for diff operations.
/// Matches workspace read behavior: normalizes line endings to LF, splits on newline.
/// A trailing newline produces a final empty line (preserving round-trip line counting).
/// </summary>
internal static class TextLineSplitter
{
    /// <summary>Split text into lines using diff-consistent rules.</summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();

        return text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
    }

    /// <summary>Split UTF-8 bytes into lines.</summary>
    public static IReadOnlyList<string> SplitLines(ReadOnlySpan<byte> utf8Bytes)
    {
        if (utf8Bytes.IsEmpty)
            return Array.Empty<string>();

        var text = System.Text.Encoding.UTF8.GetString(utf8Bytes);
        return SplitLines(text);
    }
}
