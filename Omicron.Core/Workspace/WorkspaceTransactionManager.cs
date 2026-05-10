namespace Omicron.Core.Workspace;

/// <summary>
/// Creates workspace transactions backed by the host file system.
/// </summary>
public interface IWorkspaceTransactionManager
{
    /// <summary>Begin a new workspace transaction.</summary>
    IWorkspaceTransaction BeginTransaction();
}

/// <summary>
/// Default implementation backed by HostWorkspaceFileSystem.
/// </summary>
public sealed class WorkspaceTransactionManager : IWorkspaceTransactionManager
{
    private readonly HostWorkspaceFileSystem _host;

    public WorkspaceTransactionManager(HostWorkspaceFileSystem host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public IWorkspaceTransaction BeginTransaction()
    {
        return new WorkspaceTransaction(_host);
    }
}
