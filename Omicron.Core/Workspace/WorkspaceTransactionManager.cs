using Omicron.Core.Events;

namespace Omicron.Core.Workspace;

/// <summary>
///     Creates workspace transactions backed by the host file system.
///     Optionally receives an event sink for transaction lifecycle events.
/// </summary>
public interface IWorkspaceTransactionManager
{
    /// <summary>Begin a new workspace transaction.</summary>
    IWorkspaceTransaction BeginTransaction();
}

/// <summary>
///     Default implementation backed by HostWorkspaceFileSystem.
/// </summary>
public sealed class WorkspaceTransactionManager : IWorkspaceTransactionManager
{
    private readonly IEventSink? _eventSink;
    private readonly HostWorkspaceFileSystem _host;

    public WorkspaceTransactionManager(HostWorkspaceFileSystem host, IEventSink? eventSink = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _eventSink = eventSink;
    }

    public IWorkspaceTransaction BeginTransaction()
    {
        return new WorkspaceTransaction(_host, _eventSink);
    }
}
