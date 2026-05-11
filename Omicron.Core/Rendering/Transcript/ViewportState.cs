namespace Omicron.Core.Rendering.Transcript;

/// <summary>
/// Manages scroll position, follow-tail behavior, and new-line indicators
/// for the transcript viewport.
/// </summary>
public sealed class ViewportState
{
    private int _firstVisibleWrappedRow;
    private int _lastTotalWrappedRows;

    /// <summary>First wrapped row visible at the top of the viewport.</summary>
    public int FirstVisibleWrappedRow
    {
        get => _firstVisibleWrappedRow;
        set => _firstVisibleWrappedRow = Math.Max(0, value);
    }

    /// <summary>Whether the viewport auto-follows new content at the bottom.</summary>
    public bool FollowTail { get; set; } = true;

    /// <summary>Number of new wrapped rows added since the user last scrolled to the bottom.</summary>
    public int UnseenLineCount { get; private set; }

    /// <summary>
    /// Called when new content is appended to the transcript.
    /// If <see cref="FollowTail"/> is true, auto-scrolls to show the bottom
    /// (showing <paramref name="viewportHeight"/> rows).
    /// Otherwise, increments <see cref="UnseenLineCount"/>.
    /// </summary>
    public void OnContentAppended(int newTotalWrappedRows, int viewportHeight = 1)
    {
        int added = newTotalWrappedRows - _lastTotalWrappedRows;
        _lastTotalWrappedRows = newTotalWrappedRows;

        if (FollowTail)
        {
            _firstVisibleWrappedRow = Math.Max(0, newTotalWrappedRows - viewportHeight);
        }
        else
        {
            UnseenLineCount += Math.Max(0, added);
        }
    }

    /// <summary>Scroll up by the given number of rows.</summary>
    public void ScrollUp(int rows)
    {
        FollowTail = false;
        _firstVisibleWrappedRow = Math.Max(0, _firstVisibleWrappedRow - rows);
    }

    /// <summary>Scroll down by the given number of rows.</summary>
    public void ScrollDown(int rows)
    {
        int maxTop = Math.Max(0, _lastTotalWrappedRows - 1);
        _firstVisibleWrappedRow = Math.Min(maxTop, _firstVisibleWrappedRow + rows);

        // If we reached the bottom, resume follow
        if (_firstVisibleWrappedRow >= maxTop)
        {
            FollowTail = true;
            UnseenLineCount = 0;
        }
    }

    /// <summary>Jump to the top of the transcript.</summary>
    public void ScrollToTop()
    {
        FollowTail = false;
        _firstVisibleWrappedRow = 0;
    }

    /// <summary>Jump to the bottom of the transcript and resume follow.</summary>
    public void ScrollToBottom(int totalWrappedRows, int viewportHeight = 1)
    {
        _lastTotalWrappedRows = totalWrappedRows;
        _firstVisibleWrappedRow = Math.Max(0, totalWrappedRows - viewportHeight);
        FollowTail = true;
        UnseenLineCount = 0;
    }

    /// <summary>Mark unseen lines as seen (e.g., when the user manually scrolls down).</summary>
    public void MarkSeen()
    {
        UnseenLineCount = 0;
    }

    /// <summary>
    /// Update the total row count (called when the layout cache is reflowed).
    /// </summary>
    public void UpdateTotalRows(int totalWrappedRows, int viewportHeight = 1)
    {
        _lastTotalWrappedRows = totalWrappedRows;
        if (FollowTail)
        {
            _firstVisibleWrappedRow = Math.Max(0, totalWrappedRows - viewportHeight);
        }
    }
}
