using System.Buffers;

namespace Omicron.Core.Rendering;

/// <summary>
/// Abstracts the host terminal so rendering and input can be done without
/// depending directly on <c>System.Console</c>.
/// </summary>
public interface ITerminalBackend : IDisposable
{
    /// <summary>Current terminal size (columns × rows).</summary>
    TerminalSize Size { get; }

    /// <summary>
    /// Read terminal events as an async stream. Blocks when no input is available.
    /// Yields <see cref="KeyEvent"/>, <see cref="MouseEvent"/>, and <see cref="ResizeEvent"/>.
    /// </summary>
    IAsyncEnumerable<TerminalEvent> ReadEvents(CancellationToken cancellationToken);

    /// <summary>
    /// An <see cref="IBufferWriter{T}"/> that the renderer writes raw UTF-8/ANSI bytes to.
    /// </summary>
    IBufferWriter<byte> Output { get; }

    /// <summary>Flush buffered output to the terminal (e.g., flush stdout).</summary>
    void Flush();
}
