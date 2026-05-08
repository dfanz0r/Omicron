namespace Omicron.Core.Permissions;

/// <summary>
/// The result of a permission request.
/// </summary>
public sealed record PermissionDecision(bool Allowed, string? Reason = null);

/// <summary>
/// A request for permission to perform an action.
/// </summary>
public sealed record PermissionRequest(
    string Action,
    string? Target,
    string? Description);

/// <summary>
/// Permission service seam. Controls whether tools, execution, file access,
/// and other operations are allowed.
/// </summary>
public interface IPermissionService
{
    /// <summary>
    /// Request permission for an action. Returns whether the action is allowed.
    /// </summary>
    Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken ct = default);
}

/// <summary>
/// Default implementation that allows all actions. Suitable for MVP use.
/// </summary>
public sealed class AllowAllPermissionService : IPermissionService
{
    public Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken ct = default)
        => Task.FromResult(new PermissionDecision(Allowed: true));
}
