using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Omicron.Core.Content;
using Omicron.Core.Text;
using VersOne.Epub;

namespace Omicron.Core.IO;

/// <summary>
///     Processes EPUB ebook files by extracting chapter text.
///     Uses VersOne.Epub when available; falls back to ZIP-based text extraction.
/// </summary>
public sealed class EbookProcessor : IContentProcessor
{
    private static readonly Regex HtmlWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    public string Id => "ebook";

    public IReadOnlySet<DetectedFileType> SupportedTypes { get; } =
        new HashSet<DetectedFileType>
        {
            DetectedFileType.Ebook
        };

    public OutputModality OutputModality => OutputModality.Text;

    public ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        ReadOnlyMemory<byte> bytes = context.Bytes;

        if (bytes.IsEmpty)
        {
            using var _eb = Utf8Text.CreateBuilder();
            _eb.AppendLiteral("[FILE] "u8);
            _eb.Append(context.RelativePath);
            _eb.AppendLiteral("  (empty)"u8);
            return ValueTask.FromResult(new ContentProcessorResult(
                Utf8String.FromUtf8(_eb.AsSpan()), OutputModality.Text));
        }

        Utf8Builder output = Utf8Text.CreateBuilder();
        try
        {
            Utf8CompositeFormat.AppendFormatUtf8Slow(ref output,
                "[FILE] {0}  ({1}, EPUB)"u8,
                context.RelativePath,
                FormatSize.Format(bytes.Length));
            output.AppendLine();
            output.AppendLine();

            // Try to extract text from EPUB (ZIP with .opf and .xhtml)
            List<(string? Title, string Content)>? chapters = TryExtractEpubText(bytes.Span);

            if (chapters is not null && chapters.Count > 0)
            {
                Utf8CompositeFormat.AppendFormatUtf8Slow(ref output,
                    "Chapters: {0}"u8,
                    chapters.Count);
                output.AppendLine();
                output.AppendLine();

                long totalBytes = 0;
                const long maxBytes = 50 * 1024;

                foreach ((string? title, string content) in chapters)
                {
                    ct.ThrowIfCancellationRequested();

                    string chapterText = string.IsNullOrEmpty(title)
                        ? content
                        : $"## {title}\n\n{content}";

                    int chapterBytes = Encoding.UTF8.GetByteCount(chapterText) + 1; // +1 for newline
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
                Utf8String.FromUtf8(output.AsSpan()),
                OutputModality.Text));
        }
        finally
        {
            output.Dispose();
        }
    }

    private static List<(string? Title, string Content)>? TryExtractEpubText(ReadOnlySpan<byte> bytes)
    {
        // Try VersOne.Epub first
        try
        {
            using var ms = new MemoryStream(bytes.ToArray());
            EpubBookRef epub = EpubReader.OpenBook(ms);
            var results = new List<(string? Title, string Content)>();
            List<EpubLocalTextContentFileRef>? readingOrder = epub.GetReadingOrder();
            if (readingOrder is null)
            {
                return null;
            }

            foreach (EpubLocalTextContentFileRef textContent in readingOrder)
            {
                string html = textContent.ReadContent();
                string text = StripHtmlTags(html);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    results.Add((null, text));
                }
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
            using var archive = new ZipArchive(ms,
                ZipArchiveMode.Read);

            // Find the OPF file from META-INF/container.xml
            ZipArchiveEntry? containerEntry = archive.GetEntry("META-INF/container.xml");
            if (containerEntry is null)
            {
                return null;
            }

            string? opfPath = null;
            using (var reader = new StreamReader(containerEntry.Open(), Encoding.UTF8))
            {
                string containerXml = reader.ReadToEnd();
                // Look for <rootfile full-path="..." />
                Match match = Regex.Match(containerXml,
                    "full-path\\s*=\\s*\"([^\"]+)\"",
                    RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    opfPath = match.Groups[1].Value;
                }
            }

            if (opfPath is null)
            {
                return null;
            }

            // Normalize OPF path to get base directory
            string opfDir = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? "";
            if (opfDir.Length > 0)
            {
                opfDir += "/";
            }

            // Read OPF to get spine and manifest
            ZipArchiveEntry? opfEntry = archive.GetEntry(opfPath);
            if (opfEntry is null)
            {
                return null;
            }

            string opfContent;
            using (var reader = new StreamReader(opfEntry.Open(), Encoding.UTF8))
            {
                opfContent = reader.ReadToEnd();
            }

            // Find all <item href="..." id="..." media-type="application/xhtml+xml" />
            MatchCollection hrefMatches = Regex.Matches(opfContent,
                "href\\s*=\\s*\"([^\"]+)\"",
                RegexOptions.IgnoreCase);

            var results = new List<(string?, string)>();

            foreach (Match hrefMatch in hrefMatches)
            {
                if (hrefMatch.Success)
                {
                    string href = hrefMatch.Groups[1].Value;
                    string fullPath = opfDir + href;
                    fullPath = fullPath.Replace("\\", "/").Replace("//", "/");

                    ZipArchiveEntry? entry = archive.GetEntry(fullPath);
                    if (entry is null)
                    {
                        continue;
                    }

                    using var entryReader = new StreamReader(entry.Open(), Encoding.UTF8);
                    string xhtml = entryReader.ReadToEnd();

                    // Extract title from <title> tag
                    Match titleMatch = Regex.Match(xhtml,
                        "<title[^>]*>([^<]+)</title>",
                        RegexOptions.IgnoreCase);
                    string? title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : null;

                    // Extract body content (strip HTML tags)
                    string bodyText = StripHtmlTags(xhtml);
                    if (!string.IsNullOrWhiteSpace(bodyText))
                    {
                        results.Add((title, bodyText));
                    }
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
        {
            return "(document too large)";
        }

        using Utf8Builder output = Utf8Text.CreateBuilder();
        bool inTag = false;
        bool inEntity = false;
        var entityBuf = new StringBuilder();

        for (int i = 0; i < html.Length; i++)
        {
            char c = html[i];
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

            if (inTag)
            {
                continue;
            }

            if (c == '&')
            {
                inEntity = true;
                entityBuf.Clear();
                continue;
            }

            if (c == ';' && inEntity)
            {
                inEntity = false;
                string entity = entityBuf.ToString();
                char decoded = entity switch
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
                {
                    output.Append(decoded);
                }
                else if (entity.StartsWith("#x"))
                {
                    output.Append((char)Convert.ToInt32(entity[2..], 16));
                }
                else if (entity.StartsWith("#"))
                {
                    output.Append((char)Convert.ToInt32(entity[1..]));
                }

                continue;
            }

            if (inEntity)
            {
                entityBuf.Append(c);
                continue;
            }

            output.Append(c);
        }

        string result = output.ToString();
        // Collapse whitespace
        return HtmlWhitespaceRegex.Replace(result, " ").Trim();
    }
}
