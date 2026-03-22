using System;
using System.Collections.Generic;
using RustServerMetrics.Config;

namespace RustServerMetrics.PrometheusMetrics;

internal sealed class MetricGuardrails
{
    private readonly ConfigData _config;
    private readonly Action<string, string, string> _recordSeriesDrop;
    private readonly LabelBudget _pluginBudget;
    private readonly LabelBudget _moduleBudget;
    private readonly LabelBudget _messageTypeBudget;
    private readonly LabelBudget _behaviourBudget;
    private readonly LabelBudget _commandBudget;
    private readonly Dictionary<string, LabelBudget> _methodBudgets = new(StringComparer.Ordinal);

    public MetricGuardrails(ConfigData config, Action<string, string, string> recordSeriesDrop)
    {
        _config = config;
        _recordSeriesDrop = recordSeriesDrop;
        _pluginBudget = new LabelBudget(config.SeriesBudget.Plugins, coalesceValue: "other");
        _moduleBudget = new LabelBudget(config.SeriesBudget.Modules, coalesceValue: "other");
        _messageTypeBudget = new LabelBudget(config.SeriesBudget.MessageTypes, coalesceValue: "other", config.MessageTypeAllowlistSet);
        _behaviourBudget = new LabelBudget(config.SeriesBudget.Behaviours, coalesceValue: "other", config.BehaviourAllowlistSet);
        _commandBudget = new LabelBudget(config.SeriesBudget.Commands, coalesceValue: "other", config.CommandAllowlistSet);
    }

    public string ResolvePlugin(string value)
    {
        return Resolve("plugin_hook", _pluginBudget, value);
    }

    public string ResolveModule(string value)
    {
        return Resolve("module_hook", _moduleBudget, value);
    }

    public string ResolveMessageType(string value)
    {
        return Resolve("network_updates", _messageTypeBudget, value);
    }

    public string ResolveBehaviour(string family, string value)
    {
        return Resolve(family, _behaviourBudget, value);
    }

    public string ResolveCommand(string value)
    {
        return Resolve("console_command", _commandBudget, value);
    }

    public string ResolveMethod(string family, string behaviour, string method)
    {
        var budget = GetMethodBudget(family);
        var fullName = $"{behaviour}.{method}";
        var resolved = budget.Resolve(fullName);
        if (!resolved.Changed)
        {
            return method;
        }

        _recordSeriesDrop(family, resolved.Reason, resolved.Action);
        return "other";
    }

    public bool ShouldExportMethodHistogram(string family, string behaviour, string method)
    {
        if (!_config.ExportMethodHistograms)
        {
            return false;
        }

        if (_config.MethodHistogramAllowlistSet.Count == 0)
        {
            return true;
        }

        var fullName = $"{behaviour}.{method}";
        return _config.MethodHistogramAllowlistSet.Contains(fullName) ||
               _config.MethodHistogramAllowlistSet.Contains($"{family}:{fullName}");
    }

    private LabelBudget GetMethodBudget(string family)
    {
        lock (_methodBudgets)
        {
            if (_methodBudgets.TryGetValue(family, out var budget))
            {
                return budget;
            }

            budget = new LabelBudget(_config.SeriesBudget.MethodsPerFamily, coalesceValue: "other");
            _methodBudgets.Add(family, budget);
            return budget;
        }
    }

    private string Resolve(string family, LabelBudget budget, string value)
    {
        var resolved = budget.Resolve(value);
        if (resolved.Changed)
        {
            _recordSeriesDrop(family, resolved.Reason, resolved.Action);
        }

        return resolved.Value;
    }
}

internal readonly struct LabelBudgetResult
{
    public readonly string Value;
    public readonly bool Changed;
    public readonly string Reason;
    public readonly string Action;

    public LabelBudgetResult(string value, bool changed, string reason, string action)
    {
        Value = value;
        Changed = changed;
        Reason = reason;
        Action = action;
    }
}

internal sealed class LabelBudget
{
    private readonly object _gate = new();
    private readonly int _budget;
    private readonly string _coalesceValue;
    private readonly HashSet<string> _allowlist;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public LabelBudget(int budget, string coalesceValue, HashSet<string> allowlist = null)
    {
        _budget = budget;
        _coalesceValue = coalesceValue;
        _allowlist = allowlist;
    }

    public LabelBudgetResult Resolve(string rawValue)
    {
        var value = string.IsNullOrWhiteSpace(rawValue) ? "unknown" : rawValue.Trim();

        lock (_gate)
        {
            if (_seen.Contains(value))
            {
                return new LabelBudgetResult(value, changed: false, reason: null, action: null);
            }

            if (_allowlist != null && _allowlist.Count > 0 && !_allowlist.Contains(value))
            {
                return Coalesce("allowlist_miss");
            }

            if (_seen.Count >= _budget)
            {
                return Coalesce("budget_exceeded");
            }

            _seen.Add(value);
            return new LabelBudgetResult(value, changed: false, reason: null, action: null);
        }
    }

    private LabelBudgetResult Coalesce(string reason)
    {
        _seen.Add(_coalesceValue);
        return new LabelBudgetResult(_coalesceValue, changed: true, reason: reason, action: "coalesce");
    }
}
