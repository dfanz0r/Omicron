using Omicron.Core;
using Omicron.Core.Config;
using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Providers;
using Omicron.Core.Sessions;
using Omicron.Core.Tools;

namespace Omicron.CLI;

internal enum ChatCommandAction
{
    Continue,
    ExitSession,
    ExitApp,
    ResetSession,
    ClearProviderState,
    SwitchModel,
    SwitchSession
}

internal sealed record ChatCommandResult(
    ChatCommandAction Action = ChatCommandAction.Continue,
    string? Message = null,
    string? ModelKey = null,
    AgentSession? NewSession = null,
    Model? NewModel = null,
    string? NewModelKey = null);

internal sealed record ChatLoopResult(
    ChatLoopExitReason Reason,
    string? ModelKey = null,
    AgentSession? NewSession = null,
    Model? NewModel = null,
    string? NewModelKey = null);

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
        "/persist",
        "/sessions",
        "/resume",
        "/fork",
        "/reset",
        "/exit",
        "/quit"
    ];

    public IReadOnlyList<string> GetCompletions(string input, SlashCommandContext context)
    {
        string trimmed = input.TrimStart();
        if (trimmed.Length == 0)
        {
            return CommandNames;
        }

        if (!trimmed.StartsWith('/'))
        {
            return [];
        }

        if (trimmed.StartsWith("/model ", StringComparison.OrdinalIgnoreCase))
        {
            string arg = trimmed[7..].TrimStart();
            return context
                .Catalog.Models.Where(kv =>
                    HasConfiguredProviderAccess(context.Config, context.Catalog, kv.Key, kv.Value))
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

    public bool IsCommand(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        if (input.StartsWith('/'))
        {
            return true;
        }

        string lower = input.Trim().ToLowerInvariant();
        return lower is "exit" or "reset" or "?" or "quit";
    }

    public ChatCommandResult Execute(string input, SlashCommandContext context)
    {
        string trimmed = input.Trim();
        string cmdText = trimmed.StartsWith('/') ? trimmed[1..] : trimmed;
        if (string.IsNullOrWhiteSpace(cmdText))
        {
            return ShowHelp();
        }

        string[] parts = cmdText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string command = parts[0].ToLowerInvariant();
        string args = parts.Length > 1 ? parts[1] : "";

        return command switch
        {
            "help" or "?" => ShowHelp(),
            "model" => string.IsNullOrWhiteSpace(args)
                ? ShowModel(context)
                : SwitchModel(context, args),
            "models" => ShowModels(context),
            "status" => ShowStatus(context),
            "tools" => ShowTools(context),
            "events" => ShowEvents(context, args),
            "provider-state" => ShowProviderState(context),
            "persist" => ShowPersistence(context),
            "clear-state" => ClearProviderState(context),
            "sessions" => ListSessions(context, args),
            "resume" => ResumeSession(context, args),
            "fork" => ForkSession(context, args),
            "reset" => ResetSession(context),
            "exit" or "quit" => new ChatCommandResult(ChatCommandAction.ExitApp, "Goodbye!"),
            _ => UnknownCommand(command)
        };
    }

    private static ChatCommandResult UnknownCommand(string command)
    {
        Console.WriteLine($"  Unknown command: /{command}. Type /help for available commands.");
        return new ChatCommandResult();
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
        Console.WriteLine("    /persist            Show session storage type and event counts");
        Console.WriteLine("    /sessions [n]       List persisted sessions (newest first)");
        Console.WriteLine("    /resume <id|n|-1>   Resume a persisted session; -1 means most recent");
        Console.WriteLine("    /fork <id|n|-1>     Fork a session [--model <model-key|n>] (-1 = most recent)");
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
        return new ChatCommandResult();
    }

    private static ChatCommandResult ShowModel(SlashCommandContext context)
    {
        Model m = context.Model;
        int? ctxWindow = context.Catalog.GetEffectiveContextWindow(m);
        int? maxOut = context.Catalog.GetEffectiveMaxOutputTokens(m);
        ModelMetadata? meta = context.Catalog.GetMetadata(m);

        Console.WriteLine();
        Console.WriteLine($"  Model:    {m.Name} ({m.Id})");
        Console.WriteLine($"  Provider: {m.ProviderName}");
        Console.WriteLine($"  API Type: {m.ApiType}");
        Console.WriteLine($"  Base URL: {m.BaseUrl}");
        Console.WriteLine($"  Context:  {FormatTokenCount(ctxWindow)}");
        Console.WriteLine($"  Max out:  {FormatTokenCount(maxOut)}");
        if (meta?.Source is not null)
        {
            Console.WriteLine($"  Metadata: {meta.Source}");
        }

        Console.WriteLine($"  Supports reasoning: {m.SupportsReasoning}");
        ProviderCompatibility compat = m.GetEffectiveCompatibility();
        Console.WriteLine($"  Supports store: {compat.SupportsStore}");
        Console.WriteLine($"  Supports previous_response_id: {compat.SupportsPreviousResponseId}");
        Console.WriteLine($"  Storage policy: {m.StoragePolicy}");
        Console.WriteLine();
        return new ChatCommandResult();
    }

    private static ChatCommandResult ShowModels(SlashCommandContext context)
    {
        var visible = GetVisibleModels(context).ToList();

        Console.WriteLine();
        if (visible.Count == 0)
        {
            Console.WriteLine(
                "  No models available. Add an API key via /config in a future pass or restart and use config.");
            Console.WriteLine();
            return new ChatCommandResult();
        }

        Console.WriteLine($"  Available models ({visible.Count}):");
        for (int i = 0; i < visible.Count; i++)
        {
            (string key, Model m) = visible[i];
            string marker = key == context.ModelKey ? "*" : " ";
            string free =
                context.Catalog.IsFreeModel(key)
                && !HasProviderEnvironmentKey(m.ProviderName)
                && !context.Config.ApiKeys.ContainsKey(m.ProviderName)
                    ? "free"
                    : "    ";
            int? ctxWindow = context.Catalog.GetEffectiveContextWindow(m);
            Console.WriteLine(
                $"    [{i + 1,2}] {marker} {free} {m.Name,-36} {m.ProviderName,-12} {m.ApiType,-16} {FormatTokenCountCompact(ctxWindow),-6} {key}");
        }

        Console.WriteLine();
        Console.WriteLine("  Switch with: /model <number>  or  /model <catalog-key>");
        Console.WriteLine();
        return new ChatCommandResult();
    }

    private static ChatCommandResult SwitchModel(SlashCommandContext context, string arg)
    {
        var visible = GetVisibleModels(context).ToList();
        if (visible.Count == 0)
        {
            Console.WriteLine("  No models available.");
            return new ChatCommandResult();
        }

        string? selectedKey = null;
        if (int.TryParse(arg.Trim(), out int index))
        {
            if (index < 1 || index > visible.Count)
            {
                Console.WriteLine("  Invalid model number. Use /models to list available models.");
                return new ChatCommandResult();
            }

            selectedKey = visible[index - 1].Key;
        }
        else
        {
            selectedKey = arg.Trim();
            if (!context.Catalog.Models.ContainsKey(selectedKey))
            {
                Console.WriteLine($"  Unknown model key: {selectedKey}. Use /models to list available models.");
                return new ChatCommandResult();
            }
        }

        if (selectedKey == context.ModelKey)
        {
            Console.WriteLine("  Already using that model.");
            return new ChatCommandResult();
        }

        return new ChatCommandResult(ChatCommandAction.SwitchModel, ModelKey: selectedKey);
    }

    private static IEnumerable<(string Key, Model Model)> GetVisibleModels(SlashCommandContext context)
    {
        return context
            .Catalog.Models.Where(kv =>
                HasConfiguredProviderAccess(context.Config, context.Catalog, kv.Key, kv.Value))
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
        string? envVarName = providerName.ToLowerInvariant() switch
        {
            "openai" => "OPENAI_API_KEY",
            "anthropic" => "ANTHROPIC_API_KEY",
            "opencode" or "opencode-go" => "OPENCODE_API_KEY",
            "openrouter" => "OPENROUTER_API_KEY",
            _ => null
        };
        return envVarName is not null
               && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVarName));
    }

    private static ChatCommandResult ShowStatus(SlashCommandContext context)
    {
        AgentSession session = context.Session;
        Model m = context.Model;
        OmicronHost host = context.Host;
        ProviderCompatibility compat = m.GetEffectiveCompatibility();
        int toolCount = host.Tools.AllTools.Count();
        IReadOnlyList<OmicronEvent> events = host.EventLog.GetSessionEvents(session.Id);
        int eventCount = events.Count;

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
        return new ChatCommandResult();
    }

    private static ChatCommandResult ShowTools(SlashCommandContext context)
    {
        IReadOnlyList<ToolDefinition> tools = context.Host.Tools.AllTools;

        Console.WriteLine();
        if (!tools.Any())
        {
            Console.WriteLine("  No tools registered.");
        }
        else
        {
            Console.WriteLine($"  Registered tools ({tools.Count()}):");
            foreach (ToolDefinition tool in tools)
            {
                Console.WriteLine($"    - {tool.Name}: {tool.Description}");
            }
        }

        Console.WriteLine();
        return new ChatCommandResult();
    }

    private static ChatCommandResult ShowEvents(SlashCommandContext context, string args)
    {
        if (!int.TryParse(args, out int count) || count <= 0)
        {
            count = 20;
        }

        IReadOnlyList<OmicronEvent> events = context.Host.EventLog.GetSessionEvents(context.Session.Id);
        var recent = events.TakeLast(count).ToList();

        Console.WriteLine();
        if (recent.Count == 0)
        {
            Console.WriteLine("  No session events.");
        }
        else
        {
            Console.WriteLine($"  Recent events ({recent.Count} shown, {events.Count} total):");
            foreach (OmicronEvent evt in recent)
            {
                string label = evt.GetType().Name;
                string detail = GetEventDetail(evt, context);
                Console.WriteLine($"    #{evt.Sequence,-4} {label,-40} {detail}");
            }
        }

        Console.WriteLine();
        return new ChatCommandResult();
    }

    private static string GetEventDetail(OmicronEvent evt, SlashCommandContext ctx)
    {
        return evt switch
        {
            UserMessageEvent u => Truncate(u.Text.ToString(), 60),
            AssistantTextDeltaEvent atd => Truncate(atd.Delta.ToString(), 60),
            AssistantResponseCompleteEvent arc =>
                $"{arc.Usage.InputTokens}↑ {arc.Usage.OutputTokens}↓",
            ToolInvocationStartedEvent tis => $"{tis.ToolName} {FormatArgs(tis.Arguments)}",
            ToolInvocationCompletedEvent tic => $"{tic.ToolName} {(tic.IsError ? "error" : "ok")}",
            SessionErrorEvent se => $"{se.Code}: {Truncate(se.Message, 60)}",
            ProviderStateUpdatedEvent psu =>
                $"prev_resp_id={Truncate(psu.State.PreviousResponseId ?? "(none)", 30)} reason={psu.Reason}",
            ProviderStateClearedEvent psc => $"reason={psc.Reason}",
            SessionStartedEvent => ctx.Model.Name,
            TurnStartedEvent => Truncate(evt.ToString() ?? "", 60),
            _ => ""
        };
    }

    private static ChatCommandResult ShowPersistence(SlashCommandContext context)
    {
        OmicronHost host = context.Host;
        ISessionStore store = host.SessionStore;
        string storeType = store.GetType().Name;

        Console.WriteLine();
        Console.WriteLine("  Session Storage:");
        Console.WriteLine($"    Store type:  {storeType}");
        if (store is JsonlSessionStore jsonl)
        {
            Console.WriteLine($"    Directory:   {jsonl.StoreDirectory}");
        }

        var sessions = store
            .ListSessionsAsync(new SessionListQuery(int.MaxValue))
            .GetAwaiter()
            .GetResult()
            .OrderByDescending(s => s.LastActivityAt ?? s.CreatedAt)
            .ToList();
        Console.WriteLine($"    Sessions:    {sessions.Count}");

        try
        {
            long eventCount = store.GetEventCountAsync(context.Session.Id).GetAwaiter().GetResult();
            Console.WriteLine($"    Events (current session): {eventCount}");
        }
        catch
        {
            Console.WriteLine("    Events (current session): (unavailable)");
        }

        Console.WriteLine();
        return new ChatCommandResult();
    }

    private static ChatCommandResult ShowProviderState(SlashCommandContext context)
    {
        AgentSession session = context.Session;
        Model m = context.Model;
        var key = ProviderStateKey.Create(session.Id,
            session.AgentId,
            m.ProviderName,
            m.Id,
            m.ApiType);
        ProviderTurnState? state = context.Host.ProviderStateManager.Get(key);

        Console.WriteLine();
        Console.WriteLine("  Provider state:");
        Console.WriteLine($"    Key:        {m.ProviderName} / {m.Id} / {m.ApiType}");
        Console.WriteLine($"    previous_response_id:  {state?.PreviousResponseId ?? "<none>"}");
        Console.WriteLine($"    conversation_id:       {state?.ConversationId ?? "<none>"}");
        Console.WriteLine($"    session_affinity:      {state?.SessionAffinityKey ?? "<none>"}");
        Console.WriteLine($"    storage_policy:        {m.StoragePolicy}");
        Console.WriteLine($"    state exists:          {state is not null}");
        Console.WriteLine();
        return new ChatCommandResult();
    }

    private static ChatCommandResult ClearProviderState(SlashCommandContext context)
    {
        AgentSession session = context.Session;
        Model m = context.Model;
        var key = ProviderStateKey.Create(session.Id,
            session.AgentId,
            m.ProviderName,
            m.Id,
            m.ApiType);
        context.Host.ProviderStateManager.Clear(key, "slash_command_clear_state");
        Console.WriteLine("  Provider state cleared for current session/model.");
        return new ChatCommandResult();
    }

    private static ChatCommandResult ListSessions(SlashCommandContext context, string args)
    {
        (int limit, int offset, SessionStatus? filter) = ParseSessionsArgs(args);
        IReadOnlyList<SessionRecord> sessions = context
            .Host.SessionStore.ListSessionsAsync(new SessionListQuery(int.MaxValue, 0, filter))
            .GetAwaiter()
            .GetResult();
        sessions = sessions.OrderByDescending(s => s.LastActivityAt ?? s.CreatedAt).ToList();

        if (offset < 0)
        {
            offset = Math.Max(0, sessions.Count + offset);
        }

        var page = sessions.Skip(offset).Take(limit).ToList();

        Console.WriteLine();
        if (page.Count == 0)
        {
            Console.WriteLine("  No saved sessions.");
            Console.WriteLine();
            return new ChatCommandResult();
        }

        Console.WriteLine($"  Saved sessions (showing {page.Count} of {sessions.Count}, offset {offset}):");
        Console.WriteLine($"  [  0] current active session  msgs={context.Session.Messages.Count}");
        for (int i = 0; i < page.Count; i++)
        {
            SessionRecord s = page[i];
            string shortId = s.SessionId.ToString()[..8];
            string last = s.LastActivityAt?.ToString("yyyy-MM-dd HH:mm") ?? "(never)";
            string created = s.CreatedAt.ToString("yyyy-MM-dd HH:mm");
            string status =
                s.Status == SessionStatus.Active
                    ? "active"
                    : s.Status.ToString().ToLowerInvariant();
            long msgCount = SafeEventCount(context.Host.SessionStore, s.SessionId);
            Console.WriteLine(
                $"  [{offset + i + 1,3}] {shortId}  {status,-9} msgs={msgCount,-4} {last,-16} {created,-16} {s.ProviderName,-12} {s.ModelId,-22} {Truncate(s.Label ?? "", 24)}");
        }

        Console.WriteLine();
        Console.WriteLine(
            "  Use: /resume <index|session-id-prefix|-1>   /sessions <limit> [offset] [all|active|archived|error]");
        Console.WriteLine();
        return new ChatCommandResult();
    }

    private static (int limit, int offset, SessionStatus? filter) ParseSessionsArgs(string args)
    {
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int limit = 20;
        int offset = 0;
        SessionStatus? filter = null;
        if (parts.Length > 0 && int.TryParse(parts[0], out int l) && l > 0)
        {
            limit = l;
        }

        if (parts.Length > 1 && int.TryParse(parts[1], out int o))
        {
            offset = o;
        }

        if (parts.Length > 2)
        {
            filter = parts[2].ToLowerInvariant() switch
            {
                "active" => SessionStatus.Active,
                "archived" => SessionStatus.Archived,
                "error" => SessionStatus.Error,
                _ => null
            };
        }

        return (limit, offset, filter);
    }

    private static ChatCommandResult ResumeSession(SlashCommandContext context, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            Console.WriteLine("  Usage: /resume <session-id-prefix|index|-1>");
            return new ChatCommandResult();
        }

        List<SessionRecord> sessions = GetOrderedSessions(context);
        if (TryResolveSessionArg(sessions, args, out SessionRecord? record, out string err))
        {
            return ResumeFromRecord(context, record!);
        }

        Console.WriteLine(err);
        return new ChatCommandResult();
    }

    private static ChatCommandResult ResumeFromRecord(
        SlashCommandContext context,
        SessionRecord record)
    {
        Model? model = context.Catalog.Models.Values.FirstOrDefault(m =>
            m.Id == record.ModelId && m.ProviderName == record.ProviderName);
        if (model is null)
        {
            Console.WriteLine($"  Model '{record.ModelId}' not found in catalog.");
            return new ChatCommandResult();
        }

        string? apiKey = context.Config.ApiKeys.TryGetValue(model.ProviderName, out string? key)
            ? key
            : null;
        AgentSession? resumed = context
            .Host.ResumeSessionAsync(record.SessionId, model, apiKey)
            .GetAwaiter()
            .GetResult();
        if (resumed is null)
        {
            Console.WriteLine($"  Failed to resume session {record.SessionId}.");
            return new ChatCommandResult();
        }

        string? modelKey = context
            .Catalog.Models.FirstOrDefault(kv =>
                kv.Value.Id == model.Id && kv.Value.ProviderName == model.ProviderName)
            .Key;
        Console.WriteLine(
            $"  Resumed session {record.SessionId.ToString()[..8]} with {resumed.Messages.Count} messages.");
        return new ChatCommandResult(ChatCommandAction.SwitchSession,
            NewSession: resumed,
            NewModel: model,
            NewModelKey: modelKey ?? context.ModelKey);
    }

    private static ChatCommandResult ForkSession(SlashCommandContext context, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            Console.WriteLine("  Usage: /fork <session-id-prefix|index|-1> [--model <model-key|n>]");
            return new ChatCommandResult();
        }

        string[] parts = args.Split(" --model ", 2, StringSplitOptions.RemoveEmptyEntries);
        string sessionArg = parts[0].Trim();
        string? modelArg = parts.Length > 1 ? parts[1].Trim() : null;
        List<SessionRecord> sessions = GetOrderedSessions(context);

        if (!TryResolveSessionArg(sessions, sessionArg, out SessionRecord? sourceRecord, out string err))
        {
            Console.WriteLine(err);
            return new ChatCommandResult();
        }

        Model? targetModel = null;
        if (modelArg is not null)
        {
            if (context.Catalog.Models.TryGetValue(modelArg, out Model? m))
            {
                targetModel = m;
            }
            else if (int.TryParse(modelArg, out int modelIdx) && modelIdx >= 1)
            {
                var visible = GetVisibleModels(context).ToList();
                if (modelIdx <= visible.Count)
                {
                    targetModel = visible[modelIdx - 1].Model;
                }
                else
                {
                    Console.WriteLine($"  Model index {modelIdx} out of range ({visible.Count} models).");
                    return new ChatCommandResult();
                }
            }
            else
            {
                Console.WriteLine($"  Model '{modelArg}' not found.");
                return new ChatCommandResult();
            }
        }
        else
        {
            targetModel = context.Catalog.Models.Values.FirstOrDefault(m =>
                m.Id == sourceRecord!.ModelId && m.ProviderName == sourceRecord.ProviderName);
            if (targetModel is null)
            {
                Console.WriteLine($"  Original model '{sourceRecord!.ModelId}' not found.");
                return new ChatCommandResult();
            }
        }

        if (targetModel is null)
        {
            Console.WriteLine("  No target model selected.");
            return new ChatCommandResult();
        }

        string? apiKey = context.Config.ApiKeys.TryGetValue(targetModel.ProviderName, out string? key)
            ? key
            : null;
        AgentSession? forked = context
            .Host.ForkSessionAsync(sourceRecord!.SessionId, targetModel, apiKey: apiKey)
            .GetAwaiter()
            .GetResult();
        if (forked is null)
        {
            Console.WriteLine("  Failed to fork session.");
            return new ChatCommandResult();
        }

        string? targetKey = context
            .Catalog.Models.FirstOrDefault(kv =>
                kv.Value.Id == targetModel.Id && kv.Value.ProviderName == targetModel.ProviderName)
            .Key;
        Console.WriteLine(
            $"  Forked session {forked.Id.ToString()[..8]} from {sourceRecord.SessionId.ToString()[..8]} with {forked.Messages.Count} messages.");
        return new ChatCommandResult(ChatCommandAction.SwitchSession,
            NewSession: forked,
            NewModel: targetModel,
            NewModelKey: targetKey ?? context.ModelKey);
    }

    private static List<SessionRecord> GetOrderedSessions(SlashCommandContext context)
    {
        var sessions = context
            .Host.SessionStore.ListSessionsAsync(new SessionListQuery(int.MaxValue))
            .GetAwaiter()
            .GetResult()
            .Where(s => HasSessionEvents(context.Host.SessionStore, s.SessionId))
            .OrderByDescending(s => s.LastActivityAt ?? s.CreatedAt)
            .ToList();

        return sessions;
    }

    private static bool TryResolveSessionArg(
        List<SessionRecord> sessions,
        string arg,
        out SessionRecord? record,
        out string error)
    {
        record = null;
        error = string.Empty;

        if (arg == "-1")
        {
            if (sessions.Count == 0)
            {
                error = "  No saved sessions.";
                return false;
            }

            record = sessions[0];
            return true;
        }

        if (int.TryParse(arg, out int idx))
        {
            if (idx >= 1 && idx <= sessions.Count)
            {
                record = sessions[idx - 1];
                return true;
            }

            error = $"  Session index {idx} out of range (1..{sessions.Count}).";
            return false;
        }

        var matches = sessions
            .Where(s => s.SessionId.ToString().StartsWith(arg, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            error = $"  No session matching '{arg}'.";
            return false;
        }

        if (matches.Count > 1)
        {
            error =
                $"  Multiple sessions match '{arg}':\n"
                + string.Join('\n',
                    matches.Select(m =>
                        $"    {m.SessionId.ToString()[..8]}  {m.ProviderName}  {m.ModelId}"));
            return false;
        }

        record = matches[0];
        return true;
    }

    private static bool HasSessionEvents(ISessionStore store, SessionId sessionId)
    {
        try
        {
            return store.GetEventCountAsync(sessionId).GetAwaiter().GetResult() > 0;
        }
        catch
        {
            return false;
        }
    }

    private static long SafeEventCount(ISessionStore store, SessionId sessionId)
    {
        try
        {
            return store.GetEventCountAsync(sessionId).GetAwaiter().GetResult();
        }
        catch
        {
            return 0;
        }
    }

    private static ChatCommandResult ResetSession(SlashCommandContext context)
    {
        context.Session.Reset();
        Console.WriteLine("  (Conversation and provider state reset)");
        return new ChatCommandResult();
    }

    private static string FormatArgs(IReadOnlyDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0)
        {
            return "";
        }

        return string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
    }

    private static string FormatTokenCount(int? tokens)
    {
        return tokens.HasValue ? $"{tokens.Value:N0} tokens" : "unknown";
    }

    private static string FormatTokenCountCompact(int? tokens)
    {
        if (!tokens.HasValue)
        {
            return "?";
        }

        int t = tokens.Value;
        if (t < 1_000)
        {
            return t.ToString();
        }

        if (t < 1_000_000)
        {
            return $"{t / 1_000}k";
        }

        return $"{t / 1_000_000.0:F1}M";
    }
}
