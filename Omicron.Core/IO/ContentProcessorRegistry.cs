using Omicron.Core.Events;
using Omicron.Core.Models;

namespace Omicron.Core.IO;

/// <summary>
/// What kind of data a content processor can emit.
/// A processor's <see cref="IContentProcessor.OutputModality"/> property
/// declares all modalities it MIGHT produce. The
/// <see cref="ContentProcessorResult.ActualModality"/> records what
/// was ACTUALLY emitted for a specific invocation.
///
/// Used for cross-model session porting validation:
/// if a session emitted ImageBase64, the target model must support "image".
/// </summary>
[Flags]
public enum OutputModality
{
    None        = 0,
    Text        = 1 << 0,  // Plain text or Markdown — works with any model
    ImageBase64 = 1 << 1,  // base64-encoded image inline — requires vision
    PdfBase64   = 1 << 2,  // base64-encoded PDF inline — requires pdf modality
    AudioBase64 = 1 << 3,  // base64-encoded audio inline — requires audio modality
    VideoBase64 = 1 << 4,  // base64-encoded video inline — requires video modality
    HexDump     = 1 << 5,  // Structured hex dump — always works (text)
    Metadata    = 1 << 6,  // File metadata only (dimensions, codec, size, etc.)
}

/// <summary>
/// Context passed to every <see cref="IContentProcessor.ProcessAsync"/> call.
/// Contains everything the processor needs to decide how to handle the file.
/// </summary>
public sealed record ContentProcessorContext(
    string AbsolutePath,
    string RelativePath,           // workspace-relative, for error messages
    ReadOnlyMemory<byte> Bytes,    // full file bytes (already read)
    long FileSize,                 // from stat
    SessionId SessionId,           // for ModalityUsedEvent emission
    ModelMetadata? ModelMetadata,  // null if model capabilities unknown
    string Format,                 // "auto", "text", "hex", or "base64"
    int? Offset,                   // byte offset (hex mode) or line offset (text mode)
    int? Limit,                    // byte limit (hex mode) or line limit (text mode)
    IEventSink? EventSink,         // for emitting ModalityUsedEvent
    CancellationToken CancellationToken);

/// <summary>
/// Result returned by <see cref="IContentProcessor.ProcessAsync"/>.
/// Carries the actual output modality used (which may differ from
/// the processor's declared capabilities).
/// </summary>
public sealed record ContentProcessorResult(
    string Text,                     // formatted output for the model
    OutputModality ActualModality,   // what was actually emitted
    string? MimeType = null,        // e.g., "image/png"
    string? Warning = null,         // e.g., "PDF text extracted locally"
    bool IsTruncated = false,       // true if output was cut short
    long? NextOffset = null);       // for continuation (hex or text)

/// <summary>
/// A format handler that can read and process a specific file type.
/// Returns either extracted text, base64-encoded inline data, hex dump, or metadata.
/// </summary>
public interface IContentProcessor
{
    /// <summary>Unique identifier for this processor.</summary>
    string Id { get; }

    /// <summary>Which file types this processor handles.</summary>
    IReadOnlySet<DetectedFileType> SupportedTypes { get; }

    /// <summary>
    /// All output modalities this processor CAN produce (flags).
    /// The ACTUAL modality per invocation is on <see cref="ContentProcessorResult.ActualModality"/>.
    /// Not used for session porting — that uses the runtime ActualModality.
    /// </summary>
    OutputModality OutputModality { get; }

    /// <summary>
    /// Process a file and return formatted content for the model.
    /// Receives model capabilities so it can adapt (e.g., skip base64 for text-only models).
    /// </summary>
    ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Registry that dispatches file reads to the appropriate <see cref="IContentProcessor"/>.
/// Processors are tried in registration order; first one that supports the
/// detected file type wins.
/// </summary>
public sealed class ContentProcessorRegistry
{
    private readonly List<IContentProcessor> _processors = [];
    private readonly object _lock = new();

    /// <summary>All registered processors.</summary>
    public IReadOnlyList<IContentProcessor> RegisteredProcessors
    {
        get { lock (_lock) return _processors.ToList(); }
    }

    /// <summary>
    /// Register a content processor. Processors are tried in registration order;
    /// first one whose <see cref="IContentProcessor.SupportedTypes"/> includes
    /// the detected file type wins.
    /// </summary>
    public void Register(IContentProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        lock (_lock)
        {
            _processors.Add(processor);
        }
    }

    /// <summary>
    /// Resolve a processor for the given file type.
    /// Returns the first registered processor whose SupportedTypes includes
    /// the type, or null if no processor matches.
    /// </summary>
    public IContentProcessor? Resolve(DetectedFileType fileType)
    {
        lock (_lock)
        {
            for (int i = 0; i < _processors.Count; i++)
            {
                if (_processors[i].SupportedTypes.Contains(fileType))
                    return _processors[i];
            }
        }
        return null;
    }
}
