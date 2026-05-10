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
///
/// Delegates core logic to <see cref="LineEditorEngine"/> for testability.
/// </summary>
public class LineEditor
{
    private readonly StringBuilder _buf = new();
    private readonly Func<string, IReadOnlyList<string>>? _completionProvider;
    private readonly LineEditorEngine _engine;
    private int _col;

    public LineEditor(Func<string, IReadOnlyList<string>>? completionProvider = null)
    {
        _completionProvider = completionProvider;
        _engine = new LineEditorEngine(completionProvider);
    }

    private bool _inPaste;

    public string? ReadLine(string prompt = "> ")
    {
        _buf.Clear();
        _col = 0;
        _inPaste = false;

        Console.Write(prompt);

        var state = new LineEditorState();

        while (true)
        {
            var rawKey = Console.ReadKey(intercept: true);

            // Paste detection: another key already queued?
            if (!_inPaste && Console.KeyAvailable)
                _inPaste = true;
            else if (_inPaste && !Console.KeyAvailable)
                _inPaste = false;

            state.InPaste = _inPaste;

            var key = MapKey(rawKey);
            var action = _engine.ProcessKey(key, state);

            // Sync local state from engine state
            _buf.Clear();
            _buf.Append(state.Buffer);
            _col = state.Cursor;
            switch (action)
            {
                case LineEditorAction.Submit:
                    Console.WriteLine();
                    return _buf.ToString();

                case LineEditorAction.Cancel:
                    Console.WriteLine("^C");
                    return null;
            }

            // Render the current state to console
            Render(prompt, ref key);
        }
    }

    private void Render(string prompt, ref EditorKeyInfo key)
    {
        // Handle special rendering for different key types
        if (key.Key == ConsoleKey.Escape)
        {
            Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r" + prompt);
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            // Newline insertion
            if (_inPaste || (key.Modifiers & ConsoleModifiers.Shift) != 0 || (_col > 0 && _buf[_col - 1] == '\\'))
            {
                if (!((key.Modifiers & ConsoleModifiers.Shift) != 0) && _col > 0 && _buf[_col - 1] == '\\')
                {
                    Console.Write("\b \b");
                }
                Console.WriteLine();
                return;
            }
            // Submit already handled
            return;
        }

        if (key.Key == ConsoleKey.Backspace && _col > 0)
        {
            var ch = _buf[_col]; // char at the position after deletion
            if (ch == '\n')
            {
                Console.Write("\r\x1b[1A\x1b[0J");
            }
            else
            {
                Console.Write("\b \b");
            }
            RedrawTail();
            return;
        }

        if (key.Key == ConsoleKey.LeftArrow && _col > 0)
        {
            // Cursor already moved by engine; render movement
            Console.Write(_buf[_col] == '\n' ? "\r\x1b[1A" : "\b");
            return;
        }

        if (key.Key == ConsoleKey.RightArrow && _col < _buf.Length)
        {
            if (_buf[_col - 1] == '\n') Console.WriteLine();
            else Console.Write(_buf[_col - 1]);
            return;
        }

        if (key.Key == ConsoleKey.Home)
        {
            // Cursor already at 0
            return;
        }

        if (key.Key == ConsoleKey.End)
        {
            // Cursor already at end
            return;
        }

        if (key.Key == ConsoleKey.Delete && _col < _buf.Length)
        {
            RedrawTail();
            return;
        }

        if (key.KeyChar >= ' ' || key.KeyChar == '\t')
        {
            Console.Write(key.KeyChar);
            RedrawTail();
            return;
        }

        if (key.Key == ConsoleKey.Tab)
        {
            var current = _buf.ToString();
            var completions = _completionProvider?.Invoke(current)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];

            if (completions.Count == 0) return;

            if (completions.Count == 1)
            {
                Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r" + prompt + _buf);
                return;
            }

            var commonPrefix = LongestCommonPrefix(completions);
            if (commonPrefix.Length > current.Length)
            {
                Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r" + prompt + _buf);
                return;
            }

            Console.WriteLine();
            foreach (var c in completions)
                Console.WriteLine($"  {c}");
            Console.Write(prompt + _buf);
            return;
        }
    }

    private static EditorKeyInfo MapKey(ConsoleKeyInfo raw)
        => new(raw.Key, raw.KeyChar, raw.Modifiers);

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

    private void RedrawTail()
    {
        if (_col >= _buf.Length) return;
        var tail = _buf.ToString(_col, _buf.Length - _col);
        Console.Write(tail.Replace("\n", " "));
        Console.Write(new string('\b', tail.Length));
    }
}
