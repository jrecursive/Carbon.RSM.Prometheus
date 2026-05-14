using System;
using RustServerMetrics.PrometheusMetrics;

namespace RustServerMetrics.HarmonyPatches.Utility;

internal sealed class MetricsTimeStorage<TKey>
{
    private readonly TimedMetricKind _kind;
    private readonly Func<TKey, TimedMetricLabels> _labelSelector;

    public MetricsTimeStorage(TimedMetricKind kind, Func<TKey, TimedMetricLabels> labelSelector)
    {
        _kind = kind;
        _labelSelector = labelSelector;
    }

    public void LogTime(TKey key, double milliseconds)
    {
        var logger = MetricsLogger.Instance;
        if (logger == null || !logger.Ready)
        {
            return;
        }

        logger.ObserveTimedMetric(_kind, key, _labelSelector, milliseconds / 1000d);
    }
}
