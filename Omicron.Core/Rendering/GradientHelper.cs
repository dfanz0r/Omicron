using System.Text;
using Omicron.Core.Rendering.Layout;
using Omicron.Core.Text;

namespace Omicron.Core.Rendering;

/// <summary>Direction of a linear gradient fill.</summary>
public enum GradientDirection
{
    Horizontal,
    Vertical,
}

/// <summary>A single stop in a multi-stop color gradient.</summary>
public readonly record struct ColorStop(double Position, byte R, byte G, byte B);

/// <summary>
/// Fills a rectangular region with a linear multi-stop gradient, or draws
/// text with a per-character foreground gradient while preserving existing
/// backgrounds. Works directly through <see cref="RenderContext"/> so
/// clipping is respected.
/// </summary>
public static class GradientHelper
{
    /// <summary>
    /// Fill <paramref name="bounds"/> with a linear gradient using an arbitrary
    /// number of color stops. Each cell inside the clipped region gets its own
    /// interpolated background colour.
    /// </summary>
    public static void Fill(
        RenderContext context,
        Rect bounds,
        GradientDirection direction,
        ReadOnlySpan<ColorStop> stops)
    {
        if (stops.Length == 0) return;

        var clipped = bounds.Intersect(context.Clip);
        if (clipped.Width <= 0 || clipped.Height <= 0) return;

        var cells = context.Frame.Cells;
        int frameWidth = context.Frame.Width;

        for (int row = clipped.Y; row < clipped.Bottom; row++)
        {
            int baseIdx = row * frameWidth + clipped.X;
            for (int col = clipped.X; col < clipped.Right; col++)
            {
                double t = direction == GradientDirection.Horizontal
                    ? (bounds.Width > 1 ? (col - bounds.X) / (double)(bounds.Width - 1) : 0.0)
                    : (bounds.Height > 1 ? (row - bounds.Y) / (double)(bounds.Height - 1) : 0.0);

                var (r, g, b) = Sample(stops, t);
                cells[baseIdx++] = new RenderCell
                {
                    Glyph = GlyphRef.Ascii((byte)' '),
                    Width = 1,
                    Style = new TextStyle(255, 255, 255, r, g, b, false, false, false),
                };
            }
        }
    }

    /// <summary>Convenience overload for a two-stop gradient.</summary>
    public static void Fill(
        RenderContext context,
        Rect bounds,
        GradientDirection direction,
        (byte R, byte G, byte B) start,
        (byte R, byte G, byte B) end)
    {
        Span<ColorStop> stops = stackalloc ColorStop[2];
        stops[0] = new ColorStop(0.0, start.R, start.G, start.B);
        stops[1] = new ColorStop(1.0, end.R, end.G, end.B);
        Fill(context, bounds, direction, stops);
    }

    /// <summary>
    /// Sample the gradient at a normalized position <paramref name="t"/> (0.0–1.0).
    /// Returns the interpolated RGB colour.
    /// </summary>
    public static (byte R, byte G, byte B) Sample(ReadOnlySpan<ColorStop> stops, double t)
    {
        if (stops.Length == 0) return (0, 0, 0);
        if (stops.Length == 1) return (stops[0].R, stops[0].G, stops[0].B);

        t = Math.Clamp(t, 0.0, 1.0);

        // Find the bracketing stops
        for (int i = 1; i < stops.Length; i++)
        {
            if (t <= stops[i].Position)
            {
                var prev = stops[i - 1];
                var next = stops[i];
                double range = next.Position - prev.Position;
                if (range <= 0) return (prev.R, prev.G, prev.B);
                double localT = (t - prev.Position) / range;
                return Lerp((prev.R, prev.G, prev.B), (next.R, next.G, next.B), localT);
            }
        }

        // Past the last stop
        var last = stops[stops.Length - 1];
        return (last.R, last.G, last.B);
    }

