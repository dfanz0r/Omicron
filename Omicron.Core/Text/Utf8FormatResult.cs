namespace Omicron.Core.Text;

/// <summary>
/// Result codes for the UTF-8 formatting pipeline.
/// Shared by <see cref="Utf8ValueFormatter"/>, <see cref="Utf8CompositeFormat"/>,
/// and <see cref="Utf8Builder"/>.
/// </summary>
internal enum Utf8FormatResult
{
    /// <summary>Value was formatted successfully.</summary>
    Success,

    /// <summary>Destination buffer was too small. The caller should grow and retry.</summary>
    InsufficientSpace,

    /// <summary>A formatter path exists but the requested format/specifier is unsupported.</summary>
    /// <remarks>Do not retry or fall back silently.</remarks>
    UnsupportedType,

    /// <summary>No generated or runtime formatter path exists for this type.</summary>
    /// <remarks>Only this result permits explicit slow fallback to <c>ToString()</c>.</remarks>
    NoFormatter,
}
