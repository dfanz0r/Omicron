using System.Text;
using System.Threading.Channels;
using Omicron.Core.Events;
using Omicron.Core.Rendering;
using Omicron.Core.Rendering.Layout;
using Omicron.Core.Rendering.Transcript;
using Omicron.Core.Sessions;

namespace Omicron.CLI.Tui;

/// <summary>
/// Main TUI application that integrates the <see cref="TuiShell"/> with
/// <see cref="TranscriptViewportWidget"/>, <see cref="InputEditorWidget"/>,
/// and <see cref="StatusBarWidget"/>.
/// </summary>
public sealed class AppLayout : IDisposable
{
    private readonly TuiShell _shell;
    private readonly AgentSession _session;
    private readonly TranscriptViewportWidget _transcript = new();
    private readonly InputEditorWidget _input = new();
    private readonly StatusBarWidget _statusBar = new();
    private readonly VStack _root = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public AppLayout(TuiShell shell, AgentSession session)
    {
        _shell = shell;
        _session = session;

        // Build layout tree
        _root.Add(new FlexSizeWidget { Child = _transcript });
        _root.Add(new FixedSizeWidget { Height = 1, Child = _statusBar });
        _root.Add(new FixedSizeWidget { Height = 1, Child = _input });

        // Wire input submit
        _input.OnSubmit += OnInputSubmit;

        // Wire streaming render requests
        _transcript.RequestRender = () => _shell.RequestRender();

        // Set initial status
        _statusBar.ModelName = session.Model?.Name ?? session.Model?.Id ?? "unknown";
        _statusBar.ProviderName = session.Model?.ProviderName ?? "";
        _statusBar.StatusText = "ready";
    }

    /// <summary>Run the TUI application.</summary>
    public async Task RunAsync()
    {
        await _shell.RunAsync(
            onEvent: OnTerminalEvent,
            onRender: OnRender);
    }

    private async Task<bool> OnTerminalEvent(TerminalEvent evt)
    {
        switch (evt)
        {
            case KeyEvent ke:
                // Exit on Ctrl+D when input is empty
                if (ke.Key == Key.Character && ke.Text?.Value == 4 && !_input.HasContent)
                {
                    _shell.Cancel();
                    return true;
                }

                // Exit on Escape when input is empty
                if (ke.Key == Key.Escape && !_input.HasContent)
                {
                    _shell.Cancel();
                    return true;
                }

                // Always allow input editor to consume keys first
                if (_input.HandleKey(ke))
                {
                    // After submit, process the message
                    return true;
                }

                // Navigation keys for transcript
                switch (ke.Key)
                {
                    case Key.PageUp:
                        _transcript.ScrollUp(_shell.CurrentFrame.Height - 2);
                        return true;

                    case Key.PageDown:
                        _transcript.ScrollDown(_shell.CurrentFrame.Height - 2);
                        return true;

                    case Key.Home when ke.Modifiers.HasFlag(KeyModifiers.Control):
                        _transcript.ScrollToTop();
                        return true;

                    case Key.End when ke.Modifiers.HasFlag(KeyModifiers.Control):
                        _transcript.ScrollToBottom();
                        return true;
                }
                break;
        }

        return false;
    }

    private async Task OnRender(TerminalFrame frame)
    {
        // Measure, arrange, render
        var terminalSize = new Size(frame.Width, frame.Height);
        _root.Measure(terminalSize);
        _root.Arrange(new Rect(0, 0, frame.Width, frame.Height));

        var context = new RenderContext(frame,
            new Rect(0, 0, frame.Width, frame.Height),
            TextStyle.Default);
        _root.Render(context);
    }

    private void OnInputSubmit(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        _statusBar.StatusText = "processing...";

        // Fire-and-forget the session prompt
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in _session.PromptAsync(text, _cts.Token))
                {
                    _transcript.UpdateFromEvent(evt);
                    _shell.RequestRender();

                    // Update status for specific events
                    if (evt is AssistantResponseCompleteEvent complete)
                    {
                        _statusBar.StatusText =
                            $"\u2191{complete.Usage.InputTokens} \u2193{complete.Usage.OutputTokens}";
                    }
                    else if (evt is SessionErrorEvent)
                    {
                        _statusBar.StatusText = "error";
                    }
                    else if (evt is ToolInvocationStartedEvent)
                    {
                        _statusBar.StatusText = "tool call...";
                    }
                    else if (evt is ToolInvocationCompletedEvent)
                    {
                        _statusBar.StatusText = "response...";
                    }

                    _shell.RequestRender();
                }

                if (!_cts.IsCancellationRequested)
                {
                    _statusBar.StatusText = "ready";
                    _shell.RequestRender();
                }
            }
            catch (OperationCanceledException)
            {
                _statusBar.StatusText = "cancelled";
                _shell.RequestRender();
            }
            catch (Exception ex)
            {
                _statusBar.StatusText = "error";
                _transcript.UpdateFromEvent(new SessionErrorEvent(
                    EventEnvelope.ForSession(_session.Id), ex.Message, null));
                _shell.RequestRender();
            }
        }, _cts.Token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _shell.Dispose();
    }
}
