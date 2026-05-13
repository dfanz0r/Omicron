using System.Collections.Immutable;
using System.Text;
using Omicron.Core.Rendering;
using Omicron.Core.Rendering.Layout;
using Omicron.Core.Text;

namespace Omicron.CLI.Tui;

/// <summary>
/// Multi-line text editor for the TUI input line.
/// Supports cursor movement, backspace, delete, home, end,
/// multi-line editing, and submit on Ctrl+Enter.
/// Uses <see cref="CellWidthCalculator"/> for correct cursor positioning with CJK/emoji.
/// </summary>
public sealed class InputEditorWidget : ITuiWidget
{
    private readonly List<StringBuilder> _lines = [new()];
    private int _cursorLine;
    private int _cursorColumn;
    private Rect _bounds;
    private IReadOnlyList<string>? _pendingCompletions;
    private string _completionAnchor = "";
    private string _killBuffer = ""; // for Ctrl+U/K/Y yank/paste

    // ── Input history ──
    private readonly List<string> _history = [];
    private int _historyIndex = -1; // -1 means current (new) input

    /// <summary>Maximum number of lines in the input editor.</summary>
    public int MaxInputLines { get; set; } = 10;

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

    /// <summary>Current text in the input buffer (joined with \n).</summary>
    public string Text => string.Join("\n", _lines.Select(l => l.ToString()));

    /// <summary>Completion provider for Tab key (e.g. slash command completions).</summary>
    public Func<string, IReadOnlyList<string>>? CompletionProvider { get; set; }

    /// <summary>Completions stashed for display when Tab has no unique prefix.</summary>
    public IReadOnlyList<string>? PendingCompletions => _pendingCompletions;

    /// <summary>Current cursor line index (0-based).</summary>
    public int CursorLine => _cursorLine;

    /// <summary>Current cursor column within the current line (0-based).</summary>
    public int CursorColumn => _cursorColumn;

    /// <summary>Whether the widget has unsubmitted content on any line.</summary>
    public bool HasContent => _lines.Any(l => l.Length > 0);

    /// <summary>Event raised when the user presses Ctrl+Enter with content.</summary>
    public event Action<string>? OnSubmit;

    /// <summary>Insert text at the current cursor position, handling newlines.</summary>
    public void Insert(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Normalize line endings and split
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var parts = normalized.Split('\n');

        var currentLine = _lines[_cursorLine];

        // Insert first part at cursor position
        currentLine.Insert(_cursorColumn, parts[0]);
        _cursorColumn += parts[0].Length;

        // For each subsequent part, create new lines
        for (int i = 1; i < parts.Length; i++)
        {
            // Split current line at cursor: what remains after cursor becomes start of next line
            int restLen = currentLine.Length - _cursorColumn;
            string rest = restLen > 0 ? currentLine.ToString(_cursorColumn, restLen) : "";
            if (restLen > 0)
                currentLine.Remove(_cursorColumn, restLen);

            // Insert new line with remainder + next part
            _lines.Insert(_cursorLine + 1, new StringBuilder(parts[i] + rest));
            _cursorLine++;
            _cursorColumn = parts[i].Length;
            currentLine = _lines[_cursorLine];

            // Enforce maximum input lines
            if (_lines.Count > MaxInputLines)
            {
                // Remove oldest line
                _lines.RemoveAt(0);
                _cursorLine--;
                if (_cursorLine < 0)
                {
                    _cursorLine = 0;
                    _cursorColumn = 0;
                }
            }
        }
    }

    /// <summary>Delete the character before the cursor, merging lines as needed.</summary>
    public void Backspace()
    {
        if (_cursorColumn > 0)
        {
            _lines[_cursorLine].Remove(_cursorColumn - 1, 1);
            _cursorColumn--;
        }
        else if (_cursorLine > 0)
        {
            // Merge current line with previous line
            var prevLine = _lines[_cursorLine - 1];
            var currentLine = _lines[_cursorLine];
            _cursorColumn = prevLine.Length;
            prevLine.Append(currentLine);
            _lines.RemoveAt(_cursorLine);
            _cursorLine--;
        }
    }

    /// <summary>Delete the character at the cursor, merging lines as needed.</summary>
    public void Delete()
    {
        var line = _lines[_cursorLine];
        if (_cursorColumn < line.Length)
        {
            line.Remove(_cursorColumn, 1);
        }
        else if (_cursorLine < _lines.Count - 1)
        {
            // Merge with next line
            var nextLine = _lines[_cursorLine + 1];
            line.Append(nextLine);
            _lines.RemoveAt(_cursorLine + 1);
        }
    }

