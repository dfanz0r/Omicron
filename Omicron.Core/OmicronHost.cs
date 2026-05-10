using Omicron.Core.Commands;
using Omicron.Core.Events;
using Omicron.Core.Execution;
using Omicron.Core.Extensions;
using Omicron.Core.Models;
using Omicron.Core.Permissions;
using Omicron.Core.Providers;
using Omicron.Core.Sessions;
using Omicron.Core.Tools;
using Omicron.Core.Workspace;

namespace Omicron.Core;

/// <summary>
/// Central composition root for Omicron's core services.
/// The CLI and other frontends consume this host rather than
/// manually wiring services together.
/// </summary>
public sealed class OmicronHost : IDisposable
{
    private bool _disposed;
    private readonly bool _ownsHttpClient;

    /// <summary>Extension registry — extensions contribute tools, commands, etc.</summary>
    public IExtensionRegistry Extensions { get; }

    /// <summary>Provider registry — LLM provider instances.</summary>
    public IProviderRegistry Providers { get; }

    /// <summary>Tool registry — tools available to the LLM agent.</summary>
    public IToolRegistry Tools { get; }

    /// <summary>Command registry — user/frontend commands.</summary>
    public ICommandRegistry Commands { get; }

    /// <summary>Permission service — controls access to operations.</summary>
    public IPermissionService Permissions { get; }

    /// <summary>Workspace — file system access abstraction (rich read/directory).</summary>
    public IWorkspace Workspace { get; }

    /// <summary>Workspace VFS — low-level file operations with path containment.</summary>
    public IWorkspaceFileSystem FileSystem { get; }

    /// <summary>Workspace transaction manager — creates transactions over the host file system.</summary>
    public IWorkspaceTransactionManager WorkspaceTransactions { get; }

    /// <summary>Execution broker — shell command execution.</summary>
    public IExecutionBroker Execution { get; }

    /// <summary>Model catalog — model discovery and lookup.</summary>
    public IModelCatalog ModelCatalog { get; }

    /// <summary>Event sink — receives all system events.</summary>
    public IEventSink Events { get; }

    /// <summary>In-memory event log for testing/replay.</summary>
    public InMemoryEventSink EventLog { get; }

    /// <summary>Provider state manager — wraps the store and emits events.</summary>
    public IProviderStateManager ProviderStateManager { get; }

    /// <summary>Raw provider conversation state store (for direct store access).</summary>
    public IProviderConversationStateStore ProviderState { get; }

    /// <summary>Session store for persistence. Set at construction and not replacable — Events captures it.</summary>
    public ISessionStore SessionStore { get; }

    private readonly HttpClient _metadataHttp;

    /// <summary>
    /// Creates a new OmicronHost with the default service implementations.
    /// InMemoryEventSink (EventLog) is the primary event bus.
    /// Events wraps EventLog with PersistentEventSink so all producers
    /// (AgentSession, ProviderStateManager, LocalExecutionBroker)
    /// write stamped events to the ISessionStore.
    /// </summary>
    /// <param name="workspaceRoot">Root directory for workspace file access.</param>
    /// <param name="sessionStore">Optional session store for event persistence. Defaults to InMemorySessionStore.</param>
    /// <param name="httpClient">Optional HttpClient for metadata fetching. A new one is created if not provided.</param>
    public OmicronHost(string workspaceRoot, ISessionStore? sessionStore = null, HttpClient? httpClient = null)
    {
        EventLog = new InMemoryEventSink();
        _ownsHttpClient = httpClient is null;
        _metadataHttp = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        SessionStore = sessionStore ?? new InMemorySessionStore();
        Events = new PersistentEventSink(EventLog, SessionStore);

        Tools = new ToolRegistry();
        Commands = new CommandRegistry();
        Permissions = new AllowAllPermissionService();
        FileSystem = new HostWorkspaceFileSystem(workspaceRoot);
        Workspace = new VfsWorkspaceAdapter(FileSystem);
        WorkspaceTransactions = new WorkspaceTransactionManager((HostWorkspaceFileSystem)FileSystem, Events);
        Execution = new LocalExecutionBroker(Events);
        ProviderState = new InMemoryProviderConversationStateStore();
        ProviderStateManager = new ProviderStateManager(ProviderState, Events);
        Providers = new ProviderFactory();
        ModelCatalog = new ModelCatalogService(Providers);
        Extensions = new ExtensionRegistry(Tools, Commands);
    }

