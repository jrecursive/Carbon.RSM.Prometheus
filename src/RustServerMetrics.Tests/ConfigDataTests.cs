using RustServerMetrics.Config;
using Xunit;

namespace RustServerMetrics.Tests;

public sealed class ConfigDataTests
{
    [Fact]
    public void FromJson_MigratesLegacyKeysAndIgnoresInfluxKeys()
    {
        var json = """
        {
          "Enabled": true,
          "Debug Logging": true,
          "Gather Player Averages (Client FPS, Client Latency, Player FPS, Player Memory, Player Latency, Player Packet Loss)": false,
          "Influx Database Url": "https://example.invalid",
          "Amount of metrics to submit in each request": 5000
        }
        """;

        var config = ConfigData.FromJson(json);

        Assert.True(config.Enabled);
        Assert.True(config.DebugLogging);
        Assert.False(config.ExportPlayerAggregateMetrics);
        Assert.Contains(config.LegacyWarnings, x => x.Contains("Influx Database Url"));
        Assert.Contains(config.LegacyWarnings, x => x.Contains("Debug Logging"));
    }
}
