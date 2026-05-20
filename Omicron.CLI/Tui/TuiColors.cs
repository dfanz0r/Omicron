namespace Omicron.CLI.Tui;

/// <summary>
///     Shared color configuration for TUI widgets.
///     Centralizes gradient colors so the status bar, divider, and other
///     widgets stay in sync without hardcoded duplication.
/// </summary>
public static class TuiColors
{
    // ── Status bar colors ──
    //
    // The background must be light enough that pure-black text has >= 4.5:1
    // WCAG contrast ratio. VS Code's integrated terminal (xterm.js) auto-
    // adjusts text to white when contrast falls below its threshold. The
    // original (160,100,20) bg + (40,30,20) text gave only ~3.8:1, which
    // triggered the adjustment.  Pure black on (170,110,25) gives ~5.5:1,
    // safely above the threshold and rendering identically in both VS Code
    // and Windows Terminal.
    //
    // Contrast math (WCAG relative luminance):
    //   bg(170,110,25)  L ≈ 0.224
    //   text(0,0,0)     L = 0
    //   CR = (0.224+0.05)/(0+0.05) = 5.48:1  ✓

    /// <summary>Status bar background start (left side).</summary>
    public static (byte R, byte G, byte B) StatusBarGradientStart { get; set; } = (170, 110, 25);

    /// <summary>Status bar background end (right side).</summary>
    public static (byte R, byte G, byte B) StatusBarGradientEnd { get; set; } = (245, 170, 30);

    /// <summary>
    ///     Status bar text color. Pure black for maximum contrast.
    ///     Do not use dark brown — it reduces contrast below VS Code's auto-
    ///     adjustment threshold and renders as white in VS Code.
    /// </summary>
    public static (byte R, byte G, byte B) StatusBarTextStart { get; set; } = (0, 0, 0);

    /// <summary>Status bar text gradient end. Equal to Start for flat black.</summary>
    public static (byte R, byte G, byte B) StatusBarTextEnd { get; set; } = (0, 0, 0);

    // ── Scrollbar colors ──

    /// <summary>Scrollbar track background (dark charcoal).</summary>
    public static (byte R, byte G, byte B) ScrollbarTrackBg { get; set; } = (50, 45, 38);

    /// <summary>Scrollbar thumb background (warm amber).</summary>
    public static (byte R, byte G, byte B) ScrollbarThumbBg { get; set; } = (140, 115, 60);
}
