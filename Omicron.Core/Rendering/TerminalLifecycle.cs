using System.Runtime.ExceptionServices;

namespace Omicron.Core.Rendering;

/// <summary>
/// Ensures terminal state (cursor visible, alternate screen exited, raw mode restored)
/// is restored on crash, Ctrl+C, or normal exit. Hooks <see cref="AppDomain.ProcessExit"/>
/// and <see cref="Console.CancelKeyPress"/>.
///
/// Singleton — one instance per process.
/// Tracks the most recent backend for emergency recovery.
/// </summary>
public sealed class TerminalLifecycle : IDisposable
{
    private static readonly object _lock = new();
    private static TerminalLifecycle? _instance;
    private bool _disposed;

    /// <summary>Track the most recent backend for crash recovery.</summary>
    private static ITerminalBackend? _lastBackend;
    private static readonly HashSet<TerminalScope.ScopeType> _activeScopeTypes = [];

    /// <summary>Lock for scope tracking.</summary>
    private static readonly object _trackLock = new();

    private TerminalLifecycle()
    {
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    /// <summary>Initialize the global terminal lifecycle (idempotent).</summary>
    public static TerminalLifecycle Initialize()
    {
        if (_instance is null)
        {
            lock (_lock)
            {
                _instance ??= new TerminalLifecycle();
            }
        }
        return _instance;
    }

    /// <summary>Register that a scope type is active for the given backend.</summary>
    internal static void Track(ITerminalBackend backend, TerminalScope.ScopeType type)
    {
        lock (_trackLock)
        {
            _lastBackend = backend;
            _activeScopeTypes.Add(type);
        }
    }

    /// <summary>Remove a scope type from the tracking set.</summary>
    internal static void Untrack(ITerminalBackend backend, TerminalScope.ScopeType type)
    {
        lock (_trackLock)
        {
            _activeScopeTypes.Remove(type);
        }
    }

    /// <summary>
    /// Force-restore all tracked backends to a safe terminal state.
    /// Called on crash, Ctrl+C, or process exit.
    /// </summary>
    public static void EmergencyRestore()
    {
        lock (_trackLock)
        {
            if (_lastBackend is null)
                return;

            foreach (var type in _activeScopeTypes)
            {
                string? exitSequence = type switch
                {
                    TerminalScope.ScopeType.AlternateScreen => "\x1b[?1049l",
                    TerminalScope.ScopeType.HideCursor => "\x1b[?25h",
                    TerminalScope.ScopeType.BracketedPaste => "\x1b[?2004l",
                    TerminalScope.ScopeType.Mouse => "\x1b[?1006l\x1b[?1002l\x1b[?1000l",
                    _ => null
                };

                if (exitSequence is not null)
                {
                    try
                    {
                        var bytes = System.Text.Encoding.UTF8.GetBytes(exitSequence);
                        _lastBackend.Output.Write(bytes);
                        _lastBackend.Flush();
                    }
                    catch
                    {
                        // Best-effort during crash recovery
                    }
                }
            }
        }

        // Also restore platform console modes so standard Console.ReadKey works
        // after a crash/kill that bypassed Dispose().
        try
        {
            SystemTerminalBackend.EnsureSafeConsoleInputMode();
        }
        catch
        {
            // Best-effort
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        EmergencyRestore();
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        EmergencyRestore();
        // Don't set e.Cancel = true — allow the process to terminate
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        Console.CancelKeyPress -= OnCancelKeyPress;

        EmergencyRestore();

        lock (_lock)
        {
            _instance = null;
        }
    }
}
