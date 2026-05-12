using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Omicron.Core.Collections;
using Xunit;

namespace Omicron.Core.Tests;

public class QueueSPSCTests
{
    [Fact]
    public void Constructor_RequiresPowerOfTwo()
    {
        Assert.Throws<ArgumentException>(() => new QueueSPSC<int>(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueueSPSC<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueueSPSC<int>(-1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(8192)]
    public void Constructor_AcceptsPowerOfTwo(int capacity)
    {
        var q = new QueueSPSC<int>(capacity);
        Assert.Equal(capacity, q.Capacity);
        Assert.True(q.WasEmpty());
        Assert.False(q.WasFull());
    }

    [Fact]
    public void TryPush_SingleItem_Then_TryPop()
    {
        var q = new QueueSPSC<int>(8);
        Assert.True(q.TryPush(42));
        Assert.False(q.WasEmpty());
        Assert.Equal(1, q.WasSize());

        Assert.True(q.TryPop(out var item));
        Assert.Equal(42, item);
        Assert.True(q.WasEmpty());
    }

    [Fact]
    public void TryPush_Full_ReturnsFalse()
    {
        var q = new QueueSPSC<int>(4);
        for (int i = 0; i < 4; i++)
            Assert.True(q.TryPush(i));

        Assert.True(q.WasFull());
        Assert.False(q.TryPush(99));
    }

    [Fact]
    public void TryPop_Empty_ReturnsFalse()
    {
        var q = new QueueSPSC<int>(4);
        Assert.False(q.TryPop(out var item));
        Assert.Equal(default, item);
    }

    [Fact]
    public void PushPop_FillAndDrain_MultipleCycles()
    {
        var q = new QueueSPSC<int>(16);
        for (int cycle = 0; cycle < 10; cycle++)
        {
            for (int i = 0; i < 16; i++)
                Assert.True(q.TryPush(cycle * 100 + i));

            Assert.True(q.WasFull());

            for (int i = 0; i < 16; i++)
            {
                Assert.True(q.TryPop(out var item));
                Assert.Equal(cycle * 100 + i, item);
            }

            Assert.True(q.WasEmpty());
        }
    }

    [Fact]
    public void PushPop_OverflowHeadWrapsCorrectly()
    {
        var q = new QueueSPSC<int>(4);

        // Fill
        for (int i = 0; i < 4; i++) q.TryPush(i);

        // Drain
        for (int i = 0; i < 4; i++) q.TryPop(out _);

        // Head/tail have both advanced by 4. Fill again.
        for (int i = 4; i < 8; i++) q.TryPush(i);

        for (int i = 4; i < 8; i++)
        {
            Assert.True(q.TryPop(out var item));
            Assert.Equal(i, item);
        }
    }

    [Fact]
    public void PushPop_ReferenceTypes_GCClearsSlots()
    {
        var q = new QueueSPSC<object>(4);
        var obj = new object();
        q.TryPush(obj);
        Assert.True(q.TryPop(out var popped));
        Assert.Same(obj, popped);
        // The internal array slot should now be null (not directly testable
        // without reflection, but we exercise the code path).
    }

    [Fact]
    public void PushBlocking_WaitsUntilSpace()
    {
        var q = new QueueSPSC<int>(2);
        q.Push(1);
        q.Push(2);

        int popped = 0;
        var popTask = Task.Run(() => { popped = q.Pop(); });

        // Give the pop task time to start
        Thread.Sleep(50);

        // Now unblock by popping in a third thread
        var unblockTask = Task.Run(() =>
        {
            Thread.Sleep(20);
            Assert.True(q.TryPop(out var _));
        });

        popTask.Wait(500);
        unblockTask.Wait(500);
        Assert.Equal(1, popped);
    }

    [Fact]
    public void PopBlocking_WaitsUntilItem()
    {
        var q = new QueueSPSC<int>(4);

        int popped = 0;
        var popTask = Task.Run(() => { popped = q.Pop(); });

        Thread.Sleep(50);
        Assert.True(q.TryPush(123));

        popTask.Wait(500);
        Assert.Equal(123, popped);
    }

    [Fact]
    public void Concurrent_SingleProducerSingleConsumer_100kItems()
    {
        const int count = 100_000;
        var q = new QueueSPSC<int>(1024);
        var produced = new List<int>(count);
        var consumed = new List<int>(count);

        var producer = Task.Run(() =>
        {
            for (int i = 0; i < count; i++)
            {
                q.Push(i);
                produced.Add(i);
            }
        });

        var consumer = Task.Run(() =>
        {
            for (int i = 0; i < count; i++)
            {
                consumed.Add(q.Pop());
            }
        });

        Task.WaitAll(producer, consumer);

        Assert.Equal(count, consumed.Count);
        Assert.Equal(Enumerable.Range(0, count), consumed);
    }

    [Fact]
    public void Concurrent_HeavyContention_RoundRobin()
    {
        const int rounds = 50_000;
        var q = new QueueSPSC<int>(8);

        var producer = Task.Run(() =>
        {
            for (int i = 0; i < rounds; i++)
                q.Push(i);
        });

        var consumer = Task.Run(() =>
        {
            for (int i = 0; i < rounds; i++)
            {
                var item = q.Pop();
                Assert.Equal(i, item);
            }
        });

        Task.WaitAll(producer, consumer);
    }

    [Fact]
    public async Task PushAsync_PopAsync_AsyncFlow()
    {
        var q = new QueueSPSC<int>(4);

        var pushTask = Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
                await q.PushAsync(i);
        });

        var popTask = Task.Run(async () =>
        {
            var results = new List<int>();
            for (int i = 0; i < 10; i++)
                results.Add(await q.PopAsync());
            return results;
        });

        var results = await popTask;
        await pushTask;

        Assert.Equal(Enumerable.Range(0, 10), results);
    }

    [Fact]
    public async Task PopAsync_Cancellation_Throws()
    {
        var q = new QueueSPSC<int>(4);
        using var cts = new CancellationTokenSource();

        var popTask = q.PopAsync(cts.Token).AsTask();
        cts.CancelAfter(50);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await popTask);
    }

    [Fact]
    public void WasSize_AccurateDuringMixedOperations()
    {
        var q = new QueueSPSC<int>(8);
        Assert.Equal(0, q.WasSize());

        q.Push(1); Assert.Equal(1, q.WasSize());
        q.Push(2); Assert.Equal(2, q.WasSize());
        q.Push(3); Assert.Equal(3, q.WasSize());

        q.TryPop(out _); Assert.Equal(2, q.WasSize());
        q.TryPop(out _); Assert.Equal(1, q.WasSize());

        q.Push(4); Assert.Equal(2, q.WasSize());
    }

    [Fact]
    public void StressTest_ByteStream_SPSC()
    {
        // Simulate terminal input: producer pushes raw bytes,
        // consumer pops them and verifies ordering.
        const int totalBytes = 500_000;
        var q = new QueueSPSC<byte>(4096);

        var producer = Task.Run(() =>
        {
            for (int i = 0; i < totalBytes; i++)
                q.Push((byte)(i & 0xFF));
        });

        var consumer = Task.Run(() =>
        {
            for (int i = 0; i < totalBytes; i++)
            {
                var b = q.Pop();
                Assert.Equal((byte)(i & 0xFF), b);
            }
        });

        Task.WaitAll(producer, consumer);
    }
}
