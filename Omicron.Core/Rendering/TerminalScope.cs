using System.Text;

namespace Omicron.Core.Rendering;

/// <summary>
/// Ref-counted terminal mode scope. Each scope type (alternate screen, raw mode,
/// cursor hide, bracketed paste, mouse) is tracked by a count on the backend's
/// scope counters. The ANSI enter sequence is written only on first acquire
/// (0→1). The exit sequence is written only on last release (1→0).
///
/// Safety nets via <see cref="TerminalLifecycle"/> handle crash recovery.
/// </summary>
public sealed class TerminalScope : IDisposable
{
    private readonly ITerminalBackend _backend;
    private readonly ScopeType _type;
    private bool _disposed;

    private TerminalScope(ITerminalBackend backend, ScopeType type)
    {
        _backend = backend;
        _type = type;
    }

    /// <summary>Acquire alternate screen mode. Nested calls are ref-counted.</summary>
    public static TerminalScope UseAlternateScreen(ITerminalBackend backend)
        => Acquire(backend, ScopeType.AlternateScreen, "\x1b[?1049h");

    // UseRawMode deliberately omitted — raw mode is always active
    // while SystemTerminalBackend is alive. The backend constructor
    // sets raw mode eagerly on Unix.

    /// <summary>Hide the cursor.</summary>
    public static TerminalScope HideCursor(ITerminalBackend backend)
        => Acquire(backend, ScopeType.HideCursor, "\x1b[?25l");

    /// <summary>Enable bracketed paste mode.</summary>
    public static TerminalScope UseBracketedPaste(ITerminalBackend backend)
        => Acquire(backend, ScopeType.BracketedPaste, "\x1b[?2004h");

    /// <summary>Enable mouse event reporting.</summary>
    public static TerminalScope UseMouse(ITerminalBackend backend)
        => Acquire(backend, ScopeType.Mouse, "\x1b[?1000h\x1b[?1002h\x1b[?1006h");

    private static TerminalScope Acquire(ITerminalBackend backend, ScopeType type, string? enterSequence)
    {
        var counters = ScopeCounter.GetOrCreate(backend);
        int count = counters.GetCount(type);
        if (count == 0 && enterSequence is not null)
        {
            // Write enter sequence
            var bytes = Encoding.UTF8.GetBytes(enterSequence);
            backend.Output.Write(bytes);
            backend.Flush();
        }
        counters.Increment(type);

        // Track globally for emergency cleanup
        TerminalLifecycle.Track(backend, type);

        return new TerminalScope(backend, type);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var counters = ScopeCounter.GetOrCreate(_backend);
        int count = counters.Decrement(_type);

        if (count == 0)
        {
            string? exitSequence = _type switch
            {
                ScopeType.AlternateScreen => "\x1b[?1049l",
                ScopeType.HideCursor => "\x1b[?25h",
                ScopeType.BracketedPaste => "\x1b[?2004l",
                ScopeType.Mouse => "\x1b[?1006l\x1b[?1002l\x1b[?1000l",
                _ => null
            };

            if (exitSequence is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(exitSequence);
                _backend.Output.Write(bytes);
                _backend.Flush();
            }
        }

        TerminalLifecycle.Untrack(_backend, _type);
    }

    internal enum ScopeType
    {
        AlternateScreen,
        RawMode,
        HideCursor,
        BracketedPaste,
        Mouse,
    }

    /// <summary>
    /// Per-backend scope counters. Uses a ConditionalWeakTable so counters
    /// are automatically cleaned up when the backend is disposed.
    /// </summary>
    internal sealed class ScopeCounter
    {
        private readonly int[] _counts = new int[5];

        public static ScopeCounter GetOrCreate(ITerminalBackend backend)
        {
            if (!_table.TryGetValue(backend, out var counter))
            {
                counter = new ScopeCounter();
                _table.Add(backend, counter);
            }
            return counter;
        }

        public int GetCount(ScopeType type) => Volatile.Read(ref _counts[(int)type]);
        public void Increment(ScopeType type) => Interlocked.Increment(ref _counts[(int)type]);
        public int Decrement(ScopeType type) => Interlocked.Decrement(ref _counts[(int)type]);

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ITerminalBackend, ScopeCounter> _table = new();
    }
}
