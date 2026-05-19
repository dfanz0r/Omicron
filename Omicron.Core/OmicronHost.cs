using Omicron.Core.Commands;
using Omicron.Core.Content;
using Omicron.Core.Events;
using Omicron.Core.Execution;
using Omicron.Core.Extensions;
using Omicron.Core.IO;
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

    /// <summary>Content processor registry for model-aware binary file reading.</summary>
    public ContentProcessorRegistry Processors { get; }

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

        // Initialize content processor registry (processors with missing dependencies are skipped)
        Processors = CreateContentProcessorRegistry();
    }

    /// <summary>
    /// Create the default content processor registry with all built-in processors.
    /// Processors whose NuGet packages are not available are silently skipped.
    /// </summary>
    private static ContentProcessorRegistry CreateContentProcessorRegistry()
    {
        var reg = new ContentProcessorRegistry();
        reg.Register(new TextProcessor());
        reg.Register(new HexDumpProcessor());
        RegisterProcessor(reg, new ImageProcessor());
        RegisterProcessor(reg, new PdfProcessor());
        RegisterProcessor(reg, new AudioProcessor());
        RegisterProcessor(reg, new VideoProcessor());
        RegisterProcessor(reg, new OpenXmlProcessor());
        RegisterProcessor(reg, new LegacyOfficeProcessor());
        RegisterProcessor(reg, new CsvProcessor());
        RegisterProcessor(reg, new EmailProcessor());
        RegisterProcessor(reg, new ArchiveProcessor());
        RegisterProcessor(reg, new NotebookProcessor());
        RegisterProcessor(reg, new EbookProcessor());
        RegisterProcessor(reg, new SvgProcessor());
        return reg;
    }

    /// <summary>
    /// Register a processor. All built-in processors are pure C# with no
    /// external dependencies at construction time, so no try/catch needed.
    /// Future dynamic plugins should use a dedicated factory abstraction.
    /// </summary>
    private static void RegisterProcessor(ContentProcessorRegistry reg, IContentProcessor processor)
    {
        reg.Register(processor);
    }

    /// <summary>
    /// Load built-in extensions. Called after construction to register
    /// the default C# extensions.
    /// </summary>
    public void LoadBuiltinExtensions()
    {
        Extensions.Register(new BuiltinToolsExtension());
        Extensions.Register(new BuiltinWorkspaceToolsExtension(Workspace, FileSystem, WorkspaceTransactions, Processors, Events));
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

        // Validate modality compatibility when switching models
        var targetMeta = ModelCatalog.GetMetadata(request.Model)
                        ?? BuildFallbackMetadata(request.Model);
        var errors = CheckModalityCompatibility(events, targetMeta);
        if (errors.Count > 0)
        {
            var msg = FormatModalityErrors(request.Model.Id, errors);
            throw new InvalidOperationException(msg);
        }

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

        // Validate modality compatibility when forking to a different model
        var forkTargetMeta = ModelCatalog.GetMetadata(request.Model)
                            ?? BuildFallbackMetadata(request.Model);
        var forkErrors = CheckModalityCompatibility(events, forkTargetMeta);
        if (forkErrors.Count > 0)
        {
            var forkMsg = FormatModalityErrors(request.Model.Id, forkErrors);
            throw new InvalidOperationException(forkMsg);
        }

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


    /// <summary>
    /// Result of a modality compatibility check.
    /// </summary>
    public sealed record ModalityValidationError(
        string Modality,
        IReadOnlyList<string> AffectedFiles);

    /// <summary>
    /// Check that all modalities used in a session are supported by the target model.
    /// Returns an empty list when compatible.
    /// </summary>
    internal static IReadOnlyList<ModalityValidationError> CheckModalityCompatibility(
        List<OmicronEvent> events,
        ModelMetadata targetModel)
    {
        var modalityEvents = events.OfType<ModalityUsedEvent>().ToList();
        if (modalityEvents.Count == 0)
            return Array.Empty<ModalityValidationError>();

        var byModality = modalityEvents.GroupBy(e => e.Modality);
        var errors = new List<ModalityValidationError>();

        foreach (var group in byModality)
        {
            // Use SupportsImages for "image" to also check SupportsVision boolean
            bool supported = group.Key switch
            {
                "image" => targetModel.SupportsImages(),
                _ => targetModel.SupportsModality(group.Key)
            };

            if (!supported)
            {
                errors.Add(new ModalityValidationError(
                    group.Key,
                    group.Select(e => e.RelativePath).Distinct().ToList()));
            }
        }

        return errors;
    }

    /// <summary>
    /// Format modality compatibility errors into a human-readable diagnostic.
    /// </summary>
    private static ModelMetadata BuildFallbackMetadata(Model model)
    {
        var modalities = new HashSet<string> { "text" };
        if (model.SupportsImages) modalities.Add("image");
        return new ModelMetadata(model.Id, model.ProviderName,
            SupportsVision: model.SupportsImages ? true : null,
            Modalities: modalities);
    }


    private static string FormatModalityErrors(string modelId, IReadOnlyList<ModalityValidationError> errors)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Cannot switch session to model {modelId}.");
        sb.AppendLine("The source session used modalities not supported by the target model:");
        foreach (var error in errors)
        {
            sb.AppendLine($"  - {error.Modality} ({error.AffectedFiles.Count} file(s): {string.Join(", ", error.AffectedFiles)})");
        }
        sb.AppendLine();
        sb.AppendLine("Choose a model that supports these modalities, or continue with the current model.");
        return sb.ToString();
    }


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
                        msg.TextData ?? Utf8String.Empty));
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
                            msg.TextData ?? Utf8String.Empty,
                            msg.ReasoningData,
                            new TokenUsage(0, 0)));
                        break;
                    }

                case MessageRole.ToolResult:
                    replayEvents.Add(new ToolInvocationCompletedEvent(
                        EventEnvelope.ForSession(session.Id),
                        new ToolCallId(msg.ToolCallId ?? ""),
                        msg.ToolName ?? "",
                        msg.TextData ?? Utf8String.Empty,
                        msg.IsError));
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
