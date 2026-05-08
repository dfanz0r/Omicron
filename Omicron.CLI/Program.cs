using System.Data;
using System.Diagnostics;
using System.Text;
using Omicron.Core;
using Omicron.Core.Config;
using Omicron.Core.Models;
using Omicron.Core.Providers;
using Omicron.Core.Tools;
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

// --- Provider setup ---
var providerFactory = new ProviderFactory();
Console.WriteLine("Registered providers:");
foreach (var name in providerFactory.ProviderNames)
    Console.WriteLine($"  \u2022 {name}");

// --- Model discovery (auto on startup) ---
Console.WriteLine();
var models = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase);

// Always include these fallbacks in case discovery fails
void EnsureModel(string key, Model m)
{
    if (!models.ContainsKey(key))
    {
        if (m.Provider is null && providerFactory.TryGetProvider(m.ProviderName, out var p))
            m.Provider = p;
        models[key] = m;
    }
}

// Seed with minimal hardcoded set (will be replaced by discovery)
EnsureModel("openai:gpt-4o", MakeModel("gpt-4o", "GPT-4o", "openai", ApiType.OpenAiChat, "https://api.openai.com/v1", 128000, 16384, true));
EnsureModel("openai:gpt-4o-mini", MakeModel("gpt-4o-mini", "GPT-4o Mini", "openai", ApiType.OpenAiChat, "https://api.openai.com/v1", 128000, 16384, true));
EnsureModel("anthropic:claude-sonnet-4", MakeModel("claude-sonnet-4-20250514", "Claude Sonnet 4", "anthropic", ApiType.AnthropicMessages, "https://api.anthropic.com/v1", 200000, 8192, true));

// Auto-discover from all providers
var freeModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

bool IsFreeModel(Model m) => freeModels.Contains(m.Id) || freeModels.Contains($"{m.ProviderName}:{m.Id}");

await RefreshModels(providerFactory, models, configManager, quiet: true);

// Resolve providers for any remaining unresolved models
foreach (var model in models.Values)
{
    if (model.Provider is null && providerFactory.TryGetProvider(model.ProviderName, out var p))
        model.Provider = p;
}

while (true)
{
    ShowMenu(models, cfg);
    var input = Console.ReadLine()?.Trim() ?? "";
    if (input == "0") break;

    if (input.Equals("config", StringComparison.OrdinalIgnoreCase))
    {
        ShowConfig(configManager);
        continue;
    }

    if (input.Equals("refresh", StringComparison.OrdinalIgnoreCase))
    {
        await RefreshModels(providerFactory, models, configManager, quiet: false);
        continue;
    }

    if (!int.TryParse(input, out var choice) || choice < 1 || choice > models.Count)
    {
        Console.WriteLine("Invalid choice.");
        continue;
    }

    // Build visible list matching ShowMenu ordering for correct index lookup
    var visibleList = models
        .Where(kv => cfg.ApiKeys.ContainsKey(kv.Value.ProviderName) || IsFreeModel(kv.Value))
        .OrderBy(kv => kv.Key == cfg.LastModel ? 0 : 1)
        .ThenBy(kv => kv.Value.ProviderName)
        .ThenBy(kv => kv.Value.Name)
        .ToList();

    if (choice > visibleList.Count)
    {
        Console.WriteLine("Invalid choice.");
        continue;
    }

    var (modelKey, selectedModel) = visibleList[choice - 1];
    cfg.LastModel = modelKey;
    configManager.Save();

    // Resolve API key — check config first, then env var, then prompt
    var envVarName = selectedModel.ProviderName.ToLowerInvariant() switch
    {
        "openai" => "OPENAI_API_KEY",
        "anthropic" => "ANTHROPIC_API_KEY",
        "opencode" or "opencode-go" => "OPENCODE_API_KEY",
        "openrouter" => "OPENROUTER_API_KEY",
        _ => null
    };

    var apiKey = envVarName is not null ? configManager.GetApiKey(selectedModel.ProviderName, envVarName) : null;

    if (apiKey is null)
    {
        apiKey = PromptForApiKey(selectedModel.ProviderName);
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("API key required for this provider.");
            continue;
        }
        // Save to config
        configManager.SetApiKey(selectedModel.ProviderName, apiKey);
        Console.WriteLine("  (saved to config)");
    }

    Console.WriteLine($"\nUsing: {selectedModel.Name}");
    Console.WriteLine($"  Provider: {selectedModel.ProviderName}");
    Console.WriteLine($"  API Type: {selectedModel.ApiType}");
    Console.WriteLine($"  Base URL: {selectedModel.BaseUrl}");
    Console.WriteLine("Type your message (or 'exit' to go back, 'reset' to clear history).\n");

    var agent = new Agent(selectedModel, cfg.SystemPrompt ?? "You are a helpful assistant with access to tools.")
    {
        MaxTokens = cfg.DefaultMaxTokens,
        Temperature = cfg.DefaultTemperature,
        ApiKey = apiKey,
        MaxIterations = cfg.MaxIterations
    };

    RegisterTools(agent);
    await ChatLoop(agent);
}

