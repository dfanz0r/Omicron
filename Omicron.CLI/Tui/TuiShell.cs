using System.Threading.Channels;
using Omicron.Core.Rendering;
using Omicron.Core.Text;

namespace Omicron.CLI.Tui;

/// <summary>
/// Minimal fullscreen TUI application shell. Owns the terminal backend,
/// differential renderer, and event/render loop. Frame-paced rendering
/// with configurable target FPS.
/// </summary>
public sealed class TuiShell : IDisposable
{
    private readonly ITerminalBackend _backend;
    private readonly DifferentialRenderer _renderer;
    private TerminalFrame _currentFrame;
    private readonly Channel<bool> _renderSignal;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    /// <summary>Target FPS for frame pacing. 0 = unlimited.</summary>
    public int TargetFps { get; set; } = 60;

    /// <summary>The terminal backend used by this shell.</summary>
    public ITerminalBackend Backend => _backend;

    /// <summary>The current frame buffer (draw into this).</summary>
    public TerminalFrame CurrentFrame => _currentFrame;

    public TuiShell(ITerminalBackend backend)
    {
        _backend = backend;
        _renderer = new DifferentialRenderer(backend.Size.Width, backend.Size.Height);
        _currentFrame = new TerminalFrame(backend.Size.Width, backend.Size.Height);
        _renderSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Run the main event/render loop until cancellation.
    /// </summary>
    /// <param name="onEvent">Called for each terminal event. If it returns true, a render is requested.</param>
    /// <param name="onRender">Called when a render is due. Draw into <see cref="CurrentFrame"/>.</param>
    public async Task RunAsync(
        Func<TerminalEvent, Task<bool>> onEvent,
        Func<TerminalFrame, Task> onRender)
    {
        // Enter alternate screen and hide cursor
        using var altScreen = TerminalScope.UseAlternateScreen(_backend);
        using var hideCursor = TerminalScope.HideCursor(_backend);

        RequestRender(); // Trigger initial render

        var eventStream = _backend.ReadEvents(_cts.Token).GetAsyncEnumerator(_cts.Token);

        // MoveNextAsync doesn't accept CancellationToken when used via GetAsyncEnumerator;
        // cancellation is handled by the CancellationToken passed to ReadEvents.

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                // Wait for either an event or a render signal
                var eventTask = eventStream.MoveNextAsync().AsTask();
                var renderTask = _renderSignal.Reader.WaitToReadAsync(_cts.Token).AsTask();

                var completed = await Task.WhenAny(eventTask, renderTask);

                if (completed == renderTask)
                {
                    // Render signal
                    if (renderTask.Result)
                    {
                        _renderSignal.Reader.TryRead(out _);
                        await onRender(_currentFrame);
                        _renderer.Render(_currentFrame, _backend.Output);
                        _backend.Flush();

                        // Frame pacing
                        if (TargetFps > 0)
                        {
                            int frameMs = 1000 / TargetFps;
                            if (frameMs > 0)
                                await Task.Delay(frameMs, _cts.Token);
                        }
                    }
                }
                else if (completed == eventTask)
                {
                    // Terminal event
                    if (!eventTask.Result)
                        break; // Stream ended

                    var evt = eventStream.Current;

                    // Handle resize
                    if (evt is ResizeEvent resize)
                    {
                        _renderer.Resize(resize.Width, resize.Height);
                        _currentFrame.Resize(resize.Width, resize.Height);
                        RequestRender();
                        continue;
                    }

                    // Handle Ctrl+D or Ctrl+C
                    if (evt is KeyEvent ke)
                    {
                        if (ke.Key == Key.Character && ke.Text?.Value == 4) // Ctrl+D
                            break;
                        if (ke.Key == Key.Escape)
                        {
                            // Allow caller to handle Escape
                        }
                    }

                    bool needsRender = await onEvent(evt);
                    if (needsRender)
                        RequestRender();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            try
            {
                await eventStream.DisposeAsync();
            }
            catch (NotSupportedException)
            {
                // Some platform stream enumerators don't support disposal
            }
        }
    }

    /// <summary>Signal the render loop to render the next frame.</summary>
    public void RequestRender()
    {
        _renderSignal.Writer.TryWrite(true);
    }

    /// <summary>Request cancellation of the event/render loop.</summary>
    public void Cancel() => _cts.Cancel();

    /// <summary>Resize the frame buffer when the terminal changes size.</summary>
    public void HandleResize(int width, int height)
    {
        _renderer.Resize(width, height);
        _currentFrame.Resize(width, height);
        RequestRender();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _backend.Dispose();
    }
}
