using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RustServerMetrics.PrometheusMetrics;

internal sealed class MetricRegistry
{
    private readonly object _gate = new();
    private readonly List<IMetricFamily> _families = new();

    public void Register(IMetricFamily family)
    {
        lock (_gate)
        {
            _families.Add(family);
        }
    }

    public string CollectAsText()
    {
        List<IMetricFamily> snapshot;

        lock (_gate)
        {
            snapshot = new List<IMetricFamily>(_families);
        }

        var builder = new StringBuilder(16 * 1024);
        foreach (var family in snapshot)
        {
            family.Collect(builder);
        }

        return builder.ToString();
    }
}

internal interface IMetricFamily
{
    void Collect(StringBuilder builder);
}

internal sealed class MetricFactory
{
    private readonly MetricRegistry _registry;

    public MetricFactory(MetricRegistry registry)
    {
        _registry = registry;
    }

    public Counter CreateCounter(string name, string help)
    {
        return CreateCounter(name, help, Array.Empty<string>());
    }

    public Counter CreateCounter(string name, string help, string[] labelNames)
    {
        var metric = new Counter(name, help, labelNames ?? Array.Empty<string>());
        _registry.Register(metric);
        return metric;
    }

    public Gauge CreateGauge(string name, string help)
    {
        return CreateGauge(name, help, Array.Empty<string>());
    }

    public Gauge CreateGauge(string name, string help, string[] labelNames)
    {
        var metric = new Gauge(name, help, labelNames ?? Array.Empty<string>());
        _registry.Register(metric);
        return metric;
    }

    public Histogram CreateHistogram(string name, string help, HistogramConfiguration configuration)
    {
        var metric = new Histogram(
            name,
            help,
            configuration?.LabelNames ?? Array.Empty<string>(),
            configuration?.Buckets ?? Array.Empty<double>());
        _registry.Register(metric);
        return metric;
    }
}

internal sealed class HistogramConfiguration
{
    public string[] LabelNames { get; set; } = Array.Empty<string>();
    public double[] Buckets { get; set; } = Array.Empty<double>();
}

internal sealed class Counter : IMetricFamily
{
    public sealed class Child
    {
        private readonly Counter _owner;
        private readonly CounterSeries _series;

        internal Child(Counter owner, CounterSeries series)
        {
            _owner = owner;
            _series = series;
        }

        public void Inc(double increment = 1d)
        {
            if (increment < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(increment), "Counter cannot be decreased.");
            }

            lock (_owner._gate)
            {
                _series.Value += increment;
            }
        }
    }

    private readonly object _gate = new();
    private readonly string _name;
    private readonly string _help;
    private readonly string[] _labelNames;
    private readonly Dictionary<string, CounterSeries> _series = new(StringComparer.Ordinal);

    public Counter(string name, string help, string[] labelNames)
    {
        _name = name;
        _help = help;
        _labelNames = labelNames;
    }

    public void Inc(double increment = 1d)
    {
        WithLabels(Array.Empty<string>()).Inc(increment);
    }

    public Child WithLabels(params string[] labelValues)
    {
        var normalized = NormalizeLabelValues(labelValues);
        var key = BuildKey(normalized);

        lock (_gate)
        {
            if (!_series.TryGetValue(key, out var series))
            {
                series = new CounterSeries((string[])normalized.Clone());
                _series.Add(key, series);
            }

            return new Child(this, series);
        }
    }

    public void RemoveLabelled(string[] labelValues)
    {
        lock (_gate)
        {
            _series.Remove(BuildKey(NormalizeLabelValues(labelValues)));
        }
    }

    public void Collect(StringBuilder builder)
    {
        builder.Append("# HELP ").Append(_name).Append(' ').Append(TextFormat.EscapeHelp(_help)).Append('\n');
        builder.Append("# TYPE ").Append(_name).Append(" counter\n");

        lock (_gate)
        {
            foreach (var series in _series.Values)
            {
                builder.Append(_name);
                TextFormat.AppendLabels(builder, _labelNames, series.LabelValues);
                builder.Append(' ').Append(TextFormat.FormatDouble(series.Value)).Append('\n');
            }
        }
    }

    private string[] NormalizeLabelValues(string[] labelValues)
    {
        var normalized = labelValues ?? Array.Empty<string>();
        if (normalized.Length != _labelNames.Length)
        {
            throw new ArgumentException($"Expected {_labelNames.Length} labels for metric '{_name}' but got {normalized.Length}.");
        }

        return normalized;
    }

    private static string BuildKey(string[] labelValues)
    {
        return string.Join("\u001f", labelValues);
    }
}

