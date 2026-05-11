# Scrollback Flicker Findings

## Summary

The current TUI does use an alternate screen, so the issue is probably **not** that alt-screen mode was omitted.

### What appears fixed

- The specific viewport math bug called out in the original findings appears to be resolved. `ViewportState.UpdateTotalRows(int totalWrappedRows, int viewportHeight = 1)` still expects a viewport height as its second argument, and the current call sites pass the actual viewport height instead of a scroll offset.
- In particular, the slash-command path no longer appears to pass `FirstVisibleWrappedRow` into `UpdateTotalRows(...)`.

### What does not appear fixed yet

- The more likely cause of scrollback flicker is still **render churn**: the UI redraws too often while streamed transcript content is still changing, especially when the transcript viewport is being reflowed and updated repeatedly.
- The transcript widget still performs frequent layout invalidation and reflow during assistant streaming.
- When the user is scrolled away from the tail, visible rows and unseen-line indicators can still change while the transcript is being updated.

In short:

- **Alt screen is enabled** in the TUI shell.
- The renderer still uses a **swap chain + differential rendering** approach, so the buffering layer itself appears legitimate rather than absent.
- `AppLayout.OnRender()` still clears the frame every pass before drawing, which may be fine if the diff renderer tracks prior frame state separately, but it remains worth verifying because it can increase redraw pressure if the buffer model is not fully isolated.
- The transcript widget still does **frequent layout invalidation and reflow** during assistant streaming.
- The viewport-height bug appears fixed, but render churn remains the primary unresolved concern.

## Relevant code paths

### TUI shell

`Omicron.CLI/Tui/TuiShell.cs`

Key observations:

- `RunAsync()` enters alternate screen mode:
  - `TerminalScope.UseAlternateScreen(_backend)`
  - `TerminalScope.HideCursor(_backend)`
  - `TerminalScope.UseMouse(_backend)`
- Rendering flow is:
  - `await onRender(_currentFrame);`
  - `_renderer.Render(_currentFrame, _backend.Output);`
  - `_backend.Flush();`

This means the TUI is already isolated from the normal terminal scrollback. However, alt-screen alone does not prevent flicker caused by repeated redraws or layout instability.

### Transcript viewport

`Omicron.CLI/Tui/TranscriptViewportWidget.cs`

The most suspicious logic is in streaming handling:

```csharp
case AssistantTextDeltaEvent delta:
    if (!_assistantPrefixAdded)
    {
        _store.AppendSeparator();
        _pendingAssistantText.Append("  Agent: ");
        _assistantPrefixAdded = true;
    }
    _pendingAssistantText.Append(delta.Delta);
    _pendingAssistantBytes += Encoding.UTF8.GetByteCount(delta.Delta);
    FlushPending();
    InvalidateLastBlock();
    RequestRender?.Invoke();
    break;
```

`InvalidateLastBlock()` then does:

- `InvalidateFrom(lastBlockId)`
- `ReflowForWidth(...)`
- `_viewport.UpdateTotalRows(...)`
- `RebuildBlockIndex()`

That means the transcript can be reflowed on every streamed delta, which is expensive and can cause visible jitter.

### App layout

`Omicron.CLI/Tui/AppLayout.cs`

During generation, the app requests renders repeatedly:

- after every transcript event
- again after status updates
- again in some completion/error paths

Also, streaming transcript deltas currently trigger layout invalidation and reflow on every token-sized update, which is likely the highest-cost part of the jitter.

This creates render churn, especially when streaming assistant text arrives in rapid succession.

## Most likely causes of the flicker / stutter

### 1. Layout thrash during streaming
The transcript is reflowed and its viewport metadata is updated repeatedly as deltas arrive. If the user is scrolled away from the bottom, the visible window can change while content is still being appended.

This is also the likely source of the reported scrollback stutter: the viewport state is being recomputed while content is still changing, so the visible range and unseen-line indicator may oscillate.

### 2. Too many render requests
The app asks for redraws multiple times per incoming event. Even with a diff renderer, this can produce visible instability if frames change faster than they can be rendered smoothly.

There is also a duplicate render request in the prompt event loop, which likely adds unnecessary pressure.

### 3. Double buffering is probably not the primary issue
The current system uses a `TerminalFrame` plus a `DifferentialRenderer` backed by a swap chain. That means the basic buffering model is present and working at the design level.

The more likely problem is not absence of double buffering, but viewport math and redraw churn during streaming.

## Why alt-screen does not fully solve it

Alt-screen prevents the TUI from contaminating the normal terminal scrollback. It does **not** guarantee flicker-free drawing.

Flicker can still happen because of:

- frequent full or partial redraws
- layout recomputation during streaming
- viewport changes while the transcript grows
- diff renderer invalidation behavior

So the assumption that "alt mode should prevent this" is understandable, but incomplete.

## Updated recommendations

### Short-term fixes

1. **Coalesce render requests**
   - Avoid calling `RequestRender()` multiple times for the same event/burst.
   - Remove redundant render requests in the input/generation loop.

2. **Throttle transcript reflow**
   - Reflow the transcript less often during streaming.
   - Consider batching deltas and updating the layout at a lower frequency.
   - Avoid recomputing scrollback metrics from the wrong viewport argument.

3. **Avoid auto-shifting the viewport when the user is scrolled up**
   - Keep the current scroll position stable when not following the tail.

4. **Verify the diff renderer**
   - Check whether the renderer is repainting large regions too often.
   - Confirm whether it is truly minimizing terminal writes.
   - Verify that clearing the frame before render is compatible with the renderer's diff model.
   - But treat renderer issues as secondary until the viewport-height bug is corrected.

### Medium-term improvements

1. Add a render debounce in the TUI shell.
2. Introduce a streaming text buffer that only flushes layout changes periodically.
3. Make viewport-follow behavior explicit:
   - when at tail, follow new content
   - when scrolled up, preserve the visible window
4. Consider a more explicit backbuffer/frontbuffer strategy if the diff renderer proves unstable.

## Files to inspect next

If you want to continue investigating, the next most useful files are:

- `Omicron.CLI/Tui/DifferentialRenderer.cs`
- `Omicron.CLI/Tui/TerminalScope.cs`
- `Omicron.CLI/Tui/SystemTerminalBackend.cs`
- `Omicron.Core/Rendering/Layout/ViewportState.cs`
- `Omicron.Core/Rendering/Transcript/*`

Those should reveal whether the flicker is coming from terminal mode handling, diff rendering, or viewport/layout invalidation.
