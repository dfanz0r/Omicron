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

    /// <summary>Workspace — file system access abstraction.</summary>
    public IWorkspace Workspace { get; }

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
        Workspace = new HostWorkspace(workspaceRoot);
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
}
