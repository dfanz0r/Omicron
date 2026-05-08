using System.Text;

namespace Omicron.CLI;

/// <summary>
/// Raw-keyboard line reader.
///
/// - Enter          → submit
/// - Shift+Enter    → insert literal newline
/// - Backslash+Enter → insert literal newline (fallback)
/// - Paste          → detected by pre-buffered console input;
///                     newlines stay literal, Enter does not submit.
/// </summary>
public class LineEditor
{
    private readonly StringBuilder _buf = new();
    private int _col;

    // Paste mode: enters when multiple keys are queued in the console
    // buffer.  Exits when the buffer is drained (KeyAvailable == false).
    private bool _inPaste;

    /// <summary>Set by the chat loop to signal an escape interrupt.</summary>
    public bool EscapePressed { get; private set; }

    public string? ReadLine(string prompt = "> ")
    {
        _buf.Clear();
        _col = 0;
        _inPaste = false;
        EscapePressed = false;

        Console.Write(prompt);

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            // Paste detection: was another key already queued?
            // If so, we're receiving a paste and should treat
            // Enter/newline as literal text from here on.
            if (!_inPaste && Console.KeyAvailable)
                _inPaste = true;
            else if (_inPaste && !Console.KeyAvailable)
                _inPaste = false;

            // Escape — clear the line
            if (key.Key == ConsoleKey.Escape)
            {
                _buf.Clear();
                _col = 0;
                Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r" + prompt);
                continue;
            }

            // Ctrl+C
            if (key is { Key: ConsoleKey.C, Modifiers: ConsoleModifiers.Control })
            {
                Console.WriteLine("^C");
                return null;
            }

            // Enter
            if (key.Key == ConsoleKey.Enter)
            {
                bool shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;

                // Shift+Enter or \Enter → always insert newline
                if (shift || (_col > 0 && _buf[_col - 1] == '\\'))
                {
                    if (!shift)
                    {
                        _buf.Remove(_col - 1, 1);
                        _col--;
                        Console.Write("\b \b");
                    }
                    Insert('\n');
                    Console.WriteLine();
                    continue;
                }

                // In paste mode: enter is literal newline
                if (_inPaste)
                {
                    Insert('\n');
                    Console.WriteLine();
                    continue;
                }

                // Submit
                Console.WriteLine();
                return _buf.ToString();
            }

            // Backspace
            if (key.Key == ConsoleKey.Backspace && _col > 0)
            {
                _col--;
                var ch = _buf[_col];
                _buf.Remove(_col, 1);
                if (ch == '\n')
                {
                    // Move up and clear rest of screen
                    Console.Write("\r\x1b[1A\x1b[0J");
                    RedrawTail();
                }
                else
                {
                    Console.Write("\b \b");
                    RedrawTail();
                }
                continue;
            }

            // Left arrow
            if (key.Key == ConsoleKey.LeftArrow && _col > 0)
            {
                _col--;
                Console.Write(_buf[_col] == '\n' ? "\r\x1b[1A" : "\b");
                continue;
            }

            // Right arrow
            if (key.Key == ConsoleKey.RightArrow && _col < _buf.Length)
            {
                if (_buf[_col] == '\n') Console.WriteLine();
                else Console.Write(_buf[_col]);
                _col++;
                continue;
            }

            // Home / End
            if (key.Key == ConsoleKey.Home) { while (_col > 0) { _col--; Console.Write(_buf[_col] == '\n' ? "\r\x1b[1A" : "\b"); } continue; }
            if (key.Key == ConsoleKey.End) { while (_col < _buf.Length) { if (_buf[_col] == '\n') Console.WriteLine(); else Console.Write(_buf[_col]); _col++; } continue; }

            // Delete
            if (key.Key == ConsoleKey.Delete && _col < _buf.Length)
            {
                _buf.Remove(_col, 1);
                RedrawTail();
                continue;
            }

            // Printable
            if (key.KeyChar >= ' ' || key.KeyChar == '\t')
            {
                Insert(key.KeyChar);
                continue;
            }
        }
    }

    private void Insert(char c)
    {
        _buf.Insert(_col, c);
        _col++;
        Console.Write(c);
        RedrawTail();
    }

    private void RedrawTail()
    {
        if (_col >= _buf.Length) return;
        var tail = _buf.ToString(_col, _buf.Length - _col);
        Console.Write(tail.Replace("\n", " "));
        Console.Write(new string('\b', tail.Length));
    }
}