internal sealed class Gauge : IMetricFamily
{
    public sealed class Child
    {
        private readonly Gauge _owner;
        private readonly GaugeSeries _series;

        internal Child(Gauge owner, GaugeSeries series)
        {
            _owner = owner;
            _series = series;
        }

        public void Set(double value)
        {
            lock (_owner._gate)
            {
                _series.Value = value;
            }
        }
    }

    private readonly object _gate = new();
    private readonly string _name;
    private readonly string _help;
    private readonly string[] _labelNames;
    private readonly Dictionary<string, GaugeSeries> _series = new(StringComparer.Ordinal);

    public Gauge(string name, string help, string[] labelNames)
    {
        _name = name;
        _help = help;
        _labelNames = labelNames;
    }

    public void Set(double value)
    {
        WithLabels(Array.Empty<string>()).Set(value);
    }

    public Child WithLabels(params string[] labelValues)
    {
        var normalized = NormalizeLabelValues(labelValues);
        var key = BuildKey(normalized);

        lock (_gate)
        {
            if (!_series.TryGetValue(key, out var series))
            {
                series = new GaugeSeries((string[])normalized.Clone());
                _series.Add(key, series);
            }

            return new Child(this, series);
        }
    }

    public void RemoveLabelled(string[] labelValues)
    {
        lock (_gate)
        {
            _series.Remove(BuildKey(NormalizeLabelValues(labelValues)));
        }
    }

    public void Collect(StringBuilder builder)
    {
        builder.Append("# HELP ").Append(_name).Append(' ').Append(TextFormat.EscapeHelp(_help)).Append('\n');
        builder.Append("# TYPE ").Append(_name).Append(" gauge\n");

        lock (_gate)
        {
            foreach (var series in _series.Values)
            {
                builder.Append(_name);
                TextFormat.AppendLabels(builder, _labelNames, series.LabelValues);
                builder.Append(' ').Append(TextFormat.FormatDouble(series.Value)).Append('\n');
            }
        }
    }

    private string[] NormalizeLabelValues(string[] labelValues)
    {
        var normalized = labelValues ?? Array.Empty<string>();
        if (normalized.Length != _labelNames.Length)
        {
            throw new ArgumentException($"Expected {_labelNames.Length} labels for metric '{_name}' but got {normalized.Length}.");
        }

        return normalized;
    }

    private static string BuildKey(string[] labelValues)
    {
        return string.Join("\u001f", labelValues);
    }
}

internal sealed class Histogram : IMetricFamily
{
    public sealed class Child
    {
        private readonly Histogram _owner;
        private readonly HistogramSeries _series;

        internal Child(Histogram owner, HistogramSeries series)
        {
            _owner = owner;
            _series = series;
        }

        public void Observe(double value)
        {
            lock (_owner._gate)
            {
                _series.Count += 1;
                _series.Sum += value;

                for (var i = 0; i < _owner._buckets.Length; i++)
                {
                    if (value <= _owner._buckets[i])
                    {
                        _series.CumulativeBuckets[i] += 1;
                    }
                }
            }
        }
    }

    private readonly object _gate = new();
    private readonly string _name;
    private readonly string _help;
    private readonly string[] _labelNames;
    private readonly double[] _buckets;
    private readonly Dictionary<string, HistogramSeries> _series = new(StringComparer.Ordinal);

    public Histogram(string name, string help, string[] labelNames, double[] buckets)
    {
        _name = name;
        _help = help;
        _labelNames = labelNames ?? Array.Empty<string>();
        _buckets = (buckets ?? Array.Empty<double>()).Distinct().OrderBy(x => x).ToArray();
    }

    public void Observe(double value)
    {
        WithLabels(Array.Empty<string>()).Observe(value);
    }

    public Child WithLabels(params string[] labelValues)
    {
        var normalized = NormalizeLabelValues(labelValues);
        var key = BuildKey(normalized);

        lock (_gate)
        {
            if (!_series.TryGetValue(key, out var series))
            {
                series = new HistogramSeries((string[])normalized.Clone(), _buckets.Length);
                _series.Add(key, series);
            }

            return new Child(this, series);
        }
    }

