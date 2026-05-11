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
        // Copy source into the current (back) buffer, including the glyph table
        var current = _swapChain.Current;
        Array.Copy(source.Cells, current.Cells, source.Cells.Length);
        current.GlyphTable.CopyFrom(source.GlyphTable);

        if (_firstRender)
        {
            // First render: clear screen and emit full frame
            AnsiEncoder.ClearScreen(output);
            AnsiEncoder.HideCursor(output);

            // Emit all non-empty cells
            EmitFullFrame(current, output);

            _firstRender = false;
        }
        else
        {
            // Diff against previous
            _swapChain.Diff(output, useSynchronizedOutput: false);
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

    private static void EmitFullFrame(TerminalFrame frame, IBufferWriter<byte> output)
    {
        TextStyle currentStyle = TextStyle.Default;
        int width = frame.Width;
        int height = frame.Height;

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

            AnsiEncoder.SetCursorPosition(row, 0, output);
            currentStyle = TextStyle.Default;

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
                    AnsiEncoder.WriteGlyph(cell.Glyph, output, frame.GlyphTable);
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
