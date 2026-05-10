using System.Text;
using System.Text.Json;

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

            var sb = new StringBuilder();

            // Get notebook metadata
            string? language = null;
            if (root.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("kernelspec", out var ks))
            {
                language = ks.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
            }

            sb.AppendLine($"[FILE] {context.RelativePath}  ({cells.GetArrayLength()} cells, Jupyter Notebook)");
            if (language is not null)
                sb.AppendLine($"Language: {language}");
            sb.AppendLine();

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

                var line = $"[{cellNumber}] {prefix}{cellType}\n{cellText}";
                var lineBytes = Encoding.UTF8.GetByteCount(line);

                if (totalBytes + lineBytes > maxBytes)
                {
                    sb.AppendLine($"... (output truncated, {cells.GetArrayLength() - cellNumber + 1} more cells)");
                    break;
                }

                sb.AppendLine(line);
                totalBytes += lineBytes;
            }

            return ValueTask.FromResult(new ContentProcessorResult(
                sb.ToString().TrimEnd(), OutputModality.Text));
        }
        catch (JsonException)
        {
            return ValueTask.FromResult(new ContentProcessorResult(
                $"[FILE] {context.RelativePath}  (invalid JSON)", OutputModality.Text));
        }
    }

    private static string ExtractSourceText(JsonElement source)
    {
        var sb = new StringBuilder();
        foreach (var line in source.EnumerateArray())
        {
            sb.AppendLine(line.GetString() ?? "");
        }
        return sb.ToString().TrimEnd();
    }
}
