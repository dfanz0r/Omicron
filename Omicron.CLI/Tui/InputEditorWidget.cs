using System.Text;
using Omicron.Core.Rendering;
using Omicron.Core.Rendering.Layout;
using Omicron.Core.Text;

namespace Omicron.CLI.Tui;

/// <summary>
///     Multi-line text editor for the TUI input line.
///     Supports cursor movement, backspace, delete, home, end,
///     multi-line editing, and submit on Ctrl+Enter.
///     Uses <see cref="CellWidthCalculator" /> for correct cursor positioning with CJK/emoji.
/// </summary>
public sealed class InputEditorWidget : ITuiWidget
{
    // ── Input history ──
    private readonly List<string> _history = [];
    private readonly List<StringBuilder> _lines = [new()];

    // ── Paste markers (pi-style large paste preview) ──
    private readonly Dictionary<int, string> _pastes = new();
    private Rect _bounds;
    private string _completionAnchor = "";
    private int _historyIndex = -1; // -1 means current (new) input
    private string _killBuffer = ""; // for Ctrl+U/K/Y yank/paste
    private int _pasteCounter;

    /// <summary>Maximum number of lines in the input editor.</summary>
    public int MaxInputLines { get; set; } = 50;

    /// <summary>Maximum number of history entries to keep.</summary>
    public int MaxHistory { get; set; } = 100;

    /// <summary>
    ///     Maximum number of completion items to display in the popup.
    /// </summary>
    public int MaxVisibleCompletions { get; set; } = 8;

    /// <summary>
    ///     Height reserved for the completion popup (0 when no completions).
    /// </summary>
    public int ReservedPopupHeight { get; private set; }

    /// <summary>Current text in the input buffer (joined with \n).</summary>
    public string Text => string.Join("\n", _lines.Select(l => l.ToString()));

    /// <summary>Completion provider for Tab key (e.g. slash command completions).</summary>
    public Func<string, IReadOnlyList<string>>? CompletionProvider { get; set; }

    /// <summary>Completions stashed for display when Tab has no unique prefix.</summary>
    public IReadOnlyList<string>? PendingCompletions { get; private set; }

    /// <summary>Current cursor line index (0-based).</summary>
    public int CursorLine { get; private set; }

    /// <summary>Current cursor column within the current line (0-based).</summary>
    public int CursorColumn { get; private set; }

    /// <summary>Whether the widget has unsubmitted content on any line.</summary>
    public bool HasContent => _lines.Any(l => l.Length > 0);

    // ── ITuiWidget implementation ──

    public Size Measure(Size available)
    {
        ReservedPopupHeight = 0;
        if (PendingCompletions is { Count: > 0 })
        {
            int visibleCount = Math.Min(PendingCompletions.Count, MaxVisibleCompletions);
            ReservedPopupHeight = visibleCount + 1; // items + title bar
        }

        // Grow up to half the terminal height, then let the rest overflow
        int lineCount = Math.Min(_lines.Count, Math.Max(1, available.Height / 2));
        return new Size(available.Width, lineCount + ReservedPopupHeight);
    }

    public void Arrange(Rect bounds)
    {
        _bounds = bounds;
    }

