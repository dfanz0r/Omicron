using Omicron.Core.Events;
using Omicron.Core.Models;

namespace Omicron.Core.Sessions;

/// <summary>
/// Parameters for resuming a persisted session.
/// </summary>
public sealed record SessionResumeRequest(
    SessionId SessionId,
    Model Model,
    string? ApiKey = null);

/// <summary>
/// Parameters for forking a persisted session into a new session.
/// </summary>
public sealed record SessionForkRequest(
    SessionId SourceSessionId,
    Model Model,
    string? SystemPrompt = null,
    string? ApiKey = null);
