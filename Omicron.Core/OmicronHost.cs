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

    /// <summary>
    /// Creates a new OmicronHost with the default service implementations.
    /// </summary>
    public OmicronHost(string workspaceRoot)
    {
        // Infrastructure
        EventLog = new InMemoryEventSink();
        Events = EventLog;

        // Services
        Tools = new ToolRegistry();
        Commands = new CommandRegistry();
        Permissions = new AllowAllPermissionService();
        Workspace = new HostWorkspace(workspaceRoot);
        Execution = new LocalExecutionBroker(EventLog);
        ProviderState = new InMemoryProviderConversationStateStore();
        ProviderStateManager = new ProviderStateManager(ProviderState, EventLog);
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
        return session;
    }
}
