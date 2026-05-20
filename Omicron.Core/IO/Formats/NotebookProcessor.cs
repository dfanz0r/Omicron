using System.Buffers;
using System.Text;
using System.Text.Json;
using Omicron.Core.Text;

namespace Omicron.Core.IO;

/// <summary>
/// Processes Jupyter Notebook (.ipynb) files by extracting code cells
/// and markdown cells, stripping outputs (which can be large and binary).
/// </summary>
public sealed class NotebookProcessor : IContentProcessor
{
    public string Id => "notebook";
    public IReadOnlySet<DetectedFileType> SupportedTypes { get; }
        = new HashSet<DetectedFileType> { DetectedFileType.Notebook };
    public OutputModality OutputModality => OutputModality.Text;

    public ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var bytes = context.Bytes;

        if (bytes.Length > 50 * 1024 * 1024)
        {
            return ValueTask.FromResult(new ContentProcessorResult(
                $"[FILE] {context.RelativePath}  ({bytes.Length} bytes, IPYNB — file too large)", OutputModality.Text));
        }

        if (bytes.IsEmpty)
        {
            return ValueTask.FromResult(new ContentProcessorResult(
                $"[FILE] {context.RelativePath}  (empty notebook)", OutputModality.Text));
        }

        try
        {
            // Parse directly from bytes, avoiding string allocation for the full JSON
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;

            if (!root.TryGetProperty("cells", out var cells))
            {
                return ValueTask.FromResult(new ContentProcessorResult(
                    $"[FILE] {context.RelativePath}  (not a valid .ipynb file — missing 'cells' array)", OutputModality.Text));
            }

            var output = Utf8Text.CreateBuilder();
            try
            {

                // Get notebook metadata
                string? language = null;
                if (root.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("kernelspec", out var ks))
                {
                    language = ks.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
                }

                Utf8CompositeFormat.AppendFormatUtf8Slow(
                    ref output,
                    "[FILE] {0}  ({1} cells, Jupyter Notebook)"u8,
                    context.RelativePath, cells.GetArrayLength());
                output.AppendLine();
                if (language is not null)
                {
                    Utf8CompositeFormat.AppendFormatUtf8Slow(ref output, "Language: {0}"u8, language);
                    output.AppendLine();
                }
                output.AppendLine();

                int cellNumber = 0;
                long totalBytes = 0;
                const long maxBytes = 50 * 1024;

                foreach (var cell in cells.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();

                    var cellType = cell.TryGetProperty("cell_type", out var ctProp) ? ctProp.GetString() : "unknown";
                    var source = cell.TryGetProperty("source", out var src) ? src : default;

                    if (source.ValueKind != JsonValueKind.Array) continue;

                    // Accumulate cell source text as UTF-8 bytes, counting lines
                    var cellBuffer = new ArrayBufferWriter<byte>();
                    int cellSourceLines = 0;
                    foreach (var sourceLine in source.EnumerateArray())
                    {
                        var lineText = sourceLine.GetString();
                        if (lineText is null) continue;
                        cellSourceLines++;
                        // Encode the line directly into the byte buffer
                        _ = Encoding.UTF8.GetBytes(lineText.AsSpan(), cellBuffer);
                        cellBuffer.GetSpan(1)[0] = (byte)'\n';
                        cellBuffer.Advance(1);
                    }

                    if (cellSourceLines == 0) continue;

                    cellNumber++;

                    var prefix = cellType switch
                    {
                        "code" => ">>> "u8,
                        "markdown" => "--- "u8,
                        _ => "    "u8
                    };

                    // Approximate byte cost without string allocations
                    int numDigits = cellNumber >= 10000 ? 5 : cellNumber >= 1000 ? 4 : cellNumber >= 100 ? 3 : cellNumber >= 10 ? 2 : 1;
                    int cellTypeBytes = cellType is not null ? Encoding.UTF8.GetByteCount(cellType.AsSpan()) : 0;
                    int prefixBytes = prefix.Length;
                    int linePrefixBytes = 1 + numDigits + 2 + prefixBytes + cellTypeBytes + 1; // "[" + N + "] " + prefix + cellType + "\n"
                    int cellBytes = cellBuffer.WrittenCount;
                    int lineBytes = linePrefixBytes + cellBytes + 1; // +1 for trailing newline

                    if (totalBytes + lineBytes > maxBytes)
                    {
                        Utf8CompositeFormat.AppendFormatUtf8Slow(
                            ref output,
                            "... (output truncated, {0} more cells)"u8,
                            cells.GetArrayLength() - cellNumber + 1);
                        output.AppendLine();
                        break;
                    }

                    // Write prefix: "[N] prefixcellType\n"
                    output.Append('[');
                    output.Append(cellNumber);
                    output.AppendLiteral("] "u8);
                    output.AppendLiteral(prefix);
                    output.Append(cellType ?? "unknown");
                    output.AppendLine();

                    // Write cell source bytes
                    output.AppendLiteral(cellBuffer.WrittenSpan);
                    // Ensure trailing newline
                    if (cellBuffer.WrittenCount == 0 || cellBuffer.WrittenSpan[^1] != (byte)'\n')
                        output.AppendLine();

                    totalBytes += lineBytes;
                }

                return ValueTask.FromResult(new ContentProcessorResult(
                    output.ToString().TrimEnd(), OutputModality.Text,
                    Utf8Data: output.AsSpan().ToArray()));
            }
            finally { output.Dispose(); }
        }
        catch (JsonException)
        {
            return ValueTask.FromResult(new ContentProcessorResult(
                $"[FILE] {context.RelativePath}  (invalid JSON)", OutputModality.Text));
        }
    }
}
