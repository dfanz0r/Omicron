using System.Text;
using Omicron.Core.Rendering;
using Omicron.Core.Rendering.Layout;
using Omicron.Core.Text;

namespace Omicron.CLI.Tui;

/// <summary>
/// Single-line text editor for the TUI input line.
/// Supports cursor movement, backspace, delete, home, end,
/// and submit on Enter.
/// Uses <see cref="CellWidthCalculator"/> for correct cursor positioning with CJK/emoji.
/// </summary>
public sealed class InputEditorWidget : ITuiWidget
{
    private readonly StringBuilder _buffer = new();
    private int _cursorPosition;
    private Rect _bounds;
    private IReadOnlyList<string>? _pendingCompletions;
    private string _completionAnchor = "";
    private string _killBuffer = ""; // for Ctrl+U/K+Y yank/paste

    // ── Input history ──
    private readonly List<string> _history = [];
    private int _historyIndex = -1; // -1 means current (new) input

    /// <summary>Maximum number of history entries to keep.</summary>
    public int MaxHistory { get; set; } = 100;

    /// <summary>Add an entry to the input history.</summary>
    public void AddToHistory(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Don't add duplicates of the most recent entry
        if (_history.Count > 0 && _history[^1] == text) return;
        _history.Add(text);
        if (_history.Count > MaxHistory)
            _history.RemoveAt(0);
        _historyIndex = -1;
    }

    /// <summary>
    /// Maximum number of completion items to display in the popup.
    /// </summary>
    public int MaxVisibleCompletions { get; set; } = 8;

    /// <summary>
    /// Height reserved for the completion popup (0 when no completions).
    /// </summary>
    public int ReservedPopupHeight { get; private set; }

    /// <summary>Current text in the input buffer.</summary>
    public string Text => _buffer.ToString();

    /// <summary>Completion provider for Tab key (e.g. slash command completions).</summary>
    public Func<string, IReadOnlyList<string>>? CompletionProvider { get; set; }

    /// <summary>Completions stashed for display when Tab has no unique prefix.</summary>
    public IReadOnlyList<string>? PendingCompletions => _pendingCompletions;

    /// <summary>Cursor position within the buffer (0-based string index).</summary>
    public int CursorPosition => _cursorPosition;

    /// <summary>Whether the widget has unsubmitted content.</summary>
    public bool HasContent => _buffer.Length > 0;

    /// <summary>Event raised when the user presses Enter with content.</summary>
    public event Action<string>? OnSubmit;

    /// <summary>Insert text at the current cursor position.</summary>
    public void Insert(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        _buffer.Insert(_cursorPosition, text);
        _cursorPosition += text.Length;
    }

    /// <summary>Delete the character before the cursor.</summary>
    public void Backspace()
    {
        if (_cursorPosition <= 0)
            return;

        _buffer.Remove(_cursorPosition - 1, 1);
        _cursorPosition--;
    }

    /// <summary>Delete the character at the cursor.</summary>
    public void Delete()
    {
        if (_cursorPosition >= _buffer.Length)
            return;

        _buffer.Remove(_cursorPosition, 1);
    }

    /// <summary>Move cursor left.</summary>
    public void MoveLeft()
    {
        if (_cursorPosition > 0)
            _cursorPosition--;
    }

    /// <summary>Move cursor right.</summary>
    public void MoveRight()
    {
        if (_cursorPosition < _buffer.Length)
            _cursorPosition++;
    }

    /// <summary>Move cursor to the beginning.</summary>
    public void MoveHome() => _cursorPosition = 0;

    /// <summary>Move cursor to the end.</summary>
    public void MoveEnd() => _cursorPosition = _buffer.Length;

    /// <summary>Submit the current content and clear the buffer.</summary>
    public void Submit()
    {
        if (_buffer.Length == 0)
            return;

        var text = _buffer.ToString();
        _buffer.Clear();
        _cursorPosition = 0;
        AddToHistory(text);
        OnSubmit?.Invoke(text);
    }

    /// <summary>Clear the buffer without submitting.</summary>
    public void Clear()
    {
        _buffer.Clear();
        _cursorPosition = 0;
        _pendingCompletions = null;
        _completionAnchor = "";
    }

    // ── Readline-like editing commands ──

    /// <summary>Move cursor to the previous word boundary.</summary>
    public void MoveWordLeft()
    {
        if (_cursorPosition <= 0) return;
        // Skip non-word characters before the cursor
        int pos = _cursorPosition - 1;
        while (pos >= 0 && !IsWordChar(_buffer[pos])) pos--;
        // Skip to start of word
        while (pos >= 0 && IsWordChar(_buffer[pos])) pos--;
        _cursorPosition = Math.Max(0, pos + 1);
    }

