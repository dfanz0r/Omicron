using System.Text;
using Omicron.Core.Models;
using Omicron.Core.Rendering;
using Omicron.Core.Rendering.Layout;

namespace Omicron.CLI.Tui;

/// <summary>
/// Fullscreen model picker widget. Displays available models in a list,
/// with keyboard navigation and selection.
/// </summary>
public sealed class TuiModelPicker : ITuiWidget
{
    private readonly List<ModelEntry> _entries = [];
    private int _selectedIndex;
    private int _scrollOffset; // first visible item index
    private Rect _bounds;
    private bool _completed;
    private readonly StringBuilder _filterText = new();
    private List<int> _filteredIndices = []; // indices into _entries matching filter
    private bool _filterDirty = true;

    /// <summary>Event raised when a model is selected.</summary>
    public event Action<Model>? OnModelSelected;

    /// <summary>Event raised when the picker is cancelled (Escape).</summary>
    public event Action? OnCancelled;

    /// <summary>Set the list of available models.</summary>
    public void SetModels(IReadOnlyList<Model> models, string? lastModelKey)
    {
        _entries.Clear();
        _selectedIndex = 0;
        _scrollOffset = 0;
        _filterText.Clear();
        _filterDirty = true;

        for (int i = 0; i < models.Count; i++)
        {
            bool isLast = models[i].Id == lastModelKey || models[i].Name == lastModelKey;
            _entries.Add(new ModelEntry(models[i], isLast));
            if (isLast)
                _selectedIndex = i;
        }
    }

    /// <summary>Current display list (filtered view of _entries via _filteredIndices).</summary>
    private int FilteredCount => _filteredIndices.Count;
    private ModelEntry GetFiltered(int i) => _entries[_filteredIndices[i]];
    private int GetRealIndex(int filteredIdx) => filteredIdx >= 0 && filteredIdx < _filteredIndices.Count ? _filteredIndices[filteredIdx] : -1;

    private void RebuildFilter()
    {
        _filteredIndices.Clear();
        var filter = _filterText.ToString().ToLowerInvariant();
        for (int i = 0; i < _entries.Count; i++)
        {
            if (string.IsNullOrEmpty(filter) ||
                _entries[i].Model.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                _entries[i].Model.ProviderName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                _entries[i].Model.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                _filteredIndices.Add(i);
            }
        }

        // Adjust selected index to be within filtered list
        if (_filteredIndices.Count > 0)
        {
            // Try to keep the same real selection if possible
            int realIdx = GetRealIndex(_selectedIndex);
            int newSel = _filteredIndices.IndexOf(realIdx >= 0 ? realIdx : _filteredIndices[0]);
            _selectedIndex = newSel >= 0 ? newSel : 0;
        }
        else
        {
            _selectedIndex = 0;
        }

        _scrollOffset = 0;
        _filterDirty = false;
    }

    /// <summary>Handle a key event. Returns true if consumed.</summary>
    public bool HandleKey(KeyEvent ke)
    {
        if (_completed) return false;

        if (_filterDirty) RebuildFilter();
        int count = FilteredCount;

        switch (ke.Key)
        {
            case Key.Up:
                if (_selectedIndex > 0)
                {
                    _selectedIndex--;
                    EnsureSelectedVisible();
                }
                return true;

            case Key.Down:
                if (_selectedIndex < count - 1)
                {
                    _selectedIndex++;
                    EnsureSelectedVisible();
                }
                return true;

            case Key.PageUp:
            {
                int pageSize = GetVisibleRowCount() - 2;
                if (pageSize < 1) pageSize = 1;
                _selectedIndex = Math.Max(0, _selectedIndex - pageSize);
                EnsureSelectedVisible();
                return true;
            }

            case Key.PageDown:
            {
                int pageSize = GetVisibleRowCount() - 2;
                if (pageSize < 1) pageSize = 1;
                _selectedIndex = Math.Min(count - 1, _selectedIndex + pageSize);
                EnsureSelectedVisible();
                return true;
            }

            case Key.Home:
                _selectedIndex = 0;
                _scrollOffset = 0;
                return true;

            case Key.End:
                _selectedIndex = count - 1;
                EnsureSelectedVisible();
                return true;

            case Key.Enter:
                if (_selectedIndex >= 0 && _selectedIndex < count)
                {
                    _completed = true;
                    OnModelSelected?.Invoke(GetFiltered(_selectedIndex).Model);
                }
                return true;

            case Key.Escape:
                if (_filterText.Length > 0)
                {
                    // Clear filter first, then cancel on second Escape
                    _filterText.Clear();
                    _filterDirty = true;
                    return true;
                }
                _completed = true;
                OnCancelled?.Invoke();
                return true;

            case Key.Backspace:
                if (_filterText.Length > 0)
                {
                    _filterText.Length--;
                    _filterDirty = true;
                }
                return true;

            case Key.Character when ke.Text.HasValue:
            {
                char c = (char)ke.Text.Value.Value;

                // Number keys 1-9: jump to that item in the list
                if (c >= '1' && c <= '9')
                {
                    int idx = c - '1';
                    if (idx < count)
                    {
                        _selectedIndex = idx;
                        EnsureSelectedVisible();
                    }
                    return true;
                }

                // Alphanumeric: add to filter
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '/' || c == '.')
                {
                    _filterText.Append(c);
                    _filterDirty = true;
                    return true;
                }

                return false;
            }
        }

