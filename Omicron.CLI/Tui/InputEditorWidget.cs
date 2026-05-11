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

    /// <summary>Current text in the input buffer.</summary>
    public string Text => _buffer.ToString();

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
        OnSubmit?.Invoke(text);
    }

    /// <summary>Clear the buffer without submitting.</summary>
    public void Clear()
    {
        _buffer.Clear();
        _cursorPosition = 0;
    }

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
        }

        switch (ke.Key)
        {
            case Key.Enter:
                Submit();
                return true;

            case Key.Backspace:
                Backspace();
                return true;

            case Key.Delete:
                Delete();
                return true;

            case Key.Left:
                MoveLeft();
                return true;

            case Key.Right:
                MoveRight();
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
        }

        return false;
    }

    // ── ITuiWidget implementation ──

    public Size Measure(Size available) => new(available.Width, 1);

    public void Arrange(Rect bounds)
    {
        _bounds = bounds;
    }

    public void Render(RenderContext context)
    {
        int row = _bounds.Y;
        var style = TextStyle.Default;

        // Clear the row
        var emptyCell = new RenderCell
        {
            Glyph = GlyphRef.Ascii((byte)' '),
            Width = 1,
            Style = style,
        };
        context.FillRect(_bounds, emptyCell);

        // Draw prompt prefix
        string prompt = "> ";
        int x = _bounds.X;
        context.DrawText(x, row, Encoding.UTF8.GetBytes(prompt), TextStyle.ForegroundOnly(0, 200, 0));
        x += prompt.Length;

        // Compute display width of text before cursor (for correct CJK positioning)
        int cursorDisplayWidth = GetDisplayWidth(_buffer.ToString()[.._cursorPosition]);
        int cursorScreenCol = x + cursorDisplayWidth;

        // Draw buffer text up to cursor
        if (_cursorPosition > 0)
        {
            var beforeCursor = Encoding.UTF8.GetBytes(_buffer.ToString()[.._cursorPosition]);
            context.DrawText(x, row, beforeCursor, style);
        }

        // Draw cursor (block cursor via inverted space + character)
        if (cursorScreenCol < _bounds.Right)
        {
            string cursorChar = _cursorPosition < _buffer.Length
                ? _buffer[_cursorPosition].ToString()
                : " ";
            context.DrawText(cursorScreenCol, row,
                Encoding.UTF8.GetBytes(cursorChar),
                TextStyle.Inverted);
        }

        // Draw text after cursor
        if (_cursorPosition < _buffer.Length)
        {
            var afterCursor = Encoding.UTF8.GetBytes(_buffer.ToString()[_cursorPosition..]);
            context.DrawText(cursorScreenCol + 1, row, afterCursor, style);
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
