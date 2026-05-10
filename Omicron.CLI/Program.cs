using System.Text;
using Omicron.Core;
using Omicron.Core.Config;
using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Sessions;
using Omicron.CLI;

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════╗");
Console.WriteLine("║     Omicron Agent — C# Multi-Backend MVP        ║");
Console.WriteLine("║   API Shapes: Chat · Anthropic · Responses     ║");
Console.WriteLine("║   Providers: OpenAI · Anthropic · OpenCode · OR ║");
Console.WriteLine("╚══════════════════════════════════════════════════╝");
Console.WriteLine();

// --- Config ---
var configManager = new ConfigManager();
configManager.Load();

var cfg = configManager.Config;
Console.WriteLine($"Config: {configManager.GetConfigPath()}");

// --- Host ---
// Keep the live EventLog in memory, but persist the authoritative session/event
// stream to disk by default so conversations can be inspected/replayed later.
var sessionStoreDir = Path.Combine(
    Path.GetDirectoryName(configManager.GetConfigPath())!,
    "sessions");
var sessionStore = new JsonlSessionStore(sessionStoreDir);
var host = new OmicronHost(Environment.CurrentDirectory, sessionStore);
host.LoadBuiltinExtensions();
Console.WriteLine($"Session store: {sessionStore.StoreDirectory}");

// --- Provider setup ---
Console.WriteLine("Registered providers:");
foreach (var name in host.Providers.ProviderNames)
    Console.WriteLine($"  \u2022 {name}");

// --- Model catalog ---
Console.WriteLine();
var catalog = host.ModelCatalog;

// Auto-discover from all providers
await catalog.DiscoverAsync(quiet: true);

// Create slash command dispatcher
var slashDispatcher = new SlashCommandDispatcher();

var currentModelKey = SelectStartupModelKey(catalog, cfg);
if (currentModelKey is null)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("No models available. Add an API key in config or set a provider API key environment variable.");
    Console.ResetColor();
    ShowConfig(configManager);
    currentModelKey = SelectStartupModelKey(catalog, cfg);
    if (currentModelKey is null)
    {
        Console.WriteLine("Goodbye!");
        return;
    }
}

while (true)
{
    // Track whether we have a resumed/forked session to skip model selection
    AgentSession? resumedSession = null;
    Model? resumedModel = null;
    string? currentApiKey = null;

    while (true)
    {
        if (resumedSession is null)
        {
            if (!catalog.Models.TryGetValue(currentModelKey, out var selectedModel))
            {
                currentModelKey = SelectStartupModelKey(catalog, cfg);
                if (currentModelKey is null) break;
                selectedModel = catalog.Models[currentModelKey];
            }

            cfg.LastModel = currentModelKey;
            configManager.Save();

            currentApiKey = ResolveApiKey(selectedModel);
            if (currentApiKey is null)
            {
                currentApiKey = PromptForApiKey(selectedModel.ProviderName);
                if (string.IsNullOrEmpty(currentApiKey))
                {
                    Console.WriteLine("API key required for this provider.");
                    currentModelKey = SelectStartupModelKey(catalog, cfg, excludeKey: currentModelKey);
                    if (currentModelKey is null) break;
                    continue;
                }
                configManager.SetApiKey(selectedModel.ProviderName, currentApiKey);
                Console.WriteLine("  (saved to config)");
            }

            Console.WriteLine($"\nUsing: {selectedModel.Name}");
            Console.WriteLine($"  Provider: {selectedModel.ProviderName}");
            Console.WriteLine($"  API Type: {selectedModel.ApiType}");
            Console.WriteLine($"  Base URL: {selectedModel.BaseUrl}");
            Console.WriteLine("Type your message, /help for commands, /models to list, /model <n> to switch.\n");

            resumedSession = host.CreateSession(
                selectedModel,
                cfg.SystemPrompt ?? "You are a helpful assistant with access to tools.");
            resumedSession.MaxTokens = cfg.DefaultMaxTokens;
            resumedSession.Temperature = cfg.DefaultTemperature;
            resumedSession.ApiKey = currentApiKey;
            resumedSession.MaxIterations = cfg.MaxIterations;
            resumedModel = selectedModel;
        }

        var innerSlashContext = new SlashCommandContext(
            host, resumedSession, resumedModel!, currentModelKey, cfg, catalog);

        var loopResult = await ChatLoop(resumedSession, innerSlashContext, slashDispatcher);

        if (loopResult.Reason == ChatLoopExitReason.ExitApp)
        { break; }

        if (loopResult.NewSession is not null && loopResult.NewModel is not null)
        {
            resumedSession = loopResult.NewSession;
            resumedModel = loopResult.NewModel;
            if (loopResult.NewModelKey is not null)
            {
                currentModelKey = loopResult.NewModelKey;
                cfg.LastModel = currentModelKey;
                configManager.Save();
            }
            // Apply runtime config
            resumedSession.ApiKey = currentApiKey;
            resumedSession.MaxTokens = cfg.DefaultMaxTokens;
            resumedSession.Temperature = cfg.DefaultTemperature;
            resumedSession.MaxIterations = cfg.MaxIterations;
            continue;
        }

        if (loopResult.ModelKey is not null)
        {
            currentModelKey = loopResult.ModelKey;
            resumedSession = null;
            resumedModel = null;
            continue;
        }
        break;
    }
    break;
}

