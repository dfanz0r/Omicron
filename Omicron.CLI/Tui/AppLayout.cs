using System.Text;
using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Rendering;
using Omicron.Core.Rendering.Layout;
using Omicron.Core.Rendering.Transcript;
using Omicron.Core.Sessions;

namespace Omicron.CLI.Tui;

/// <summary>
/// Main TUI application that integrates the <see cref="TuiShell"/> with
/// <see cref="TranscriptViewportWidget"/>, <see cref="InputEditorWidget"/>,
/// and <see cref="StatusBarWidget"/>. Supports slash commands via
/// <see cref="TuiSlashCommandBridge"/>.
/// </summary>
public sealed class AppLayout : IDisposable
{
    private readonly TuiShell _shell;
    private AgentSession _session;
    private readonly TranscriptViewportWidget _transcript = new();
    private readonly InputEditorWidget _input = new();
    private readonly StatusBarWidget _statusBar = new();
    private readonly VStack _root = new();
    private CancellationTokenSource _cts = new();
    private TuiSlashCommandBridge? _slashBridge;
    private string _modelKey = "";
    private bool _exiting;
    private bool _disposed;
    private bool _isGenerating;
    private bool _isFindMode;
    private bool _isDraggingScrollbar;
    private DateTime _lastDragRenderTime = DateTime.MinValue;
    private const int DragRenderIntervalMs = 16; // ~60 FPS cap during drag
    private string? _toastMessage;
    private DateTime _toastExpiry = DateTime.MinValue;

    /// <summary>Number of rows to scroll per mouse wheel tick.</summary>
    public int MouseScrollAmount { get; set; } = 3;

    /// <summary>Whether find/search mode is active.</summary>
    public bool IsFindMode => _isFindMode;

    public AppLayout(TuiShell shell, AgentSession session, string modelKey)
    {
        _shell = shell;
        _session = session;
        _modelKey = modelKey;

        _root.Gap = 1;
        _root.Add(new FlexSizeWidget { Child = _transcript });
        _root.Add(new FixedSizeWidget { Height = 1, Child = new DividerWidget() });
        _root.Add(_input); // dynamic height — may grow for completion popup
        _root.Add(new FixedSizeWidget { Height = 1, Child = _statusBar });

        _input.OnSubmit += OnInputSubmit;
        _transcript.RequestRender = () => _shell.RequestRender();

        _statusBar.ModelName = session.Model?.Name ?? session.Model?.Id ?? "unknown";
        _statusBar.ProviderName = session.Model?.ProviderName ?? "";
        _statusBar.StatusText = "ready";

        _transcript.Store.AppendNotice("");
        _transcript.Store.AppendNotice("  Welcome to Omicron TUI");
        _transcript.Store.AppendNotice($"  Model: {session.Model?.Name ?? "unknown"}");
        _transcript.Store.AppendNotice("  Type a message to start. /help for commands. Ctrl+D or Escape to exit.");
        _transcript.Store.AppendNotice("");
        _transcript.Layout.ReflowForWidth(Math.Max(1, _transcript.Layout.TerminalWidth), _transcript.Store);
        _transcript.Viewport.UpdateTotalRows(_transcript.Layout.TotalWrappedRows, Math.Max(1, _shell.CurrentFrame.Height - 2));
    }

    public TuiSlashCommandBridge? SlashBridge
    {
        set
        {
            if (_slashBridge is not null)
            {
                _slashBridge.OnSessionSwitched -= HandleSessionSwitched;
                _slashBridge.OnNotice -= ShowToast;
            }

            _slashBridge = value;
            if (_slashBridge is not null)
            {
                _slashBridge.OnSessionSwitched += HandleSessionSwitched;
                _slashBridge.OnNotice += ShowToast;
                _input.CompletionProvider = input => _slashBridge.GetCompletions(input, _modelKey);
            }
        }
    }

    private void ShowToast(string message)
    {
        _toastMessage = message;
        _toastExpiry = DateTime.UtcNow.AddSeconds(4);
        _shell.RequestRender();
    }

    public bool IsExiting => _exiting;

    public async Task RunAsync() => await _shell.RunAsync(onEvent: OnTerminalEvent, onRender: OnRender);

    public void ClearTranscript()
    {
        _transcript.Store.Clear();
        _transcript.Viewport.ScrollToBottom(0, Math.Max(1, _shell.CurrentFrame.Height - 2));
        _shell.RequestRender();
    }

    public void Exit()
    {
        _exiting = true;
        _shell.Cancel();
    }

    public void SwitchSession(AgentSession session, Model model, string modelKey)
    {
        // Cancel any in-progress generation first
        if (_isGenerating)
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            _isGenerating = false;
        }

        _session = session;
        _session.ResolvePendingToolCalls();
        _modelKey = modelKey;
        _statusBar.ModelName = model.Name;
        _statusBar.ProviderName = model.ProviderName;
        _statusBar.StatusText = "ready";
        _input.Clear();

