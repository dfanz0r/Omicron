using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Omicron.Core.Collections;

// Must be a top-level struct — generic types cannot have explicit layout.
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct CacheLineUInt32
{
    [FieldOffset(0)] public uint Value;
}

/// <summary>
///     Single-producer single-consumer (SPSC) lock-free ring buffer.
///     One thread calls <see cref="TryPush" /> / <see cref="PushAsync" /> (producer).
///     A different thread calls <see cref="TryPop" /> / <see cref="PopAsync" /> (consumer).
///     Mixing producers or consumers on the same side is undefined behaviour.
///     Index shuffling spreads concurrent access across cache lines to minimise
///     false sharing on CPUs with aggressive prefetchers.
/// </summary>
public sealed class QueueSPSC<T>
{
    private readonly T[] mQueueBuffer;
    private readonly int mShuffleBits;
    private CacheLineUInt32 mCachedHead;
    private CacheLineUInt32 mCachedTail;

    // TaskCompletionSources for async yielding
    private TaskCompletionSource<bool>? mConsumerTcs;

    // Async waiting states (0 = awake, 1 = waiting)
    private int mConsumerWaiting;
    private CacheLineUInt32 mHead;
    private TaskCompletionSource<bool>? mProducerTcs;
    private int mProducerWaiting;
    private CacheLineUInt32 mTail;

    public QueueSPSC(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        if (!AtomicQueueDetails.IsPowerOfTwo((uint)capacity))
        {
            throw new ArgumentException("Capacity must be a power of two.", nameof(capacity));
        }

        Capacity = capacity;
        mQueueBuffer = new T[capacity];
        mShuffleBits = AtomicQueueDetails.GetIndexShuffleBits(capacity, Unsafe.SizeOf<T>(), true);
    }

    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint RemapIndex(uint value)
    {
        return AtomicQueueDetails.RemapAnd(value, (uint)Capacity, mShuffleBits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPush(T item)
    {
        // .Relaxed equivalent (plain read is atomic for 32-bit types)
        uint currentHead = mHead.Value;
        uint cachedTail = mCachedTail.Value;

        if ((int)(currentHead - cachedTail) >= Capacity)
        {
            // .Acquire equivalent
            cachedTail = Volatile.Read(ref mTail.Value);
            mCachedTail.Value = cachedTail;

            if ((int)(currentHead - cachedTail) >= Capacity)
            {
                return false;
            }
        }

        mQueueBuffer[(int)RemapIndex(currentHead)] = item;

        // .Release equivalent
        Volatile.Write(ref mHead.Value, currentHead + 1);

        // MEMORY BARRIER: Prevent CPU StoreLoad reordering. We must ensure the new
        // mHead is globally visible before we check if the consumer is asleep.
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref mConsumerWaiting) == 1)
        {
            WakeConsumer();
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPop(out T item)
    {
        // .Relaxed equivalent
        uint currentTail = mTail.Value;
        uint cachedHead = mCachedHead.Value;

        if (currentTail == cachedHead)
        {
            // .Acquire equivalent
            cachedHead = Volatile.Read(ref mHead.Value);
            mCachedHead.Value = cachedHead;

            if (currentTail == cachedHead)
            {
                item = default!;
                return false;
            }
        }

        int index = (int)RemapIndex(currentTail);
        item = mQueueBuffer[index];

        // .NET GC Fix: clear reference so the array doesn't hold memory leaks
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            mQueueBuffer[index] = default!;
        }

        // .Release equivalent
        Volatile.Write(ref mTail.Value, currentTail + 1);

        // MEMORY BARRIER: Prevent CPU StoreLoad reordering.
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref mProducerWaiting) == 1)
        {
            WakeProducer();
        }

