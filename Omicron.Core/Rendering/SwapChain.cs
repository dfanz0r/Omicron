using System.Buffers;

namespace Omicron.Core.Rendering;

/// <summary>
/// Double-buffering swap chain for the terminal frame buffer.
/// <see cref="Current"/> is the back buffer (being drawn to),
/// <see cref="Previous"/> is the front buffer (last rendered).
/// <see cref="Swap"/> exchanges them without copying cells.
/// </summary>
public sealed class SwapChain
{
    private TerminalFrame _frameA;
    private TerminalFrame _frameB;
    private bool _useA;

    /// <summary>The back buffer (being drawn to).</summary>
    public TerminalFrame Current => _useA ? _frameA : _frameB;

    /// <summary>The front buffer (last rendered).</summary>
    public TerminalFrame Previous => _useA ? _frameB : _frameA;

    public SwapChain(int width, int height)
    {
        _frameA = new TerminalFrame(width, height);
        _frameB = new TerminalFrame(width, height);
    }

    /// <summary>
    /// Swap buffers: the current back buffer becomes the new front buffer.
    /// The old front buffer is cleared and becomes the new back buffer.
    /// </summary>
    public void Swap()
    {
        _useA = !_useA;
        // Clear the new back buffer (the one we will draw into next frame).
        Current.Clear();
    }

    /// <summary>Resize both buffers.</summary>
    public void Resize(int width, int height)
    {
        _frameA.Resize(width, height);
        _frameB.Resize(width, height);
    }

    /// <summary>
    /// Compute the diff between <see cref="Previous"/> and <see cref="Current"/>
    /// and emit the minimal ANSI sequences to update the terminal.
    ///
    /// If <paramref name="useSynchronizedOutput"/> is true, wraps the diff
    /// in DEC 2026 synchronization sequences.
    /// </summary>
    public void Diff(IBufferWriter<byte> output, bool useSynchronizedOutput = false, GlyphInternTable? glyphTable = null)
    {
        var prev = Previous;
        var cur = Current;
        var glyphs = glyphTable ?? cur.GlyphTable;

        int width = cur.Width;
        int height = cur.Height;
        bool syncStarted = false;

        for (int row = 0; row < height; row++)
        {
            // Find the first and last changed cell in this row
            int firstChanged = -1;
            int lastChanged = -1;

            for (int col = 0; col < width; col++)
            {
                var curCell = cur.Cells[row * width + col];
                var prevCell = prev.Cells[row * width + col];

                if (curCell.Glyph != prevCell.Glyph ||
                    curCell.Width != prevCell.Width ||
                    curCell.Style != prevCell.Style)
                {
                    if (firstChanged == -1)
                        firstChanged = col;
                    lastChanged = col;
                }
            }

            if (firstChanged == -1)
                continue; // Entire row unchanged

            // Skip trailing empty cells
            while (lastChanged >= 0)
            {
                var cell = cur.Cells[row * width + lastChanged];
                if (!cell.IsEmpty || cell.Width > 0)
                    break;
                lastChanged--;
            }

            if (lastChanged < firstChanged)
                continue;

            // Start synchronized output on the first row that actually changes
            if (useSynchronizedOutput && !syncStarted)
            {
                AnsiEncoder.BeginSynchronizedOutput(output);
                syncStarted = true;
            }

            // Move cursor to start of changed region
            AnsiEncoder.SetCursorPosition(row, firstChanged, output);

            // Emit the changed run.
            // currentStyle is nullable so the FIRST cell always emits its full
            // style. If we initialized to TextStyle.Default and the first cell
            // also happened to be Default, no style would be emitted and the
            // terminal's previous active style would bleed through.
            TextStyle? currentStyle = null;
            ReadOnlySpan<byte> spaceSpan = " "u8;

            for (int col = firstChanged; col <= lastChanged; col++)
            {
                var cell = cur.Cells[row * width + col];

                // Skip continuation cells (width 0) — they're rendered as part of the wide char
                if (cell.Width == 0)
                    continue;

                // Emit style change if needed
                if (cell.Style != currentStyle)
                {
                    AnsiEncoder.SetStyle(cell.Style, currentStyle, output);
                    currentStyle = cell.Style;
                }

                // Write a space for empty cells so they clear old glyphs on screen
                if (cell.IsEmpty)
                {
                    output.Write(spaceSpan);
                }
                else
                {
                    // Write the glyph
                    AnsiEncoder.WriteGlyph(cell.Glyph, output, glyphs);
                }
            }

            // Restore default style at end of run if we changed it
            if (currentStyle != TextStyle.Default)
            {
                AnsiEncoder.ResetStyle(output);
            }
        }

        if (syncStarted)
            AnsiEncoder.EndSynchronizedOutput(output);
    }
}