        _transcript.LoadMessages(session.Messages);
        _transcript.Store.AppendNotice($"Switched to session {session.Id}");
        _transcript.Store.AppendNotice($"Model: {model.Name}");
        _transcript.Store.AppendNotice($"Provider: {model.ProviderName}");
        _transcript.Layout.ReflowForWidth(Math.Max(1, _transcript.Layout.TerminalWidth), _transcript.Store);
        _transcript.Viewport.UpdateTotalRows(_transcript.Layout.TotalWrappedRows, _transcript.ViewportHeight);
        _transcript.ScrollToBottom();
        _shell.RequestRender();
    }

    private void HandleSessionSwitched(AgentSession session, Model model, string modelKey)
        => SwitchSession(session, model, modelKey);

    private async Task<bool> OnTerminalEvent(TerminalEvent evt)
    {
        switch (evt)
        {
            case KeyEvent ke:
                // Ctrl+D — exit
                if (ke.Key == Key.Character && ke.Text?.Value == 4)
                {
                    if (_isGenerating)
                    {
                        _cts.Cancel();
                        _cts = new CancellationTokenSource();
                        _isGenerating = false;
                    }
                    Exit();
                    return true;
                }

                // Ctrl+F — toggle find/search mode
                if (ke.Key == Key.Character && ke.Text?.Value == 6) // Ctrl+F
                {
                    if (_isFindMode)
                    {
                        ExitFindMode();
                    }
                    else
                    {
                        EnterFindMode();
                    }
                    return true;
                }

                if (_isFindMode)
                {
                    return HandleFindKey(ke);
                }

                if (ke.Key == Key.Escape && !_input.HasContent)
                {
                    if (_isGenerating)
                    {
                        _cts.Cancel();
                        _cts = new CancellationTokenSource();
                        _isGenerating = false;
                        _statusBar.StatusText = "cancelled";
                        _shell.RequestRender();
                        return true;
                    }
                    Exit();
                    return true;
                }

                if (_input.HandleKey(ke))
                {
                    // If we were in find mode, search as the user types
                    if (_isFindMode)
                        UpdateFindQuery();
                    return true;
                }

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

            case MouseEvent me:
                // ── Scrollbar drag ──
                // Once grabbed, follow the mouse Y everywhere in the terminal
                // until the button is released.  Some terminals send Dragged
                // events while holding; others send Pressed on re-entry after
                // the cursor left the window. Accept either while dragging.
                if (_isDraggingScrollbar)
                {
                    if (me.Button == MouseButton.Left &&
                        (me.Kind == MouseEventKind.Dragged ||
                         me.Kind == MouseEventKind.Pressed))
                    {
                        _transcript.ScrollToMouseY(me.Row);
                        // Throttle renders during rapid dragging so the
                        // render pipeline isn't overwhelmed. The scroll
                        // position updates immediately but the screen only
                        // refreshes at ~60 FPS.
                        if ((DateTime.UtcNow - _lastDragRenderTime).TotalMilliseconds >= DragRenderIntervalMs)
                        {
                            _lastDragRenderTime = DateTime.UtcNow;
                            _shell.RequestRender();
                        }
                        return true;
                    }
                    if (me.Kind == MouseEventKind.Released)
                    {
                        _isDraggingScrollbar = false;
                        _shell.RequestRender(); // final render to clear any skipped frames
                        return true;
                    }
                }

                // Start a new scrollbar drag (only when Left is pressed inside
                // the scrollbar column).
                if (me.Button == MouseButton.Left &&
                    me.Kind == MouseEventKind.Pressed &&
                    _transcript.IsScrollbarHit(me.Row, me.Column))
                {
                    _isDraggingScrollbar = true;
                    _transcript.ScrollToMouseY(me.Row);
                    _shell.RequestRender();
                    return true;
                }

                // ── Mouse wheel ──
                switch (me.Button)
                {
                    case MouseButton.ScrollUp:
                        _transcript.ScrollUp(MouseScrollAmount);
                        return true;
                    case MouseButton.ScrollDown:
                        _transcript.ScrollDown(MouseScrollAmount);
                        return true;
                }
                break;
        }

        return false;
    }

    private void EnterFindMode()
    {
        _isFindMode = true;
        _input.Clear();
        _statusBar.StatusText = "find: type to search, Enter/Shift+Enter for next/prev, Escape to exit";
        _shell.RequestRender();
    }

    private void ExitFindMode()
    {
        _isFindMode = false;
        _input.Clear();
        _transcript.ClearFind();
        _statusBar.StatusText = "ready";
        _shell.RequestRender();
    }

    private void UpdateFindQuery()
    {
        string query = _input.Text;
        if (string.IsNullOrEmpty(query))
        {
            _transcript.ClearFind();
        }
        else
        {
            int count = _transcript.Find(query);
            _statusBar.StatusText = $"find: {count} matches";
        }
        _shell.RequestRender();
    }

    private bool HandleFindKey(KeyEvent ke)
    {
        switch (ke.Key)
        {
            case Key.Escape:
                ExitFindMode();
                return true;

            case Key.Enter when ke.Modifiers.HasFlag(KeyModifiers.Shift):
                _transcript.FindPrevious();
                _shell.RequestRender();
                return true;

            case Key.Enter:
                _transcript.FindNext();
                _shell.RequestRender();
                return true;

            case Key.Character when ke.Text.HasValue:
            {
                char c = (char)ke.Text.Value.Value;
                if (c >= 32 && c != 127)
                {
                    _input.Insert(c.ToString());
                    UpdateFindQuery();
                }
                return true;
            }

            case Key.Backspace:
                _input.Backspace();
                UpdateFindQuery();
                return true;

            default:
                return false;
        }
    }

    private async Task OnRender(TerminalFrame frame)
    {
        // Clear the frame before each render so stale content from previous
        // phases (model picker, etc.) doesn't leak through the diff renderer.
        frame.Clear();

        // The root layout already positions widgets by size; avoid double-arranging
        // individual widgets here so the layout engine stays the single source of truth.
        var layoutSize = new Size(frame.Width, frame.Height);
        _root.Measure(layoutSize);
        _root.Arrange(new Rect(0, 0, frame.Width, frame.Height));

        var context = new RenderContext(frame, new Rect(0, 0, frame.Width, frame.Height), TextStyle.Default);
        _root.Render(context);

        // Draw toast notification overlay (if active)
        if (_toastMessage is not null && DateTime.UtcNow < _toastExpiry)
        {
            DrawToast(frame, context);
        }
        else
        {
            _toastMessage = null;
        }
    }

    private void DrawToast(TerminalFrame frame, RenderContext context)
    {
        if (string.IsNullOrEmpty(_toastMessage)) return;

        string toast = _toastMessage.ReplaceLineEndings(" ");
        int toastWidth = Math.Min(toast.Length + 4, frame.Width - 4);
        int toastX = (frame.Width - toastWidth) / 2;
        if (toastX < 0) toastX = 0;

        // Dark background bar at the top
        int toastY = 0;
        var toastBg = new RenderCell
        {
            Glyph = GlyphRef.Ascii((byte)' '),
            Width = 1,
            Style = new TextStyle(255, 255, 255, 30, 30, 35, false, false, false),
        };
        context.FillRect(new Rect(toastX, toastY, toastWidth, 1), toastBg);

        // Draw text
        context.DrawText(toastX + 2, toastY,
            System.Text.Encoding.UTF8.GetBytes($" {toast} "),
            TextStyle.ForegroundOnly(200, 200, 200));
    }

    private void OnInputSubmit(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (_slashBridge is not null && _slashBridge.TryHandle(text, this, _session, _session.Model, _modelKey, out var displayMessage))
        {
            if (displayMessage is not null)
            {
                _transcript.Store.AppendNotice(displayMessage);
                _transcript.Layout.ReflowForWidth(Math.Max(1, _transcript.Layout.TerminalWidth), _transcript.Store);
                _transcript.Viewport.UpdateTotalRows(_transcript.Layout.TotalWrappedRows, _transcript.ViewportHeight);
                _shell.RequestRender();
            }
            return;
        }

        // The user message will be rendered when PromptAsync emits the
        // UserMessageEvent — UpdateFromEvent handles it. Do NOT eagerly
        // append here or the message appears twice (once now, once from
        // the event stream).

        _isGenerating = true;
        _statusBar.StatusText = "processing...";
        var localCts = _cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in _session.PromptAsync(text, localCts.Token))
                {
                    _transcript.UpdateFromEvent(evt);
                    if (evt is AssistantResponseCompleteEvent complete)
                    {
                        _statusBar.StatusText = $"↑{complete.Usage.InputTokens} ↓{complete.Usage.OutputTokens}";
                        _shell.RequestRender();
                    }
                    else if (evt is SessionErrorEvent)
                    {
                        _statusBar.StatusText = "error";
                        _shell.RequestRender();
                    }
                    else if (evt is ToolInvocationStartedEvent)
                    {
                        _statusBar.StatusText = "tool call...";
                        _shell.RequestRender();
                    }
                    else if (evt is ToolInvocationCompletedEvent)
                    {
                        _statusBar.StatusText = "response...";
                        _shell.RequestRender();
                    }
                }

                if (!localCts.IsCancellationRequested)
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
                _transcript.UpdateFromEvent(new SessionErrorEvent(EventEnvelope.ForSession(_session.Id), ex.Message, null));
                _shell.RequestRender();
            }
            finally
            {
                _isGenerating = false;
            }
        }, localCts.Token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
