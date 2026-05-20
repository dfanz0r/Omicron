namespace Omicron.Core.Models;

/// <summary>
///     Extension methods for querying model capabilities from <see cref="ModelMetadata" />.
/// </summary>
public static class ModelCapabilityExtensions
{
    /// <summary>Check whether the model supports a specific modality.</summary>
    public static bool SupportsModality(this ModelMetadata? meta, string modality)
    {
        return meta?.Modalities?.Contains(modality) == true;
    }

    /// <summary>Model supports image inputs (vision).</summary>
    public static bool SupportsImages(this ModelMetadata? meta)
    {
        return meta.SupportsModality("image") || meta?.SupportsVision == true;
    }

    /// <summary>Model supports native PDF inputs.</summary>
    public static bool SupportsPdf(this ModelMetadata? meta)
    {
        return meta.SupportsModality("pdf");
    }

    /// <summary>Model supports native audio inputs.</summary>
    public static bool SupportsAudio(this ModelMetadata? meta)
    {
        return meta.SupportsModality("audio");
    }

    /// <summary>Model supports native video inputs.</summary>
    public static bool SupportsVideo(this ModelMetadata? meta)
    {
        return meta.SupportsModality("video");
    }
}