Console.WriteLine("Goodbye!");
return;

// ---------------------------------------------------------------
// Local functions
// ---------------------------------------------------------------

void ShowMenu(Dictionary<string, Model> allModels, AgentConfig cfg)
{
    // Only show models for providers with API keys, or free models
    var visible = new List<(string Key, Model Model, bool IsFree)>();
    foreach (var (key, m) in allModels)
    {
        bool hasKey = cfg.ApiKeys.ContainsKey(m.ProviderName);
        bool isFree = IsFreeModel(m);
        if (hasKey || isFree)
            visible.Add((key, m, isFree && !hasKey));
    }

    // Sort: last-used first, then provider, then name
    visible.Sort((a, b) =>
    {
        var aLast = a.Key == cfg.LastModel ? 0 : 1;
        var bLast = b.Key == cfg.LastModel ? 0 : 1;
        if (aLast != bLast) return aLast.CompareTo(bLast);
        var p = string.Compare(a.Model.ProviderName, b.Model.ProviderName, StringComparison.OrdinalIgnoreCase);
        if (p != 0) return p;
        return string.Compare(a.Model.Name, b.Model.Name, StringComparison.OrdinalIgnoreCase);
    });

    Console.WriteLine();
    var keyCount = cfg.ApiKeys.Count;
    Console.WriteLine($"Models ({visible.Count} available, {keyCount} provider{(keyCount == 1 ? "" : "s")} with keys)");

    for (int i = 0; i < visible.Count; i++)
    {
        var (key, m, freeOnly) = visible[i];
        var shape = m.ApiType switch
        {
            ApiType.OpenAiChat => "Chat",
            ApiType.AnthropicMessages => "Anthr",
            ApiType.OpenAiResponses => "Resp",
            ApiType.GoogleGenAi => "Google",
            _ => "?"
        };
        var marker = key == cfg.LastModel ? "★" : " ";
        var freeTag = freeOnly ? "free" : "    ";
        Console.WriteLine($"  [{i + 1,2}] {marker} {freeTag} {m.Name,-36} {m.ProviderName,-12} {shape,-6} {m.ContextWindow,7}");
    }

    if (visible.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  No models available. Add an API key via [config] or check your connection.");
        Console.ResetColor();
    }

    Console.WriteLine("  [ 0] Exit");
    Console.WriteLine("  [config] Settings & API keys");
    Console.WriteLine("  [refresh] Re-fetch models from providers");
    Console.Write("\n> ");
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

async Task RefreshModels(ProviderFactory factory, Dictionary<string, Model> models, ConfigManager cm, bool quiet)
{
    if (!quiet)
    {
        Console.WriteLine("\n--- Refreshing models ---\n");
    }

    var before = models.Count;
    var addedCount = 0;

    void AddDiscovered(string prefix, string id, string name, string provider, ApiType apiType, string baseUrl, int ctx, int maxTokens, bool isFree, bool? supportsImages = null)
    {
        var key = $"{prefix}:{id}";
        if (models.ContainsKey(key)) return;

        var model = MakeModel(id, name, provider, apiType, baseUrl, ctx, maxTokens,
            supportsImages ?? (apiType != ApiType.AnthropicMessages));
        if (factory.TryGetProvider(provider, out var p))
            model.Provider = p;
        models[key] = model;

        if (isFree) freeModels.Add(key);

        addedCount++;
        if (!quiet && addedCount <= 15)
            Console.WriteLine($"    + {id} [{apiType}]{(isFree ? " free" : "")}");
    }

    // OpenCode Zen
    if (factory.TryGetProvider("opencode", out var zen) && zen is OpenCodeProvider ocp)
    {
        if (!quiet) Console.Write("OpenCode Zen... ");
        var entries = await ocp.FetchModelsAsync();
        if (!quiet) Console.WriteLine($"{entries.Count} models");
        foreach (var e in entries)
        {
            var apiType = OpenCodeProvider.ResolveApiType(e.Id);
            var baseUrl = apiType == ApiType.AnthropicMessages
                ? "https://opencode.ai/zen"
                : "https://opencode.ai/zen/v1";
            AddDiscovered("zen", e.Id, $"{e.Name} (Zen)", "opencode", apiType, baseUrl, 128000, 16384, false);
        }
    }

    // OpenCode Go
    if (factory.TryGetProvider("opencode-go", out var go) && go is OpenCodeProvider ocpGo)
    {
        if (!quiet) Console.Write("OpenCode Go... ");
        var entries = await ocpGo.FetchModelsAsync();
        if (!quiet) Console.WriteLine($"{entries.Count} models");
        foreach (var e in entries)
            AddDiscovered("go", e.Id, $"{e.Name} (Go)", "opencode-go", ApiType.OpenAiChat,
                "https://opencode.ai/zen/go/v1", 128000, 16384, false);
    }

    // OpenRouter
    if (factory.TryGetProvider("openrouter", out var or) && or is OpenRouterProvider orp)
    {
        if (!quiet) Console.Write("OpenRouter... ");
        var entries = await orp.FetchModelsAsync();
        if (!quiet) Console.WriteLine($"{entries.Count} models");
        foreach (var e in entries)
        {
            var isFree = e.PromptCost == 0 && e.CompletionCost == 0;
            var displayName = e.Name.Length > 36 ? e.Name[..33] + "..." : e.Name;
            AddDiscovered("or", e.Id, $"{displayName} (OR)", "openrouter", ApiType.OpenAiChat,
                "https://openrouter.ai/api/v1", e.ContextLength, 16384, isFree);
        }
    }

    var totalAdded = models.Count - before;
    if (!quiet || totalAdded > 0)
    {
        Console.WriteLine($"  {models.Count} models total ({freeModels.Count} free){(!quiet ? "" : "")}");
    }

    // Re-resolve providers
    foreach (var m in models.Values)
    {
        if (m.Provider is null && factory.TryGetProvider(m.ProviderName, out var p))
            m.Provider = p;
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

static Model MakeModel(string id, string name, string provider, ApiType apiType, string baseUrl,
    int ctx, int maxTokens, bool supportsImages = false)
{
    return new Model
    {
        Id = id,
        Name = name,
        ProviderName = provider,
        ApiType = apiType,
        BaseUrl = baseUrl,
        ContextWindow = ctx,
        MaxTokens = maxTokens,
        SupportsImages = supportsImages
    };
}

static void RegisterTools(Agent agent)
{
    agent.AddTool(new Tool
    {
        Name = "calculator",
        Description = "Evaluate a mathematical expression",
        Parameters = ToolSchema.Object(
            new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["expression"] = ToolSchema.StringProperty("The mathematical expression to evaluate (e.g., '2 + 2 * 3')")
            },
            new[] { "expression" }
        ),
        ExecuteAsync = (id, args) =>
        {
            var expr = args?.GetValueOrDefault("expression")?.ToString() ?? "";
            try
            {
                var result = new DataTable().Compute(expr, "");
                return Task.FromResult($"```\n{expr} = {result}\n```");
            }
            catch (Exception ex)
            {
                return Task.FromResult($"Error evaluating '{expr}': {ex.Message}");
            }
        }
    });

    agent.AddTool(new Tool
    {
        Name = "get_current_time",
        Description = "Get the current date and time",
        Parameters = ToolSchema.Object(
            new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["timezone"] = ToolSchema.StringProperty("Optional timezone (e.g., 'UTC', 'America/New_York')")
            },
            new[] { "timezone" }
        ),
        ExecuteAsync = (id, args) =>
        {
            var tz = args?.GetValueOrDefault("timezone")?.ToString();
            var now = string.IsNullOrEmpty(tz)
                ? DateTime.UtcNow
                : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, tz!);
            return Task.FromResult(
                $"Current time: {now:yyyy-MM-dd HH:mm:ss} {(string.IsNullOrEmpty(tz) ? "UTC" : tz)}");
        }
    });

    // File system tool
    agent.AddTool(FileTools.Create(Environment.CurrentDirectory));

    // Shell execution tool
    agent.AddTool(ShellTools.Create(Environment.CurrentDirectory));
}

