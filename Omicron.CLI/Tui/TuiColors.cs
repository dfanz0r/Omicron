namespace Omicron.CLI.Tui;

/// <summary>
/// Shared color configuration for TUI widgets.
/// Centralizes gradient colors so the status bar, divider, and other
/// widgets stay in sync without hardcoded duplication.
/// </summary>
public static class TuiColors
{
    /// <summary>Status bar gradient start (left side).</summary>
    public static (byte R, byte G, byte B) StatusBarGradientStart { get; set; } = (160, 100, 20);

    /// <summary>Status bar gradient end (right side).</summary>
    public static (byte R, byte G, byte B) StatusBarGradientEnd { get; set; } = (245, 165, 20);

    /// <summary>Status bar text gradient start.</summary>
    public static (byte R, byte G, byte B) StatusBarTextStart { get; set; } = (60, 50, 30);

    /// <summary>Status bar text gradient end.</summary>
    public static (byte R, byte G, byte B) StatusBarTextEnd { get; set; } = (100, 80, 50);
}
