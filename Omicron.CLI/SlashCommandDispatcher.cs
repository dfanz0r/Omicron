using Omicron.Core;
using Omicron.Core.Config;
using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Sessions;

namespace Omicron.CLI;

// ============================================================
// Types
// ============================================================

internal enum ChatCommandAction
{
    Continue,
    ExitSession,
    ExitApp,
    ResetSession,
    ClearProviderState,
    SwitchModel
}

internal sealed record ChatCommandResult(
    ChatCommandAction Action = ChatCommandAction.Continue,
    string? Message = null,
    string? ModelKey = null);

internal sealed record SlashCommandContext(
    OmicronHost Host,
    AgentSession Session,
    Model Model,
    string ModelKey,
    AgentConfig Config,
    IModelCatalog Catalog);

internal enum ChatLoopExitReason
{
    ExitSession,
    ExitApp
}

internal sealed record ChatLoopResult(
    ChatLoopExitReason Reason,
    string? ModelKey = null);

// ============================================================
// Slash command dispatcher
// ============================================================

internal sealed class SlashCommandDispatcher
{
    private static readonly string[] CommandNames =
    [
        "/help",
        "/model",
        "/models",
        "/status",
        "/tools",
        "/events",
        "/provider-state",
        "/clear-state",
        "/reset",
        "/exit",
        "/quit"
    ];

    /// <summary>Return possible completions for the current input.</summary>
    public IReadOnlyList<string> GetCompletions(string input, SlashCommandContext context)
    {
        var trimmed = input.TrimStart();
        if (trimmed.Length == 0)
            return CommandNames;

        if (!trimmed.StartsWith('/'))
            return [];

        if (trimmed.StartsWith("/model ", StringComparison.OrdinalIgnoreCase))
        {
            var arg = trimmed[7..].TrimStart();
            return context.Catalog.Models
                .Where(kv => HasConfiguredProviderAccess(context.Config, context.Catalog, kv.Key, kv.Value))
                .Select(kv => kv.Key)
                .Where(key => key.StartsWith(arg, StringComparison.OrdinalIgnoreCase))
                .Select(key => "/model " + key)
                .Take(25)
                .ToList();
        }

        return CommandNames
            .Where(c => c.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Check if input starts with a slash or is a bare alias.</summary>
    public bool IsCommand(string input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        if (input.StartsWith('/')) return true;

        // Bare aliases
        var lower = input.Trim().ToLowerInvariant();
        return lower is "exit" or "reset" or "?" or "quit";
    }

    /// <summary>
    /// Execute a slash command and return a result.
    /// Handlers print their own output for simplicity.
    /// </summary>
    public ChatCommandResult Execute(string input, SlashCommandContext context)
    {
        var trimmed = input.Trim();
        var lower = trimmed.ToLowerInvariant();

        // Normalize: strip leading slash for command matching.
        // A bare "/" should not crash; treat it as help.
        var cmdText = trimmed.StartsWith('/') ? trimmed[1..] : trimmed;
        if (string.IsNullOrWhiteSpace(cmdText))
            return ShowHelp();

        var parts = cmdText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1] : "";

        switch (command)
        {
            case "help":
            case "?":
                return ShowHelp();

            case "model":
                return string.IsNullOrWhiteSpace(args)
                    ? ShowModel(context)
                    : SwitchModel(context, args);

            case "models":
                return ShowModels(context);

            case "status":
                return ShowStatus(context);

            case "tools":
                return ShowTools(context);

            case "events":
                return ShowEvents(context, args);

            case "provider-state":
                return ShowProviderState(context);

            case "clear-state":
                return ClearProviderState(context);

            case "reset":
                return ResetSession(context);

            case "exit":
                return new ChatCommandResult(ChatCommandAction.ExitApp, "Goodbye!");

            case "quit":
                return new ChatCommandResult(ChatCommandAction.ExitApp, "Goodbye!");

            default:
                Console.WriteLine($"  Unknown command: /{command}. Type /help for available commands.");
                return new ChatCommandResult(ChatCommandAction.Continue);
        }
    }

    private static ChatCommandResult ShowHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  Slash Commands:");
        Console.WriteLine("    /help               Show this help");
        Console.WriteLine("    /model              Show current model/provider/API type");
        Console.WriteLine("    /models             List available models");
        Console.WriteLine("    /model <n|key>      Switch to model by list number or catalog key");
        Console.WriteLine("    /status             Show current session, model, and provider-state summary");
        Console.WriteLine("    /tools              List registered tools");
        Console.WriteLine("    /events [n]         Show recent session events (default 20)");
        Console.WriteLine("    /provider-state     Show provider state for current session/model");
        Console.WriteLine("    /clear-state        Clear provider state for current session/model only");
        Console.WriteLine("    /reset              Reset the conversation and all provider state");
        Console.WriteLine("    /exit               Exit the application");
        Console.WriteLine("    /quit               Exit the application");
        Console.WriteLine();
        Console.WriteLine("  Aliases:");
        Console.WriteLine("    ?                   Same as /help");
        Console.WriteLine("    exit                Same as /exit");
        Console.WriteLine("    reset               Same as /reset");
        Console.WriteLine("    quit                Same as /quit");
        Console.WriteLine();
        return new ChatCommandResult(ChatCommandAction.Continue);
    }

