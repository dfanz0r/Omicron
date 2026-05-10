namespace Omicron.Core.Content;

/// <summary>
/// Human-readable file size formatting.
/// Shared across renderers to avoid duplication.
/// </summary>
internal static class FormatSize
{
    public static string Format(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
