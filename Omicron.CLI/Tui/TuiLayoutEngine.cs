using Omicron.Core.Rendering;
using Omicron.Core.Rendering.Layout;

namespace Omicron.CLI.Tui;

/// <summary>
/// Minimal three-panel layout calculator for the TUI shell.
/// Layout:
///   ┌──────────────────────────────┐
///   │ transcript viewport (flex)   │  height = total - statusHeight - inputHeight
///   ├──────────────────────────────┤
///   │ status bar (1 row)           │
///   ├──────────────────────────────┤
///   │ input line (1 row)           │
///   └──────────────────────────────┘
/// </summary>
public static class TuiLayoutEngine
{
    public const int StatusBarHeight = 1;
    public const int InputLineHeight = 1;

    /// <summary>Compute the three panel rectangles from the terminal size.</summary>
    public static (Rect Transcript, Rect StatusBar, Rect InputLine) ComputeLayout(TerminalSize terminalSize)
    {
        int totalHeight = terminalSize.Height;
        int transcriptHeight = totalHeight - StatusBarHeight - InputLineHeight;

        if (transcriptHeight < 1) transcriptHeight = 1;

        var transcript = new Rect(0, 0, terminalSize.Width, transcriptHeight);
        var statusBar = new Rect(0, transcriptHeight, terminalSize.Width, StatusBarHeight);
        var inputLine = new Rect(0, transcriptHeight + StatusBarHeight, terminalSize.Width, InputLineHeight);

        return (transcript, statusBar, inputLine);
    }
}