        return false;
    }

    private void EnsureSelectedVisible()
    {
        int visibleRows = GetVisibleRowCount();
        if (visibleRows <= 0) return;

        if (_selectedIndex < _scrollOffset)
            _scrollOffset = _selectedIndex;
        else if (_selectedIndex >= _scrollOffset + visibleRows)
            _scrollOffset = _selectedIndex - visibleRows + 1;
    }

    private int GetVisibleRowCount()
    {
        // Subtract title (2 rows) and potential bottom padding (1 row)
        return Math.Max(1, _bounds.Height - 4);
    }

    /// <summary>Whether the picker has completed (selected or cancelled).</summary>
    public bool IsCompleted => _completed;

    // ── ITuiWidget implementation ──

    public Size Measure(Size available) => available;

    public void Arrange(Rect bounds)
    {
        _bounds = bounds;
    }

    public void Render(RenderContext context)
    {
        if (_filterDirty) RebuildFilter();

        // Fill background
        var bgCell = new RenderCell
        {
            Glyph = GlyphRef.Ascii((byte)' '),
            Width = 1,
            Style = TextStyle.Default,
        };
        context.FillRect(_bounds, bgCell);

        int count = FilteredCount;

        // Title + filter bar
        int y = _bounds.Y + 1;
        if (y < _bounds.Bottom)
        {
            string title = _filterText.Length > 0
                ? $" Filter: [{_filterText}]  (type to filter, ↑↓ navigate, Enter select, Esc clear/cancel): "
                : " Select a model (type to filter, ↑↓ navigate, Enter select, Escape to cancel): ";
            context.DrawText(_bounds.X + 1, y,
                Encoding.UTF8.GetBytes(title),
                TextStyle.ForegroundOnly(200, 200, 200));
            y += 2;
        }

        if (count == 0)
        {
            string msg = _filterText.Length > 0
                ? " No models match the filter."
                : " No models available. Configure an API key and restart.";
            context.DrawText(_bounds.X + 1, y,
                Encoding.UTF8.GetBytes(msg),
                TextStyle.ForegroundOnly(255, 100, 100));
            return;
        }

        int visibleRows = GetVisibleRowCount();

        // Show scroll indicator if scrolled
        if (_scrollOffset > 0)
        {
            context.DrawText(_bounds.X + 1, y,
                Encoding.UTF8.GetBytes($" \u2191 {_scrollOffset} more..."),
                TextStyle.ForegroundOnly(120, 120, 130));
            y++;
            visibleRows--;
        }

        // List models (with viewport windowing)
        int endIndex = Math.Min(count, _scrollOffset + visibleRows);
        for (int i = _scrollOffset; i < endIndex && y < _bounds.Bottom - 1; i++)
        {
            var entry = GetFiltered(i);
            var model = entry.Model;

            bool isSelected = i == _selectedIndex;
            string indicator = isSelected ? " \u25B6 " : "   ";
            string line = $"{indicator}{model.Name}";
            if (!string.IsNullOrEmpty(model.ProviderName))
                line += $"  ({model.ProviderName})";
            if (entry.IsLastUsed)
                line += "  [last]";

            var style = isSelected ? TextStyle.Inverted : TextStyle.Default;
            context.DrawText(_bounds.X + 1, y, Encoding.UTF8.GetBytes(line), style);
            y++;
        }

        // Show bottom scroll indicator
        if (endIndex < count)
        {
            int remaining = count - endIndex;
            context.DrawText(_bounds.X + 1, y,
                Encoding.UTF8.GetBytes($" \u2193 {remaining} more..."),
                TextStyle.ForegroundOnly(120, 120, 130));
        }
    }

    private sealed record ModelEntry(Model Model, bool IsLastUsed);
}
