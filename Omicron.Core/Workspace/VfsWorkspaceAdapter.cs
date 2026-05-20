using Omicron.Core.Content;

namespace Omicron.Core.Workspace;

/// <summary>
///     Adapts IWorkspaceFileSystem → IWorkspace by composing a read service
///     and an LLM text renderer. Contains no formatting logic itself.
/// </summary>
public sealed class VfsWorkspaceAdapter : IWorkspace
{
    private readonly IWorkspaceReadService _readService;
    private readonly IWorkspaceReadRenderer<WorkspaceReadResult> _renderer;
    private readonly IWorkspaceFileSystem _vfs;

    public VfsWorkspaceAdapter(IWorkspaceFileSystem vfs)
    {
        Id = WorkspaceId.New();
        _vfs = vfs ?? throw new ArgumentNullException(nameof(vfs));
        _readService = new WorkspaceReadService(vfs);
        _renderer = new WorkspaceLlmTextRenderer();
    }

    public WorkspaceId Id { get; }
    public string RootPath => _vfs.RootPath;

    public string? ResolvePath(string raw)
    {
        return _vfs.Resolve(raw)?.Value;
    }

    public async Task<WorkspaceReadResult> ReadPathAsync(
        string path,
        ReadOptions? options = null,
        CancellationToken ct = default)
    {
        WorkspaceReadContent content = await _readService.ReadAsync(path, options, ct);
        WorkspaceReadResult result = _renderer.Render(content);

        // Produce structured content blocks lazily — only when the caller
        // accesses .Blocks. This avoids building both string output and
        // structured blocks when only one representation is needed.
        var blocks = new Lazy<List<IContentBlock>>(() =>
            WorkspaceLlmTextRenderer.ToContentBlocks(content));

        return result with
        {
            Blocks = blocks
        };
    }
}
