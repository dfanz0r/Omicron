using System.Runtime.InteropServices;

namespace Omicron.Core.Rendering;

/// <summary>
///     Creates the appropriate <see cref="ITerminalBackend" /> for the current OS.
/// </summary>
public static class TerminalBackendFactory
{
    /// <summary>
    ///     Create the system-appropriate terminal backend.
    /// </summary>
    public static ITerminalBackend CreateSystemBackend()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsTerminalBackend();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsTerminalBackend();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxTerminalBackend();
        }

        // Fallback: attempt to detect via runtime information
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsTerminalBackend();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacOsTerminalBackend();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxTerminalBackend();
        }

        throw new PlatformNotSupportedException("No terminal backend available for the current OS.");
    }

    /// <summary>
    ///     Static safety-net: restore the console input mode to a state compatible
    ///     with <see cref="Console.ReadKey" /> and standard line reading.
    ///     Call this before entering standard CLI mode if the TUI may have left
    ///     the terminal in raw / VT-input mode.
    /// </summary>
    public static void EnsureSafeConsoleInputMode()
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsTerminalBackend.EnsureSafeConsoleInputMode();
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacOsTerminalBackend.EnsureSafeConsoleInputMode();
        }
        else if (OperatingSystem.IsLinux())
        {
            LinuxTerminalBackend.EnsureSafeConsoleInputMode();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WindowsTerminalBackend.EnsureSafeConsoleInputMode();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            MacOsTerminalBackend.EnsureSafeConsoleInputMode();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            LinuxTerminalBackend.EnsureSafeConsoleInputMode();
        }
    }
}
