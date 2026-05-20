using Omicron.Core.Text;

namespace Omicron.Core.Rendering;

/// <summary>
///     A terminal frame buffer: a rectangular grid of <see cref="RenderCell" /> values.
///     Rows × Width cells, stored in a flat array for cache locality.
/// </summary>
public sealed class TerminalFrame
{
    public TerminalFrame(int width, int height)
    {
        Width = width;
        Height = height;
        Cells = new RenderCell[width * height];
        Clear();
    }

    /// <summary>Frame width in columns.</summary>
    public int Width { get; private set; }

    /// <summary>Frame height in rows.</summary>
    public int Height { get; private set; }

    /// <summary>Flat cell array (row-major: cells[row * Width + col]).</summary>
    public RenderCell[] Cells { get; private set; }

    /// <summary>The intern table for non-ASCII glyphs in this frame.</summary>
    public GlyphInternTable GlyphTable { get; } = new();

    /// <summary>Indexer into the cell array by row and column.</summary>
    public ref RenderCell this[int row, int col] => ref Cells[row * Width + col];

    /// <summary>Fill every cell with the default empty cell.</summary>
    public void Clear()
    {
        RenderCell empty = RenderCell.Empty;
        for (int i = 0; i < Cells.Length; i++)
        {
            Cells[i] = empty;
        }

        GlyphTable.Reset();
    }

    /// <summary>
    ///     Render a UTF-8 string into the frame starting at (row, col).
    ///     Handles grapheme clusters, wide characters, and truncation at the right edge.
    /// </summary>
    public void SetText(int row, int col, ReadOnlySpan<byte> utf8, TextStyle style)
    {
        if (row < 0 || row >= Height || col < 0 || col >= Width)
        {
            return;
        }

        int currentCol = col;

        List<GraphemeCluster> clusters = GraphemeSegmenter.SegmentUtf8(utf8);

        foreach (GraphemeCluster cluster in clusters)
        {
            if (currentCol >= Width)
            {
                break; // Truncate
            }

            int width = CellWidthCalculator.GetWidth(utf8.Slice((int)cluster.ByteOffset, cluster.ByteLength));

            if (width == 0)
            {
                continue; // Combining mark or zero-width — skip
            }

            // Get the GlyphRef
            GlyphRef glyph;
            int clusterOffset = (int)cluster.ByteOffset;
            if (cluster.ByteLength == 1 && utf8[clusterOffset] < 0x80)
            {
                // ASCII fast path
                glyph = GlyphRef.Ascii(utf8[clusterOffset]);
            }
            else
            {
                // Non-ASCII: intern
                ReadOnlySpan<byte> clusterBytes = utf8.Slice(clusterOffset, cluster.ByteLength);
                int internId = GlyphTable.Intern(clusterBytes);
                glyph = GlyphRef.Interned(internId);
            }

            // Write the glyph into the cell
            Cells[row * Width + currentCol] = new RenderCell
            {
                Glyph = glyph,
                Width = (byte)Math.Min(width, 2),
                Style = style
            };

            currentCol++;

            // For wide characters (width 2), mark the next cell as a continuation
            if (width == 2 && currentCol < Width)
            {
                Cells[row * Width + currentCol] = new RenderCell
                {
                    Glyph = GlyphRef.Ascii((byte)' '),
                    Width = 0, // Continuation marker
                    Style = style
                };
                currentCol++;
            }
        }
    }

    /// <summary>
    ///     Fill a rectangular region with a single cell value.
    /// </summary>
    public void FillRect(int row, int col, int w, int h, RenderCell cell)
    {
        int endRow = Math.Min(row + h, Height);
        int endCol = Math.Min(col + w, Width);

        for (int r = row; r < endRow; r++)
        {
            for (int c = col; c < endCol; c++)
            {
                Cells[r * Width + c] = cell;
            }
        }
    }

    /// <summary>Resize the frame to new dimensions (preserves content within bounds).</summary>
    public void Resize(int newWidth, int newHeight)
    {
        if (newWidth == Width && newHeight == Height)
        {
            return;
        }

        var newCells = new RenderCell[newWidth * newHeight];
        RenderCell empty = RenderCell.Empty;
        for (int i = 0; i < newCells.Length; i++)
        {
            newCells[i] = empty;
        }

        // Copy old content that fits in new bounds
        int copyRows = Math.Min(Height, newHeight);
        int copyCols = Math.Min(Width, newWidth);

        for (int r = 0; r < copyRows; r++)
        {
            int oldRowStart = r * Width;
            int newRowStart = r * newWidth;
            Array.Copy(Cells, oldRowStart, newCells, newRowStart, copyCols);
        }

        Cells = newCells;
        Width = newWidth;
        Height = newHeight;
    }
}
