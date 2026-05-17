using System.Text;
using Omicron.Core.Content;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Omicron.Core.IO;

/// <summary>
/// Processes archive files by listing their table of contents
/// (file tree with sizes, compressed/original size, modification dates).
/// Does NOT extract file contents — the model must use shell commands for that.
/// </summary>
public sealed class ArchiveProcessor : IContentProcessor
{
    public string Id => "archive";
    public IReadOnlySet<DetectedFileType> SupportedTypes { get; }
        = new HashSet<DetectedFileType> { DetectedFileType.Archive };
    public OutputModality OutputModality => OutputModality.Text;

    public ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var bytes = context.Bytes;
        var ext = Path.GetExtension(context.RelativePath)?.ToLowerInvariant() ?? "";
        var formatName = ext switch
        {
            ".zip" => "ZIP",
            ".tar" => "TAR",
            ".gz" or ".tgz" => "GZip",
            ".bz2" => "BZip2",
            ".xz" => "XZ",
            ".7z" => "7z",
            ".rar" => "RAR",
            _ => "Archive"
        };

        var sb = new StringBuilder();
        sb.AppendLine($"[FILE] {context.RelativePath}  ({FormatSize.Format(bytes.Length)}, {formatName})");
        sb.AppendLine();

        // Try to list contents from archive (SharpCompress handles ZIP, RAR, 7z, TAR, GZip, BZip2, XZ)
        var entries = TryListContents(bytes.Span, ext);
        if (entries is not null)
        {
            sb.AppendLine($"Contents ({entries.Count} entries):");
            sb.AppendLine();
            foreach (var entry in entries)
            {
                sb.AppendLine($"  {entry}");
            }
        }
        else
        {
            sb.AppendLine("(archive listing unavailable — use shell commands to inspect)");
        }

        return ValueTask.FromResult(new ContentProcessorResult(
            sb.ToString().TrimEnd(), OutputModality.Text));
    }

    /// <summary>
    /// List contents of an archive using SharpCompress (auto-detects ZIP, RAR, 7z, TAR, GZip, BZip2, XZ).
    /// Falls back to System.IO.Compression.ZipArchive for ZIP-only listing.
    /// </summary>
    private static List<string>? TryListContents(ReadOnlySpan<byte> bytes, string ext)
    {
        // Try SharpCompress first (handles ZIP, RAR, 7z, TAR, GZip, BZip2, XZ)
        try
        {
            using var ms = new MemoryStream(bytes.ToArray());
            using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(ms, new SharpCompress.Readers.ReaderOptions());

            var entries = new List<string>();
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory)
                    entries.Add($"[DIR]  {entry.Key}");
                else
                    entries.Add($"       {entry.Key}  ({FormatSize.Format(entry.Size)})");
            }
            return entries;
        }
        catch
        {
            // SharpCompress failed — fall back to ZIP-only listing
        }

        // Fallback: ZIP-only via System.IO.Compression
        try
        {
            if (bytes.Length < 4) return null;
            if (bytes[0] != 0x50 || bytes[1] != 0x4B) return null;

            using var ms = new MemoryStream(bytes.ToArray());
            using var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);

            var entries = new List<string>();
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (name.EndsWith("/"))
                    entries.Add($"[DIR]  {name}");
                else
                    entries.Add($"       {name}  ({FormatSize.Format(entry.Length)})");
            }
            return entries;
        }
        catch
        {
            return null;
        }
    }


}
