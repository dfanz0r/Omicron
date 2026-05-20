using Omicron.Core.IO;

namespace Omicron.Core.Events;

/// <summary>
///     Emitted when a content processor produces binary output that requires
///     a specific model modality. Persisted in the session event log
///     for cross-model porting validation.
///     Only emitted for ImageBase64, PdfBase64, AudioBase64, and VideoBase64.
/// </summary>
public sealed record ModalityUsedEvent(
    EventEnvelope Envelope,
    string Modality, // "image", "pdf", "audio", or "video"
    string RelativePath, // workspace-relative path of the file
    OutputModality OutputKind) // ImageBase64, PdfBase64, AudioBase64, or VideoBase64
    : OmicronEvent(Envelope);