    /// <summary>Move cursor left, wrapping to previous line at start.</summary>
    public void MoveLeft()
    {
        if (_cursorColumn > 0)
        {
            _cursorColumn--;
        }
        else if (_cursorLine > 0)
        {
            _cursorLine--;
            _cursorColumn = _lines[_cursorLine].Length;
        }
    }

    /// <summary>Move cursor right, wrapping to next line at end.</summary>
    public void MoveRight()
    {
        var line = _lines[_cursorLine];
        if (_cursorColumn < line.Length)
        {
            _cursorColumn++;
        }
        else if (_cursorLine < _lines.Count - 1)
        {
            _cursorLine++;
            _cursorColumn = 0;
        }
    }

    /// <summary>Move cursor up one line, clamping column.</summary>
    public void MoveUp()
    {
        if (_cursorLine > 0)
        {
            _cursorLine--;
            _cursorColumn = Math.Min(_cursorColumn, _lines[_cursorLine].Length);
        }
    }

    /// <summary>Move cursor down one line, clamping column.</summary>
    public void MoveDown()
    {
        if (_cursorLine < _lines.Count - 1)
        {
            _cursorLine++;
            _cursorColumn = Math.Min(_cursorColumn, _lines[_cursorLine].Length);
        }
    }

    /// <summary>Move cursor to the beginning of the current line.</summary>
    public void MoveHome() => _cursorColumn = 0;

    /// <summary>Move cursor to the end of the current line.</summary>
    public void MoveEnd() => _cursorColumn = _lines[_cursorLine].Length;

    /// <summary>Submit the current content and clear the buffer.</summary>
    public void Submit()
    {
        if (_lines.Count == 0 || (_lines.Count == 1 && _lines[0].Length == 0))
            return;

        var text = Text;
        _lines.Clear();
        _lines.Add(new StringBuilder());
        _cursorLine = 0;
        _cursorColumn = 0;
        _pendingCompletions = null;
        _completionAnchor = "";
        AddToHistory(text);
        OnSubmit?.Invoke(text);
    }

    /// <summary>Clear the buffer without submitting.</summary>
    public void Clear()
    {
        _lines.Clear();
        _lines.Add(new StringBuilder());
        _cursorLine = 0;
        _cursorColumn = 0;
        _pendingCompletions = null;
        _completionAnchor = "";
    }

    // ── Readline-like editing commands ──

    /// <summary>
    /// Pi-style backslash workaround: if the character before the cursor is \,
    /// backspace it and insert a newline.  Returns true if the workaround
    /// was applied (caller should NOT submit/insert after this).
    /// </summary>
    private bool TryBackslashNewline()
    {
        var line = _lines[_cursorLine];
        if (_cursorColumn > 0 && line.Length > 0 && line[_cursorColumn - 1] == '\\')
        {
            Backspace();
            Insert("\n");
            return true;
        }
        return false;
    }

    /// <summary>Move cursor to the previous word boundary on the current line.</summary>
    public void MoveWordLeft()
    {
        var line = _lines[_cursorLine];
        if (_cursorColumn <= 0) return;
        int pos = _cursorColumn - 1;
        while (pos >= 0 && !IsWordChar(line[pos])) pos--;
        while (pos >= 0 && IsWordChar(line[pos])) pos--;
        _cursorColumn = Math.Max(0, pos + 1);
    }

    /// <summary>Move cursor to the next word boundary on the current line.</summary>
    public void MoveWordRight()
    {
        var line = _lines[_cursorLine];
        if (_cursorColumn >= line.Length) return;
        int pos = _cursorColumn;
        while (pos < line.Length && IsWordChar(line[pos])) pos++;
        while (pos < line.Length && !IsWordChar(line[pos])) pos++;
        _cursorColumn = pos;
    }

    /// <summary>Delete the word backward from the cursor on the current line.</summary>
    public void DeleteWordBackward()
    {
        var line = _lines[_cursorLine];
        if (_cursorColumn <= 0) return;
        int start = _cursorColumn - 1;
        while (start >= 0 && !IsWordChar(line[start])) start--;
        while (start >= 0 && IsWordChar(line[start])) start--;
        start++;
        int len = _cursorColumn - start;
        _killBuffer = line.ToString(start, len);
        line.Remove(start, len);
        _cursorColumn = start;
    }

