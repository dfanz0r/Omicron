using System.Text;

namespace Omicron.Core.Content;

/// <summary>
/// Simple text renderer for <see cref="ContentBlock"/> instances.
/// Produces CLI-friendly text. No markdown parsing, no syntax
/// highlighting, no terminal-width awareness.
/// </summary>
public static class ContentBlockTextRenderer
{
    /// <summary>Render a single content block as text.</summary>
    public static string Render(ContentBlock block)
    {
        return block switch
        {
            PlainTextContentBlock t => t.Text,
            MarkdownContentBlock m => m.Markdown,
            CodeContentBlock c => RenderCode(c),
            DiffContentBlock d => d.UnifiedDiff,
            FilePreviewContentBlock f => RenderFilePreview(f),
            ErrorContentBlock e => RenderError(e),
            ToolCallContentBlock t => RenderToolCall(t),
            _ => $"Error: unknown content block type ({block.GetType().Name})"
        };
    }

    /// <summary>Render a sequence of content blocks, separated by newlines.</summary>
    public static string RenderAll(IReadOnlyList<ContentBlock> blocks)
    {
        if (blocks.Count == 0) return "";
        if (blocks.Count == 1) return Render(blocks[0]);

        var sb = new StringBuilder();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.Append(Render(blocks[i]));
        }
        return sb.ToString();
    }

    private static string RenderCode(CodeContentBlock block)
    {
        var sb = new StringBuilder();
        if (block.Path is not null)
            sb.AppendLine($"// {block.Path}");
        if (block.Language is not null)
            sb.AppendLine($"```{block.Language}");
        else
            sb.AppendLine("```");
        sb.Append(block.Code);
        if (!block.Code.EndsWith('\n'))
            sb.AppendLine();
        sb.Append("```");
        return sb.ToString();
    }

    private static string RenderFilePreview(FilePreviewContentBlock block)
    {
        if (block.IsBinary)
            return $"[FILE] {block.Path}  ({FormatSize.Format(block.Size)}, binary)";

        var lineInfo = block.LineCount.HasValue
            ? $"{block.LineCount} lines"
            : "";

        var sb = new StringBuilder();
        sb.AppendLine($"[FILE] {block.Path}  ({FormatSize.Format(block.Size)}" +
            (lineInfo.Length > 0 ? $", {lineInfo}" : "") + ")");
        sb.Append(block.Preview);
        return sb.ToString();
    }

    private static string RenderError(ErrorContentBlock block)
    {
        var sb = new StringBuilder();
        sb.Append($"Error: {block.Message}");
        if (block.Details is not null)
            sb.Append($"\nDetails: {block.Details}");
        return sb.ToString();
    }

    private static string RenderToolCall(ToolCallContentBlock block)
    {
        var args = block.Arguments is not null && block.Arguments.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(block.Arguments)
            : "";
        return $"Calling {block.ToolName}({args})...";
    }

}

