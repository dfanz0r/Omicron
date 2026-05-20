namespace Omicron.Core.Workspace;

/// <summary>
///     Base record for workspace read results.
///     Source-independent — can be produced from host VFS, transaction
///     overlays, snapshots, remote workspaces, or staged edit views.
/// </summary>
public abstract record WorkspaceReadContent
{
    /// <summary>The path as originally requested.</summary>
    public required string RequestedPath { get; init; }

    /// <summary>The resolved contained path, or null if resolution failed.</summary>
    public WorkspacePath? ResolvedPath { get; init; }
}

/// <summary>
///     A text file with its lines loaded.
/// </summary>
public sealed record WorkspaceFileContent : WorkspaceReadContent
{
    public required FileStat Stat { get; init; }
    public required IReadOnlyList<WorkspaceTextLine> Lines { get; init; }
    public int TotalLines { get; init; }
    public bool Truncated { get; init; }
    public int? NextOffset { get; init; }
}

/// <summary>
///     A binary file (not displayable as text).
/// </summary>
public sealed record WorkspaceBinaryFileContent : WorkspaceReadContent
{
    public required FileStat Stat { get; init; }
}

/// <summary>
///     A directory listing.
/// </summary>
public sealed record WorkspaceDirectoryContent : WorkspaceReadContent
{
    public required IReadOnlyList<DirectoryEntry> Entries { get; init; }
    public int TotalFileCount { get; init; }
    public int TotalDirCount { get; init; }
    public bool Truncated { get; init; }
}

/// <summary>
///     A read error (not found, escapes root, unreadable, etc.).
/// </summary>
public sealed record WorkspaceReadErrorContent : WorkspaceReadContent
{
    public WorkspaceReadErrorKind Kind { get; init; }
    public required string Message { get; init; }
}

/// <summary>
///     A single line from a text file.
/// </summary>
public sealed record WorkspaceTextLine(int Number, string Text);

/// <summary>
///     Kinds of read errors.
/// </summary>
public enum WorkspaceReadErrorKind
{
    EscapesRoot,
    NotFound,
    Unreadable,
    InvalidPath
}