        return true;
    }

    // --- SYNCHRONOUS PRIMITIVES ---

    public void Push(T item)
    {
        var spinner = new SpinWait();
        while (!TryPush(item))
        {
            spinner.SpinOnce();
        }
    }

    public T Pop()
    {
        T item = default!;
        var spinner = new SpinWait();
        while (!TryPop(out item))
        {
            spinner.SpinOnce();
        }

        return item;
    }

    // --- ASYNC PRIMITIVES ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<T> PopAsync(CancellationToken cancellationToken = default)
    {
        T item = default!;
        // Fast path: if we pop successfully immediately, return zero-allocation ValueTask
        if (TryPop(out item))
        {
            return new ValueTask<T>(item);
        }

        return PopAsyncSlow(cancellationToken);
    }

    private async ValueTask<T> PopAsyncSlow(CancellationToken ct)
    {
        T item = default!;
        var spinner = new SpinWait();

        while (!TryPop(out item))
        {
            ct.ThrowIfCancellationRequested();

            // Spin briefly first to absorb micro-stalls without OS context switching
            if (spinner.Count < 16)
            {
                spinner.SpinOnce();
                continue;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mConsumerTcs = tcs;

            // Signal to the Producer that we are going to sleep
            Interlocked.Exchange(ref mConsumerWaiting, 1);

            // DOUBLE-CHECK: Did the producer push an item right before we went to sleep?
            if (TryPop(out item))
            {
                if (Interlocked.Exchange(ref mConsumerWaiting, 0) == 1)
                {
                    mConsumerTcs = null; // We got the item, cancel the sleep
                }

                return item;
            }

            // Sleep asynchronously
            using CancellationTokenRegistration ctr = ct.Register(state =>
                {
                    if (Interlocked.Exchange(ref mConsumerWaiting, 0) == 1)
                    {
                        ((TaskCompletionSource<bool>)state!).TrySetCanceled();
                    }
                },
                tcs);

            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                throw new OperationCanceledException(ct);
            }
        }

        return item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask PushAsync(T item, CancellationToken cancellationToken = default)
    {
        if (TryPush(item))
        {
            return default;
        }

        return PushAsyncSlow(item, cancellationToken);
    }

    private async ValueTask PushAsyncSlow(T item, CancellationToken ct)
    {
        var spinner = new SpinWait();

        while (!TryPush(item))
        {
            ct.ThrowIfCancellationRequested();

            if (spinner.Count < 16)
            {
                spinner.SpinOnce();
                continue;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mProducerTcs = tcs;

            Interlocked.Exchange(ref mProducerWaiting, 1);

            if (TryPush(item))
            {
                if (Interlocked.Exchange(ref mProducerWaiting, 0) == 1)
                {
                    mProducerTcs = null;
                }

                return;
            }

            using CancellationTokenRegistration ctr = ct.Register(state =>
                {
                    if (Interlocked.Exchange(ref mProducerWaiting, 0) == 1)
                    {
                        ((TaskCompletionSource<bool>)state!).TrySetCanceled();
                    }
                },
                tcs);

            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                throw new OperationCanceledException(ct);
            }
        }
    }

    // --- WAKE MECHANISMS ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WakeConsumer()
    {
        // Atomically claim the right to wake the consumer
        if (Interlocked.Exchange(ref mConsumerWaiting, 0) == 1)
        {
            TaskCompletionSource<bool>? tcs = mConsumerTcs;
            if (tcs != null)
            {
                mConsumerTcs = null;
                tcs.TrySetResult(true);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WakeProducer()
    {
        // Atomically claim the right to wake the producer
        if (Interlocked.Exchange(ref mProducerWaiting, 0) == 1)
        {
            TaskCompletionSource<bool>? tcs = mProducerTcs;
            if (tcs != null)
            {
                mProducerTcs = null;
                tcs.TrySetResult(true);
            }
        }
    }

    // --- UTILITIES ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int WasSize()
    {
        // .Acquire equivalent
        uint head = Volatile.Read(ref mHead.Value);
        uint tail = Volatile.Read(ref mTail.Value);
        return (int)(head - tail);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool WasEmpty()
    {
        return WasSize() == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool WasFull()
    {
        return WasSize() >= Capacity;
    }
}
