using System.Text;
using Omicron.Core;
using Omicron.Core.Config;
using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Rendering;
using Omicron.Core.Rendering.Layout;
using Omicron.Core.Sessions;
using Omicron.CLI;
using Omicron.CLI.Tui;

Console.OutputEncoding = Encoding.UTF8;

// === Shared initialization (used by both console and TUI modes) ===

var configManager = new ConfigManager();
configManager.Load();
var cfg = configManager.Config;

var sessionStoreDir = Path.Combine(
    Path.GetDirectoryName(configManager.GetConfigPath())!,
    "sessions");
var sessionStore = new JsonlSessionStore(sessionStoreDir);
using var host = new OmicronHost(Environment.CurrentDirectory, sessionStore);
host.LoadBuiltinExtensions();

var catalog = host.ModelCatalog;
await catalog.DiscoverAsync(quiet: true);
try
{
    using var metaCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await host.RefreshModelMetadataAsync(metaCts.Token);
}
catch { } // non-fatal

// Safety net: if a previous TUI session crashed and left the console in raw / VT-input mode,
// restore traditional input processing before we decide which mode to enter.
TerminalBackendFactory.EnsureSafeConsoleInputMode();

// ── TUI mode ──
if (args is { Length: > 0 } && args.Contains("--tui"))
{
    await RunTuiSessionAsync(host, catalog, cfg, sessionStore);
    return;
}

Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════╗");
Console.WriteLine("║     Omicron Agent — C# Multi-Backend MVP        ║");
Console.WriteLine("║   API Shapes: Chat · Anthropic · Responses     ║");
Console.WriteLine("║   Providers: OpenAI · Anthropic · OpenCode · OR ║");
Console.WriteLine("╚══════════════════════════════════════════════════╝");
Console.WriteLine();
Console.WriteLine($"Config: {configManager.GetConfigPath()}");
Console.WriteLine($"Session store: {sessionStore.StoreDirectory}");
Console.WriteLine("Registered providers:");
foreach (var name in host.Providers.ProviderNames)
    Console.WriteLine($"  \u2022 {name}");
Console.WriteLine();

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

            var sessionConfig = SessionConfig.Create(
                selectedModel,
                cfg.SystemPrompt ?? "You are a helpful assistant with access to tools.",
                currentApiKey,
                cfg.DefaultMaxTokens,
                cfg.DefaultTemperature,
                maxIterations: cfg.MaxIterations);
            resumedSession = host.CreateSession(sessionConfig);
            resumedModel = selectedModel;
        }

        var innerSlashContext = new SlashCommandContext(
            host, resumedSession, resumedModel!, currentModelKey, cfg, catalog);

        var loopResult = await ChatLoop(resumedSession, innerSlashContext, slashDispatcher);

        if (loopResult.Reason == ChatLoopExitReason.ExitApp)
        {
            Console.WriteLine("Goodbye!");
            break;
        }

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
            // Runtime config is already applied at construction via SessionConfig
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

    // Display conversation history for resumed sessions
    if (session.Messages.Count > 0)
    {
        Console.WriteLine($"\n  Session has {session.Messages.Count} previous messages.\n");
        foreach (var msg in session.Messages)
        {
            switch (msg.Role)
            {
                case MessageRole.User:
                    Console.WriteLine($"\x1b[33mYou:\x1b[0m {msg.Text}");
                    break;
                case MessageRole.Assistant when msg.ToolCalls is { Count: > 0 }:
                    Console.WriteLine($"\x1b[36mAssistant:\x1b[0m {msg.Text}");
                    foreach (var tc in msg.ToolCalls)
                        Console.WriteLine($"  \x1b[90m[tool: {tc.Name}]\x1b[0m");
                    break;
                case MessageRole.Assistant:
                    Console.WriteLine($"\x1b[36mAssistant:\x1b[0m {msg.Text}");
                    break;
                case MessageRole.ToolResult:
                    var preview = msg.Text?.Length > 80 ? msg.Text[..80] + "…" : msg.Text;
                    Console.WriteLine($"  \x1b[90m[result: {msg.ToolName}] {preview}\x1b[0m");
                    break;
            }
        }
        Console.WriteLine();
    }

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

