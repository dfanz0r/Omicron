namespace Omicron.Core.Content;

/// <summary>
/// Base record for all semantic content blocks.
/// Every block is frontend-neutral — no UI, terminal, or rendering concerns.
/// </summary>
public abstract record ContentBlock;

/// <summary>
/// Plain, unformatted text. No markdown, no special formatting.
/// </summary>
public sealed record PlainTextContentBlock(string Text) : ContentBlock;

/// <summary>
/// Markdown-formatted text. Future TUI/GUI renderers may parse
/// the markdown; the text renderer passes it through as-is.
/// </summary>
public sealed record MarkdownContentBlock(string Markdown) : ContentBlock;

/// <summary>
/// A code snippet with optional language hint and source path.
/// </summary>
public sealed record CodeContentBlock(
    string Code,
    string? Language = null,
    string? Path = null) : ContentBlock;

/// <summary>
/// A unified diff (patch) with optional source path.
/// </summary>
public sealed record DiffContentBlock(
    string UnifiedDiff,
    string? Path = null) : ContentBlock;

/// <summary>
/// A file preview — typically from a read_path or file read tool.
/// Carries the path, a text preview excerpt, size, and line count.
/// </summary>
public sealed record FilePreviewContentBlock(
    string Path,
    string Preview,
    long Size,
    int? LineCount,
    bool IsBinary) : ContentBlock;

/// <summary>
/// An error message with optional detail.
/// </summary>
public sealed record ErrorContentBlock(
    string Message,
    string? Details = null) : ContentBlock;

/// <summary>
/// A tool call invocation. Used for structured tool call display.
/// </summary>
public sealed record ToolCallContentBlock(
    string ToolCallId,
    string ToolName,
    IReadOnlyDictionary<string, object?>? Arguments = null) : ContentBlock;