Console.WriteLine("Goodbye!");
return;

// ---------------------------------------------------------------
// Local functions
// ---------------------------------------------------------------

IReadOnlyList<KeyValuePair<string, Model>> GetVisibleModels(IModelCatalog catalog, AgentConfig cfg, string? excludeKey = null)
{
    return catalog.Models
        .Where(kv => kv.Key != excludeKey)
        .Where(kv => catalog.IsFreeModel(kv.Key) || ResolveApiKey(kv.Value) is not null)
        .OrderBy(kv => kv.Key == cfg.LastModel ? 0 : 1)
        .ThenBy(kv => kv.Value.ProviderName)
        .ThenBy(kv => kv.Value.Name)
        .ToList();
}

string? SelectStartupModelKey(IModelCatalog catalog, AgentConfig cfg, string? excludeKey = null)
{
    var visible = GetVisibleModels(catalog, cfg, excludeKey);
    if (visible.Count == 0) return null;

    if (cfg.LastModel is not null && excludeKey != cfg.LastModel && visible.Any(kv => kv.Key == cfg.LastModel))
        return cfg.LastModel;

    return visible[0].Key;
}

string? ResolveApiKey(Model selectedModel)
{
    var envVarName = selectedModel.ProviderName.ToLowerInvariant() switch
    {
        "openai" => "OPENAI_API_KEY",
        "anthropic" => "ANTHROPIC_API_KEY",
        "opencode" or "opencode-go" => "OPENCODE_API_KEY",
        "openrouter" => "OPENROUTER_API_KEY",
        _ => null
    };

    return envVarName is not null
        ? configManager.GetApiKey(selectedModel.ProviderName, envVarName)
        : null;
}