async Task ChatLoop(Agent agent)
{
    var editor = new LineEditor();

    while (true)
    {
        var input = editor.ReadLine("You: ");
        if (input is null) break;  // Ctrl+C at empty prompt
        if (string.IsNullOrWhiteSpace(input)) continue;

        if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
            break;

        if (input.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            agent.Reset();
            Console.WriteLine("(Conversation reset)");
            continue;
        }

        Console.Write("Agent: ");

        using var cts = new CancellationTokenSource();
        var interrupted = false;

        // Start agent in background; poll for Escape in foreground
        var agentTask = ReadAgentOutput(agent, input, cts.Token);

        while (!agentTask.IsCompleted)
        {
            // Check for Escape key every 100ms
            var delay = Task.Delay(100);
            var completed = await Task.WhenAny(agentTask, delay);

            if (completed == delay)
            {
                // Poll for Escape
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

        // Wait for agent to finish cancelling
        try { await agentTask; }
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
}

async Task ReadAgentOutput(Agent agent, string input, CancellationToken ct)
{
    await foreach (var evt in agent.PromptAsync(input, ct: ct))
    {
        switch (evt.Type)
        {
            case AgentEventType.TextDelta:
                Console.Write(evt.Text);
                break;

            case AgentEventType.ToolCallStart:
                Console.Write($"\n\n  \u2699\ufe0f Calling tool: {evt.Text}... ");
                break;

            case AgentEventType.ToolCallEnd:
                Console.WriteLine("done.");
                Console.WriteLine();
                DisplayHelpers.DisplayTruncated(evt.ToolResult ?? "", cfg.DisplayLineWidth, cfg.DisplayMaxLines);
                Console.WriteLine();
                break;

            case AgentEventType.Response:
                Console.WriteLine();
                if (evt.Usage is not null)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine(
                        $"\n(Used {evt.Usage.InputTokens}\u2191 + {evt.Usage.OutputTokens}\u2193 tokens)");
                    Console.ResetColor();
                }
                break;

            case AgentEventType.Error:
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[Error: {evt.ErrorMessage}]");
                Console.ResetColor();
                break;
        }
    }
}

public static class DisplayHelpers
{
    /// <summary>
    /// Write output to the console, truncating every line to <paramref name="lineWidth"/>
    /// to prevent terminal wrapping, and capping at <paramref name="maxLines"/> lines.
    /// </summary>
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
            Console.ForegroundColor = prev;
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
