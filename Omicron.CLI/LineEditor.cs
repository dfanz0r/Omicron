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

            var previousBuffer = state.Buffer.ToString();
            var previousCursor = state.Cursor;

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
            Render(prompt, key, previousBuffer, previousCursor, state);
        }
    }

    private void Render(string prompt, EditorKeyInfo key, string previousBuffer, int previousCursor, LineEditorState state)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r" + prompt);
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            if (_inPaste || (key.Modifiers & ConsoleModifiers.Shift) != 0 || (_col > 0 && _buf[_col - 1] == '\\'))
            {
                if (!((key.Modifiers & ConsoleModifiers.Shift) != 0) && _col > 0 && _buf[_col - 1] == '\\')
                {
                    Console.Write("\b \b");
                }
                Console.WriteLine();
                return;
            }
            return;
        }

        if (key.Key == ConsoleKey.Backspace && previousCursor > 0)
        {
            var deleted = previousBuffer[previousCursor - 1];
            if (deleted == '\n')
            {
                Console.Write("\r\x1b[1A\x1b[0J");
                RedrawTail();
            }
            else
            {
                Console.Write("\b \b");
                RedrawTail();
            }
            return;
        }

        if (key.Key == ConsoleKey.LeftArrow && previousCursor > 0)
        {
            Console.Write(previousBuffer[previousCursor - 1] == '\n' ? "\r\x1b[1A" : "\b");
            return;
        }

        if (key.Key == ConsoleKey.RightArrow && previousCursor < previousBuffer.Length)
        {
            if (previousBuffer[previousCursor] == '\n') Console.WriteLine();
            else Console.Write(previousBuffer[previousCursor]);
            return;
        }

        if (key.Key == ConsoleKey.Home || key.Key == ConsoleKey.End)
            return;

        if (key.Key == ConsoleKey.Delete && previousCursor < previousBuffer.Length)
        {
            RedrawTail();
            return;
        }

        if (key.Key == ConsoleKey.Tab)
        {
            // Engine already completed the buffer if applicable.
            // Redraw the full line to reflect the engine's state.
            Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r" + prompt + _buf);

            // If the engine stashed multiple completions, display them below
            if (state.PendingCompletions is { Count: > 0 })
            {
                Console.WriteLine();
                foreach (var c in state.PendingCompletions)
                    Console.WriteLine($"  {c}");
                Console.Write(prompt + _buf);
                state.PendingCompletions = null;
            }
            return;
        }

        if (key.KeyChar >= ' ')
        {
            Console.Write(key.KeyChar);
            RedrawTail();
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

    private void RedrawTail(bool clearExtraCharacter = false)
    {
        var tailLength = Math.Max(0, _buf.Length - _col);
        if (tailLength > 0)
        {
            var tail = _buf.ToString(_col, tailLength);
            Console.Write(tail.Replace("\n", " "));
        }

        if (clearExtraCharacter)
            Console.Write(' ');

        var backtrack = tailLength + (clearExtraCharacter ? 1 : 0);
        if (backtrack > 0)
            Console.Write(new string('\b', backtrack));
    }
}