/// <summary>
/// TUI mode entry point — creates or resumes a session, launches the full chat TUI.
/// </summary>
async Task RunTuiSessionAsync(OmicronHost host, IModelCatalog catalog, AgentConfig cfg, JsonlSessionStore sessionStore)
{
    using var backend = TerminalBackendFactory.CreateSystemBackend();
    backend.Initialize();
    using var shell = new TuiShell(backend);

    // Build the slash command bridge
    var slashBridge = new TuiSlashCommandBridge(host, cfg, catalog);

    // Try to find a model and create a session
    var visibleModels = GetVisibleModels(catalog, cfg);
    AgentSession? session = null;
    Model? selectedModel = null;
    string modelKey = "";

    if (visibleModels.Count > 0)
    {
        // Try last-used model first
        if (cfg.LastModel is not null)
        {
            // cfg.LastModel stores the catalog KEY, not the model Id/Name.
            var lastModelEntry = visibleModels
                .FirstOrDefault(kv => kv.Key == cfg.LastModel);
            var lastModel = lastModelEntry.Value;
            if (lastModel is not null)
            {
                var apiKey = ResolveApiKey(lastModel);
                if (apiKey is not null)
                {
                    selectedModel = lastModel;
                    modelKey = lastModelEntry.Key;
                    session = host.CreateSession(SessionConfig.Create(selectedModel, cfg.SystemPrompt ?? "You are a helpful assistant with access to tools.", apiKey, cfg.DefaultMaxTokens, cfg.DefaultTemperature, maxIterations: cfg.MaxIterations));
                }
            }
        }

        // If no session yet, show model picker inside TUI
        if (session is null)
        {
            var picker = new TuiModelPicker();
            picker.SetModels(visibleModels.Select(kv => kv.Value).ToList(), cfg.LastModel);

            var pickerTask = new TaskCompletionSource<Model?>();
            picker.OnModelSelected += (m) => pickerTask.TrySetResult(m);
            picker.OnCancelled += () => pickerTask.TrySetResult(null);

            // Run a mini event loop for the picker
            await shell.RunAsync(
                onEvent: (evt) =>
                {
                    if (evt is KeyEvent ke)
                    {
                        if (ke.Key == Key.Character && ke.Text?.Value == 4) // Ctrl+D in picker = cancel
                        {
                            pickerTask.TrySetResult(null);
                            shell.Cancel();
                            return Task.FromResult(true);
                        }
                        picker.HandleKey(ke);
                        if (picker.IsCompleted)
                        {
                            shell.Cancel(); // Exit the event loop — modal complete
                        }
                    }
                    return Task.FromResult(true);
                },
                onRender: (frame) =>
                {
                    frame.Clear();
                    var bounds = new Rect(0, 0, frame.Width, frame.Height);
                    picker.Measure(new Size(frame.Width, frame.Height));
                    picker.Arrange(bounds);
                    var ctx = new RenderContext(frame, bounds, TextStyle.Default);
                    picker.Render(ctx);
                    return Task.CompletedTask;
                });

            selectedModel = await pickerTask.Task;
            shell.ForceFullRedraw(); // Prevent picker artifacts from leaking into main app
            if (selectedModel is not null)
            {
                // Look up the catalog key for the selected model
                modelKey = catalog.Models
                    .FirstOrDefault(kv => kv.Value.Id == selectedModel.Id && kv.Value.ProviderName == selectedModel.ProviderName)
                    .Key ?? selectedModel.Id;
                var apiKey = ResolveApiKey(selectedModel);
                bool promptedForApiKey = false;
                if (apiKey is null)
                {
                    // Show API key prompt inside TUI
                    var prompt = new TuiApiKeyPrompt();
                    prompt.Reset(selectedModel.ProviderName);

                    var promptTask = new TaskCompletionSource<string?>();
                    await shell.RunAsync(
                        onEvent: (evt) =>
                        {
                            if (evt is PasteEvent pe)
                            {
                                prompt.Insert(pe.Text);
                                return Task.FromResult(true);
                            }

                            if (evt is KeyEvent ke)
                            {
                                if (prompt.HandleKey(ke))
                                {
                                    if (prompt.IsCompleted)
                                    {
                                        promptTask.TrySetResult(prompt.IsCancelled ? null : prompt.ApiKey);
                                        shell.Cancel(); // Exit the event loop — modal complete
                                    }
                                }
                            }
                            return Task.FromResult(true);
                        },
                        onRender: (frame) =>
                        {
                            frame.Clear();
                            var bounds = new Rect(0, 0, frame.Width, frame.Height);
                            prompt.Measure(new Size(frame.Width, frame.Height));
                            prompt.Arrange(bounds);
                            var ctx = new RenderContext(frame, bounds, TextStyle.Default);
                            prompt.Render(ctx);
                            return Task.CompletedTask;
                        });

                    apiKey = await promptTask.Task;
                    apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
                    promptedForApiKey = apiKey is not null;
                    shell.ForceFullRedraw(); // Prevent prompt artifacts from leaking into main app
                }

                if (apiKey is not null)
                {
                    // Persist keys entered through the TUI prompt, matching the
                    // non-TUI startup flow. Do not copy env-var keys into config.
                    if (promptedForApiKey)
                        configManager.SetApiKey(selectedModel.ProviderName, apiKey);

                    var sessionConfig = SessionConfig.Create(selectedModel, cfg.SystemPrompt ?? "You are a helpful assistant with access to tools.", apiKey, cfg.DefaultMaxTokens, cfg.DefaultTemperature, maxIterations: cfg.MaxIterations);
                    session = host.CreateSession(sessionConfig);
                    cfg.LastModel = modelKey;
                    configManager.Save();
                }
            }
        }
    }

    if (session is null || selectedModel is null)
    {
        // No session — cancelled by user or no models available
        Console.WriteLine("\nReturning to CLI mode. Use --tui to re-enter the TUI.");
        return;
    }

    // Run the main chat TUI
    using var app = new AppLayout(shell, session, modelKey)
    {
        SlashBridge = slashBridge
    };
    await app.RunAsync();

    // Cleanup: only persist sessions that actually have content.
    try
    {
        if (session.Messages.Count == 0)
        {
            var sessionPath = Path.Combine(sessionStore.StoreDirectory, $"{session.Id}.jsonl");
            if (File.Exists(sessionPath))
                File.Delete(sessionPath);
        }
        else
        {
            Console.WriteLine("\nSession saved. Exiting Omicron TUI.");
        }
    }
    catch
    {
        // Non-fatal
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
