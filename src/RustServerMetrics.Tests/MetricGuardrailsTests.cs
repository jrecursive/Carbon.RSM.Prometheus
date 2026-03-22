using System.Collections.Generic;
using RustServerMetrics.Config;
using RustServerMetrics.PrometheusMetrics;
using Xunit;

namespace RustServerMetrics.Tests;

public sealed class MetricGuardrailsTests
{
    [Fact]
    public void ResolveMethod_CoalescesWhenBudgetExceeded()
    {
        var drops = new List<string>();
        var config = new ConfigData
        {
            SeriesBudget = new SeriesBudgetConfig
            {
                MethodsPerFamily = 1
            }
        };
        config.Normalize();

        var guardrails = new MetricGuardrails(config, (family, reason, action) => drops.Add($"{family}:{reason}:{action}"));

        var first = guardrails.ResolveMethod("rpc", "BasePlayer", "PerformanceReport");
        var second = guardrails.ResolveMethod("rpc", "BasePlayer", "OtherMethod");

        Assert.Equal("PerformanceReport", first);
        Assert.Equal("other", second);
        Assert.Contains("rpc:budget_exceeded:coalesce", drops);
    }

    [Fact]
    public void ResolveMessageType_UsesAllowlistAndCoalescesUnknownValues()
    {
        var drops = new List<string>();
        var config = new ConfigData
        {
            MessageTypeAllowlist = new List<string> { "EntityUpdate" }
        };
        config.Normalize();

        var guardrails = new MetricGuardrails(config, (family, reason, action) => drops.Add($"{family}:{reason}:{action}"));

        Assert.Equal("EntityUpdate", guardrails.ResolveMessageType("EntityUpdate"));
        Assert.Equal("other", guardrails.ResolveMessageType("NotAllowed"));
        Assert.Contains("network_updates:allowlist_miss:coalesce", drops);
    }
}
