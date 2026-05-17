using System.Diagnostics;
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

    private CancellationTokenSource _cts;
    private bool _disposed;

    /// <summary>Target FPS for frame pacing. 0 = unlimited.</summary>
    public int TargetFps { get; set; } = 60;

    private long _lastRenderTicks;

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


    /// <summary>Reset internal state so RunAsync can be called again.</summary>
    public void ResetState()
    {
        if (_cts.IsCancellationRequested)
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }
        // Drain any stale render signals
        while (_renderSignal.Reader.TryRead(out _)) { }
    }

    /// <summary>
    /// Force a full screen clear + full frame redraw on the next render.
    /// Call this when switching from a modal overlay (picker, prompt) back
    /// to the main app so the diff renderer doesn't leave modal artifacts.
    /// </summary>
    public void ForceFullRedraw() => _renderer.ForceFullRedraw();

    public async Task RunAsync(
        Func<TerminalEvent, Task<bool>> onEvent,
        Func<TerminalFrame, Task> onRender)
    {
        ResetState();

        // Enter alternate screen, hide cursor, enable mouse tracking, bracketed paste
        // Kitty keyboard protocol is negotiated during backend initialization
        // during backend initialization, not as a terminal scope.
        using var altScreen = TerminalScope.UseAlternateScreen(_backend);
        using var hideCursor = TerminalScope.HideCursor(_backend);
        using var mouseScope = TerminalScope.UseMouse(_backend);
        using var bracketedPaste = TerminalScope.UseBracketedPaste(_backend);

        // Ensure frame/renderer have the correct terminal size before first render.
        // On some platforms (e.g. Windows via dotnet run) the initial backend.Size
        // may be (0,0) until Initialize() has run.
        var actualSize = _backend.Size;
        if (_currentFrame.Width != actualSize.Width || _currentFrame.Height != actualSize.Height)
        {
            _renderer.Resize(actualSize.Width, actualSize.Height);
            _currentFrame.Resize(actualSize.Width, actualSize.Height);
        }

        // Render the first frame before starting the input stream. Some native
        // backends perform synchronous polling before their first async
        // suspension point; if we start MoveNextAsync first, the alternate
        // screen can be entered while the initial render signal is starved,
        // presenting as a black screen until input arrives.
        await onRender(_currentFrame);
        _renderer.Render(_currentFrame, _backend.Output);
        _lastRenderTicks = Stopwatch.GetTimestamp();
        _backend.Flush();

        var eventStream = _backend.ReadEvents(_cts.Token).GetAsyncEnumerator(_cts.Token);

        // MoveNextAsync doesn't accept CancellationToken when used via GetAsyncEnumerator;
        // cancellation is handled by the CancellationToken passed to ReadEvents.

        try
        {
            ValueTask<bool> moveNextTask = default;
            bool hasPendingMoveNext = false;
            Task<bool>? eventTask = null;

            while (!_cts.Token.IsCancellationRequested)
            {
                if (!hasPendingMoveNext)
                {
                    moveNextTask = eventStream.MoveNextAsync();
                    eventTask = moveNextTask.AsTask();
                    hasPendingMoveNext = true;
                }

                var renderTask = _renderSignal.Reader.WaitToReadAsync(_cts.Token).AsTask();
                var completed = await Task.WhenAny(eventTask!, renderTask);

                if (completed == renderTask)
                {
                    if (renderTask.Result)
                    {
                        // Frame pacing: respect TargetFps
                        if (TargetFps > 0)
                        {
                            long now = Stopwatch.GetTimestamp();
                            long minInterval = Stopwatch.Frequency / TargetFps;
                            long elapsed = now - _lastRenderTicks;
                            if (elapsed < minInterval)
                            {
                                int delayMs = (int)((minInterval - elapsed) * 1000 / Stopwatch.Frequency);
                                if (delayMs > 0)
                                    await Task.Delay(delayMs);
                            }
                        }

                        _renderSignal.Reader.TryRead(out _);
                        await onRender(_currentFrame);
                        _renderer.Render(_currentFrame, _backend.Output);
                        _lastRenderTicks = Stopwatch.GetTimestamp();
                        _backend.Flush();
                    }
                }
                else if (completed == eventTask)
                {
                    hasPendingMoveNext = false;

                    if (!eventTask.Result)
                        break; // Stream ended

                    var evt = eventStream.Current;

                    if (evt is ResizeEvent resize)
                    {
                        _renderer.Resize(resize.Width, resize.Height);
                        _currentFrame.Resize(resize.Width, resize.Height);
                        RequestFullRedraw();
                        continue;
                    }

                    if (await onEvent(evt))
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



    /// <summary>Force a full frame redraw on the next render (avoids diff flicker).</summary>
    public void RequestFullRedraw()
    {
        _renderer.RequestFullRedraw();
        RequestRender();
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