void ShowConfig(ConfigManager cm)
{
    var cfg = cm.Config;
    Console.WriteLine("\n--- Configuration ---");
    Console.WriteLine($"  Config file: {cm.GetConfigPath()}");
    Console.WriteLine($"  Saved API keys: {cfg.ApiKeys.Count}");
    foreach (var (provider, _) in cfg.ApiKeys.OrderBy(k => k.Key))
    {
        Console.WriteLine($"    - {provider}: ****{cfg.ApiKeys[provider][^4..]}");
    }
    Console.WriteLine($"  Last model: {cfg.LastModel ?? "(none)"}");
    Console.WriteLine($"  Max tokens: {cfg.DefaultMaxTokens}");
    Console.WriteLine($"  Temperature: {cfg.DefaultTemperature}");
    Console.WriteLine($"  System prompt: {(cfg.SystemPrompt is not null ? cfg.SystemPrompt[..Math.Min(60, cfg.SystemPrompt.Length)] + "..." : "(default)")}");
    Console.WriteLine($"  Display width: {cfg.DisplayLineWidth} cols");
    Console.WriteLine($"  Display lines: {cfg.DisplayMaxLines} max");
    Console.WriteLine($"  Max iterations: {cfg.MaxIterations}");

    Console.WriteLine("\nCommands:");
    Console.WriteLine("  key set <provider>  - Set API key for a provider");
    Console.WriteLine("  key rm <provider>   - Remove a saved API key");
    Console.WriteLine("  prompt <text>       - Set system prompt");
    Console.WriteLine("  tokens <n>          - Set default max tokens");
    Console.WriteLine("  temp <n>            - Set temperature");
    Console.WriteLine("  width <n>           - Set display line width (default 120)");
    Console.WriteLine("  lines <n>           - Set display max lines (default 60)");
    Console.WriteLine("  iters <n>           - Set max tool loop iterations (default 100)");
    Console.WriteLine("  [enter] to return");

    while (true)
    {
        Console.Write("config> ");
        var line = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(line)) break;

        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "key" when parts.Length >= 3 && parts[1] == "set":
                var key = PromptForApiKey(parts[2]);
                if (!string.IsNullOrEmpty(key))
                {
                    cm.SetApiKey(parts[2], key);
                    Console.WriteLine($"  Key saved for '{parts[2]}'.");
                }
                break;

            case "key" when parts.Length >= 2 && parts[1] == "rm":
                if (parts.Length >= 3 && cfg.ApiKeys.Remove(parts[2]))
                {
                    cm.Save();
                    Console.WriteLine($"  Key removed for '{parts[2]}'.");
                }
                break;

            case "prompt" when parts.Length >= 2:
                cfg.SystemPrompt = parts[1];
                cm.Save();
                Console.WriteLine("  System prompt updated.");
                break;

            case "tokens" when parts.Length >= 2 && int.TryParse(parts[1], out var n):
                cfg.DefaultMaxTokens = n;
                cm.Save();
                Console.WriteLine($"  Max tokens set to {n}.");
                break;

            case "temp" when parts.Length >= 2 && double.TryParse(parts[1], out var t):
                cfg.DefaultTemperature = t;
                cm.Save();
                Console.WriteLine($"  Temperature set to {t}.");
                break;

            case "width" when parts.Length >= 2 && int.TryParse(parts[1], out var w) && w >= 20:
                cfg.DisplayLineWidth = w;
                cm.Save();
                Console.WriteLine($"  Display line width set to {w}.");
                break;

            case "lines" when parts.Length >= 2 && int.TryParse(parts[1], out var ml) && ml >= 5:
                cfg.DisplayMaxLines = ml;
                cm.Save();
                Console.WriteLine($"  Display max lines set to {ml}.");
                break;

            case "iters" when parts.Length >= 2 && int.TryParse(parts[1], out var it) && it >= 1:
                cfg.MaxIterations = it;
                cm.Save();
                Console.WriteLine($"  Max iterations set to {it}.");
                break;

            default:
                Console.WriteLine("  Unknown command.");
                break;
        }
    }
}

string? PromptForApiKey(string provider)
{
    Console.Write($"  Enter your {provider} API key (will be saved to config): ");
    var key = ReadPassword();
    Console.WriteLine();
    return key;
}

string ReadPassword()
{
    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
        {
            sb.Length--;
            continue;
        }
        if (key.KeyChar != 0)
            sb.Append(key.KeyChar);
    }
    return sb.ToString();
}

