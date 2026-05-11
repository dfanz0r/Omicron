namespace Omicron.CLI;

/// <summary>
/// Represents a key press for the LineEditor engine.
/// Models the minimal inputs needed for testing.
/// </summary>
public readonly record struct EditorKeyInfo(
    ConsoleKey Key,
    char KeyChar,
    ConsoleModifiers Modifiers)
{
    public static readonly EditorKeyInfo Enter = new(ConsoleKey.Enter, '\r', 0);
    public static readonly EditorKeyInfo ShiftEnter = new(ConsoleKey.Enter, '\r', ConsoleModifiers.Shift);
    public static readonly EditorKeyInfo Backspace = new(ConsoleKey.Backspace, '\0', 0);
    public static readonly EditorKeyInfo Tab = new(ConsoleKey.Tab, '\t', 0);
    public static readonly EditorKeyInfo Escape = new(ConsoleKey.Escape, '\0', 0);
    public static readonly EditorKeyInfo LeftArrow = new(ConsoleKey.LeftArrow, '\0', 0);
    public static readonly EditorKeyInfo RightArrow = new(ConsoleKey.RightArrow, '\0', 0);
    public static readonly EditorKeyInfo Home = new(ConsoleKey.Home, '\0', 0);
    public static readonly EditorKeyInfo End = new(ConsoleKey.End, '\0', 0);
    public static readonly EditorKeyInfo Delete = new(ConsoleKey.Delete, '\0', 0);
    public static readonly EditorKeyInfo CtrlC = new(ConsoleKey.C, '\0', ConsoleModifiers.Control);

    public static EditorKeyInfo Char(char c) => new(ConsoleKey.OemPeriod, c, 0);
}

/// <summary>
/// Mutable state for the LineEditor engine.
/// Tracks the buffer, cursor position, and completion state.
/// </summary>
public sealed class LineEditorState
{
    public System.Text.StringBuilder Buffer { get; } = new();
    public int Cursor { get; set; }
    public bool EscapePressed { get; set; }

    /// <summary>Set by the engine when multiple completions should be displayed (e.g. tab pressed with no unique prefix).</summary>
    public IReadOnlyList<string>? PendingCompletions { get; set; }
}

/// <summary>
/// Result of processing a key in LineEditorEngine.
/// </summary>
public enum LineEditorAction
{
    None,
    Submit,
    Cancel,
}

/// <summary>
/// Testable core of the LineEditor. Processes key input against state
/// without any console I/O. The real LineEditor wraps this engine.
/// </summary>
public class LineEditorEngine
{
    private readonly Func<string, IReadOnlyList<string>>? _completionProvider;

    public LineEditorEngine(Func<string, IReadOnlyList<string>>? completionProvider = null)
    {
        _completionProvider = completionProvider;
    }

    /// <summary>
    /// Process a single key press against the given state.
    /// Returns the action that should be taken (Submit/Cancel/None).
    /// The caller is responsible for updating the console display.
    /// </summary>
    public LineEditorAction ProcessKey(EditorKeyInfo key, LineEditorState state)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            state.Buffer.Clear();
            state.Cursor = 0;
            state.EscapePressed = true;
            return LineEditorAction.None;
        }

        if (key == EditorKeyInfo.CtrlC)
            return LineEditorAction.Cancel;

        if (key.Key == ConsoleKey.Enter)
        {
            bool shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;

            if (shift || (state.Cursor > 0 && state.Buffer[state.Cursor - 1] == '\\'))
            {
                if (!shift)
                {
                    state.Buffer.Remove(state.Cursor - 1, 1);
                    state.Cursor--;
                }
                Insert('\n', state);
                return LineEditorAction.None;
            }

            return LineEditorAction.Submit;
        }

        if (key.Key == ConsoleKey.Tab)
        {
            Complete(state);
            return LineEditorAction.None;
        }

        if (key.Key == ConsoleKey.Backspace && state.Cursor > 0)
        {
            state.Cursor--;
            state.Buffer.Remove(state.Cursor, 1);
            return LineEditorAction.None;
        }

        if (key.Key == ConsoleKey.LeftArrow && state.Cursor > 0)
        {
            state.Cursor--;
            return LineEditorAction.None;
        }

        if (key.Key == ConsoleKey.RightArrow && state.Cursor < state.Buffer.Length)
        {
            state.Cursor++;
            return LineEditorAction.None;
        }

        if (key.Key == ConsoleKey.Home)
        {
            state.Cursor = 0;
            return LineEditorAction.None;
        }

        if (key.Key == ConsoleKey.End)
        {
            state.Cursor = state.Buffer.Length;
            return LineEditorAction.None;
        }

        if (key.Key == ConsoleKey.Delete && state.Cursor < state.Buffer.Length)
        {
            state.Buffer.Remove(state.Cursor, 1);
            return LineEditorAction.None;
        }

        if (key.KeyChar >= ' ' || key.KeyChar == '\t')
        {
            Insert(key.KeyChar, state);
            state.PendingCompletions = null;
            return LineEditorAction.None;
        }

        return LineEditorAction.None;
    }

    private void Insert(char c, LineEditorState state)
    {
        state.Buffer.Insert(state.Cursor, c);
        state.Cursor++;
    }

    private void Complete(LineEditorState state)
    {
        if (_completionProvider is null || state.Cursor != state.Buffer.Length)
            return;

        var current = state.Buffer.ToString();
        var completions = _completionProvider(current)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (completions.Count == 0)
            return;

        if (completions.Count == 1)
        {
            state.PendingCompletions = null;
            ReplaceBuffer(state, completions[0]);
            return;
        }

        var commonPrefix = LongestCommonPrefix(completions);
        if (commonPrefix.Length > current.Length)
        {
            state.PendingCompletions = null;
            ReplaceBuffer(state, commonPrefix);
            return;
        }

        // Multiple completions, no common prefix extension — stash for display
        state.PendingCompletions = completions;
    }

    private void ReplaceBuffer(LineEditorState state, string value)
    {
        state.Buffer.Clear();
        state.Buffer.Append(value);
        state.Cursor = state.Buffer.Length;
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
}
