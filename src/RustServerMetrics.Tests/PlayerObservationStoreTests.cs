using System;
using System.Collections.Generic;
using RustServerMetrics.PrometheusMetrics;
using Xunit;

namespace RustServerMetrics.Tests;

public sealed class PlayerObservationStoreTests
{
    [Fact]
    public void CreateSnapshot_AggregatesLatestSamplesWithoutPlayerLabels()
    {
        var store = new PlayerObservationStore();
        var now = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);

        store.UpdateNetworkSample(1, "one", "10.0.0.1", 0.200d, 0.060d, now);
        store.UpdateClientSample(1, "one", "10.0.0.1", 25d, 1024L, now);
        store.UpdateNetworkSample(2, "two", "10.0.0.2", 0.100d, 0.010d, now);
        store.UpdateClientSample(2, "two", "10.0.0.2", 60d, 2048L, now);

        var snapshot = store.CreateSnapshot(
            now,
            TimeSpan.FromMinutes(5),
            new List<int> { 150, 250 },
            new List<int> { 30, 45 },
            0.05d);

        Assert.Equal(2, snapshot.Population["ping"]);
        Assert.Equal(2, snapshot.Population["fps"]);
        Assert.Equal(2, snapshot.Population["memory"]);
        Assert.Equal(2, snapshot.Population["packet_loss"]);
        Assert.Equal(1, snapshot.ConditionCount["ping_gt_0_150s"]);
        Assert.Equal(0, snapshot.ConditionCount["ping_gt_0_250s"]);
        Assert.Equal(1, snapshot.ConditionCount["fps_lt_30"]);
        Assert.Equal(1, snapshot.ConditionCount["fps_lt_45"]);
        Assert.Equal(1, snapshot.ConditionCount["packet_loss_gt_0_05"]);
        Assert.DoesNotContain(snapshot.ConditionCount.Keys, x => x.Contains("steamid", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snapshot.ConditionCount.Keys, x => x.Contains("10.0.0.", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateSnapshot_EvictsStalePlayers()
    {
        var store = new PlayerObservationStore();
        var now = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);

        store.UpdateNetworkSample(1, "one", "10.0.0.1", 0.200d, 0.060d, now - TimeSpan.FromMinutes(10));
        store.UpdateNetworkSample(2, "two", "10.0.0.2", 0.100d, 0.010d, now);

        var snapshot = store.CreateSnapshot(
            now,
            TimeSpan.FromMinutes(5),
            new List<int> { 150, 250 },
            new List<int> { 30, 45 },
            0.05d);

        Assert.Single(snapshot.DebugPlayers);
        Assert.Equal("2", snapshot.DebugPlayers[0].SteamId);
        Assert.Equal(1, snapshot.Population["ping"]);
    }
}
