using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RustServerMetrics.PrometheusMetrics;
using Xunit;

namespace RustServerMetrics.Tests;

public sealed class MetricsWorkerTests
{
    [Fact]
    public void EnqueueLatestCoalescesToNewestWork()
    {
        using var worker = new MetricsWorker();
        var blocker = new ManualResetEventSlim(false);
        var values = new List<int>();

        worker.Start();
        worker.Enqueue(() => blocker.Wait(TimeSpan.FromSeconds(5)));

        worker.EnqueueLatest("snapshot", () => values.Add(1));
        worker.EnqueueLatest("snapshot", () => values.Add(2));
        worker.EnqueueLatest("snapshot", () => values.Add(3));

        blocker.Set();

        Assert.True(worker.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { 3 }, values);
        Assert.Equal(2, worker.CoalescedCount);
    }

    [Fact]
    public void EnqueueDrainsExactWorkInOrder()
    {
        using var worker = new MetricsWorker();
        var values = new List<int>();

        worker.Start();
        worker.Enqueue(() => values.Add(1));
        worker.Enqueue(() => values.Add(2));
        worker.Enqueue(() => values.Add(3));

        Assert.True(worker.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { 1, 2, 3 }, values);
    }

    [Fact]
    public void StopIsIdempotentAndDrainsPendingWork()
    {
        using var worker = new MetricsWorker();
        var count = 0;

        worker.Start();
        worker.Enqueue(() => Interlocked.Increment(ref count));

        worker.Stop();
        worker.Stop();

        Assert.Equal(1, count);
        Assert.False(worker.IsRunning);
    }

    [Fact]
    public void OverflowDropsWithoutBlockingProducer()
    {
        using var worker = new MetricsWorker(maxPendingWork: 1);
        var blocker = new ManualResetEventSlim(false);

        worker.Start();
        Assert.True(worker.Enqueue(() => blocker.Wait(TimeSpan.FromSeconds(5))));

        var accepted = worker.Enqueue(() => { });

        blocker.Set();

        Assert.False(accepted);
        Assert.Equal(1, worker.DroppedCount);
        Assert.True(worker.Flush(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RegistryCanCollectWhileWorkerUpdatesMetrics()
    {
        var registry = new MetricRegistry();
        var counter = new MetricFactory(registry).CreateCounter("rsm_worker_test_total", "Worker test counter.");
        using var worker = new MetricsWorker();

        worker.Start();

        var collector = Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                registry.CollectAsText();
            }
        });

        for (var i = 0; i < 1000; i++)
        {
            Assert.True(worker.Enqueue(() => counter.Inc()));
        }

        Assert.True(worker.Flush(TimeSpan.FromSeconds(5)));
        await collector.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("rsm_worker_test_total 1000", registry.CollectAsText());
    }
}
