namespace Omicron.Core.Diff;

internal static class TextLineSplitter
{
    public static IReadOnlyList<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();
        return text.Replace("\r\n", "\n").Split('\n');
    }

    public static IReadOnlyList<string> SplitLines(ReadOnlySpan<byte> utf8Bytes)
    {
        if (utf8Bytes.IsEmpty)
            return Array.Empty<string>();
        var text = System.Text.Encoding.UTF8.GetString(utf8Bytes);
        return SplitLines(text);
    }
}
