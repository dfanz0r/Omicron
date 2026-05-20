namespace Omicron.Core.Rendering.Layout;

/// <summary>A rectangular region in terminal cell coordinates.</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public readonly int Right => X + Width;
    public readonly int Bottom => Y + Height;

    /// <summary>Check if (x, y) is inside this rect.</summary>
    public bool Contains(int x, int y)
    {
        return x >= X && x < X + Width && y >= Y && y < Y + Height;
    }

    /// <summary>Intersect this rect with another.</summary>
    public Rect Intersect(Rect other)
    {
        return new Rect(Math.Max(X, other.X),
            Math.Max(Y, other.Y),
            Math.Max(0, Math.Min(Right, other.Right) - Math.Max(X, other.X)),
            Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y)));
    }
}
