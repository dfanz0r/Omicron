using System.Runtime.CompilerServices;

namespace Omicron.Core.Collections;

/// <summary>
///     Internal helpers for the lock-free SPSC ring buffer.
///     Provides power-of-two validation and cache-line-aware index shuffling
///     to reduce false sharing between producer and consumer threads.
/// </summary>
internal static class AtomicQueueDetails
{
    public const int CacheLineSize = 64;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPowerOfTwo(uint value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetCacheLineIndexBits(int elementsPerCacheLine)
    {
        return elementsPerCacheLine switch
        {
            256 => 8,
            128 => 7,
            64 => 6,
            32 => 5,
            16 => 4,
            8 => 3,
            4 => 2,
            2 => 1,
            _ => 0
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetIndexShuffleBits(int size, int elementSize, bool minimizeContention)
    {
        if (!minimizeContention || size <= 0 || elementSize <= 0 || !IsPowerOfTwo((uint)size))
        {
            return 0;
        }

        int elementsPerCacheLine = CacheLineSize / elementSize;
        int bits = GetCacheLineIndexBits(elementsPerCacheLine);
        if (bits == 0)
        {
            return 0;
        }

        int minSize = 1 << (bits * 2);
        return size < minSize ? 0 : bits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint RemapAnd(uint index, uint size, int bits)
    {
        if (bits == 0)
        {
            return size == 0 ? index : index % size;
        }

        uint maskElemIdx = (1U << bits) - 1;
        uint maskHi = ~((1U << (bits * 2)) - 1);

        uint remapped =
            ((index >> bits) & maskElemIdx)
            | ((index & maskElemIdx) << bits)
            | (index & maskHi & (size - 1));

        return remapped;
    }
}
