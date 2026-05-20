using System.Text;
using Omicron.Core;
using Omicron.Core.Config;
using Omicron.Core.Models;
using Omicron.Core.Sessions;

namespace Omicron.CLI.Tui;

/// <summary>
///     Bridges the existing <see cref="SlashCommandDispatcher" /> into the TUI.
///     Intercepts messages starting with '/' and executes them as commands.
///     Captures console output from command handlers and redirects it to the TUI transcript.
/// </summary>
public sealed class TuiSlashCommandBridge
{
    private readonly IModelCatalog _catalog;
    private readonly AgentConfig _config;
    private readonly SlashCommandDispatcher _dispatcher = new();
    private readonly OmicronHost _host;

    public TuiSlashCommandBridge(OmicronHost host, AgentConfig config, IModelCatalog catalog)
    {
        _host = host;
        _config = config;
        _catalog = catalog;
    }

    public event Action<AgentSession, Model, string>? OnSessionSwitched;
    public event Action<string>? OnNotice;

    /// <summary>
    ///     Invoke the OnNotice event (used for toast-style notifications).
    /// </summary>
    public void Notify(string message)
    {
        OnNotice?.Invoke(message);
    }

    /// <summary>
    ///     Returns possible completions for the current input (for Tab key).
    /// </summary>
    public IReadOnlyList<string> GetCompletions(string input, string modelKey)
    {
        // Use first visible model as fallback for completion context
        Model? model = null;
        if (!string.IsNullOrEmpty(modelKey) && _catalog.Models.TryGetValue(modelKey, out Model? m))
        {
            model = m;
        }
        else
        {
            model = _catalog.Models.FirstOrDefault().Value;
        }

        model ??= new Model
        {
            Id = "",
            Name = "",
            ProviderName = "",
            BaseUrl = ""
        }; // dummy for completion-only context

        var ctx = new SlashCommandContext(_host, null!, model, modelKey, _config, _catalog);
        return _dispatcher.GetCompletions(input, ctx);
    }

    /// <summary>
    ///     Try to handle a slash command. Returns true if the input was a command
    ///     and was handled (suppress normal session submission).
    /// </summary>
    public bool TryHandle(
        string input,
        AppLayout app,
        AgentSession session,
        Model model,
        string modelKey,
        out string? displayMessage)
    {
        displayMessage = null;

        if (!_dispatcher.IsCommand(input))
        {
            return false;
        }

        var ctx = new SlashCommandContext(_host, session, model, modelKey, _config, _catalog);

        // Capture Console.WriteLine and Console.Error output during command execution
        var captured = new StringBuilder();
        TextWriter oldOut = Console.Out;
        TextWriter oldError = Console.Error;
        var errorWriter = new StringWriter(captured);
        Console.SetOut(new StringWriter(captured));
        Console.SetError(errorWriter);
        try
        {
            ChatCommandResult result = _dispatcher.Execute(input, ctx);

            // Handle actions that affect TUI state
            switch (result.Action)
            {
                case ChatCommandAction.ExitApp:
                    displayMessage = string.IsNullOrEmpty(result.Message)
                        ? "Exiting..."
                        : result.Message;
                    app.Exit();
                    break;

                case ChatCommandAction.ResetSession:
                    session.Reset();
                    app.ClearTranscript();
                    displayMessage = "(Conversation and provider state reset)";
                    break;

                case ChatCommandAction.SwitchModel:
                    if (
                        result.ModelKey is not null
                        && _catalog.Models.TryGetValue(result.ModelKey, out Model? newModel)
                    )
                    {
                        string? newApiKey = ResolveApiKey(newModel);
                        if (string.IsNullOrEmpty(newApiKey))
                        {
                            displayMessage =
                                $"No API key available for {newModel.ProviderName}. Use 'key set {newModel.ProviderName}' first.";
                        }
                        else
                        {
                            var newConfig = SessionConfig.Create(newModel,
                                _config.SystemPrompt ?? "You are a helpful assistant.",
                                newApiKey,
                                _config.DefaultMaxTokens,
                                _config.DefaultTemperature,
                                maxIterations: _config.MaxIterations);
                            AgentSession newSession = _host.CreateSession(newConfig);
                            OnSessionSwitched?.Invoke(newSession, newModel, result.ModelKey);
                            displayMessage = $"Switched to model: {newModel.Name}";
                        }
                    }
                    else
                    {
                        displayMessage =
                            $"Model '{result.ModelKey ?? "(none)"}' not found in catalog.";
                    }

                    break;

                case ChatCommandAction.SwitchSession:
                    if (result.NewSession is not null && result.NewModel is not null)
                    {
                        OnSessionSwitched?.Invoke(result.NewSession,
                            result.NewModel,
                            result.NewModelKey ?? modelKey);
                        displayMessage =
                            captured.Length > 0
                                ? captured.ToString().TrimEnd()
                                : "Session switched.";
                    }
                    else
                    {
                        displayMessage = "Unable to switch session.";
                    }

                    break;

                case ChatCommandAction.ClearProviderState:
                    displayMessage =
                        captured.Length > 0
                            ? captured.ToString().TrimEnd()
                            : "Provider state cleared.";
                    break;

                default:
                    // Continue: display captured output in transcript
                    displayMessage = captured.Length > 0 ? captured.ToString().TrimEnd() : null;
                    break;
            }
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }

        return true;
    }

    private string? ResolveApiKey(Model selectedModel)
    {
        if (_config.ApiKeys.TryGetValue(selectedModel.ProviderName, out string? saved))
        {
            return saved;
        }

        string? envVarName = selectedModel.ProviderName.ToLowerInvariant() switch
        {
            "openai" => "OPENAI_API_KEY",
            "anthropic" => "ANTHROPIC_API_KEY",
            "opencode" or "opencode-go" => "OPENCODE_API_KEY",
            "openrouter" => "OPENROUTER_API_KEY",
            _ => null
        };

        return envVarName is not null ? Environment.GetEnvironmentVariable(envVarName) : null;
    }
}