    /// <summary>Move cursor to the next word boundary.</summary>
    public void MoveWordRight()
    {
        if (_cursorPosition >= _buffer.Length) return;
        int pos = _cursorPosition;
        // Skip current word
        while (pos < _buffer.Length && IsWordChar(_buffer[pos])) pos++;
        // Skip to start of next word
        while (pos < _buffer.Length && !IsWordChar(_buffer[pos])) pos++;
        _cursorPosition = pos;
    }

    /// <summary>Delete the word backward from the cursor.</summary>
    public void DeleteWordBackward()
    {
        if (_cursorPosition <= 0) return;
        int start = _cursorPosition - 1;
        while (start >= 0 && !IsWordChar(_buffer[start])) start--;
        while (start >= 0 && IsWordChar(_buffer[start])) start--;
        start++;
        int len = _cursorPosition - start;
        _killBuffer = _buffer.ToString(start, len);
        _buffer.Remove(start, len);
        _cursorPosition = start;
    }

    /// <summary>Kill (cut) text from cursor to beginning of line.</summary>
    public void KillToStart()
    {
        if (_cursorPosition <= 0) return;
        _killBuffer = _buffer.ToString(0, _cursorPosition);
        _buffer.Remove(0, _cursorPosition);
        _cursorPosition = 0;
    }

    /// <summary>Kill (cut) text from cursor to end of line.</summary>
    public void KillToEnd()
    {
        if (_cursorPosition >= _buffer.Length) return;
        _killBuffer = _buffer.ToString(_cursorPosition, _buffer.Length - _cursorPosition);
        _buffer.Remove(_cursorPosition, _buffer.Length - _cursorPosition);
    }

    /// <summary>Yank (paste) the last killed text at the cursor.</summary>
    public void Yank()
    {
        if (string.IsNullOrEmpty(_killBuffer)) return;
        _buffer.Insert(_cursorPosition, _killBuffer);
        _cursorPosition += _killBuffer.Length;
    }

