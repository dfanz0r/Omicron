namespace Omicron.Core.Diff;

// ============================================================
// Options
// ============================================================

/// <summary>
/// Options controlling diff behavior.
/// </summary>
internal sealed record TextDiffOptions
{
    public static TextDiffOptions Default { get; } = new();

    /// <summary>Trim whitespace from both ends of each line before comparison.</summary>
    public bool TrimWhitespace { get; init; }

    /// <summary>Collapse multiple whitespace characters to a single space before comparison.</summary>
    public bool IgnoreWhitespaceRuns { get; init; }

    /// <summary>Ignore case when comparing lines.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>Maximum number of lines for exact diff. 0 = no limit.
    /// Exceeding this returns an explicit omitted/truncated status.
    /// The facade selects strategy based on input size internally.</summary>
    public int MaxLineCount { get; init; }
}

// ============================================================
// Edit script
// ============================================================

/// <summary>
/// A single edit operation: insertion, deletion, or replacement.
/// All indexes are 0-based line numbers.
/// </summary>
internal readonly record struct TextDiffEdit(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount)
{
    public bool IsInsert => OldCount == 0 && NewCount > 0;
    public bool IsDelete => OldCount > 0 && NewCount == 0;
    public bool IsReplace => OldCount > 0 && NewCount > 0;
}

/// <summary>
/// Result of diffing two line sequences.
/// IsTruncated indicates the result is partial (input exceeded limits).
/// </summary>
internal sealed record TextDiffResult(
    IReadOnlyList<TextDiffEdit> Edits,
    int OldLineCount,
    int NewLineCount,
    bool IsTruncated = false,
    string? TruncationReason = null)
{
    public bool HasChanges => Edits.Count > 0;
}

// ============================================================
// Hunks (context-aware rendering model)
// ============================================================

/// <summary>
/// A diff hunk with surrounding context lines.
/// </summary>
internal sealed record TextDiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<TextDiffLine> Lines);

/// <summary>
/// A single line in a diff hunk.
/// </summary>
internal enum TextDiffLineKind
{
    Context,
    Removed,
    Added
}

/// <summary>
/// A single line in a diff hunk with its kind and original line numbers.
/// </summary>
internal sealed record TextDiffLine(
    TextDiffLineKind Kind,
    int? OldLineNumber,
    int? NewLineNumber,
    string Text);
