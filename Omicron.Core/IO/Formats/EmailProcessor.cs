using System.Text;
using Cysharp.Text;
using Omicron.Core.Content;

namespace Omicron.Core.IO;

/// <summary>
/// Processes email files (.eml) by extracting headers and body.
/// Uses MimeKit when available; falls back to simple header/body split.
/// </summary>
public sealed class EmailProcessor : IContentProcessor
{
    public string Id => "email";
    public IReadOnlySet<DetectedFileType> SupportedTypes { get; }
        = new HashSet<DetectedFileType> { DetectedFileType.Email };
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
        output.AppendFormat("[FILE] {0}  ({1}, email)", context.RelativePath, FormatSize.Format(bytes.Length));
        output.AppendLine();
        output.AppendLine();

        // Try MimeKit first
        try
        {
            using var ms = new MemoryStream(bytes.ToArray());
            using var mimeMessage = MimeKit.MimeMessage.Load(ms);

            output.AppendLiteral("--- Headers ---"u8);
            output.AppendLine();
            output.AppendFormat("From: {0}", mimeMessage.From);
            output.AppendLine();
            output.AppendFormat("To: {0}", mimeMessage.To);
            output.AppendLine();
            output.AppendFormat("Subject: {0}", mimeMessage.Subject);
            output.AppendLine();
            output.AppendFormat("Date: {0}", mimeMessage.Date);
            output.AppendLine();
            output.AppendLine();

            output.AppendLiteral("--- Body ---"u8);
            output.AppendLine();
            output.Append(mimeMessage.Body?.ToString() ?? "(no text body)");
            output.AppendLine();

            return ValueTask.FromResult(new ContentProcessorResult(
                output.ToString().TrimEnd(), OutputModality.Text,
                Utf8Data: output.AsSpan().ToArray()));
        }
        catch
        {
            // MimeKit failed — fall back to RFC 822 parsing
        }

        // Fallback: manual RFC 822 parsing
        var text = Encoding.UTF8.GetString(bytes.Span);
        var (headers, body) = SplitEmail(text);

        if (headers.Count > 0)
        {
            output.AppendLiteral("--- Headers ---"u8);
            output.AppendLine();
            foreach (var (key, value) in headers)
            {
                output.AppendFormat("{0}: {1}", key, value);
                output.AppendLine();
            }
            output.AppendLine();
        }

        if (!string.IsNullOrEmpty(body))
        {
            output.AppendLiteral("--- Body ---"u8);
            output.AppendLine();
            output.Append(body);
            output.AppendLine();
        }

        return ValueTask.FromResult(new ContentProcessorResult(
            output.ToString().TrimEnd(), OutputModality.Text,
            Utf8Data: output.AsSpan().ToArray()));
    }

    /// <summary>
    /// Split RFC 822 email into headers and body.
    /// Returns headers as key-value pairs and the body text.
    /// </summary>
    internal static (List<(string Key, string Value)> Headers, string Body) SplitEmail(string text)
    {
        text = text.Replace("\r\n", "\n");
        var headers = new List<(string, string)>();
        string body = "";

        // Find the blank line separating headers from body
        int bodyStart = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (bodyStart < 0)
        {
            // No body, entire text might be headers
            bodyStart = text.Length;
        }
        else
        {
            bodyStart += 2; // Skip \n\n
            body = text[bodyStart..].Trim();
        }

        var headerSection = text[..Math.Max(0, bodyStart - 2)];
        var headerLines = headerSection.Split('\n');

        string currentKey = "";
        var currentValue = new StringBuilder();

        foreach (var line in headerLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line[0] == ' ' || line[0] == '\t')
            {
                // Continuation line
                if (currentValue.Length > 0) currentValue.Append(' ');
                currentValue.Append(line.Trim());
            }
            else
            {
                // New header
                if (currentKey != "")
                {
                    headers.Add((currentKey, currentValue.ToString().Trim()));
                }

                int colonIdx = line.IndexOf(':');
                if (colonIdx > 0)
                {
                    currentKey = line[..colonIdx];
                    currentValue = new StringBuilder(line[(colonIdx + 1)..].Trim());
                }
            }
        }

        if (currentKey != "")
        {
            headers.Add((currentKey, currentValue.ToString().Trim()));
        }

        return (headers, body);
    }


}