    private static ChatCommandResult ShowModel(SlashCommandContext context)
    {
        var m = context.Model;
        Console.WriteLine();
        Console.WriteLine($"  Model:    {m.Name} ({m.Id})");
        Console.WriteLine($"  Provider: {m.ProviderName}");
        Console.WriteLine($"  API Type: {m.ApiType}");
        Console.WriteLine($"  Base URL: {m.BaseUrl}");
        Console.WriteLine($"  Context:  {m.ContextWindow:N0} tokens");
        Console.WriteLine($"  Supports reasoning: {m.SupportsReasoning}");
        var compat = m.GetEffectiveCompatibility();
        Console.WriteLine($"  Supports store: {compat.SupportsStore}");
        Console.WriteLine($"  Supports previous_response_id: {compat.SupportsPreviousResponseId}");
        Console.WriteLine($"  Storage policy: {m.StoragePolicy}");
        Console.WriteLine();
        return new ChatCommandResult(ChatCommandAction.Continue);
    }

    private static ChatCommandResult ShowModels(SlashCommandContext context)
    {
        var visible = GetVisibleModels(context).ToList();

        Console.WriteLine();
        if (visible.Count == 0)
        {
            Console.WriteLine("  No models available. Add an API key via /config in a future pass or restart and use config.");
            Console.WriteLine();
            return new ChatCommandResult(ChatCommandAction.Continue);
        }

        Console.WriteLine($"  Available models ({visible.Count}):");
        for (int i = 0; i < visible.Count; i++)
        {
            var (key, m) = visible[i];
            var marker = key == context.ModelKey ? "*" : " ";
            var free = context.Catalog.IsFreeModel(key) && !HasProviderEnvironmentKey(m.ProviderName) && !context.Config.ApiKeys.ContainsKey(m.ProviderName)
                ? "free"
                : "    ";
            Console.WriteLine($"    [{i + 1,2}] {marker} {free} {m.Name,-36} {m.ProviderName,-12} {m.ApiType,-16} {key}");
        }
        Console.WriteLine();
        Console.WriteLine("  Switch with: /model <number>  or  /model <catalog-key>");
        Console.WriteLine();
        return new ChatCommandResult(ChatCommandAction.Continue);
    }

