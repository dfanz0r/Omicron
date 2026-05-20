using Omicron.Core.Text;

namespace Omicron.Core.Content;

/// <summary>
///     Human-readable file size formatting.
///     Shared across renderers to avoid duplication.
/// </summary>
internal static class FormatSize
{
    public static string Format(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    /// <summary>
    ///     Append a human-readable file size directly into a UTF-8 builder,
    ///     avoiding the intermediate string allocation of <see cref="Format" />.
    /// </summary>
    public static void AppendUtf8To(ref Utf8Builder sb, long bytes)
    {
        if (bytes < 1024)
        {
            sb.Append(bytes);
            sb.AppendLiteral(" B"u8);
            return;
        }

        if (bytes < 1024 * 1024)
        {
            Utf8CompositeFormat.AppendFormatUtf8(ref sb, "{0:F1} KB"u8, bytes / 1024.0);
        }
        else
        {
            Utf8CompositeFormat.AppendFormatUtf8(ref sb, "{0:F1} MB"u8, bytes / (1024.0 * 1024.0));
        }
    }
}