    public void Render(RenderContext context)
    {
        TextStyle style = TextStyle.Default;

        // Input area occupies the bottom rows of our bounds
        int visibleLineCount = Math.Min(_lines.Count, _bounds.Height - ReservedPopupHeight);
        int inputStartRow = _bounds.Bottom - visibleLineCount;

        // If there are pending completions, draw popup above the input area
        if (PendingCompletions is { Count: > 0 })
        {
            DrawCompletions(context, _bounds.Y, inputStartRow);
        }

        var emptyCell = new RenderCell
        {
            Glyph = GlyphRef.Ascii((byte)' '),
            Width = 1,
            Style = style
        };

        // Determine which slice of _lines to render (newest lines at bottom)
        int lineOffset = _lines.Count - visibleLineCount;
        if (lineOffset < 0)
        {
            lineOffset = 0;
        }

        // Draw each visible line
        for (int i = 0; i < visibleLineCount; i++)
        {
            int lineIndex = lineOffset + i;
            int row = inputStartRow + i;
            string prefix = lineIndex == 0 ? "> " : "  ";
            const int prefixWidth = 2;

            // Clear the row
            var inputRect = new Rect(_bounds.X, row, _bounds.Width, 1);
            context.FillRect(inputRect, emptyCell);

            // Draw prefix
            TextStyle prefixStyle =
                lineIndex == 0
                    ? TextStyle.ForegroundOnly(235, 195, 80)
                    : TextStyle.ForegroundOnly(120, 120, 130);
            context.DrawText(_bounds.X, row, Encoding.UTF8.GetBytes(prefix), prefixStyle);

            int lineStartX = _bounds.X + prefixWidth;
            string lineText = _lines[lineIndex].ToString();

            if (lineIndex == CursorLine)
            {
                // This line has the cursor
                int cursorDisplayWidth = GetDisplayWidth(lineText[..CursorColumn]);
                int cursorScreenCol = lineStartX + cursorDisplayWidth;

                // Draw text before cursor
                if (CursorColumn > 0)
                {
                    context.DrawText(lineStartX,
                        row,
                        Encoding.UTF8.GetBytes(lineText[..CursorColumn]),
                        style);
                }

                // Draw cursor (block cursor via inverted character)
                if (cursorScreenCol < _bounds.Right)
                {
                    string cursorChar =
                        CursorColumn < lineText.Length ? lineText[CursorColumn].ToString() : " ";
                    context.DrawText(cursorScreenCol,
                        row,
                        Encoding.UTF8.GetBytes(cursorChar),
                        TextStyle.Inverted);
                }

                // Draw text after cursor
                if (CursorColumn + 1 < lineText.Length)
                {
                    context.DrawText(cursorScreenCol + 1,
                        row,
                        Encoding.UTF8.GetBytes(lineText[(CursorColumn + 1)..]),
                        style);
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

    /// <summary>Add an entry to the input history.</summary>
    public void AddToHistory(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Don't add duplicates of the most recent entry
        if (_history.Count > 0 && _history[^1] == text)
        {
            return;
        }

        _history.Add(text);
        if (_history.Count > MaxHistory)
        {
            _history.RemoveAt(0);
        }

        _historyIndex = -1;
    }

    /// <summary>Event raised when the user presses Ctrl+Enter with content.</summary>
    public event Action<string>? OnSubmit;

    /// <summary>Insert text at the current cursor position, handling newlines.</summary>
    public void Insert(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Normalize line endings and split
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] parts = normalized.Split('\n');

        // Large paste: store full text and insert a short marker instead
        if (parts.Length > 10 || normalized.Length > 1000)
        {
            _pasteCounter++;
            int pasteId = _pasteCounter;
            _pastes[pasteId] = normalized;
            string marker =
                parts.Length > 10
                    ? $"[paste #{pasteId} +{parts.Length} lines]"
                    : $"[paste #{pasteId} {normalized.Length} chars]";
            InsertSingleLine(marker);
            return;
        }

        StringBuilder currentLine = _lines[CursorLine];

        // Insert first part at cursor position
        currentLine.Insert(CursorColumn, parts[0]);
        CursorColumn += parts[0].Length;

        // For each subsequent part, create new lines
        for (int i = 1; i < parts.Length; i++)
        {
            // Split current line at cursor: what remains after cursor becomes start of next line
            int restLen = currentLine.Length - CursorColumn;
            string rest = restLen > 0 ? currentLine.ToString(CursorColumn, restLen) : "";
            if (restLen > 0)
            {
                currentLine.Remove(CursorColumn, restLen);
            }

            // Insert new line with remainder + next part
            _lines.Insert(CursorLine + 1, new StringBuilder(parts[i] + rest));
            CursorLine++;
            CursorColumn = parts[i].Length;
            currentLine = _lines[CursorLine];
        }
    }

    /// <summary>Insert text at cursor on the current line without splitting on newlines.</summary>
    private void InsertSingleLine(string text)
    {
        StringBuilder line = _lines[CursorLine];
        line.Insert(CursorColumn, text);
        CursorColumn += text.Length;
    }

    /// <summary>Delete the character before the cursor, merging lines as needed.</summary>
    public void Backspace()
    {
        if (CursorColumn > 0)
        {
            _lines[CursorLine].Remove(CursorColumn - 1, 1);
            CursorColumn--;
        }
        else if (CursorLine > 0)
        {
            // Merge current line with previous line
            StringBuilder prevLine = _lines[CursorLine - 1];
            StringBuilder currentLine = _lines[CursorLine];
            CursorColumn = prevLine.Length;
            prevLine.Append(currentLine);
            _lines.RemoveAt(CursorLine);
            CursorLine--;
        }
    }

    /// <summary>Delete the character at the cursor, merging lines as needed.</summary>
    public void Delete()
    {
        StringBuilder line = _lines[CursorLine];
        if (CursorColumn < line.Length)
        {
            line.Remove(CursorColumn, 1);
        }
        else if (CursorLine < _lines.Count - 1)
        {
            // Merge with next line
            StringBuilder nextLine = _lines[CursorLine + 1];
            line.Append(nextLine);
            _lines.RemoveAt(CursorLine + 1);
        }
    }

    /// <summary>Move cursor left, wrapping to previous line at start.</summary>
    public void MoveLeft()
    {
        if (CursorColumn > 0)
        {
            CursorColumn--;
        }
        else if (CursorLine > 0)
        {
            CursorLine--;
            CursorColumn = _lines[CursorLine].Length;
        }
    }

    /// <summary>Move cursor right, wrapping to next line at end.</summary>
    public void MoveRight()
    {
        StringBuilder line = _lines[CursorLine];
        if (CursorColumn < line.Length)
        {
            CursorColumn++;
        }
        else if (CursorLine < _lines.Count - 1)
        {
            CursorLine++;
            CursorColumn = 0;
        }
    }

    /// <summary>Move cursor up one line, clamping column.</summary>
    public void MoveUp()
    {
        if (CursorLine > 0)
        {
            CursorLine--;
            CursorColumn = Math.Min(CursorColumn, _lines[CursorLine].Length);
        }
    }

    /// <summary>Move cursor down one line, clamping column.</summary>
    public void MoveDown()
    {
        if (CursorLine < _lines.Count - 1)
        {
            CursorLine++;
            CursorColumn = Math.Min(CursorColumn, _lines[CursorLine].Length);
        }
    }

    /// <summary>Move cursor to the beginning of the current line.</summary>
    public void MoveHome()
    {
        CursorColumn = 0;
    }

    /// <summary>Move cursor to the end of the current line.</summary>
    public void MoveEnd()
    {
        CursorColumn = _lines[CursorLine].Length;
    }

    /// <summary>Submit the current content and clear the buffer.</summary>
    public void Submit()
    {
        if (_lines.Count == 0 || (_lines.Count == 1 && _lines[0].Length == 0))
        {
            return;
        }

        string expanded = ExpandPasteMarkers(Text);
        _lines.Clear();
        _lines.Add(new StringBuilder());
        CursorLine = 0;
        CursorColumn = 0;
        PendingCompletions = null;
        _completionAnchor = "";
        _pastes.Clear();
        _pasteCounter = 0;
        AddToHistory(expanded);
        OnSubmit?.Invoke(expanded);
    }

    private string ExpandPasteMarkers(string text)
    {
        foreach ((int pasteId, string content) in _pastes)
        {
            string marker = $"[paste #{pasteId}";
            int idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                // Find end of marker (at closing bracket)
                int end = text.IndexOf(']', idx);
                if (end >= 0)
                {
                    text = text[..idx] + content + text[(end + 1)..];
                }
            }
        }

        return text;
    }

    /// <summary>Clear the buffer without submitting.</summary>
    public void Clear()
    {
        _lines.Clear();
        _lines.Add(new StringBuilder());
        CursorLine = 0;
        CursorColumn = 0;
        PendingCompletions = null;
        _completionAnchor = "";
    }

    // ── Readline-like editing commands ──

    /// <summary>
    ///     Pi-style backslash workaround: if the character before the cursor is \,
    ///     backspace it and insert a newline.  Returns true if the workaround
    ///     was applied (caller should NOT submit/insert after this).
    /// </summary>
    private bool TryBackslashNewline()
    {
        StringBuilder line = _lines[CursorLine];
        if (CursorColumn > 0 && line.Length > 0 && line[CursorColumn - 1] == '\\')
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
        StringBuilder line = _lines[CursorLine];
        if (CursorColumn <= 0)
        {
            return;
        }

        int pos = CursorColumn - 1;
        while (pos >= 0 && !IsWordChar(line[pos]))
        {
            pos--;
        }

        while (pos >= 0 && IsWordChar(line[pos]))
        {
            pos--;
        }

        CursorColumn = Math.Max(0, pos + 1);
    }

    /// <summary>Move cursor to the next word boundary on the current line.</summary>
    public void MoveWordRight()
    {
        StringBuilder line = _lines[CursorLine];
        if (CursorColumn >= line.Length)
        {
            return;
        }

        int pos = CursorColumn;
        while (pos < line.Length && IsWordChar(line[pos]))
        {
            pos++;
        }

        while (pos < line.Length && !IsWordChar(line[pos]))
        {
            pos++;
        }

        CursorColumn = pos;
    }

    /// <summary>Delete the word backward from the cursor.</summary>
    public void DeleteWordBackward()
    {
        StringBuilder line = _lines[CursorLine];

        // At start of line — merge with previous line first, then continue
        if (CursorColumn <= 0)
        {
            if (CursorLine > 0)
            {
                StringBuilder prevLine = _lines[CursorLine - 1];
                CursorColumn = prevLine.Length;
                prevLine.Append(line);
                _lines.RemoveAt(CursorLine);
                CursorLine--;
            }
            else
            {
                return; // Top of buffer, nothing to delete
            }

            line = _lines[CursorLine];
        }

        int start = CursorColumn - 1;
        while (start >= 0 && !IsWordChar(line[start]))
        {
            start--;
        }

        while (start >= 0 && IsWordChar(line[start]))
        {
            start--;
        }

        start++;
        int len = CursorColumn - start;
        _killBuffer = line.ToString(start, len);
        line.Remove(start, len);
        CursorColumn = start;
    }

    /// <summary>Kill (cut) text from cursor to beginning of the current line.</summary>
    public void KillToStart()
    {
        StringBuilder line = _lines[CursorLine];
        if (CursorColumn <= 0)
        {
            return;
        }

        _killBuffer = line.ToString(0, CursorColumn);
        line.Remove(0, CursorColumn);
        CursorColumn = 0;
    }

    /// <summary>Kill (cut) text from cursor to end of the current line.</summary>
    public void KillToEnd()
    {
        StringBuilder line = _lines[CursorLine];
        if (CursorColumn >= line.Length)
        {
            return;
        }

        _killBuffer = line.ToString(CursorColumn, line.Length - CursorColumn);
        line.Remove(CursorColumn, line.Length - CursorColumn);
    }

    /// <summary>Yank (paste) the last killed text at the cursor.</summary>
    public void Yank()
    {
        if (string.IsNullOrEmpty(_killBuffer))
        {
            return;
        }

        StringBuilder line = _lines[CursorLine];
        line.Insert(CursorColumn, _killBuffer);
        CursorColumn += _killBuffer.Length;
    }

    /// <summary>Transpose characters at and before the cursor on the current line.</summary>
    public void TransposeChars()
    {
        StringBuilder line = _lines[CursorLine];
        if (line.Length < 2)
        {
            return;
        }

        int pos = CursorColumn;
        if (pos == 0)
        {
            pos = 1;
        }

        if (pos >= line.Length)
        {
            pos = line.Length - 1;
        }

        char tmp = line[pos - 1];
        line[pos - 1] = line[pos];
        line[pos] = tmp;
        CursorColumn = pos + 1;
    }

    private static bool IsWordChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_';
    }

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
                    if (TryBackslashNewline())
                    {
                        return true;
                    }

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
                if (TryBackslashNewline())
                {
                    return true;
                }

                Submit();
                return true;

            // Standard terminals send Enter as a raw \r character with no modifiers,
            // so Shift+Enter is indistinguishable from Enter.  Ctrl+J sends \n
            // (line-feed) and works everywhere as a newline shortcut.
            case Key.Character when ke.Text?.Value == '\n':
                Insert("\n");
                return true;

            case Key.Character when ke.Text?.Value == '\r':
                if (TryBackslashNewline())
                {
                    return true;
                }

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

            case Key.Up when CursorLine == 0 && _history.Count > 0:
                HistoryPrevious();
                return true;

            case Key.Up:
                MoveUp();
                return true;

            case Key.Down when CursorLine == _lines.Count - 1 && _historyIndex >= 0:
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
    ///     Navigate to the previous entry in history (most recent first).
    ///     History is stored oldest-first, so we access from the end.
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
            CursorLine = 0;
            CursorColumn = 0;
        }
    }

    private void LoadHistoryEntry(int index)
    {
        if (index < 0 || index >= _history.Count)
        {
            return;
        }

        string text = _history[index];
        _lines.Clear();
        foreach (string part in text.Split('\n'))
        {
            _lines.Add(new StringBuilder(part));
        }

        if (_lines.Count == 0)
        {
            _lines.Add(new StringBuilder());
        }

        CursorLine = _lines.Count - 1;
        CursorColumn = _lines[CursorLine].Length;
    }

    /// <summary>Trigger tab completion on the current buffer content.</summary>
    private void Complete()
    {
        if (CompletionProvider is null)
        {
            return;
        }

        string current = _lines[CursorLine].ToString();
        if (PendingCompletions is not null && current != _completionAnchor)
        {
            PendingCompletions = null;
        }

        var completions = CompletionProvider(current)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (completions.Count == 0)
        {
            return;
        }

        if (completions.Count == 1)
        {
            PendingCompletions = null;
            _completionAnchor = "";
            ReplaceBuffer(completions[0]);
            return;
        }

        string commonPrefix = LongestCommonPrefix(completions);
        if (commonPrefix.Length > current.Length)
        {
            PendingCompletions = null;
            _completionAnchor = "";
            ReplaceBuffer(commonPrefix);
            return;
        }

        // Multiple completions, no common prefix extension — stash for display
        PendingCompletions = completions;
        _completionAnchor = current;
    }

    private void ReplaceBuffer(string value)
    {
        _lines.Clear();
        foreach (string part in value.Split('\n'))
        {
            _lines.Add(new StringBuilder(part));
        }

        if (_lines.Count == 0)
        {
            _lines.Add(new StringBuilder());
        }

        CursorLine = _lines.Count - 1;
        CursorColumn = _lines[CursorLine].Length;
    }

    private static string LongestCommonPrefix(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return string.Empty;
        }

        string prefix = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            while (!values[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (prefix.Length == 0)
                {
                    return string.Empty;
                }

                prefix = prefix[..^1];
            }
        }

        return prefix;
    }

    private void DrawCompletions(RenderContext context, int popupStart, int inputStartRow)
    {
        if (PendingCompletions is null || PendingCompletions.Count == 0)
        {
            return;
        }

        int maxItems = Math.Min(PendingCompletions.Count, MaxVisibleCompletions);
        int popupWidth = Math.Min(_bounds.Width - 4, 60);
        int popupX = _bounds.X + 2;

        // Draw popup background
        var popupBg = new RenderCell
        {
            Glyph = GlyphRef.Ascii((byte)' '),
            Width = 1,
            Style = new TextStyle(200, 200, 200, 40, 40, 45, false, false, false)
        };
        context.FillRect(new Rect(popupX, popupStart, popupWidth, maxItems + 1), popupBg);

        // Title
        using var titleBuilder = new Utf8Builder();
        titleBuilder.AppendLiteral(" Completions ("u8);
        titleBuilder.Append(PendingCompletions.Count);
        titleBuilder.AppendLiteral("): "u8);
        context.DrawText(popupX + 1, popupStart, titleBuilder.AsSpan(),
            TextStyle.ForegroundOnly(150, 150, 160));

        // Items (first item highlighted)
        for (int i = 0; i < maxItems; i++)
        {
            var itemStyle = (i == 0)
                ? TextStyle.Inverted
                : TextStyle.ForegroundOnly(200, 200, 200);
            using var itemBuilder = new Utf8Builder();
            itemBuilder.AppendLiteral(" "u8);
            itemBuilder.Append(PendingCompletions[i]);
            context.DrawText(popupX + 1, popupStart + 1 + i, itemBuilder.AsSpan(), itemStyle);
        }

        // If there are more items than we can show, indicate that
        if (PendingCompletions.Count > maxItems)
        {
            using var moreBuilder = new Utf8Builder();
            moreBuilder.AppendLiteral(" ... and "u8);
            moreBuilder.Append(PendingCompletions.Count - maxItems + 1);
            moreBuilder.AppendLiteral(" more"u8);
            context.DrawText(popupX + 1, popupStart + 1 + maxItems - 1, moreBuilder.AsSpan(),
                TextStyle.ForegroundOnly(120, 120, 130));
        }
    }

    private static int GetDisplayWidth(string text)
    {
        int width = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            width += CellWidthCalculator.GetWidth(rune);
        }

        return width;
    }
}