    private static ChatCommandResult SwitchModel(SlashCommandContext context, string arg)
    {
        var visible = GetVisibleModels(context).ToList();
        if (visible.Count == 0)
        {
            Console.WriteLine("  No models available.");
            return new ChatCommandResult(ChatCommandAction.Continue);
        }

        string? selectedKey = null;
        if (int.TryParse(arg.Trim(), out var index))
        {
            if (index < 1 || index > visible.Count)
            {
                Console.WriteLine($"  Invalid model number. Use /models to list available models.");
                return new ChatCommandResult(ChatCommandAction.Continue);
            }
            selectedKey = visible[index - 1].Key;
        }
        else
        {
            selectedKey = arg.Trim();
            if (!context.Catalog.Models.ContainsKey(selectedKey))
            {
                Console.WriteLine($"  Unknown model key: {selectedKey}. Use /models to list available models.");
                return new ChatCommandResult(ChatCommandAction.Continue);
            }
        }

        if (selectedKey == context.ModelKey)
        {
            Console.WriteLine("  Already using that model.");
            return new ChatCommandResult(ChatCommandAction.Continue);
        }

        return new ChatCommandResult(ChatCommandAction.SwitchModel, ModelKey: selectedKey);
    }

    private static IEnumerable<(string Key, Model Model)> GetVisibleModels(SlashCommandContext context)
    {
        return context.Catalog.Models
            .Where(kv => HasConfiguredProviderAccess(context.Config, context.Catalog, kv.Key, kv.Value))
            .OrderBy(kv => kv.Key == context.Config.LastModel ? 0 : 1)
            .ThenBy(kv => kv.Value.ProviderName)
            .ThenBy(kv => kv.Value.Name)
            .Select(kv => (kv.Key, kv.Value));
    }

    private static bool HasConfiguredProviderAccess(
        AgentConfig config,
        IModelCatalog catalog,
        string key,
        Model model)
    {
        return catalog.IsFreeModel(key)
            || config.ApiKeys.ContainsKey(model.ProviderName)
            || HasProviderEnvironmentKey(model.ProviderName);
    }

