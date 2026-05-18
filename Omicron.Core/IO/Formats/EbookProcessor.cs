using System.Text;
using System.Text.RegularExpressions;
using Cysharp.Text;
using Omicron.Core.Content;

namespace Omicron.Core.IO;

/// <summary>
/// Processes EPUB ebook files by extracting chapter text.
/// Uses VersOne.Epub when available; falls back to ZIP-based text extraction.
/// </summary>
public sealed class EbookProcessor : IContentProcessor
{
    private static readonly Regex HtmlWhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);
    public string Id => "ebook";

    public IReadOnlySet<DetectedFileType> SupportedTypes { get; }
        = new HashSet<DetectedFileType> { DetectedFileType.Ebook };
    public OutputModality OutputModality => OutputModality.Text;

    public ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var bytes = context.Bytes;

        if (bytes.IsEmpty)
        {
            return ValueTask.FromResult(new ContentProcessorResult(
                $"[FILE] {context.RelativePath}  (empty)", OutputModality.Text));
        }

        using var output = ZString.CreateUtf8StringBuilder();
        output.AppendFormat("[FILE] {0}  ({1}, EPUB)", context.RelativePath, FormatSize.Format(bytes.Length));
        output.AppendLine();
        output.AppendLine();

        // Try to extract text from EPUB (ZIP with .opf and .xhtml)
        var chapters = TryExtractEpubText(bytes.Span);

        if (chapters is not null && chapters.Count > 0)
        {
            output.AppendFormat("Chapters: {0}", chapters.Count);
            output.AppendLine();
            output.AppendLine();

            long totalBytes = 0;
            const long maxBytes = 50 * 1024;

            foreach (var (title, content) in chapters)
            {
                ct.ThrowIfCancellationRequested();

                var chapterText = string.IsNullOrEmpty(title)
                    ? content
                    : $"## {title}\n\n{content}";

                var chapterBytes = Encoding.UTF8.GetByteCount(chapterText) + 1; // +1 for newline
                if (totalBytes + chapterBytes > maxBytes)
                {
                    output.AppendLiteral("... (output truncated)"u8);
                    output.AppendLine();
                    break;
                }

                output.Append(chapterText);
                output.AppendLine();
                output.AppendLine();
                totalBytes += chapterBytes;
            }
        }
        else
        {
            output.AppendLiteral("(text extraction unavailable)"u8);
            output.AppendLine();
        }

        return ValueTask.FromResult(new ContentProcessorResult(
            output.ToString().TrimEnd(), OutputModality.Text,
            Utf8Data: output.AsSpan().ToArray()));
    }

    private static List<(string? Title, string Content)>? TryExtractEpubText(ReadOnlySpan<byte> bytes)
    {
        // Try VersOne.Epub first
        try
        {
            using var ms = new MemoryStream(bytes.ToArray());
            var epub = VersOne.Epub.EpubReader.OpenBook(ms);
            var results = new List<(string? Title, string Content)>();
            var readingOrder = epub.GetReadingOrder();
            if (readingOrder is null) return null;
            foreach (var textContent in readingOrder)
            {
                var html = textContent.ReadContent();
                var text = StripHtmlTags(html);
                if (!string.IsNullOrWhiteSpace(text))
                    results.Add((null, text));
            }
            return results.Count > 0 ? results : null;
        }
        catch
        {
            // VersOne.Epub failed — fall back to manual ZIP traversal
        }

        // Fallback: manual ZIP + OPF traversal
        try
        {
            using var ms = new MemoryStream(bytes.ToArray());
            using var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);

            // Find the OPF file from META-INF/container.xml
            var containerEntry = archive.GetEntry("META-INF/container.xml");
            if (containerEntry is null) return null;

            string? opfPath = null;
            using (var reader = new System.IO.StreamReader(containerEntry.Open(), Encoding.UTF8))
            {
                var containerXml = reader.ReadToEnd();
                // Look for <rootfile full-path="..." />
                var match = System.Text.RegularExpressions.Regex.Match(containerXml,
                    "full-path\\s*=\\s*\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                    opfPath = match.Groups[1].Value;
            }

            if (opfPath is null) return null;

            // Normalize OPF path to get base directory
            var opfDir = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? "";
            if (opfDir.Length > 0) opfDir += "/";

            // Read OPF to get spine and manifest
            var opfEntry = archive.GetEntry(opfPath);
            if (opfEntry is null) return null;

            string opfContent;
            using (var reader = new System.IO.StreamReader(opfEntry.Open(), Encoding.UTF8))
                opfContent = reader.ReadToEnd();

            // Find all <item href="..." id="..." media-type="application/xhtml+xml" />
            var hrefMatches = System.Text.RegularExpressions.Regex.Matches(opfContent,
                "href\\s*=\\s*\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var results = new List<(string?, string)>();

            foreach (System.Text.RegularExpressions.Match hrefMatch in hrefMatches)
            {
                if (hrefMatch.Success)
                {
                    var href = hrefMatch.Groups[1].Value;
                    var fullPath = opfDir + href;
                    fullPath = fullPath.Replace("\\", "/").Replace("//", "/");

                    var entry = archive.GetEntry(fullPath);
                    if (entry is null) continue;

                    using var entryReader = new System.IO.StreamReader(entry.Open(), Encoding.UTF8);
                    var xhtml = entryReader.ReadToEnd();

                    // Extract title from <title> tag
                    var titleMatch = System.Text.RegularExpressions.Regex.Match(xhtml,
                        "<title[^>]*>([^<]+)</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : null;

                    // Extract body content (strip HTML tags)
                    var bodyText = StripHtmlTags(xhtml);
                    if (!string.IsNullOrWhiteSpace(bodyText))
                        results.Add((title, bodyText));
                }
            }

            return results;
        }
        catch
        {
            return null;
        }
    }

    private static string StripHtmlTags(string html)
    {
        if (html.Length > 10 * 1024 * 1024) // 10 MB cap
            return "(document too large)";

        using var output = ZString.CreateUtf8StringBuilder();
        bool inTag = false;
        bool inEntity = false;
        var entityBuf = new System.Text.StringBuilder();

        for (int i = 0; i < html.Length; i++)
        {
            var c = html[i];
            if (c == '<')
            {
                inTag = true;
                continue;
            }
            if (c == '>')
            {
                inTag = false;
                continue;
            }
            if (inTag) continue;

            if (c == '&')
            {
                inEntity = true;
                entityBuf.Clear();
                continue;
            }
            if (c == ';' && inEntity)
            {
                inEntity = false;
                var entity = entityBuf.ToString();
                var decoded = entity switch
                {
                    "amp" => '&',
                    "lt" => '<',
                    "gt" => '>',
                    "quot" => '"',
                    "apos" => '\'',
                    "nbsp" => ' ',
                    _ => '\0'
                };
                if (decoded != '\0')
                    output.Append(decoded);
                else if (entity.StartsWith("#x"))
                    output.Append((char)Convert.ToInt32(entity[2..], 16));
                else if (entity.StartsWith("#"))
                    output.Append((char)Convert.ToInt32(entity[1..]));
                continue;
            }
            if (inEntity)
            {
                entityBuf.Append(c);
                continue;
            }

            output.Append(c);
        }

        var result = output.ToString();
        // Collapse whitespace
        return HtmlWhitespaceRegex.Replace(result, " ").Trim();
    }


}
