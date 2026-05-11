using System.Text;
using Omicron.Core.Events;
using Omicron.Core.Rendering;
using Omicron.Core.Rendering.Layout;
using Omicron.Core.Rendering.Transcript;
using Omicron.Core.Text;

namespace Omicron.CLI.Tui;

/// <summary>
/// Renders the conversation transcript into a <see cref="TerminalFrame"/> sub-rectangle.
/// Owns the <see cref="TranscriptStore"/>, <see cref="TranscriptLayoutCache"/>,
/// and <see cref="ViewportState"/>.
/// </summary>
public sealed class TranscriptViewportWidget : ITuiWidget
{
    private readonly TranscriptStore _store = new();
    private readonly TranscriptLayoutCache _layout = new();
    private readonly ViewportState _viewport = new();
    private readonly Dictionary<BlockId, TranscriptBlock> _blockById = new();
    private Rect _bounds;

    /// <summary>The transcript store.</summary>
    public TranscriptStore Store => _store;

    /// <summary>The layout cache.</summary>
    public TranscriptLayoutCache Layout => _layout;

    /// <summary>The viewport state.</summary>
    public ViewportState Viewport => _viewport;

    /// <summary>Callback invoked when a render is needed after streaming updates.</summary>
    public Action? RequestRender { get; set; }

    /// <summary>Scroll up by the given number of rows.</summary>
    public void ScrollUp(int rows) => _viewport.ScrollUp(rows);

    /// <summary>Scroll down by the given number of rows.</summary>
    public void ScrollDown(int rows) => _viewport.ScrollDown(rows);

    /// <summary>Scroll to the top of the transcript.</summary>
    public void ScrollToTop() => _viewport.ScrollToTop();

    /// <summary>Scroll to the bottom of the transcript.</summary>
    public void ScrollToBottom() => _viewport.ScrollToBottom(_layout.TotalWrappedRows, _bounds.Height);

    /// <summary>
    /// Process an OmicronEvent and update the transcript store accordingly.
    /// </summary>
    public void UpdateFromEvent(OmicronEvent evt)
    {
        switch (evt)
        {
            case UserMessageEvent ue:
                var userBytes = Encoding.UTF8.GetBytes(ue.Text);
                _store.AppendUserMessage(userBytes);
                FlushPending();
                RebuildLayout();
                break;

            case AssistantTextDeltaEvent delta:
                // Flush every delta so streaming text is visible immediately
                _pendingAssistantText.Append(delta.Delta);
                _pendingAssistantBytes += Encoding.UTF8.GetByteCount(delta.Delta);
                FlushPending();
                InvalidateLastBlock();
                RequestRender?.Invoke();
                break;

            case AssistantResponseCompleteEvent complete:
                FlushPending();
                if (_store.Blocks.Count > 0 && _store.Blocks[^1] is AssistantMessageBlock)
                {
                    _store.CompleteLastAssistantBlock();
                }
                RebuildLayout();
                break;

            case ToolInvocationStartedEvent tis:
                FlushPending();
                _store.AppendToolCall(tis.ToolName);
                RebuildLayout();
                break;

            case ToolInvocationCompletedEvent tic:
                var resultBytes = Encoding.UTF8.GetBytes(
                    tic.IsError
                        ? $"\n[Error: {tic.Result}]"
                        : $"\n{tic.Result}");
                // Update the last tool call block
                var blocks = _store.Blocks;
                for (int i = blocks.Count - 1; i >= 0; i--)
                {
                    if (blocks[i] is ToolCallBlock tcb)
                    {
                        _store.UpdateToolCall(tcb.Id, ToolCallState.Completed, resultBytes);
                        break;
                    }
                }
                RebuildLayout();
                break;

            case SessionErrorEvent err:
                FlushPending();
                _store.AppendNotice($"[Error: {err.Message}]");
                RebuildLayout();
                break;

            case SessionResetEvent:
                FlushPending();
                _store.Clear();
                _viewport.ScrollToBottom(0, _bounds.Height);
                _layout.ReflowForWidth(Math.Max(1, _bounds.Width), _store);
                break;
        }
    }