    private static bool HasProviderEnvironmentKey(string providerName)
    {
        var envVarName = providerName.ToLowerInvariant() switch
        {
            "openai" => "OPENAI_API_KEY",
            "anthropic" => "ANTHROPIC_API_KEY",
            "opencode" or "opencode-go" => "OPENCODE_API_KEY",
            "openrouter" => "OPENROUTER_API_KEY",
            _ => null
        };

        return envVarName is not null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVarName));
    }

    private static ChatCommandResult ShowStatus(SlashCommandContext context)
    {
        var session = context.Session;
        var m = context.Model;
        var host = context.Host;
        var compat = m.GetEffectiveCompatibility();
        var toolCount = host.Tools.AllTools.Count();

        // Count session events
        var events = host.EventLog.GetSessionEvents(session.Id);
        var eventCount = events.Count;

        Console.WriteLine();
        Console.WriteLine($"  Session:    {session.Id}");
        Console.WriteLine($"  Agent:      {session.AgentId}");
        Console.WriteLine($"  Model:      {m.ProviderName}: {m.Name}");
        Console.WriteLine($"  API:        {m.ApiType}");
        Console.WriteLine($"  Storage:    {m.StoragePolicy}");
        Console.WriteLine($"  Supports previous_response_id: {compat.SupportsPreviousResponseId}");
        Console.WriteLine($"  Tools:      {toolCount} registered");
        Console.WriteLine($"  Messages:   {session.Messages.Count}");
        Console.WriteLine($"  Events:     {eventCount}");
        Console.WriteLine();
        return new ChatCommandResult(ChatCommandAction.Continue);
    }

    private static ChatCommandResult ShowTools(SlashCommandContext context)
    {
        var tools = context.Host.Tools.AllTools;
        Console.WriteLine();
        if (!tools.Any())
        {
            Console.WriteLine("  No tools registered.");
        }
        else
        {
            Console.WriteLine($"  Registered tools ({tools.Count()}):");
            foreach (var tool in tools)
            {
                Console.WriteLine($"    - {tool.Name}: {tool.Description}");
            }
        }
        Console.WriteLine();
        return new ChatCommandResult(ChatCommandAction.Continue);
    }

    private static ChatCommandResult ShowEvents(SlashCommandContext context, string args)
    {
        // Parse count, default 20
        if (!int.TryParse(args, out var count) || count <= 0)
            count = 20;

        var events = context.Host.EventLog.GetSessionEvents(context.Session.Id);
        var recent = events.TakeLast(count).ToList();

        Console.WriteLine();
        if (recent.Count == 0)
        {
            Console.WriteLine("  No session events.");
        }
        else
        {
            Console.WriteLine($"  Recent events ({recent.Count} shown, {events.Count} total):");
            foreach (var evt in recent)
            {
                var label = evt.GetType().Name;
                var detail = GetEventDetail(evt, context);
                Console.WriteLine($"    #{evt.Sequence,-4} {label,-40} {detail}");
            }
        }
        Console.WriteLine();
        return new ChatCommandResult(ChatCommandAction.Continue);
    }

    private static string GetEventDetail(OmicronEvent evt, SlashCommandContext ctx) => evt switch
    {
        UserMessageEvent u => Truncate(u.Text, 60),
        AssistantTextDeltaEvent atd => Truncate(atd.Delta, 60),
        AssistantResponseCompleteEvent arc => $"{arc.InputTokens}↑ {arc.OutputTokens}↓",
        ToolInvocationStartedEvent tis => $"{tis.ToolName} {FormatArgs(tis.Arguments)}",
        ToolInvocationCompletedEvent tic => $"{tic.ToolName} {(tic.IsError ? "error" : "ok")}",
        SessionErrorEvent se => $"{se.Code}: {Truncate(se.Message, 60)}",
        ProviderStateUpdatedEvent psu => $"prev_resp_id={Truncate(psu.PreviousResponseId ?? "(none)", 30)} reason={psu.Reason}",
        ProviderStateClearedEvent psc => $"reason={psc.Reason}",
        SessionStartedEvent => ctx.Model.Name,
        TurnStartedEvent => Truncate(evt.ToString() ?? "", 60),
        _ => ""
    };

    private static ChatCommandResult ShowProviderState(SlashCommandContext context)
    {
        var session = context.Session;
        var m = context.Model;

        var key = ProviderStateKey.Create(
            session.Id,
            session.AgentId,
            m.ProviderName,
            m.Id,
            m.ApiType);

        var state = context.Host.ProviderStateManager.Get(key);

        Console.WriteLine();
        Console.WriteLine("  Provider state:");
        Console.WriteLine($"    Key:        {m.ProviderName} / {m.Id} / {m.ApiType}");
        Console.WriteLine($"    previous_response_id:  {state?.PreviousResponseId ?? "<none>"}");
        Console.WriteLine($"    conversation_id:       {state?.ConversationId ?? "<none>"}");
        Console.WriteLine($"    session_affinity:      {state?.SessionAffinityKey ?? "<none>"}");
        Console.WriteLine($"    storage_policy:        {m.StoragePolicy}");
        Console.WriteLine($"    state exists:          {state is not null}");
        Console.WriteLine();
        return new ChatCommandResult(ChatCommandAction.Continue);
    }

    private static ChatCommandResult ClearProviderState(SlashCommandContext context)
    {
        var session = context.Session;
        var m = context.Model;

        var key = ProviderStateKey.Create(
            session.Id,
            session.AgentId,
            m.ProviderName,
            m.Id,
            m.ApiType);

        context.Host.ProviderStateManager.Clear(key, reason: "slash_command_clear_state");
        Console.WriteLine("  Provider state cleared for current session/model.");
        return new ChatCommandResult(ChatCommandAction.Continue);
    }

    private static ChatCommandResult ResetSession(SlashCommandContext context)
    {
        context.Session.Reset();
        Console.WriteLine("  (Conversation and provider state reset)");
        return new ChatCommandResult(ChatCommandAction.Continue);
    }

    private static string FormatArgs(IReadOnlyDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0) return "";
        return string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= maxLength ? value : value[..(maxLength - 1)] + "\u2026";
    }
}