    public void RemoveLabelled(string[] labelValues)
    {
        lock (_gate)
        {
            _series.Remove(BuildKey(NormalizeLabelValues(labelValues)));
        }
    }

    public void Collect(StringBuilder builder)
    {
        builder.Append("# HELP ").Append(_name).Append(' ').Append(TextFormat.EscapeHelp(_help)).Append('\n');
        builder.Append("# TYPE ").Append(_name).Append(" histogram\n");

        lock (_gate)
        {
            foreach (var series in _series.Values)
            {
                for (var i = 0; i < _buckets.Length; i++)
                {
                    builder.Append(_name).Append("_bucket");
                    TextFormat.AppendLabels(builder, _labelNames, series.LabelValues, "le", TextFormat.FormatBucket(_buckets[i]));
                    builder.Append(' ').Append(series.CumulativeBuckets[i].ToString(CultureInfo.InvariantCulture)).Append('\n');
                }

                builder.Append(_name).Append("_bucket");
                TextFormat.AppendLabels(builder, _labelNames, series.LabelValues, "le", "+Inf");
                builder.Append(' ').Append(series.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');

                builder.Append(_name).Append("_sum");
                TextFormat.AppendLabels(builder, _labelNames, series.LabelValues);
                builder.Append(' ').Append(TextFormat.FormatDouble(series.Sum)).Append('\n');

                builder.Append(_name).Append("_count");
                TextFormat.AppendLabels(builder, _labelNames, series.LabelValues);
                builder.Append(' ').Append(series.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }
    }

    private string[] NormalizeLabelValues(string[] labelValues)
    {
        var normalized = labelValues ?? Array.Empty<string>();
        if (normalized.Length != _labelNames.Length)
        {
            throw new ArgumentException($"Expected {_labelNames.Length} labels for metric '{_name}' but got {normalized.Length}.");
        }

        return normalized;
    }

    private static string BuildKey(string[] labelValues)
    {
        return string.Join("\u001f", labelValues);
    }
}

internal sealed class CounterSeries
{
    public readonly string[] LabelValues;
    public double Value;

    public CounterSeries(string[] labelValues)
    {
        LabelValues = labelValues;
    }
}

internal sealed class GaugeSeries
{
    public readonly string[] LabelValues;
    public double Value;

    public GaugeSeries(string[] labelValues)
    {
        LabelValues = labelValues;
    }
}

internal sealed class HistogramSeries
{
    public readonly string[] LabelValues;
    public readonly long[] CumulativeBuckets;
    public long Count;
    public double Sum;

    public HistogramSeries(string[] labelValues, int bucketCount)
    {
        LabelValues = labelValues;
        CumulativeBuckets = new long[bucketCount];
    }
}

internal static class TextFormat
{
    public static string EscapeHelp(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\n", "\\n");
    }

    public static string EscapeLabelValue(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n");
    }

    public static string FormatDouble(double value)
    {
        if (double.IsPositiveInfinity(value))
        {
            return "+Inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Inf";
        }

        if (double.IsNaN(value))
        {
            return "NaN";
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    public static string FormatBucket(double value)
    {
        return value.ToString("0.###############", CultureInfo.InvariantCulture);
    }

    public static void AppendLabels(StringBuilder builder, string[] labelNames, string[] labelValues, string extraLabelName = null, string extraLabelValue = null)
    {
        var count = (labelNames?.Length ?? 0) + (extraLabelName == null ? 0 : 1);
        if (count == 0)
        {
            return;
        }

        builder.Append('{');
        var needsComma = false;

        if (labelNames != null)
        {
            for (var i = 0; i < labelNames.Length; i++)
            {
                if (needsComma)
                {
                    builder.Append(',');
                }

                builder.Append(labelNames[i])
                    .Append("=\"")
                    .Append(EscapeLabelValue(labelValues[i]))
                    .Append('"');

                needsComma = true;
            }
        }

        if (extraLabelName != null)
        {
            if (needsComma)
            {
                builder.Append(',');
            }

            builder.Append(extraLabelName)
                .Append("=\"")
                .Append(EscapeLabelValue(extraLabelValue))
                .Append('"');
        }

        builder.Append('}');
    }
}
