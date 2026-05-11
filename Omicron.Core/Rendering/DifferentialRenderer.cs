using System.Buffers;

namespace Omicron.Core.Rendering;

/// <summary>
/// Renders <see cref="TerminalFrame"/> instances through a <see cref="SwapChain"/>
/// double-buffer, emitting minimal ANSI updates to an <see cref="IBufferWriter{T}"/>.
/// </summary>
public sealed class DifferentialRenderer
{
    private readonly SwapChain _swapChain;
    private bool _firstRender = true;
    private bool _fullRedrawQueued;

    /// <summary>The swap chain backing this renderer.</summary>
    public SwapChain SwapChain => _swapChain;

    public DifferentialRenderer(int width, int height)
    {
        _swapChain = new SwapChain(width, height);
    }

    /// <summary>
    /// Render the next frame: copies the source into the back buffer,
    /// diffs against the front buffer, and emits ANSI updates.
    /// </summary>
    public void Render(TerminalFrame source, IBufferWriter<byte> output)
    {
        // Copy source cells into the back buffer.
        // We do NOT copy the glyph table — instead we pass the source glyph
        // table directly to Diff/EmitFullFrame, avoiding an O(n) hash-map
        // rebuild on every single render.
        var current = _swapChain.Current;
        Array.Copy(source.Cells, current.Cells, source.Cells.Length);

        if (_firstRender || _fullRedrawQueued)
        {
            // Full frame redraw: first render emits clear+hide, scroll redraws skip it.
            if (_firstRender)
            {
                AnsiEncoder.ClearScreen(output);
                AnsiEncoder.HideCursor(output);
            }

            // Wrap full frame in synchronized output (DEC 2026)
            AnsiEncoder.BeginSynchronizedOutput(output);
            EmitFullFrame(current, output, source.GlyphTable);
            AnsiEncoder.EndSynchronizedOutput(output);

            _firstRender = false;
            _fullRedrawQueued = false;
        }
        else
        {
            // Diff against previous, using synchronized output (DEC 2026)
            _swapChain.Diff(output, useSynchronizedOutput: true, source.GlyphTable);
        }

        // Swap buffers
        _swapChain.Swap();
    }

    /// <summary>Resize the rendering buffers.</summary>
    public void Resize(int width, int height)
    {
        _swapChain.Resize(width, height);
        _firstRender = true; // Force full redraw on resize
    }

    /// <summary>
    /// Request a full frame redraw on the next render (without clearing the screen).
    /// Call this when the viewport scrolls to avoid the row-by-row flicker
    /// that the diff renderer produces during large viewport shifts.
    /// The full redraw wraps the entire frame in synchronized output so the
    /// terminal renders all rows atomically.
    /// </summary>
    public void RequestFullRedraw()
    {
        _fullRedrawQueued = true;
    }

    private static void EmitFullFrame(TerminalFrame frame, IBufferWriter<byte> output, GlyphInternTable? glyphTable = null)
    {
        int width = frame.Width;
        int height = frame.Height;
        var glyphs = glyphTable ?? frame.GlyphTable;

        for (int row = 0; row < height; row++)
        {
            // Find the rightmost cell that needs rendering (non-empty or styled)
            int lastCol = -1;
            for (int c = width - 1; c >= 0; c--)
            {
                var cell = frame.Cells[row * width + c];
                if (!cell.IsEmpty || cell.Style != TextStyle.Default)
                {
                    lastCol = c;
                    break;
                }
            }

            if (lastCol < 0)
                continue; // Entirely empty row — skip

            // Write the entire row so trailing empty cells get the explicit
            // default background. Otherwise cells beyond lastCol retain the
            // terminal's default background from ESC[2J, creating a visible
            // mismatch when diff later clears text cells to explicit black.
            lastCol = width - 1;

            AnsiEncoder.SetCursorPosition(row, 0, output);
            AnsiEncoder.ResetStyle(output); // ESC[2J does not reset SGR
            TextStyle? currentStyle = null;

            ReadOnlySpan<byte> spaceSpan = " "u8;

            for (int col = 0; col <= lastCol; col++)
            {
                var cell = frame.Cells[row * width + col];

                if (cell.Width == 0) // Continuation cell — skip
                    continue;

                if (cell.Style != currentStyle)
                {
                    AnsiEncoder.SetStyle(cell.Style, currentStyle, output);
                    currentStyle = cell.Style;
                }

                if (cell.IsEmpty)
                {
                    // Write explicit space so the cursor advances over gaps
                    output.Write(spaceSpan);
                }
                else
                {
                    AnsiEncoder.WriteGlyph(cell.Glyph, output, glyphs);
                }
            }

            // Restore default style at end of row
            if (currentStyle != TextStyle.Default)
            {
                AnsiEncoder.ResetStyle(output);
                currentStyle = TextStyle.Default;
            }
        }

        // Move cursor home after full render
        AnsiEncoder.SetCursorPosition(0, 0, output);
    }
}
