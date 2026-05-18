using System.Text;
using System.Text.Json;
using Cysharp.Text;

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
            var json = Encoding.UTF8.GetString(bytes.Span);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("cells", out var cells))
            {
                return ValueTask.FromResult(new ContentProcessorResult(
                    $"[FILE] {context.RelativePath}  (not a valid .ipynb file — missing 'cells' array)", OutputModality.Text));
            }

            using var output = ZString.CreateUtf8StringBuilder();

            // Get notebook metadata
            string? language = null;
            if (root.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("kernelspec", out var ks))
            {
                language = ks.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
            }

            output.AppendFormat("[FILE] {0}  ({1} cells, Jupyter Notebook)", context.RelativePath, cells.GetArrayLength());
            output.AppendLine();
            if (language is not null)
            {
                output.AppendFormat("Language: {0}", language);
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

                var cellText = ExtractSourceText(source);

                if (string.IsNullOrWhiteSpace(cellText)) continue;

                cellNumber++;

                var prefix = cellType switch
                {
                    "code" => ">>> ",
                    "markdown" => "--- ",
                    _ => "    "
                };

                // Compute byte cost: "[N] prefix cellType\ncellText\n"
                var linePrefix = $"[{cellNumber}] {prefix}{cellType}\n";
                var linePrefixBytes = Encoding.UTF8.GetByteCount(linePrefix);
                var cellTextBytes = Encoding.UTF8.GetByteCount(cellText);
                var lineBytes = linePrefixBytes + cellTextBytes + 1; // +1 for newline after cellText

                if (totalBytes + lineBytes > maxBytes)
                {
                    output.AppendFormat("... (output truncated, {0} more cells)", cells.GetArrayLength() - cellNumber + 1);
                    output.AppendLine();
                    break;
                }

                output.Append(linePrefix);
                output.Append(cellText);
                output.AppendLine();
                totalBytes += lineBytes;
            }

            return ValueTask.FromResult(new ContentProcessorResult(
                output.ToString().TrimEnd(), OutputModality.Text,
                Utf8Data: output.AsSpan().ToArray()));
        }
        catch (JsonException)
        {
            return ValueTask.FromResult(new ContentProcessorResult(
                $"[FILE] {context.RelativePath}  (invalid JSON)", OutputModality.Text));
        }
    }

    private static string ExtractSourceText(JsonElement source)
    {
        using var output = ZString.CreateUtf8StringBuilder();
        foreach (var line in source.EnumerateArray())
        {
            var text = line.GetString() ?? "";
            output.Append(text);
            output.AppendLine();
        }
        return output.ToString().TrimEnd();
    }
}