async Task<ChatLoopResult> ChatLoop(AgentSession session, SlashCommandContext slashCtx, SlashCommandDispatcher dispatcher)
{
    var editor = new LineEditor(input => dispatcher.GetCompletions(input, slashCtx));

    while (true)
    {
        var input = editor.ReadLine("You: ");
        if (input is null) break;
        if (string.IsNullOrWhiteSpace(input)) continue;

        // Check for slash commands first
        if (dispatcher.IsCommand(input))
        {
            var result = dispatcher.Execute(input, slashCtx);
            switch (result.Action)
            {
                case ChatCommandAction.Continue:
                    continue;

                case ChatCommandAction.ExitSession:
                    if (result.Message is not null)
                        Console.WriteLine(result.Message);
                    return new ChatLoopResult(ChatLoopExitReason.ExitSession);

                case ChatCommandAction.ExitApp:
                    if (result.Message is not null)
                        Console.WriteLine(result.Message);
                    return new ChatLoopResult(ChatLoopExitReason.ExitApp);

                case ChatCommandAction.ResetSession:
                    continue;

                case ChatCommandAction.ClearProviderState:
                    continue;

                case ChatCommandAction.SwitchModel:
                    if (result.ModelKey is not null)
                    {
                        if (result.Message is not null)
                            Console.WriteLine(result.Message);
                        return new ChatLoopResult(ChatLoopExitReason.ExitSession, result.ModelKey);
                    }
                    continue;

                case ChatCommandAction.SwitchSession:
                    if (result.NewSession is not null && result.NewModel is not null)
                    {
                        return new ChatLoopResult(ChatLoopExitReason.ExitSession,
                            NewSession: result.NewSession, NewModel: result.NewModel);
                    }
                    continue;
            }
        }

        Console.Write("Agent: ");

        using var cts = new CancellationTokenSource();
        var interrupted = false;

        var sessionTask = ReadSessionOutput(session, input, cts.Token);

        while (!sessionTask.IsCompleted)
        {
            var delay = Task.Delay(100);
            var completed = await Task.WhenAny(sessionTask, delay);

            if (completed == delay)
            {
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Escape)
                    {
                        interrupted = true;
                        cts.Cancel();
                        break;
                    }
                }
            }

            if (interrupted) break;
        }

        try { await sessionTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[Error: {ex.Message}]");
            Console.ResetColor();
        }

        if (interrupted)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n[interrupted]");
            Console.ResetColor();
        }
    }

    return new ChatLoopResult(ChatLoopExitReason.ExitSession);
}

async Task ReadSessionOutput(AgentSession session, string input, CancellationToken ct)
{
    await foreach (var evt in session.PromptAsync(input, ct))
    {
        switch (evt)
        {
            case AssistantTextDeltaEvent delta:
                Console.Write(delta.Delta);
                break;

            case ToolInvocationStartedEvent toolStart:
                Console.Write($"\n\n  \u2699\ufe0f Calling tool: {toolStart.ToolName}... ");
                break;

            case ToolInvocationCompletedEvent toolEnd:
                Console.WriteLine("done.");
                Console.WriteLine();
                DisplayHelpers.DisplayTruncated(toolEnd.Result, cfg.DisplayLineWidth, cfg.DisplayMaxLines);
                Console.WriteLine();
                break;

            case AssistantResponseCompleteEvent complete:
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(
                    $"\n(Used {complete.Usage.InputTokens}\u2191 + {complete.Usage.OutputTokens}\u2193 tokens)");
                Console.ResetColor();
                break;

            case SessionErrorEvent error:
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[Error: {error.Message}]");
                Console.ResetColor();
                break;

            case UserMessageEvent:
            case SessionStartedEvent:
            case TurnStartedEvent:
            case SessionEndedEvent:
            case SessionResetEvent:
            case PermissionRequestedEvent:
            case ExecutionStartedEvent:
            case ExecutionCompletedEvent:
            case ProviderStateUpdatedEvent:
            case ProviderStateClearedEvent:
                // Not displayed in console
                break;
        }
    }
}

public static class DisplayHelpers
{
    public static void DisplayTruncated(string text, int lineWidth, int maxLines)
    {
        if (string.IsNullOrEmpty(text)) return;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var totalLines = lines.Length;
        var shown = 0;

        for (int i = 0; i < totalLines && shown < maxLines; i++, shown++)
        {
            Console.WriteLine(TruncateLine(lines[i], lineWidth));
        }

        if (totalLines > maxLines)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [display limit: {maxLines}/{totalLines:N0} lines, width {lineWidth}]");
            Console.ResetColor();
        }
    }

    private static string TruncateLine(string line, int maxWidth)
    {
        if (line.Length <= maxWidth) return line;
        return line[..(maxWidth - 1)] + "\u2026";
    }

    public static string Truncate(this string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 1)] + "\u2026";
}

