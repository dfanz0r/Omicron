using Cysharp.Text;

namespace Omicron.Core.Content;

/// <summary>
/// Simple text renderer for <see cref="IContentBlock"/> instances.
/// Produces CLI-friendly text. No markdown parsing, no syntax
/// highlighting, no terminal-width awareness.
/// </summary>
public static class ContentBlockTextRenderer
{
    /// <summary>
    /// Render a single content block as text. This is a boundary method: hot
    /// paths should prefer <see cref="AppendUtf8To"/> and keep the caller-owned
    /// builder alive across render operations.
    /// </summary>
    public static string Render(IContentBlock block)
    {
        var sb = ZString.CreateUtf8StringBuilder();
        try
        {
            AppendUtf8To(ref sb, block);
            return sb.ToString();
        }
        finally
        {
            sb.Dispose();
        }
    }

    /// <summary>
    /// Render a sequence of content blocks, separated by newlines. This is a
    /// boundary method; prefer <see cref="AppendAllUtf8To"/> in hot paths.
    /// </summary>
    public static string RenderAll(List<IContentBlock> blocks)
    {
        if (blocks.Count == 0) return "";

        var sb = ZString.CreateUtf8StringBuilder();
        try
        {
            AppendAllUtf8To(ref sb, blocks);
            return sb.ToString();
        }
        finally
        {
            sb.Dispose();
        }
    }

    /// <summary>Append a rendered content block directly into a caller-owned UTF-8 value string builder.</summary>
    public static void AppendUtf8To(ref Utf8ValueStringBuilder sb, IContentBlock block)
    {
        switch (block)
        {
            case PlainTextContentBlock t:
                t.TextBuffer.AppendTo(ref sb);
                break;
            case MarkdownContentBlock m:
                m.TextBuffer.AppendTo(ref sb);
                break;
            case CodeContentBlock c:
                AppendCodeUtf8(ref sb, c);
                break;
            case DiffContentBlock d:
                d.TextBuffer.AppendTo(ref sb);
                break;
            case FilePreviewContentBlock f:
                AppendFilePreviewUtf8(ref sb, f);
                break;
            case ErrorContentBlock e:
                AppendErrorUtf8(ref sb, e);
                break;
            case ToolCallContentBlock tc:
                AppendToolCallUtf8(ref sb, tc);
                break;
            default:
                sb.AppendLiteral("Error: unknown content block type ("u8);
                sb.Append(block.GetType().Name);
                sb.Append(')');
                break;
        }
    }

    /// <summary>Append rendered content blocks directly into a caller-owned UTF-8 value string builder.</summary>
    public static void AppendAllUtf8To(ref Utf8ValueStringBuilder sb, List<IContentBlock> blocks)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            AppendUtf8To(ref sb, blocks[i]);
        }
    }

    private static void AppendCodeUtf8(ref Utf8ValueStringBuilder sb, CodeContentBlock block)
    {
        if (block.Path is not null)
        {
            sb.AppendLiteral("// "u8);
            sb.AppendLine(block.Path);
        }

        sb.AppendLiteral("```"u8);
        if (block.Language is not null)
            sb.Append(block.Language);
        sb.AppendLine();

        block.TextBuffer.AppendTo(ref sb);
        if (!EndsWithLineFeed(block.TextBuffer))
            sb.AppendLine();
        sb.AppendLiteral("```"u8);
    }

    private static void AppendFilePreviewUtf8(ref Utf8ValueStringBuilder sb, FilePreviewContentBlock block)
    {
        sb.AppendLiteral("[FILE] "u8);
        sb.Append(block.Path);
        sb.AppendLiteral("  ("u8);
        FormatSize.AppendUtf8To(ref sb, block.Size);

        if (block.IsBinary)
        {
            sb.AppendLiteral(", binary)"u8);
            return;
        }

        if (block.LineCount.HasValue)
        {
            sb.AppendLiteral(", "u8);
            sb.Append(block.LineCount.Value);
            sb.AppendLiteral(" lines"u8);
        }

        sb.AppendLine(")");
        block.TextBuffer.AppendTo(ref sb);
    }

    private static void AppendErrorUtf8(ref Utf8ValueStringBuilder sb, ErrorContentBlock block)
    {
        sb.AppendLiteral("Error: "u8);
        block.TextBuffer.AppendTo(ref sb);
        if (block.DetailsBuffer is not null)
        {
            sb.AppendLiteral("\nDetails: "u8);
            block.DetailsBuffer.AppendTo(ref sb);
        }
    }

    private static void AppendToolCallUtf8(ref Utf8ValueStringBuilder sb, ToolCallContentBlock block)
    {
        sb.AppendLiteral("[Tool Call: "u8);
        sb.Append(block.ToolName);
        if (block.ToolCallId is not null)
        {
            sb.AppendLiteral(" ("u8);
            sb.Append(block.ToolCallId);
            sb.Append(')');
        }
        sb.Append(']');
    }

    private static bool EndsWithLineFeed(Utf8ContentBuffer buffer)
    {
        var span = buffer.AsSpan();
        return span.Length > 0 && span[^1] == (byte)'\n';
    }
}
