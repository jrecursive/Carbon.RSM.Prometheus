using System;
using System.Collections.Generic;

namespace RustServerMetrics.PrometheusMetrics;

internal enum TimedMetricKind
{
    Invoke,
    Rpc,
    WorkQueue,
    ServerUpdate,
    TimeWarning,
    ConsoleCommand
}

internal readonly struct TimedMetricLabels
{
    public readonly string Behaviour;
    public readonly string Method;
    public readonly string Command;

    public TimedMetricLabels(string behaviour, string method)
    {
        Behaviour = behaviour;
        Method = method;
        Command = null;
    }

    public TimedMetricLabels(string command)
    {
        Behaviour = null;
        Method = null;
        Command = command;
    }
}

internal sealed class TimedMetricFamily
{
    private readonly string _guardrailFamily;
    private readonly bool _isCommandFamily;
    private readonly bool _exportMethodCounters;
    private readonly bool _exportMethodHistograms;
    private readonly MetricGuardrails _guardrails;
    private readonly Histogram _coarseHistogram;
    private readonly Counter _callsTotal;
    private readonly Counter _durationTotal;
    private readonly Histogram _methodHistogram;
    private readonly ExpiringSeriesTracker _coarseSeries = new();
    private readonly ExpiringSeriesTracker _fineSeries = new();

    public TimedMetricFamily(MetricFactory metrics,
        MetricGuardrails guardrails,
        string guardrailFamily,
        string metricStem,
        bool isCommandFamily,
        bool exportMethodCounters,
        bool exportMethodHistograms,
        double[] durationBuckets)
    {
        _guardrails = guardrails;
        _guardrailFamily = guardrailFamily;
        _isCommandFamily = isCommandFamily;
        _exportMethodCounters = exportMethodCounters;
        _exportMethodHistograms = exportMethodHistograms && !isCommandFamily;

        var coarseLabelNames = isCommandFamily ? new[] { "command" } : new[] { "behaviour" };
        var fineLabelNames = isCommandFamily ? new[] { "command" } : new[] { "behaviour", "method" };

        _coarseHistogram = metrics.CreateHistogram(
            $"rsm_{metricStem}_duration_seconds",
            $"Latency distribution for {metricStem.Replace('_', ' ')} observations.",
            new HistogramConfiguration
            {
                Buckets = durationBuckets,
                LabelNames = coarseLabelNames
            });

        _callsTotal = metrics.CreateCounter(
            $"rsm_{metricStem}_calls_total",
            $"Total observed calls for {metricStem.Replace('_', ' ')}.",
            fineLabelNames);

        _durationTotal = metrics.CreateCounter(
            $"rsm_{metricStem}_duration_seconds_total",
            $"Total observed duration for {metricStem.Replace('_', ' ')} in seconds.",
            fineLabelNames);

        if (_exportMethodHistograms)
        {
            _methodHistogram = metrics.CreateHistogram(
                $"rsm_{metricStem}_method_duration_seconds",
                $"Optional method-level latency distribution for {metricStem.Replace('_', ' ')} observations.",
                new HistogramConfiguration
                {
                    Buckets = durationBuckets,
                    LabelNames = fineLabelNames
                });
        }
    }

    public void Observe(TimedMetricLabels labels, double durationSeconds, DateTime nowUtc)
    {
        if (_isCommandFamily)
        {
            ObserveCommand(labels, durationSeconds, nowUtc);
            return;
        }

        ObserveBehaviour(labels, durationSeconds, nowUtc);
    }

    public void ExpireStale(DateTime cutoffUtc)
    {
        _coarseSeries.ExpireOlderThan(cutoffUtc);
        _fineSeries.ExpireOlderThan(cutoffUtc);
    }

    private void ObserveCommand(TimedMetricLabels labels, double durationSeconds, DateTime nowUtc)
    {
        var command = _guardrails.ResolveCommand(labels.Command ?? "unknown");

        _coarseHistogram.WithLabels(command).Observe(durationSeconds);
        _coarseSeries.Touch(new[] { command }, nowUtc, values => _coarseHistogram.RemoveLabelled(values));

        if (!_exportMethodCounters)
        {
            return;
        }

        _callsTotal.WithLabels(command).Inc();
        _durationTotal.WithLabels(command).Inc(durationSeconds);
        _fineSeries.Touch(new[] { command }, nowUtc, RemoveFineSeries);
    }

    private void ObserveBehaviour(TimedMetricLabels labels, double durationSeconds, DateTime nowUtc)
    {
        var behaviour = _guardrails.ResolveBehaviour(_guardrailFamily, labels.Behaviour ?? "unknown");
        var method = _guardrails.ResolveMethod(_guardrailFamily, behaviour, labels.Method ?? "unknown");

        _coarseHistogram.WithLabels(behaviour).Observe(durationSeconds);
        _coarseSeries.Touch(new[] { behaviour }, nowUtc, values => _coarseHistogram.RemoveLabelled(values));

        if (_exportMethodCounters)
        {
            _callsTotal.WithLabels(behaviour, method).Inc();
            _durationTotal.WithLabels(behaviour, method).Inc(durationSeconds);
            _fineSeries.Touch(new[] { behaviour, method }, nowUtc, RemoveFineSeries);
        }

        if (_exportMethodHistograms && _guardrails.ShouldExportMethodHistogram(_guardrailFamily, behaviour, method))
        {
            _methodHistogram.WithLabels(behaviour, method).Observe(durationSeconds);
            _fineSeries.Touch(new[] { behaviour, method }, nowUtc, RemoveFineSeries);
        }
    }

    private void RemoveFineSeries(string[] labelValues)
    {
        _callsTotal.RemoveLabelled(labelValues);
        _durationTotal.RemoveLabelled(labelValues);
        _methodHistogram?.RemoveLabelled(labelValues);
    }
}

internal sealed class ExpiringSeriesTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TrackedSeries> _series = new(StringComparer.Ordinal);

    public void Touch(string[] labelValues, DateTime observedAtUtc, Action<string[]> removeAction)
    {
        var key = BuildKey(labelValues);

        lock (_gate)
        {
            _series[key] = new TrackedSeries((string[])labelValues.Clone(), observedAtUtc, removeAction);
        }
    }

    public void ExpireOlderThan(DateTime cutoffUtc)
    {
        List<TrackedSeries> expired = null;

        lock (_gate)
        {
            foreach (var item in _series)
            {
                if (item.Value.LastObservedUtc >= cutoffUtc)
                {
                    continue;
                }

                expired ??= new List<TrackedSeries>();
                expired.Add(item.Value);
            }

            if (expired == null)
            {
                return;
            }

            foreach (var item in expired)
            {
                _series.Remove(BuildKey(item.LabelValues));
            }
        }

        foreach (var item in expired)
        {
            item.RemoveAction(item.LabelValues);
        }
    }

    private static string BuildKey(string[] labelValues)
    {
        return string.Join("\u001f", labelValues);
    }
}

internal sealed class TrackedSeries
{
    public readonly string[] LabelValues;
    public readonly DateTime LastObservedUtc;
    public readonly Action<string[]> RemoveAction;

    public TrackedSeries(string[] labelValues, DateTime lastObservedUtc, Action<string[]> removeAction)
    {
        LabelValues = labelValues;
        LastObservedUtc = lastObservedUtc;
        RemoveAction = removeAction;
    }
}
