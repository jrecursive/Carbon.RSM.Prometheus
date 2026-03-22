using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RustServerMetrics.Config;

internal sealed class ConfigData
{
    public const string DefaultPrometheusListenHost = "127.0.0.1";
    public const int DefaultPrometheusListenPort = 9108;
    public const string DefaultPrometheusMetricsPath = "/metrics";
    public const string DefaultDebugEndpointListenHost = "127.0.0.1";
    public const int DefaultDebugEndpointListenPort = 9109;
    public const string LegacyPlayerMetricsKey = "Gather Player Averages (Client FPS, Client Latency, Player FPS, Player Memory, Player Latency, Player Packet Loss)";

    [JsonProperty(PropertyName = "Enabled")]
    public bool Enabled = false;

    [JsonProperty(PropertyName = "PrometheusExporterEnabled")]
    public bool PrometheusExporterEnabled = true;

    [JsonProperty(PropertyName = "PrometheusListenHost")]
    public string PrometheusListenHost = DefaultPrometheusListenHost;

    [JsonProperty(PropertyName = "PrometheusListenPort")]
    public int PrometheusListenPort = DefaultPrometheusListenPort;

    [JsonProperty(PropertyName = "PrometheusMetricsPath")]
    public string PrometheusMetricsPath = DefaultPrometheusMetricsPath;

    [JsonProperty(PropertyName = "UsePrometheusNet")]
    public bool UsePrometheusNet = false;

    [JsonProperty(PropertyName = "SuppressDefaultRuntimeMetrics")]
    public bool SuppressDefaultRuntimeMetrics = true;

    [JsonProperty(PropertyName = "ExportMethodCounters")]
    public bool ExportMethodCounters = true;

    [JsonProperty(PropertyName = "ExportMethodHistograms")]
    public bool ExportMethodHistograms = false;

    [JsonProperty(PropertyName = "MethodHistogramAllowlist")]
    public List<string> MethodHistogramAllowlist = new();

    [JsonProperty(PropertyName = "ExportPlayerAggregateMetrics")]
    public bool ExportPlayerAggregateMetrics = true;

    [JsonProperty(PropertyName = "ExportConnectionDiagnostics")]
    public bool ExportConnectionDiagnostics = true;

    [JsonProperty(PropertyName = "MetricExpiryMinutes")]
    public int MetricExpiryMinutes = 30;

    [JsonProperty(PropertyName = "MessageTypeAllowlist")]
    public List<string> MessageTypeAllowlist = new();

    [JsonProperty(PropertyName = "BehaviourAllowlist")]
    public List<string> BehaviourAllowlist = new();

    [JsonProperty(PropertyName = "CommandAllowlist")]
    public List<string> CommandAllowlist = new();

    [JsonProperty(PropertyName = "SeriesBudget")]
    public SeriesBudgetConfig SeriesBudget = new();

    [JsonProperty(PropertyName = "HighPingThresholdsMs")]
    public List<int> HighPingThresholdsMs = new() { 150, 250 };

    [JsonProperty(PropertyName = "LowFpsThresholds")]
    public List<int> LowFpsThresholds = new() { 30, 45 };

    [JsonProperty(PropertyName = "HighPacketLossRatio")]
    public double HighPacketLossRatio = 0.05d;

    [JsonProperty(PropertyName = "DebugEndpointEnabled")]
    public bool DebugEndpointEnabled = false;

    [JsonProperty(PropertyName = "DebugEndpointListenHost")]
    public string DebugEndpointListenHost = DefaultDebugEndpointListenHost;

    [JsonProperty(PropertyName = "DebugEndpointListenPort")]
    public int DebugEndpointListenPort = DefaultDebugEndpointListenPort;

    [JsonProperty(PropertyName = "DebugEndpointBearerToken")]
    public string DebugEndpointBearerToken = string.Empty;

    [JsonProperty(PropertyName = "DebugLogging")]
    public bool DebugLogging = false;

    [JsonIgnore]
    public List<string> LegacyWarnings = new();

    [JsonIgnore]
    public HashSet<string> MessageTypeAllowlistSet => ToNormalizedSet(MessageTypeAllowlist);

    [JsonIgnore]
    public HashSet<string> BehaviourAllowlistSet => ToNormalizedSet(BehaviourAllowlist);

    [JsonIgnore]
    public HashSet<string> CommandAllowlistSet => ToNormalizedSet(CommandAllowlist);

    [JsonIgnore]
    public HashSet<string> MethodHistogramAllowlistSet => ToNormalizedSet(MethodHistogramAllowlist);

    [JsonIgnore]
    public TimeSpan MetricExpiry => TimeSpan.FromMinutes(Math.Max(1, MetricExpiryMinutes));

    public static ConfigData FromJson(string configJson)
    {
        var root = JsonConvert.DeserializeObject<JObject>(configJson) ?? new JObject();
        var config = root.ToObject<ConfigData>() ?? new ConfigData();
        config.ApplyLegacyCompatibility(root);
        config.Normalize();
        return config;
    }

