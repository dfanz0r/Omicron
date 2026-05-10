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
public sealed class OmicronHost
{
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

    /// <summary>
    /// Creates a new OmicronHost with the default service implementations.
    /// InMemoryEventSink (EventLog) is the primary event bus.
    /// Events wraps EventLog with PersistentEventSink so all producers
    /// (AgentSession, ProviderStateManager, LocalExecutionBroker)
    /// write stamped events to the ISessionStore.
    /// </summary>
    /// <param name="workspaceRoot">Root directory for workspace file access.</param>
    /// <param name="sessionStore">Optional session store for event persistence. Defaults to InMemorySessionStore.</param>
    public OmicronHost(string workspaceRoot, ISessionStore? sessionStore = null)
    {
        // Infrastructure
        EventLog = new InMemoryEventSink();
        SessionStore = sessionStore ?? new InMemorySessionStore();

        // Wrap EventLog with PersistentEventSink so all event producers persist events.
        Events = new PersistentEventSink(EventLog, SessionStore);

        // Nested service producers use Events directly so their output is persisted
        Tools = new ToolRegistry();
        Commands = new CommandRegistry();
        Permissions = new AllowAllPermissionService();
        FileSystem = new HostWorkspaceFileSystem(workspaceRoot);
        Workspace = new VfsWorkspaceAdapter(FileSystem);
        WorkspaceTransactions = new WorkspaceTransactionManager((HostWorkspaceFileSystem)FileSystem);
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
        // Calculator and time tools
        Extensions.Register(new BuiltinToolsExtension());

        // Workspace tools (read_path via IWorkspace)
        Extensions.Register(new BuiltinWorkspaceToolsExtension(Workspace));

        // Execution tools (shell via IExecutionBroker)
        Extensions.Register(new BuiltinExecutionToolsExtension(Execution, Workspace));
    }

    /// <summary>
    /// Create a new agent session for a given model.
    /// Creates a session record in the store and returns the session.
    /// Events are automatically persisted through the host-level PersistentEventSink.
    /// </summary>
    public AgentSession CreateSession(
        Models.Model model,
        string? systemPrompt = null,
        string? apiKey = null)
    {
        var session = new AgentSession(
            model,
            Tools,
            Permissions,
            Events,
            ProviderStateManager,
            systemPrompt,
            apiKey);

        // Create session record in store so events can be persisted
        var request = new SessionCreateRequest(
            model.Id,
            model.ProviderName,
            model.ApiType,
            SessionId: session.Id,
            SystemPrompt: systemPrompt);
        SessionStore.CreateSessionAsync(request).GetAwaiter().GetResult();

        return session;
    }

    /// <summary>
    /// Resume a persisted session: hydrate from stored events, restore transcript.
    /// Uses the same session ID and restores provider state when safe (same model/provider/API).
    /// </summary>
    public async Task<AgentSession?> ResumeSessionAsync(
        SessionId sessionId,
        Model model,
        string? apiKey = null,
        CancellationToken ct = default)
    {
        var record = await SessionStore.GetSessionAsync(sessionId, ct);
        if (record is null) return null;

        var events = new List<OmicronEvent>();
        await foreach (var e in SessionStore.ReadEventsAsync(sessionId, EventSequenceRange.All, ct))
            events.Add(e);

        var projector = new SessionProjector();
        var projection = projector.Project(events);

        // Same provider/model/API → safe to restore provider state
        bool sameProvider = string.Equals(record.ProviderName, model.ProviderName, StringComparison.OrdinalIgnoreCase);
        bool restoreProvider = sameProvider && record.ModelId == model.Id && record.ApiType == model.ApiType;

        return AgentSession.FromProjection(
            projection, model, sessionId, Tools, Permissions, Events, ProviderStateManager,
            record.SystemPrompt, apiKey, restoreProvider);
    }

    /// <summary>
    /// Fork a persisted session: replay transcript into a new session.
    /// Always clears provider state. Allows model/provider change.
    /// </summary>
    public async Task<AgentSession?> ForkSessionAsync(
        SessionId sourceId,
        Model model,
        string? systemPrompt = null,
        string? apiKey = null,
        CancellationToken ct = default)
    {
        var events = new List<OmicronEvent>();
        await foreach (var e in SessionStore.ReadEventsAsync(sourceId, EventSequenceRange.All, ct))
            events.Add(e);

        var projector = new SessionProjector();
        var projection = projector.Project(events);

        // Fork creates a new session (new ID, no provider state)
        var session = AgentSession.FromProjection(
            projection, model, null, Tools, Permissions, Events, ProviderStateManager,
            systemPrompt, apiKey, restoreProviderState: false);

        // Create session record
        var sourceRecord = await SessionStore.GetSessionAsync(sourceId, ct);
        var forkSystemPrompt = systemPrompt ?? sourceRecord?.SystemPrompt;
        var request = new SessionCreateRequest(
            model.Id,
            model.ProviderName,
            model.ApiType,
            SessionId: session.Id,
            SystemPrompt: forkSystemPrompt);
        await SessionStore.CreateSessionAsync(request, ct);

        // Build replay events from projection — go through Events.EmitBatch
        // so PersistentEventSink stamps sequences and mirrors to EventLog
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
                    // Emit tool-call events if present
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

                    // Emit the response event (text may be empty for tool-call-only turns)
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

        // Emit through the host event sink for proper sequence stamping
        Events.EmitBatch(replayEvents);

        return session;
    }
}