    /// <summary>
    /// Load built-in extensions. Called after construction to register
    /// the default C# extensions.
    /// </summary>
    public void LoadBuiltinExtensions()
    {
        Extensions.Register(new BuiltinToolsExtension());
        Extensions.Register(new BuiltinWorkspaceToolsExtension(Workspace, FileSystem, WorkspaceTransactions));
        Extensions.Register(new BuiltinExecutionToolsExtension(Execution, Workspace));
    }

    // ============================================================
    // Model metadata refresh
    // ============================================================

    /// <summary>
    /// Refresh model metadata from the default sources (static + models.dev).
    /// Best-effort: propagates cancellation but fails closed on errors.
    /// Returns the number of metadata entries loaded.
    /// </summary>
    public async ValueTask<int> RefreshModelMetadataAsync(CancellationToken ct = default)
    {
        var sources = new IModelMetadataSource[]
        {
            new StaticModelMetadataSource(ModelCatalog),
            new ModelsDevMetadataSource(_metadataHttp)
        };

        if (ModelCatalog is ModelCatalogService svc)
            return await svc.RefreshMetadataAsync(sources, ct);

        return 0;
    }

    // ============================================================
    // Session creation — async-first
    // ============================================================

    /// <summary>
    /// Create a new agent session from a <see cref="SessionConfig"/>.
    /// Async-first API: the store I/O is naturally async.
    /// </summary>
    public async ValueTask<AgentSession> CreateSessionAsync(
        SessionConfig config,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var session = new AgentSession(
            config,
            Tools,
            Permissions,
            Events,
            ProviderStateManager);

        var request = new SessionCreateRequest(
            config.Model.Id,
            config.Model.ProviderName,
            config.Model.ApiType,
            SessionId: session.Id,
            SystemPrompt: config.SystemPrompt);
        await SessionStore.CreateSessionAsync(request, ct);

        return session;
    }

    /// <summary>
    /// Sync convenience wrapper over <see cref="CreateSessionAsync"/>.
    /// </summary>
    public AgentSession CreateSession(SessionConfig config)
        => CreateSessionAsync(config).GetAwaiter().GetResult();

    /// <summary>
    /// Legacy parameter-list overload — delegates to <see cref="CreateSession(SessionConfig)"/>.
    /// Prefer constructing a <see cref="SessionConfig"/> explicitly.
    /// </summary>
    public AgentSession CreateSession(
        Model model,
        string? systemPrompt = null,
        string? apiKey = null,
        int? maxTokens = null,
        double? temperature = null,
        int maxIterations = 100)
        => CreateSession(SessionConfig.Create(model, systemPrompt, apiKey, maxTokens, temperature, maxIterations: maxIterations));

    // ============================================================
    // Session resume / fork
    // ============================================================

    /// <summary>
    /// Resume a persisted session: hydrate from stored events, restore transcript.
    /// Uses the same session ID and restores provider state when safe (same model/provider/API).
    /// </summary>
    public async Task<AgentSession?> ResumeSessionAsync(
        SessionResumeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Model);
        var record = await SessionStore.GetSessionAsync(request.SessionId, ct);
        if (record is null) return null;

        var events = new List<OmicronEvent>();
        await foreach (var e in SessionStore.ReadEventsAsync(request.SessionId, EventSequenceRange.All, ct))
            events.Add(e);

        var projector = new SessionProjector();
        var projection = projector.Project(events);

        bool sameProvider = string.Equals(record.ProviderName, request.Model.ProviderName, StringComparison.OrdinalIgnoreCase);
        bool restoreProvider = sameProvider && record.ModelId == request.Model.Id && record.ApiType == request.Model.ApiType;