    public void Normalize()
    {
        PrometheusListenHost = NormalizeHost(PrometheusListenHost, DefaultPrometheusListenHost);
        PrometheusListenPort = NormalizePort(PrometheusListenPort, DefaultPrometheusListenPort);
        PrometheusMetricsPath = NormalizePath(PrometheusMetricsPath, DefaultPrometheusMetricsPath);

        DebugEndpointListenHost = NormalizeHost(DebugEndpointListenHost, DefaultDebugEndpointListenHost);
        DebugEndpointListenPort = NormalizePort(DebugEndpointListenPort, DefaultDebugEndpointListenPort);
        DebugEndpointBearerToken ??= string.Empty;

        MetricExpiryMinutes = Math.Max(1, MetricExpiryMinutes);
        HighPacketLossRatio = Clamp01(HighPacketLossRatio);
        SeriesBudget ??= new SeriesBudgetConfig();
        SeriesBudget.Normalize();

        MethodHistogramAllowlist ??= new List<string>();
        MessageTypeAllowlist ??= new List<string>();
        BehaviourAllowlist ??= new List<string>();
        CommandAllowlist ??= new List<string>();

        HighPingThresholdsMs = NormalizeThresholds(HighPingThresholdsMs, 150, 250);
        LowFpsThresholds = NormalizeThresholds(LowFpsThresholds, 30, 45);
    }

    private void ApplyLegacyCompatibility(JObject root)
    {
        ApplyLegacyBoolean(root, "Debug Logging", value => DebugLogging = value, nameof(DebugLogging));
        ApplyLegacyBoolean(root, LegacyPlayerMetricsKey, value => ExportPlayerAggregateMetrics = value, nameof(ExportPlayerAggregateMetrics));

        var ignoredKeys = new[]
        {
            "Influx Database Url",
            "Influx Database Name",
            "Influx Database User",
            "Influx Database Password",
            "Amount of metrics to submit in each request",
            "Server Tag"
        };

        foreach (var key in ignoredKeys)
        {
            if (root.TryGetValue(key, out _))
            {
                LegacyWarnings.Add($"Ignoring legacy configuration key '{key}'. InfluxDB push mode is no longer supported.");
            }
        }
    }

    private void ApplyLegacyBoolean(JObject root, string legacyKey, Action<bool> setter, string newKey)
    {
        if (root.TryGetValue(newKey, out _))
        {
            return;
        }

        if (!root.TryGetValue(legacyKey, out var token))
        {
            return;
        }

        if (token.Type == JTokenType.Boolean)
        {
            setter(token.Value<bool>());
        }

        LegacyWarnings.Add($"Migrated legacy configuration key '{legacyKey}' to '{newKey}'.");
    }

    private static string NormalizeHost(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim();
    }

    private static int NormalizePort(int value, int fallback)
    {
        return value is > 0 and <= 65535 ? value : fallback;
    }

    private static string NormalizePath(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Trim();
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized;
        }

        return normalized;
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0d;
        }

        if (value < 0d)
        {
            return 0d;
        }

        if (value > 1d)
        {
            return 1d;
        }

        return value;
    }

    private static List<int> NormalizeThresholds(IEnumerable<int> values, params int[] fallback)
    {
        var normalized = (values ?? Array.Empty<int>())
            .Where(x => x > 0)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        if (normalized.Count > 0)
        {
            return normalized;
        }

        return fallback.Distinct().OrderBy(x => x).ToList();
    }

    private static HashSet<string> ToNormalizedSet(IEnumerable<string> values)
    {
        return new HashSet<string>(
            (values ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()),
            StringComparer.Ordinal);
    }
}

internal sealed class SeriesBudgetConfig
{
    [JsonProperty(PropertyName = "Plugins")]
    public int Plugins = 128;

    [JsonProperty(PropertyName = "Modules")]
    public int Modules = 128;

    [JsonProperty(PropertyName = "Behaviours")]
    public int Behaviours = 128;

    [JsonProperty(PropertyName = "MethodsPerFamily")]
    public int MethodsPerFamily = 256;

    [JsonProperty(PropertyName = "Commands")]
    public int Commands = 128;

    [JsonProperty(PropertyName = "MessageTypes")]
    public int MessageTypes = 256;

    public void Normalize()
    {
        Plugins = NormalizeBudget(Plugins, 128);
        Modules = NormalizeBudget(Modules, 128);
        Behaviours = NormalizeBudget(Behaviours, 128);
        MethodsPerFamily = NormalizeBudget(MethodsPerFamily, 256);
        Commands = NormalizeBudget(Commands, 128);
        MessageTypes = NormalizeBudget(MessageTypes, 256);
    }

    private static int NormalizeBudget(int value, int fallback)
    {
        return value > 0 ? value : fallback;
    }
}