    private void InvalidateLastBlock()
    {
        if (_store.Blocks.Count > 0)
        {
            var lastBlockId = _store.Blocks[^1].Id;
            _layout.InvalidateFrom(lastBlockId);
            _layout.ReflowForWidth(Math.Max(1, _bounds.Width), _store);
            _viewport.UpdateTotalRows(_layout.TotalWrappedRows, _bounds.Height);
        }
    }

    // Streaming state
    private readonly StringBuilder _pendingAssistantText = new();
    private int _pendingAssistantBytes;

    private void FlushPending()
    {
        if (_pendingAssistantText.Length > 0)
        {
            var utf8 = Encoding.UTF8.GetBytes(_pendingAssistantText.ToString());
            _store.AppendAssistantDelta(utf8);
            _pendingAssistantText.Clear();
            _pendingAssistantBytes = 0;
        }
    }

    private void RebuildLayout()
    {
        _layout.InvalidateFrom(BlockId.None);
        _layout.ReflowForWidth(Math.Max(1, _bounds.Width), _store);
        _viewport.UpdateTotalRows(_layout.TotalWrappedRows, _bounds.Height);
        RebuildBlockIndex();
    }

    private void RebuildBlockIndex()
    {
        _blockById.Clear();
        foreach (var block in _store.Blocks)
            _blockById[block.Id] = block;
    }

    // ── ITuiWidget implementation ──

    public Size Measure(Size available) => available;

    public void Arrange(Rect bounds)
    {
        _bounds = bounds;
        if (_layout.TerminalWidth != bounds.Width && bounds.Width > 0)
        {
            _layout.ReflowForWidth(bounds.Width, _store);
            _viewport.UpdateTotalRows(_layout.TotalWrappedRows, _bounds.Height);
            RebuildBlockIndex();
        }
    }

    public void Render(RenderContext context)
    {
        int visibleHeight = _bounds.Height;
        int firstRow = _viewport.FirstVisibleWrappedRow;

        var visibleLines = _layout.GetVisibleLines(firstRow, visibleHeight);

        int row = 0;
        foreach (var line in visibleLines)
        {
            if (row >= visibleHeight)
                break;

            // Read the bytes from the store
            var bytes = _store.Text.Slice(line.ByteStart, line.ByteLength);

            // Determine style based on block type (O(1) via dictionary)
            var style = GetStyleForBlock(line.BlockId);

            // Draw the line at the widget's bounds Y + row
            if (bytes.Length > 0)
            {
                context.DrawText(_bounds.X, _bounds.Y + row, bytes.Span, style);
            }
            row++;
        }

        // Draw unseen-line indicator
        if (_viewport.UnseenLineCount > 0 && !_viewport.FollowTail)
        {
            string indicator = $" \u2193 {_viewport.UnseenLineCount} new ";
            int indicatorX = _bounds.X + _bounds.Width - GetDisplayWidth(indicator);
            if (indicatorX >= _bounds.X && row > 0)
            {
                context.DrawText(indicatorX, _bounds.Y + row - 1,
                    Encoding.UTF8.GetBytes(indicator),
                    TextStyle.Inverted);
            }
        }
    }

    private TextStyle GetStyleForBlock(BlockId blockId)
    {
        if (_blockById.TryGetValue(blockId, out var block))
        {
            return block switch
            {
                UserMessageBlock => TextStyle.ForegroundOnly(100, 200, 255), // light blue
                AssistantMessageBlock => TextStyle.Default,
                ToolCallBlock => TextStyle.ForegroundOnly(128, 128, 128), // dim gray
                SystemNoticeBlock => TextStyle.ForegroundOnly(255, 100, 100), // red-ish
                _ => TextStyle.Default
            };
        }

        return TextStyle.Default;
    }

    private static int GetDisplayWidth(string text)
    {
        int width = 0;
        foreach (var rune in text.EnumerateRunes())
            width += CellWidthCalculator.GetWidth(rune);
        return width;
    }
}