        var config = SessionConfig.Create(request.Model, record.SystemPrompt, request.ApiKey);
        return AgentSession.FromProjection(
            projection, config, request.SessionId, Tools, Permissions, Events, ProviderStateManager, restoreProvider);
    }

    /// <summary>
    /// Legacy overload — convenience for callers that prefer individual parameters.
    /// </summary>
    public Task<AgentSession?> ResumeSessionAsync(
        SessionId sessionId,
        Model model,
        string? apiKey = null,
        CancellationToken ct = default)
        => ResumeSessionAsync(new SessionResumeRequest(sessionId, model, apiKey), ct);

    /// <summary>
    /// Fork a persisted session: replay transcript into a new session.
    /// Always clears provider state. Allows model/provider change.
    /// </summary>
    public async Task<AgentSession?> ForkSessionAsync(
        SessionForkRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Model);
        var events = new List<OmicronEvent>();
        await foreach (var e in SessionStore.ReadEventsAsync(request.SourceSessionId, EventSequenceRange.All, ct))
            events.Add(e);

        var projector = new SessionProjector();
        var projection = projector.Project(events);

        var config = SessionConfig.Create(request.Model, request.SystemPrompt, request.ApiKey);
        var session = AgentSession.FromProjection(
            projection, config, null, Tools, Permissions, Events, ProviderStateManager, restoreProviderState: false);

        var sourceRecord = await SessionStore.GetSessionAsync(request.SourceSessionId, ct);
        var forkSystemPrompt = request.SystemPrompt ?? sourceRecord?.SystemPrompt;
        var createRequest = new SessionCreateRequest(
            request.Model.Id,
            request.Model.ProviderName,
            request.Model.ApiType,
            SessionId: session.Id,
            SystemPrompt: forkSystemPrompt);
        await SessionStore.CreateSessionAsync(createRequest, ct);

        var replayEvents = BuildForkReplayEvents(session, request.Model, projection);
        Events.EmitBatch(replayEvents);

        return session;
    }

    /// <summary>
    /// Legacy overload — convenience for callers that prefer individual parameters.
    /// </summary>
    public Task<AgentSession?> ForkSessionAsync(
        SessionId sourceId,
        Model model,
        string? systemPrompt = null,
        string? apiKey = null,
        CancellationToken ct = default)
        => ForkSessionAsync(new SessionForkRequest(sourceId, model, systemPrompt, apiKey), ct);

    private static List<OmicronEvent> BuildForkReplayEvents(AgentSession session, Model model, SessionProjection projection)
    {
        var replayEvents = new List<OmicronEvent>();
        replayEvents.Add(new SessionStartedEvent(
            EventEnvelope.ForSession(session.Id),
            session.AgentId, model.Id, model.ProviderName));

        foreach (var msg in projection.Messages)
        {
            switch (msg.Role)
            {
                case MessageRole.User:
                    replayEvents.Add(new UserMessageEvent(
                        EventEnvelope.ForSession(session.Id),
                        msg.Text ?? ""));
                    break;

                case MessageRole.Assistant:
                {
                    var toolCalls = msg.ToolCalls ?? (msg.ToolCall is not null ? new List<ToolCallContent> { msg.ToolCall } : null);
                    if (toolCalls is { Count: > 0 })
                    {
                        foreach (var tc in toolCalls)
                        {
                            replayEvents.Add(new ToolInvocationStartedEvent(
                                EventEnvelope.ForSession(session.Id),
                                new ToolCallId(tc.Id), tc.Name,
                                tc.Arguments ?? new Dictionary<string, object?>()));
                        }
                    }

                    replayEvents.Add(new AssistantResponseCompleteEvent(
                        EventEnvelope.ForSession(session.Id),
                        msg.Text ?? "", msg.Reasoning, new TokenUsage(0, 0)));
                    break;
                }

                case MessageRole.ToolResult:
                    replayEvents.Add(new ToolInvocationCompletedEvent(
                        EventEnvelope.ForSession(session.Id),
                        new ToolCallId(msg.ToolCallId ?? ""),
                        msg.ToolName ?? "",
                        msg.Text ?? "", msg.IsError));
                    break;
            }
        }

        return replayEvents;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_ownsHttpClient)
                _metadataHttp?.Dispose();
        }
    }
}