    /// <summary>Kill (cut) text from cursor to beginning of the current line.</summary>
    public void KillToStart()
    {
        var line = _lines[_cursorLine];
        if (_cursorColumn <= 0) return;
        _killBuffer = line.ToString(0, _cursorColumn);
        line.Remove(0, _cursorColumn);
        _cursorColumn = 0;
    }

    /// <summary>Kill (cut) text from cursor to end of the current line.</summary>
    public void KillToEnd()
    {
        var line = _lines[_cursorLine];
        if (_cursorColumn >= line.Length) return;
        _killBuffer = line.ToString(_cursorColumn, line.Length - _cursorColumn);
        line.Remove(_cursorColumn, line.Length - _cursorColumn);
    }

    /// <summary>Yank (paste) the last killed text at the cursor.</summary>
    public void Yank()
    {
        if (string.IsNullOrEmpty(_killBuffer)) return;
        var line = _lines[_cursorLine];
        line.Insert(_cursorColumn, _killBuffer);
        _cursorColumn += _killBuffer.Length;
    }

    /// <summary>Transpose characters at and before the cursor on the current line.</summary>
    public void TransposeChars()
    {
        var line = _lines[_cursorLine];
        if (line.Length < 2) return;
        int pos = _cursorColumn;
        if (pos == 0) pos = 1;
        if (pos >= line.Length) pos = line.Length - 1;
        char tmp = line[pos - 1];
        line[pos - 1] = line[pos];
        line[pos] = tmp;
        _cursorColumn = pos + 1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Handle a terminal key event. Returns true if the key was consumed.</summary>
    public bool HandleKey(KeyEvent ke)
    {
        // When the kitty protocol provides text via ResolvedText, use it directly.
        // This handles shifted keys correctly (Shift+1 → "!") and control characters
        // (Ctrl+D → "\x04") via the ResolveText logic in the terminal backend.
        if (ke.Key == Key.Character && (ke.ResolvedText ?? ke.Text?.ToString()) is string text)
        {
            char c = text[0];

            // If the resolved text is multi-character, insert all of it
            if (text.Length > 1)
            {
                Insert(text);
                return true;
            }

            if (c >= 32 && c != 127) // Printable characters (excluding DEL)
            {
                Insert(text);
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
                case '\r': // Enter via legacy raw character
                    if (TryBackslashNewline()) return true;
                    Submit();
                    return true;
                case '\n': // Ctrl+J / legacy line feed
                    Insert("\n");
                    return true;
            }
        }

        switch (ke.Key)
        {
            // Terminals that support enhanced key reporting (xterm modifyOtherKeys,
            // kitty keyboard protocol) send Enter as Key.Enter with modifiers.
            case Key.Enter when ke.Modifiers.HasFlag(KeyModifiers.Shift):
            case Key.Enter when ke.Modifiers.HasFlag(KeyModifiers.Control):
                Insert("\n");
                return true;

            case Key.Enter:
                // Pi-style backslash workaround: if char before cursor is \,
                // backspace it and insert newline instead of submitting.
                // This handles Shift+Enter on terminals that send \ + \r.
                if (TryBackslashNewline()) return true;
                Submit();
                return true;

            // Standard terminals send Enter as a raw \r character with no modifiers,
            // so Shift+Enter is indistinguishable from Enter.  Ctrl+J sends \n
            // (line-feed) and works everywhere as a newline shortcut.
            case Key.Character when ke.Text?.Value == '\n':
                Insert("\n");
                return true;

            case Key.Character when ke.Text?.Value == '\r':
                if (TryBackslashNewline()) return true;
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

            case Key.Up when _cursorLine == 0 && _history.Count > 0:
                HistoryPrevious();
                return true;

            case Key.Up:
                MoveUp();
                return true;

            case Key.Down when _cursorLine == _lines.Count - 1 && _historyIndex >= 0:
                HistoryNext();
                return true;

            case Key.Down:
                MoveDown();
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
            _lines.Clear();
            _lines.Add(new StringBuilder());
            _cursorLine = 0;
            _cursorColumn = 0;
        }
    }

    private void LoadHistoryEntry(int index)
    {
        if (index < 0 || index >= _history.Count) return;
        string text = _history[index];
        _lines.Clear();
        foreach (var part in text.Split('\n'))
            _lines.Add(new StringBuilder(part));
        if (_lines.Count == 0)
            _lines.Add(new StringBuilder());
        _cursorLine = _lines.Count - 1;
        _cursorColumn = _lines[_cursorLine].Length;
    }

    /// <summary>Trigger tab completion on the current buffer content.</summary>
    private void Complete()
    {
        if (CompletionProvider is null)
            return;

        var current = _lines[_cursorLine].ToString();
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
        _lines.Clear();
        foreach (var part in value.Split('\n'))
            _lines.Add(new StringBuilder(part));
        if (_lines.Count == 0)
            _lines.Add(new StringBuilder());
        _cursorLine = _lines.Count - 1;
        _cursorColumn = _lines[_cursorLine].Length;
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
        int lineCount = Math.Min(_lines.Count, MaxInputLines);
        return new Size(available.Width, lineCount + ReservedPopupHeight);
    }

    public void Arrange(Rect bounds)
    {
        _bounds = bounds;
    }

    public void Render(RenderContext context)
    {
        var style = TextStyle.Default;

        // Input area occupies the bottom rows of our bounds
        int visibleLineCount = Math.Min(_lines.Count, MaxInputLines);
        int inputStartRow = _bounds.Bottom - visibleLineCount;

        // If there are pending completions, draw popup above the input area
        if (_pendingCompletions is { Count: > 0 })
        {
            DrawCompletions(context, _bounds.Y, inputStartRow);
        }

        var emptyCell = new RenderCell
        {
            Glyph = GlyphRef.Ascii((byte)' '),
            Width = 1,
            Style = style,
        };

        // Determine which slice of _lines to render (if more than MaxInputLines)
        int lineOffset = _lines.Count > MaxInputLines ? _lines.Count - MaxInputLines : 0;

        // Draw each visible line
        for (int i = 0; i < visibleLineCount; i++)
        {
            int lineIndex = lineOffset + i;
            int row = inputStartRow + i;
            string prefix = (lineIndex == 0) ? "> " : "  ";
            const int prefixWidth = 2;

            // Clear the row
            var inputRect = new Rect(_bounds.X, row, _bounds.Width, 1);
            context.FillRect(inputRect, emptyCell);

            // Draw prefix
            var prefixStyle = (lineIndex == 0)
                ? TextStyle.ForegroundOnly(235, 195, 80)
                : TextStyle.ForegroundOnly(120, 120, 130);
            context.DrawText(_bounds.X, row, Encoding.UTF8.GetBytes(prefix), prefixStyle);

            int lineStartX = _bounds.X + prefixWidth;
            string lineText = _lines[lineIndex].ToString();

            if (lineIndex == _cursorLine)
            {
                // This line has the cursor
                int cursorDisplayWidth = GetDisplayWidth(lineText[.._cursorColumn]);
                int cursorScreenCol = lineStartX + cursorDisplayWidth;

                // Draw text before cursor
                if (_cursorColumn > 0)
                {
                    context.DrawText(lineStartX, row, Encoding.UTF8.GetBytes(lineText[.._cursorColumn]), style);
                }

                // Draw cursor (block cursor via inverted character)
                if (cursorScreenCol < _bounds.Right)
                {
                    string cursorChar = _cursorColumn < lineText.Length
                        ? lineText[_cursorColumn].ToString()
                        : " ";
                    context.DrawText(cursorScreenCol, row,
                        Encoding.UTF8.GetBytes(cursorChar),
                        TextStyle.Inverted);
                }

                // Draw text after cursor
                if (_cursorColumn + 1 < lineText.Length)
                {
                    context.DrawText(cursorScreenCol + 1, row,
                        Encoding.UTF8.GetBytes(lineText[(_cursorColumn + 1)..]), style);
                }
            }
            else
            {
                // No cursor on this line, draw full text
                if (lineText.Length > 0)
                {
                    context.DrawText(lineStartX, row, Encoding.UTF8.GetBytes(lineText), style);
                }
            }
        }
    }

    private void DrawCompletions(RenderContext context, int popupStart, int inputStartRow)
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

    private static int GetDisplayWidth(string text)
    {
        int width = 0;
        foreach (var rune in text.EnumerateRunes())
            width += CellWidthCalculator.GetWidth(rune);
        return width;
    }
}
