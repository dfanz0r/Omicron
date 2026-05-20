using System.Buffers;

namespace Omicron.Core.Rendering;

/// <summary>
///     Abstracts the host terminal so rendering and input can be done without
///     depending directly on <c>System.Console</c>.
/// </summary>
public interface ITerminalBackend : IDisposable
{
    /// <summary>Current terminal size (columns × rows).</summary>
    TerminalSize Size { get; }

    /// <summary>
    ///     An <see cref="IBufferWriter{T}" /> that the renderer writes raw UTF-8/ANSI bytes to.
    /// </summary>
    IBufferWriter<byte> Output { get; }

    /// <summary>
    ///     Initialize platform-specific terminal state (raw mode, VT processing, etc.).
    ///     Must be called before entering TUI mode. Safe to call multiple times (idempotent).
    /// </summary>
    void Initialize();

    /// <summary>
    ///     Read terminal events as an async stream. Blocks when no input is available.
    ///     Yields <see cref="KeyEvent" />, <see cref="MouseEvent" />, and <see cref="ResizeEvent" />.
    /// </summary>
    IAsyncEnumerable<TerminalEvent> ReadEvents(CancellationToken cancellationToken);

    /// <summary>Flush buffered output to the terminal (e.g., flush stdout).</summary>
    void Flush();
}