    /// <summary>
    /// Draw UTF-8 text at (<paramref name="x"/>, <paramref name="y"/>) with a
    /// per-character foreground gradient. Existing cell backgrounds are preserved.
    /// </summary>
    /// <param name="gradientDomain">
    /// The rectangle that defines the 0.0 → 1.0 gradient range.
    /// If null, the gradient domain defaults to the text's own bounding box
    /// (width = byte count, height = 1).
    /// </param>
    public static void DrawText(
        RenderContext context,
        int x, int y,
        ReadOnlySpan<byte> utf8,
        GradientDirection direction,
        ReadOnlySpan<ColorStop> stops,
        Rect? gradientDomain = null)
    {
        if (stops.Length == 0 || utf8.IsEmpty) return;
        if (y < context.Clip.Y || y >= context.Clip.Bottom) return;
        if (x >= context.Clip.Right) return;

        int maxWidth = context.Clip.Right - x;
        if (maxWidth <= 0) return;

        var domain = gradientDomain ?? new Rect(x, y, utf8.Length, 1);
        var cells = context.Frame.Cells;
        int frameWidth = context.Frame.Width;

        var clusters = GraphemeSegmenter.SegmentUtf8(utf8);
        int currentCol = x;

        foreach (var cluster in clusters)
        {
            if (currentCol >= context.Clip.Right) break;

            int w = CellWidthCalculator.GetWidth(
                utf8.Slice((int)cluster.ByteOffset, cluster.ByteLength));
            if (w == 0) continue;

            double t = direction == GradientDirection.Horizontal
                ? (domain.Width > 1 ? (currentCol - domain.X) / (double)(domain.Width - 1) : 0.0)
                : (domain.Height > 1 ? (y - domain.Y) / (double)(domain.Height - 1) : 0.0);

            var (r, g, b) = Sample(stops, t);
            int idx = y * frameWidth + currentCol;
            var existingBg = cells[idx].Style;

            // ASCII fast path
            if (cluster.ByteLength == 1 && utf8[(int)cluster.ByteOffset] < 0x80)
            {
                cells[idx] = new RenderCell
                {
                    Glyph = GlyphRef.Ascii(utf8[(int)cluster.ByteOffset]),
                    Width = (byte)Math.Min(w, 2),
                    Style = new TextStyle(r, g, b, existingBg.BgR, existingBg.BgG, existingBg.BgB, false, false, false),
                };
            }
            else
            {
                var glyphBytes = utf8.Slice((int)cluster.ByteOffset, cluster.ByteLength);
                int internId = context.Frame.GlyphTable.Intern(glyphBytes);
                cells[idx] = new RenderCell
                {
                    Glyph = GlyphRef.Interned(internId),
                    Width = (byte)Math.Min(w, 2),
                    Style = new TextStyle(r, g, b, existingBg.BgR, existingBg.BgG, existingBg.BgB, false, false, false),
                };
            }

            // Mark continuation cell for wide chars
            if (w == 2 && currentCol + 1 < frameWidth)
            {
                cells[idx + 1] = new RenderCell
                {
                    Glyph = GlyphRef.Ascii((byte)' '),
                    Width = 0,
                    Style = new TextStyle(r, g, b, existingBg.BgR, existingBg.BgG, existingBg.BgB, false, false, false),
                };
            }

            currentCol += w;
        }
    }

    /// <summary>
    /// Draw a string at (<paramref name="x"/>, <paramref name="y"/>) with a
    /// per-character foreground gradient. Existing cell backgrounds are preserved.
    /// </summary>
    public static void DrawText(
        RenderContext context,
        int x, int y,
        string text,
        GradientDirection direction,
        ReadOnlySpan<ColorStop> stops,
        Rect? gradientDomain = null)
    {
        DrawText(context, x, y, Encoding.UTF8.GetBytes(text), direction, stops, gradientDomain);
    }

    /// <summary>Convenience overload: two-stop foreground gradient on text.</summary>
    public static void DrawText(
        RenderContext context,
        int x, int y,
        string text,
        GradientDirection direction,
        (byte R, byte G, byte B) start,
        (byte R, byte G, byte B) end,
        Rect? gradientDomain = null)
    {
        Span<ColorStop> stops = stackalloc ColorStop[2];
        stops[0] = new ColorStop(0.0, start.R, start.G, start.B);
        stops[1] = new ColorStop(1.0, end.R, end.G, end.B);
        DrawText(context, x, y, text, direction, stops, gradientDomain);
    }

    /// <summary>
    /// Draw text with a single uniform foreground colour, preserving existing
    /// backgrounds. No gradient — just a constant colour across the whole string.
    /// </summary>
    public static void DrawText(
        RenderContext context,
        int x, int y,
        string text,
        (byte R, byte G, byte B) foreground)
    {
        Span<ColorStop> stops = stackalloc ColorStop[1];
        stops[0] = new ColorStop(0.0, foreground.R, foreground.G, foreground.B);
        DrawText(context, x, y, text, GradientDirection.Horizontal, stops, null);
    }

    private static (byte R, byte G, byte B) Lerp(
        (byte R, byte G, byte B) a,
        (byte R, byte G, byte B) b,
        double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return (
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
