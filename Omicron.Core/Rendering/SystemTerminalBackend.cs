using System.Buffers;

namespace Omicron.Core.Rendering;

/// <summary>
/// <para><b>Obsolete:</b> Use <see cref="TerminalBackendFactory.CreateSystemBackend"/> instead.</para>
/// <para>Compatibility wrapper around the OS-specific terminal backends.</para>
/// <para>
/// This class remains for backward compatibility but delegates all operations to the
/// platform-appropriate backend created by <see cref="TerminalBackendFactory"/>.
/// </para>
/// </summary>
[Obsolete("Use TerminalBackendFactory.CreateSystemBackend() instead.")]
public sealed class SystemTerminalBackend : ITerminalBackend
{
    private readonly ITerminalBackend _inner;

    public SystemTerminalBackend()
    {
        _inner = TerminalBackendFactory.CreateSystemBackend();
    }

    public TerminalSize Size => _inner.Size;

    public IBufferWriter<byte> Output => _inner.Output;

    public void Initialize() => _inner.Initialize();

    public IAsyncEnumerable<TerminalEvent> ReadEvents(CancellationToken cancellationToken)
        => _inner.ReadEvents(cancellationToken);

    public void Flush() => _inner.Flush();

    public void Dispose() => _inner.Dispose();

    /// <summary>
    /// Safety-net: restore the console input mode to a state compatible
    /// with <see cref="Console.ReadKey"/> and standard line reading.
    /// </summary>
    public static void EnsureSafeConsoleInputMode()
        => TerminalBackendFactory.EnsureSafeConsoleInputMode();

    /// <summary>
    /// Kitty protocol parser (delegated to <see cref="TerminalInputParser"/>).
    /// </summary>
    [Obsolete("Use TerminalInputParser.TryParseKittyKeySequence directly.")]
    internal static bool TryParseKittyKeySequence(byte[] buffer, ref int offset, int length, out TerminalEvent? terminalEvent)
        => TerminalInputParser.TryParseKittyKeySequence(buffer, ref offset, length, out terminalEvent);

    /// <summary>
    /// Text resolution helper (delegated to <see cref="TerminalInputParser"/>).
    /// </summary>
    [Obsolete("Use TerminalInputParser.ResolveText directly.")]
    internal static string? ResolveText(int keyCode, KeyModifiers modifiers, int? shiftedKey, string? textAsCodepoints)
        => TerminalInputParser.ResolveText(keyCode, modifiers, shiftedKey, textAsCodepoints);
}
