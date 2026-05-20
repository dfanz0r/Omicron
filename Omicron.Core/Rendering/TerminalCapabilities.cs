namespace Omicron.Core.Rendering;

/// <summary>Color depth supported by the terminal.</summary>
public enum ColorLevel
{
    None,
    Color16,
    Color256,
    TrueColor
}

/// <summary>
///     Detected terminal capabilities. Used to decide which ANSI features to use.
/// </summary>
public sealed record TerminalCapabilities(
    bool AnsiEnabled,
    ColorLevel ColorLevel,
    bool SupportsAlternateScreen,
    bool SupportsCursorVisibility,
    bool SupportsMouse,
    bool SupportsBracketedPaste,
    bool SupportsSynchronizedOutput,
    bool SupportsWindowSize,
    string TerminalName)
{
    /// <summary>
    ///     Detect capabilities from the environment.
    /// </summary>
    public static TerminalCapabilities Detect()
    {
        string terminalName = GetTerminalName();
        ColorLevel colorLevel = DetectColorLevel(terminalName);
        bool ansiEnabled = colorLevel != ColorLevel.None;

        return new TerminalCapabilities(ansiEnabled,
            colorLevel,
            ansiEnabled,
            ansiEnabled,
            ansiEnabled,
            ansiEnabled,
            DetectSynchronizedOutput(terminalName),
            ansiEnabled,
            terminalName);
    }

    private static string GetTerminalName()
    {
        string? termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM");
        if (!string.IsNullOrEmpty(termProgram))
        {
            return termProgram;
        }

        string? term = Environment.GetEnvironmentVariable("TERM");
        if (!string.IsNullOrEmpty(term))
        {
            return term;
        }

        string? wtSession = Environment.GetEnvironmentVariable("WT_SESSION");
        if (!string.IsNullOrEmpty(wtSession))
        {
            return "WindowsTerminal";
        }

        return "unknown";
    }

    private static ColorLevel DetectColorLevel(string terminalName)
    {
        string? colorterm = Environment.GetEnvironmentVariable("COLORTERM");
        if (colorterm == "truecolor" || colorterm == "24bit")
        {
            return ColorLevel.TrueColor;
        }

        if (
            terminalName.Contains("WindowsTerminal", StringComparison.OrdinalIgnoreCase)
            || terminalName.Contains("vscode", StringComparison.OrdinalIgnoreCase)
            || terminalName.Contains("iTerm", StringComparison.OrdinalIgnoreCase)
            || terminalName.Contains("kitty", StringComparison.OrdinalIgnoreCase)
            || terminalName.Contains("wezterm", StringComparison.OrdinalIgnoreCase)
            || terminalName.Contains("ghostty", StringComparison.OrdinalIgnoreCase)
            || terminalName.Contains("foot", StringComparison.OrdinalIgnoreCase)
        )
        {
            return ColorLevel.TrueColor;
        }

        string? term = Environment.GetEnvironmentVariable("TERM");
        if (term?.Contains("256color", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ColorLevel.Color256;
        }

        return ColorLevel.Color16;
    }

    private static bool DetectSynchronizedOutput(string terminalName)
    {
        // Windows Terminal ≥1.22, iTerm2 ≥3.5, WezTerm, Kitty, foot, Ghostty, VSCode
        if (
            terminalName.Contains("WindowsTerminal", StringComparison.OrdinalIgnoreCase)
            || terminalName.Contains("vscode", StringComparison.OrdinalIgnoreCase)
            || terminalName.Contains("iTerm", StringComparison.OrdinalIgnoreCase)
            || terminalName.Contains("kitty", StringComparison.OrdinalIgnoreCase)
            || terminalName.Contains("wezterm", StringComparison.OrdinalIgnoreCase)
            || terminalName.Contains("ghostty", StringComparison.OrdinalIgnoreCase)
            || terminalName.Contains("foot", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        return false;
    }
}
