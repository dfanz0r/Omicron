using Omicron.Core.Content;

namespace Omicron.Core.Workspace;

/// <summary>
/// Identifier for a workspace instance.
/// </summary>
public readonly record struct WorkspaceId(Guid Value)
{
    public static WorkspaceId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Options for reading a file path.
/// </summary>
public sealed record ReadOptions
{
    public int? Offset { get; init; }
    public int? Limit { get; init; }
    public int? Chunk { get; init; }
}

/// <summary>
/// Result of a workspace read operation.
/// Blocks are lazily computed from the same underlying content so callers
/// that only need <see cref="Content"/> don't pay for block construction.
/// </summary>
public sealed record WorkspaceReadResult(
    string Content,
    bool IsDirectory,
    bool IsBinary,
    bool Truncated,
    Lazy<List<IContentBlock>>? Blocks = null);

/// <summary>
/// Abstraction for file system access within a workspace root.
/// </summary>
public interface IWorkspace
{
    /// <summary>Workspace identity.</summary>
    WorkspaceId Id { get; }

    /// <summary>Root directory of the workspace.</summary>
    string RootPath { get; }

    /// <summary>
    /// Resolve a path relative to the workspace root, ensuring it does not escape.
    /// Returns the full resolved path, or null if the path escapes.
    /// </summary>
    string? ResolvePath(string raw);

    /// <summary>
    /// Read a file or list a directory within the workspace.
    /// Path resolution must prevent traversal outside the root.
    /// </summary>
    Task<WorkspaceReadResult> ReadPathAsync(string path, ReadOptions? options = null, CancellationToken ct = default);
}
