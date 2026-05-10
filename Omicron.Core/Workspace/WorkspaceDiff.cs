namespace Omicron.Core.Workspace;

// ============================================================
// Types for workspace diffs and change tracking
// ============================================================

/// <summary>
/// Unique identifier for a workspace transaction.
/// </summary>
public readonly record struct WorkspaceTransactionId(Guid Value)
{
    public static WorkspaceTransactionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Kind of change in a workspace file diff.
/// </summary>
public enum WorkspaceChangeKind
{
    Modified,
    Added,
    Deleted,
    Moved
}

/// <summary>
/// Diff result for a single file in a workspace transaction.
/// </summary>
public sealed record WorkspaceFileDiff(
    WorkspacePath Path,
    WorkspaceChangeKind Kind,
    string? OldPath,
    string? TextDiff,
    bool IsBinary,
    bool IsDirectory);

/// <summary>
/// Complete diff result for a workspace transaction.
/// </summary>
public sealed record WorkspaceDiff(
    WorkspaceTransactionId TransactionId,
    IReadOnlyList<WorkspaceFileDiff> FileDiffs)
{
    public bool HasChanges => FileDiffs.Count > 0;
}
