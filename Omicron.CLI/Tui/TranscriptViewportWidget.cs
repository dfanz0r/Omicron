using System;
using System.Text;
using Cysharp.Text;
using Omicron.Core.Content;
using Omicron.Core.Events;
using Omicron.Core.Models;
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
    private bool _assistantPrefixAdded;

    public TranscriptStore Store => _store;
    public int ViewportHeight => _bounds.Height;
    public TranscriptLayoutCache Layout => _layout;
    public ViewportState Viewport => _viewport;
    public Action? RequestRender { get; set; }
    public Rect Bounds => _bounds;

    private DateTime _lastScrollTime = DateTime.MinValue;
    private const int ScrollbarTimeoutMs = 2000;

    private bool IsScrollbarVisible =>
        _layout.TotalWrappedRows > _bounds.Height &&
        (DateTime.UtcNow - _lastScrollTime).TotalMilliseconds < ScrollbarTimeoutMs;

    /// <summary>
    /// Whether the given terminal coordinate is within the scrollbar column.
    /// </summary>
    public bool IsScrollbarHit(int row, int col) =>
        IsScrollbarVisible &&
        col == _bounds.X + _bounds.Width - 1 &&
        row >= _bounds.Y && row < _bounds.Bottom;

    /// <summary>
    /// Whether the given terminal coordinate is on the scrollbar thumb.
    /// </summary>
    public bool IsScrollbarThumbHit(int row, int col)
    {
        if (!IsScrollbarHit(row, col)) return false;

        int totalRows = _layout.TotalWrappedRows;
        int viewportHeight = _bounds.Height;
        int thumbHeight = Math.Max(1, viewportHeight * viewportHeight / totalRows);
        int maxScroll = Math.Max(1, totalRows - viewportHeight);
        int thumbStart = _viewport.FirstVisibleWrappedRow * (viewportHeight - thumbHeight) / maxScroll;

        int relativeY = row - _bounds.Y;
        return relativeY >= thumbStart && relativeY < thumbStart + thumbHeight;
    }

    /// <summary>
    /// Scroll so the scrollbar thumb centres on the given mouse Y (terminal row).
    /// Clicking anywhere on the scrollbar track jumps to that proportional
    /// position.
    /// </summary>
    public void ScrollToMouseY(int mouseY)
    {
        int totalRows = _layout.TotalWrappedRows;
        int viewportHeight = _bounds.Height;
        if (totalRows <= viewportHeight) return;

        int thumbHeight = Math.Max(1, viewportHeight * viewportHeight / totalRows);
        int maxScroll = Math.Max(1, totalRows - viewportHeight);
        int trackLength = Math.Max(1, viewportHeight - thumbHeight);

        int relativeY = mouseY - _bounds.Y;
        int targetRow = relativeY * maxScroll / trackLength;
        targetRow = Math.Clamp(targetRow, 0, maxScroll);

        _viewport.FirstVisibleWrappedRow = targetRow;
        _viewport.FollowTail = false;
        _lastScrollTime = DateTime.UtcNow;
    }

    public void ScrollUp(int rows)
    {
        _viewport.ScrollUp(rows);
        _lastScrollTime = DateTime.UtcNow;
    }

    public void ScrollDown(int rows)
    {
        _viewport.ScrollDown(rows);
        _lastScrollTime = DateTime.UtcNow;
    }

    public void ScrollToTop()
    {
        _viewport.ScrollToTop();
        _lastScrollTime = DateTime.UtcNow;
    }

    public void ScrollToBottom()
    {
        _viewport.ScrollToBottom(_layout.TotalWrappedRows, _bounds.Height);
        _lastScrollTime = DateTime.UtcNow;
    }

    public void UpdateFromEvent(OmicronEvent evt)
    {
        switch (evt)
        {
            case UserMessageEvent ue:
                FlushPending();
                _store.AppendSeparator(bgR: 75, bgG: 55, bgB: 20);
                _store.AppendUserMessage(Encoding.UTF8.GetBytes($"  You: {ue.Text}"));
                _store.AppendSeparator(bgR: 75, bgG: 55, bgB: 20);
                RebuildLayout();
                RequestRender?.Invoke();
                break;

            case AssistantTextDeltaEvent delta:
                if (!_assistantPrefixAdded)
                {
                    _store.AppendSeparator();
                    _pendingAssistantText.Append("  Agent: ");
                    _assistantPrefixAdded = true;
                }
                _pendingAssistantText.Append(delta.Delta);
                RequestRender?.Invoke();
                break;

            case AssistantResponseCompleteEvent:
                FlushPending();
                if (_store.Blocks.Count > 0 && _store.Blocks[^1] is AssistantMessageBlock)
                    _store.CompleteLastAssistantBlock();
                _assistantPrefixAdded = false;
                _store.AppendSeparator();
                RebuildLayout();
                break;

            case ToolInvocationStartedEvent tis:
                FlushPending();
                _store.AppendToolCall(tis.ToolName, tis.ToolCallId.Value);
                RebuildLayout();
                break;

            case ToolInvocationCompletedEvent tic:
                List<IContentBlock>? ticBlocks = tic.Blocks;

                // Local function to find and update the matching ToolCallBlock
                void UpdateWithSpan(ReadOnlySpan<byte> bytes, List<IContentBlock>? blocks)
                {
                    // Match by ToolCallId first (precise), then by ToolName + Running state
                    for (int i = _store.Blocks.Count - 1; i >= 0; i--)
                    {
                        if (_store.Blocks[i] is ToolCallBlock tcb &&
                            tcb.ToolCallId == tic.ToolCallId.Value &&
                            tcb.State is ToolCallState.Running or ToolCallState.Pending)
                        {
                            _store.UpdateToolCall(tcb.Id, ToolCallState.Completed, bytes, blocks);
                            return;
                        }
                    }

                    // Fallback: match by tool name + Running state
                    for (int i = _store.Blocks.Count - 1; i >= 0; i--)
                    {
                        if (_store.Blocks[i] is ToolCallBlock tcb &&
                            tcb.ToolName == tic.ToolName &&
                            tcb.State is ToolCallState.Running or ToolCallState.Pending)
                        {
                            _store.UpdateToolCall(tcb.Id, ToolCallState.Completed, bytes, blocks);
                            return;
                        }
                    }

                    // Last resort: match any Running/Pending tool block
                    for (int i = _store.Blocks.Count - 1; i >= 0; i--)
                    {
                        if (_store.Blocks[i] is ToolCallBlock tcb &&
                            tcb.State is ToolCallState.Running or ToolCallState.Pending)
                        {
                            _store.UpdateToolCall(tcb.Id, ToolCallState.Completed, bytes, blocks);
                            return;
                        }
                    }
                }

                // When blocks are available, build bytes directly from block
                // buffers avoiding the string → UTF-8 roundtrip.
                if (ticBlocks is { Count: > 0 })
                {
                    var blockSb = ZString.CreateUtf8StringBuilder();
                    try
                    {
                        blockSb.AppendLine();
                        ContentBlockTextRenderer.AppendAllUtf8To(ref blockSb, ticBlocks);

                        // Consume the span while the builder is alive
                        UpdateWithSpan(blockSb.AsSpan(), ticBlocks);
                    }
                    finally
                    {
                        blockSb.Dispose();
                    }
                }
                else
                {
                    var str = tic.IsError ? $"\n[Error: {tic.Result}]" : $"\n{tic.Result}";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(str);
                    UpdateWithSpan(bytes.AsSpan(), null);
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
                _assistantPrefixAdded = false;
                _viewport.ScrollToBottom(0, _bounds.Height);
                _layout.ReflowForWidth(Math.Max(1, _bounds.Width), _store);
                break;
        }
    }

    public void LoadMessages(IEnumerable<Message> messages)
    {
        _store.Clear();
        _assistantPrefixAdded = false;
        foreach (var msg in messages)
            AppendMessage(msg);

        RebuildLayout();
        ScrollToBottom();
    }

    private void AppendMessage(Message msg)
    {
        switch (msg.Role)
        {
            case MessageRole.User:
                _store.AppendSeparator(bgR: 75, bgG: 55, bgB: 20);
                _store.AppendUserMessage(Encoding.UTF8.GetBytes($"  You: {msg.Text}"));
                _store.AppendSeparator(bgR: 75, bgG: 55, bgB: 20);
                break;
            case MessageRole.Assistant:
                _store.AppendSeparator();
                var assistant = "  Agent: " + (msg.Text ?? string.Empty);
                _store.AppendAssistantDelta(Encoding.UTF8.GetBytes(assistant));
                _store.CompleteLastAssistantBlock();
                _store.AppendSeparator();
                break;
            case MessageRole.ToolResult:
                _store.AppendToolCall(msg.ToolName ?? "tool", msg.ToolCallId);
                for (int i = _store.Blocks.Count - 1; i >= 0; i--)
                {
                    if (_store.Blocks[i] is ToolCallBlock tcb &&
                        tcb.ToolCallId == msg.ToolCallId &&
                        tcb.State is ToolCallState.Running or ToolCallState.Pending)
                    {
                        _store.UpdateToolCall(tcb.Id, msg.IsError ? ToolCallState.Failed : ToolCallState.Completed, Encoding.UTF8.GetBytes(msg.Text ?? string.Empty));
                        break;
                    }
                }
                break;
        }
    }

    private void InvalidateLastBlock()
    {
        if (_store.Blocks.Count > 0)
        {
            var lastBlock = _store.Blocks[^1];
            _layout.ReflowBlock(lastBlock, _store, Math.Max(1, _bounds.Width));
            _viewport.OnContentAppended(_layout.TotalWrappedRows, _bounds.Height);
            RebuildBlockIndex();
        }
    }

    // ── Search / Find state ──

    /// <summary>
    /// A match found during search. Stores the wrapped line and byte offset.
    /// </summary>
    public readonly record struct SearchMatch(int WrappedRow, long ByteOffset, int Length);

    private readonly List<SearchMatch> _searchMatches = [];
    private int _currentMatchIndex = -1;
    private string _findQuery = "";

    /// <summary>Current find query (empty if not searching).</summary>
    public string FindQuery => _findQuery;

    /// <summary>Number of matches for the current query.</summary>
    public int MatchCount => _searchMatches.Count;

    /// <summary>Index of the currently highlighted match (-1 if none).</summary>
    public int CurrentMatchIndex => _currentMatchIndex;

    /// <summary>Whether find mode is active.</summary>
    public bool IsFindActive => _findQuery.Length > 0;

    /// <summary>
    /// Perform a search for the given query. Scrolls to the first match.
    /// Returns the number of matches found.
    /// </summary>
    public int Find(string query)
    {
        _findQuery = query ?? "";
        _searchMatches.Clear();
        _currentMatchIndex = -1;

        if (string.IsNullOrEmpty(_findQuery))
            return 0;

        var pattern = System.Text.Encoding.UTF8.GetBytes(_findQuery.ToLowerInvariant());
        if (pattern.Length == 0)
            return 0;

        var allText = _store.Text.ToArray();
        var allTextLower = ToLowerBytes(allText);

        int searchStart = 0;
        while (true)
        {
            int idx = IndexOfBytes(allTextLower, pattern, searchStart);
            if (idx < 0) break;

            // Find which wrapped row this byte offset falls into
            int wrappedRow = FindWrappedRowForOffset(idx);
            if (wrappedRow >= 0)
            {
                _searchMatches.Add(new SearchMatch(wrappedRow, idx, pattern.Length));
            }

            searchStart = idx + 1;
        }

        if (_searchMatches.Count > 0)
        {
            _currentMatchIndex = 0;
            GoToMatch(0);
        }

        return _searchMatches.Count;
    }

    /// <summary>Go to the next match. Returns true if there was a next match.</summary>
    public bool FindNext()
    {
        if (_searchMatches.Count == 0) return false;
        _currentMatchIndex = (_currentMatchIndex + 1) % _searchMatches.Count;
        GoToMatch(_currentMatchIndex);
        return true;
    }

    /// <summary>Go to the previous match. Returns true if there was a previous match.</summary>
    public bool FindPrevious()
    {
        if (_searchMatches.Count == 0) return false;
        _currentMatchIndex = (_currentMatchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
        GoToMatch(_currentMatchIndex);
        return true;
    }

    /// <summary>Clear search state.</summary>
    public void ClearFind()
    {
        _findQuery = "";
        _searchMatches.Clear();
        _currentMatchIndex = -1;
    }

    private void GoToMatch(int index)
    {
        if (index < 0 || index >= _searchMatches.Count) return;
        var match = _searchMatches[index];
        // Scroll to the wrapped row of the match
        int targetRow = match.WrappedRow - _bounds.Height / 3;
        if (targetRow < 0) targetRow = 0;
        _viewport.FirstVisibleWrappedRow = targetRow;
        _viewport.FollowTail = false;
    }

    private int FindWrappedRowForOffset(long byteOffset)
    {
        for (int i = 0; i < _layout.WrappedLines.Count; i++)
        {
            var line = _layout.WrappedLines[i];
            if (byteOffset >= line.ByteStart && byteOffset < line.ByteStart + line.ByteLength)
                return i;
        }
        return -1;
    }

    private static byte[] ToLowerBytes(ReadOnlySpan<byte> utf8)
    {
        // Simple ASCII-only lowercasing. For full Unicode case folding,
        // we'd need a proper case-folding implementation, but ASCII covers
        // most search scenarios.
        var result = new byte[utf8.Length];
        for (int i = 0; i < utf8.Length; i++)
        {
            byte b = utf8[i];
            if (b >= (byte)'A' && b <= (byte)'Z')
                result[i] = (byte)(b + 32);
            else
                result[i] = b;
        }
        return result;
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle, int start)
    {
        if (needle.Length == 0) return -1;
        int end = haystack.Length - needle.Length;
        for (int i = start; i <= end; i++)
        {
            bool found = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }
            if (found) return i;
        }
        return -1;
    }

    private readonly StringBuilder _pendingAssistantText = new();

    private void FlushPending()
    {
        if (_pendingAssistantText.Length > 0)
        {
            _store.AppendAssistantDelta(Encoding.UTF8.GetBytes(_pendingAssistantText.ToString()));
            _pendingAssistantText.Clear();
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

    public Size Measure(Size available) => available;

    public void Arrange(Rect bounds)
    {
        _bounds = bounds;
        if (bounds.Width > 0 && _layout.TerminalWidth != bounds.Width)
        {
            _layout.ReflowForWidth(bounds.Width, _store);
            _viewport.ScrollToBottom(_layout.TotalWrappedRows, _bounds.Height);
            RebuildBlockIndex();
        }

        _viewport.UpdateTotalRows(_layout.TotalWrappedRows, _bounds.Height);
    }

    public void Render(RenderContext context)
    {
        FlushPending();
        if (_assistantPrefixAdded && _store.Blocks.Count > 0 && _store.Blocks[^1] is AssistantMessageBlock)
            InvalidateLastBlock();

        int visibleHeight = _bounds.Height;
        if (visibleHeight <= 0) return;

        int firstRow = _viewport.FirstVisibleWrappedRow;
        var visibleLines = _layout.GetVisibleLines(firstRow, visibleHeight);
        int row = 0;
        foreach (var line in visibleLines)
        {
            if (row >= visibleHeight)
                break;

            var style = GetStyleForBlock(line.BlockId);
            _blockById.TryGetValue(line.BlockId, out var blk);
            bool hasAmberAccent = blk switch
            {
                UserMessageBlock => true,
                SeparatorBlock sep => sep.BgR == 75,
                _ => false
            };

            if (hasAmberAccent)
            {
                var barCell = new RenderCell
                {
                    Glyph = GlyphRef.Ascii((byte)' '),
                    Width = 1,
                    Style = new TextStyle(235, 195, 80, 75, 55, 20, false, false, false),
                };
                int barWidth = Math.Min(3, _bounds.Width);
                context.FillRect(new Rect(_bounds.X, _bounds.Y + row, barWidth, 1), barCell);
            }

            if (!(blk is SeparatorBlock))
            {
                var bytes = _store.Text.Slice(line.ByteStart, line.ByteLength);
                if (bytes.Length > 0)
                {
                    int textOffset = hasAmberAccent ? 3 : 0;
                    context.DrawText(_bounds.X + textOffset, _bounds.Y + row, bytes.Span, style);
                }
            }
            row++;
        }

        if (_viewport.UnseenLineCount > 0 && !_viewport.FollowTail)
        {
            string indicator = $" ↓ {_viewport.UnseenLineCount} new ";
            int indicatorX = _bounds.X + _bounds.Width - GetDisplayWidth(indicator);
            if (indicatorX >= _bounds.X && row > 0)
            {
                context.DrawText(indicatorX, _bounds.Y + row - 1, Encoding.UTF8.GetBytes(indicator), new TextStyle(235, 195, 80, 75, 55, 20, false, false, false));
            }
        }

        // Draw scrollbar overlay on the right edge
        if (IsScrollbarVisible)
            DrawScrollbar(context);
    }

    private void DrawScrollbar(RenderContext context)
    {
        int totalRows = _layout.TotalWrappedRows;
        int viewportHeight = _bounds.Height;
        if (totalRows <= viewportHeight) return;

        int thumbHeight = Math.Max(1, viewportHeight * viewportHeight / totalRows);
        int maxScroll = Math.Max(1, totalRows - viewportHeight);
        int thumbStart = _viewport.FirstVisibleWrappedRow * (viewportHeight - thumbHeight) / maxScroll;

        int scrollbarCol = _bounds.X + _bounds.Width - 1;
        if (scrollbarCol < context.Clip.X || scrollbarCol >= context.Clip.Right) return;
        if (scrollbarCol >= context.Frame.Width) return;

        var trackBg = TuiColors.ScrollbarTrackBg;
        var thumbBg = TuiColors.ScrollbarThumbBg;

        for (int i = 0; i < viewportHeight; i++)
        {
            int row = _bounds.Y + i;
            if (row < context.Clip.Y || row >= context.Clip.Bottom) continue;
            if (row < 0 || row >= context.Frame.Height) continue;

            bool isThumb = i >= thumbStart && i < thumbStart + thumbHeight;
            var bg = isThumb ? thumbBg : trackBg;
            int idx = row * context.Frame.Width + scrollbarCol;
            var existing = context.Frame.Cells[idx];
            context.Frame.Cells[idx] = new RenderCell
            {
                Glyph = GlyphRef.Ascii((byte)' '),
                Width = 1,
                Style = new TextStyle(existing.Style.FgR, existing.Style.FgG, existing.Style.FgB,
                    bg.R, bg.G, bg.B, false, false, false),
            };
        }
    }

    private TextStyle GetStyleForBlock(BlockId blockId)
    {
        if (_blockById.TryGetValue(blockId, out var block))
        {
            // If this is a tool call with content blocks, render based on type
            if (block is ToolCallBlock tcb && tcb.ContentBlocks is { Count: > 0 })
            {
                return tcb.ContentBlocks[0] switch
                {
                    FilePreviewContentBlock f when f.IsBinary => new TextStyle(150, 135, 100, 40, 25, 15, false, false, false),
                    FilePreviewContentBlock => new TextStyle(150, 155, 120, 0, 0, 0, false, false, false),
                    ErrorContentBlock => TextStyle.ForegroundOnly(255, 120, 100),
                    _ => TextStyle.ForegroundOnly(150, 135, 100),
                };
            }

            return block switch
            {
                UserMessageBlock => TextStyle.ForegroundOnly(235, 195, 80),
                AssistantMessageBlock => TextStyle.ForegroundOnly(200, 200, 200),
                ToolCallBlock => TextStyle.ForegroundOnly(150, 135, 100),
                SystemNoticeBlock => TextStyle.ForegroundOnly(255, 200, 80),
                SeparatorBlock sep => new TextStyle(235, 195, 80, sep.BgR, sep.BgG, sep.BgB, false, false, false),
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
