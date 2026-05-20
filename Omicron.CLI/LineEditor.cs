using System.Text;

namespace Omicron.CLI;

/// <summary>
///     Raw-keyboard line reader.
///     - Enter          → submit
///     - Shift+Enter    → insert literal newline
///     - Backslash+Enter → insert literal newline (fallback)
///     Delegates core logic to <see cref="LineEditorEngine" /> for testability.
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

    public string? ReadLine(string prompt = "> ")
    {
        _buf.Clear();
        _col = 0;

        Console.Write(prompt);

        var state = new LineEditorState();

        while (true)
        {
            ConsoleKeyInfo rawKey = Console.ReadKey(true);

            string previousBuffer = state.Buffer.ToString();
            int previousCursor = state.Cursor;

            EditorKeyInfo key = MapKey(rawKey);
            LineEditorAction action = _engine.ProcessKey(key, state);

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

    private void Render(
        string prompt,
        EditorKeyInfo key,
        string previousBuffer,
        int previousCursor,
        LineEditorState state)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r" + prompt);
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            if (
                (key.Modifiers & ConsoleModifiers.Shift) != 0
                || (_col > 0 && _buf[_col - 1] == '\\')
            )
            {
                if (
                    !((key.Modifiers & ConsoleModifiers.Shift) != 0)
                    && _col > 0
                    && _buf[_col - 1] == '\\'
                )
                {
                    Console.Write("\b \b");
                }

                Console.WriteLine();
            }

            return;
        }

        if (key.Key == ConsoleKey.Backspace && previousCursor > 0)
        {
            char deleted = previousBuffer[previousCursor - 1];
            if (deleted == '\n')
            {
                Console.Write("\x1b[K\x1b[1A\x1b[K");
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
            if (previousBuffer[previousCursor] == '\n')
            {
                Console.WriteLine();
            }
            else
            {
                Console.Write(previousBuffer[previousCursor]);
            }

            return;
        }

        if (key.Key == ConsoleKey.Home || key.Key == ConsoleKey.End)
        {
            return;
        }

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
                foreach (string c in state.PendingCompletions)
                {
                    Console.WriteLine($"  {c}");
                }

                Console.Write(prompt + _buf);
                state.PendingCompletions = null;
            }

            return;
        }

        if (key.KeyChar >= ' ')
        {
            Console.Write(key.KeyChar);
            RedrawTail();
        }
    }

    private static EditorKeyInfo MapKey(ConsoleKeyInfo raw)
    {
        return new EditorKeyInfo(raw.Key, raw.KeyChar, raw.Modifiers);
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

    private void RedrawTail(bool clearExtraCharacter = false)
    {
        int tailLength = Math.Max(0, _buf.Length - _col);
        if (tailLength > 0)
        {
            string tail = _buf.ToString(_col, tailLength);
            Console.Write(tail.Replace("\n", " "));
        }

        if (clearExtraCharacter)
        {
            Console.Write(' ');
        }

        int backtrack = tailLength + (clearExtraCharacter ? 1 : 0);
        if (backtrack > 0)
        {
            Console.Write(new string('\b', backtrack));
        }
    }
}
