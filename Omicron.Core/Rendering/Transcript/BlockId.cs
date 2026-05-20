namespace Omicron.Core.Rendering.Transcript;

/// <summary>
///     Unique identifier for a transcript block.
///     Generated sequentially starting from 1.
/// </summary>
public readonly record struct BlockId(long Value)
{
    private static long _next;

    /// <summary>Block ID constant for "no block" (value 0).</summary>
    public static BlockId None => default;

    /// <summary>Create a new unique block ID.</summary>
    public static BlockId New()
    {
        return new BlockId(Interlocked.Increment(ref _next));
    }
}