    /// <summary>Transpose characters at and before the cursor.</summary>
    public void TransposeChars()
    {
        if (_buffer.Length < 2) return;
        int pos = _cursorPosition;
        if (pos == 0) pos = 1; // If at start, swap first two
        if (pos >= _buffer.Length) pos = _buffer.Length - 1;
        char tmp = _buffer[pos - 1];
        _buffer[pos - 1] = _buffer[pos];
        _buffer[pos] = tmp;
        _cursorPosition = pos + 1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Handle a terminal key event. Returns true if the key was consumed.</summary>
    public bool HandleKey(KeyEvent ke)
    {
        if (ke.Key == Key.Character && ke.Text.HasValue)
        {
            char c = (char)ke.Text.Value.Value;
            if (c >= 32 && c != 127) // Printable characters (excluding DEL)
            {
                Insert(c.ToString());
                return true;
            }
            // Backspace sent as control character (BS = 0x08, DEL = 0x7F)
            if (c == '\b' || c == 0x7F)
            {
                Backspace();
                return true;
            }

            // Readline-like control character shortcuts
            switch (c)
            {
                case '\u0001': // Ctrl+A → Home
                    MoveHome();
                    return true;
                case '\u0005': // Ctrl+E → End
                    MoveEnd();
                    return true;
                case '\u0015': // Ctrl+U → Kill to start
                    KillToStart();
                    return true;
                case '\u000B': // Ctrl+K → Kill to end
                    KillToEnd();
                    return true;
                case '\u0019': // Ctrl+Y → Yank
                    Yank();
                    return true;
                case '\u0017': // Ctrl+W → Delete word backward
                    DeleteWordBackward();
                    return true;
                case '\u0014': // Ctrl+T → Transpose
                    TransposeChars();
                    return true;
            }
        }

        switch (ke.Key)
        {
            case Key.Enter:
                Submit();
                return true;

            // Windows VT input sends Enter as Key.Character with '\r'
            case Key.Character when ke.Text?.Value == '\r':
                Submit();
                return true;

            case Key.Backspace:
                Backspace();
                return true;

            case Key.Delete:
                Delete();
                return true;

            case Key.Left when ke.Modifiers.HasFlag(KeyModifiers.Control):
                MoveWordLeft();
                return true;

            case Key.Left:
                MoveLeft();
                return true;

            case Key.Right when ke.Modifiers.HasFlag(KeyModifiers.Control):
                MoveWordRight();
                return true;

            case Key.Right:
                MoveRight();
                return true;

            case Key.Up when _history.Count > 0:
                HistoryPrevious();
                return true;

            case Key.Down when _historyIndex >= 0:
                HistoryNext();
                return true;

            case Key.Home:
                MoveHome();
                return true;

            case Key.End:
                MoveEnd();
                return true;

            case Key.Escape:
                Clear();
                return true;

            case Key.Tab:
                Complete();
                return true;
        }

        return false;
    }

    /// <summary>
    /// Navigate to the previous entry in history (most recent first).
    /// History is stored oldest-first, so we access from the end.
    /// </summary>
    private void HistoryPrevious()
    {
        // _historyIndex = -1 means current input, 0 means most recent history entry
        if (_historyIndex < _history.Count - 1)
        {
            _historyIndex++;
            int idx = _history.Count - 1 - _historyIndex;
            LoadHistoryEntry(idx);
        }
    }

    private void HistoryNext()
    {
        if (_historyIndex > 0)
        {
            _historyIndex--;
            int idx = _history.Count - 1 - _historyIndex;
            LoadHistoryEntry(idx);
        }
        else if (_historyIndex == 0)
        {
            // Return to current (new) input
            _historyIndex = -1;
            _buffer.Clear();
            _cursorPosition = 0;
        }
    }

    private void LoadHistoryEntry(int index)
    {
        if (index < 0 || index >= _history.Count) return;
        _buffer.Clear();
        _buffer.Append(_history[index]);
        _cursorPosition = _buffer.Length;
    }

    /// <summary>Trigger tab completion on the current buffer content.</summary>
    private void Complete()
    {
        if (CompletionProvider is null)
            return;

        var current = _buffer.ToString();
        if (_pendingCompletions is not null && current != _completionAnchor)
            _pendingCompletions = null;

        var completions = CompletionProvider(current)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (completions.Count == 0)
            return;

        if (completions.Count == 1)
        {
            _pendingCompletions = null;
            _completionAnchor = "";
            ReplaceBuffer(completions[0]);
            return;
        }

        var commonPrefix = LongestCommonPrefix(completions);
        if (commonPrefix.Length > current.Length)
        {
            _pendingCompletions = null;
            _completionAnchor = "";
            ReplaceBuffer(commonPrefix);
            return;
        }

        // Multiple completions, no common prefix extension — stash for display
        _pendingCompletions = completions;
        _completionAnchor = current;
    }

    private void ReplaceBuffer(string value)
    {
        _buffer.Clear();
        _buffer.Append(value);
        _cursorPosition = _buffer.Length;
    }

    private static string LongestCommonPrefix(IReadOnlyList<string> values)
    {
        if (values.Count == 0) return string.Empty;
        var prefix = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            while (!values[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (prefix.Length == 0) return string.Empty;
                prefix = prefix[..^1];
            }
        }
        return prefix;
    }

    // ── ITuiWidget implementation ──

    public Size Measure(Size available)
    {
        ReservedPopupHeight = 0;
        if (_pendingCompletions is { Count: > 0 })
        {
            int visibleCount = Math.Min(_pendingCompletions.Count, MaxVisibleCompletions);
            ReservedPopupHeight = visibleCount + 1; // items + title bar
        }
        return new Size(available.Width, 1 + ReservedPopupHeight);
    }

    public void Arrange(Rect bounds)
    {
        _bounds = bounds;
    }

    public void Render(RenderContext context)
    {
        var style = TextStyle.Default;

        // Calculate where the input line goes (at the bottom of our bounds)
        int inputRow = _bounds.Bottom - 1;

        // If there are pending completions, draw popup above the input line
        if (_pendingCompletions is { Count: > 0 })
        {
            DrawCompletions(context, _bounds.Y, inputRow);
        }

        // Clear the input row
        var emptyCell = new RenderCell
        {
            Glyph = GlyphRef.Ascii((byte)' '),
            Width = 1,
            Style = style,
        };
        var inputRect = new Rect(_bounds.X, inputRow, _bounds.Width, 1);
        context.FillRect(inputRect, emptyCell);

        // Draw prompt prefix in warm gold
        string prompt = "> ";
        int x = _bounds.X;
        context.DrawText(x, inputRow, Encoding.UTF8.GetBytes(prompt), TextStyle.ForegroundOnly(235, 195, 80));
        x += prompt.Length;

        // Compute display width of text before cursor (for correct CJK positioning)
        int cursorDisplayWidth = GetDisplayWidth(_buffer.ToString()[.._cursorPosition]);
        int cursorScreenCol = x + cursorDisplayWidth;

        // Draw buffer text up to cursor
        if (_cursorPosition > 0)
        {
            var beforeCursor = Encoding.UTF8.GetBytes(_buffer.ToString()[.._cursorPosition]);
            context.DrawText(x, inputRow, beforeCursor, style);
        }

        // Draw cursor (block cursor via inverted space + character)
        if (cursorScreenCol < _bounds.Right)
        {
            string cursorChar = _cursorPosition < _buffer.Length
                ? _buffer[_cursorPosition].ToString()
                : " ";
            context.DrawText(cursorScreenCol, inputRow,
                Encoding.UTF8.GetBytes(cursorChar),
                TextStyle.Inverted);
        }

        // Draw text after cursor (skip the character under the cursor — it's already drawn as inverted)
        if (_cursorPosition + 1 < _buffer.Length)
        {
            var afterCursor = Encoding.UTF8.GetBytes(_buffer.ToString()[(_cursorPosition + 1)..]);
            context.DrawText(cursorScreenCol + 1, inputRow, afterCursor, style);
        }
    }

    private void DrawCompletions(RenderContext context, int popupStart, int inputRow)
    {
        if (_pendingCompletions is null || _pendingCompletions.Count == 0)
            return;

        int maxItems = Math.Min(_pendingCompletions.Count, MaxVisibleCompletions);
        int popupWidth = Math.Min(_bounds.Width - 4, 60);
        int popupX = _bounds.X + 2;

        // Draw popup background
        var popupBg = new RenderCell
        {
            Glyph = GlyphRef.Ascii((byte)' '),
            Width = 1,
            Style = new TextStyle(200, 200, 200, 40, 40, 45, false, false, false),
        };
        context.FillRect(new Rect(popupX, popupStart, popupWidth, maxItems + 1), popupBg);

        // Title
        context.DrawText(popupX + 1, popupStart,
            Encoding.UTF8.GetBytes($" Completions ({_pendingCompletions.Count}): "),
            TextStyle.ForegroundOnly(150, 150, 160));

        // Items (first item highlighted)
        for (int i = 0; i < maxItems; i++)
        {
            string line = $" {_pendingCompletions[i]}";
            var itemStyle = (i == 0)
                ? TextStyle.Inverted
                : TextStyle.ForegroundOnly(200, 200, 200);
            context.DrawText(popupX + 1, popupStart + 1 + i,
                Encoding.UTF8.GetBytes(line), itemStyle);
        }

        // If there are more items than we can show, indicate that
        if (_pendingCompletions.Count > maxItems)
        {
            context.DrawText(popupX + 1, popupStart + 1 + maxItems - 1,
                Encoding.UTF8.GetBytes($" ... and {_pendingCompletions.Count - maxItems + 1} more"),
                TextStyle.ForegroundOnly(120, 120, 130));
        }
    }

    private void DrawCompletions(RenderContext context, int startRow)
    {
        if (_pendingCompletions is null || _pendingCompletions.Count == 0)
            return;

        int popupWidth = Math.Min(_bounds.Width - 2, 60);
        int popupX = _bounds.X + 2;
        int maxItems = Math.Min(_pendingCompletions.Count, 8);

        // Draw popup background
        var popupBg = new RenderCell
        {
            Glyph = GlyphRef.Ascii((byte)' '),
            Width = 1,
            Style = new TextStyle(200, 200, 200, 40, 40, 45, false, false, false),
        };
        context.FillRect(new Rect(popupX, startRow, popupWidth, maxItems + 1), popupBg);

        // Title
        context.DrawText(popupX + 1, startRow,
            Encoding.UTF8.GetBytes($" Completions ({_pendingCompletions.Count}): "),
            TextStyle.ForegroundOnly(150, 150, 160));

        // Items
        for (int i = 0; i < maxItems; i++)
        {
            string line = $" {_pendingCompletions[i]}";
            if (i == 0)
            {
                // Highlight first item
                context.DrawText(popupX + 1, startRow + 1 + i,
                    Encoding.UTF8.GetBytes(line),
                    TextStyle.Inverted);
            }
            else
            {
                context.DrawText(popupX + 1, startRow + 1 + i,
                    Encoding.UTF8.GetBytes(line),
                    TextStyle.ForegroundOnly(200, 200, 200));
            }
        }

        // If there are more items than we can show, indicate that
        if (_pendingCompletions.Count > maxItems)
        {
            context.DrawText(popupX + 1, startRow + 1 + maxItems - 1,
                Encoding.UTF8.GetBytes($" ... and {_pendingCompletions.Count - maxItems + 1} more"),
                TextStyle.ForegroundOnly(120, 120, 130));
        }
    }

    private static int GetDisplayWidth(string text)
    {
        int width = 0;
        foreach (var rune in text.EnumerateRunes())
            width += CellWidthCalculator.GetWidth(rune);
        return width;
    }
}
