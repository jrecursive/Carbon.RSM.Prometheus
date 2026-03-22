# Install

This guide covers the confirmed deployment path for this fork.

## Support Scope

- confirmed working: Linux RustDedicated + Carbon
- not tested: Windows

Windows is not documented as a supported deployment target here. The project still contains a `Windows` build configuration inherited from the original module, but this fork has only been validated on Linux.

## Build

From the repo root:

```bash
./build-linux.sh
```

Output:

- `src/RustServerMetrics/bin/Linux/net48/Carbon.Linux.RSM.dll`

## Deploy To Carbon

1. Stop the Rust server.
2. Copy the built DLL to the Carbon managed modules directory:

```bash
cp src/RustServerMetrics/bin/Linux/net48/Carbon.Linux.RSM.dll /path/to/server/carbon/managed/modules/
```

3. Remove any older Carbon.RSM sidecar DLLs left over from previous experiments.
4. Start the Rust server.

## Configure The Exporter

Config path:

- `HarmonyMods_Data/ServerMetrics/Configuration.json`

Recommended starting point:

```json
{
  "Enabled": true,
  "PrometheusExporterEnabled": true,
  "PrometheusListenHost": "127.0.0.1",
  "PrometheusListenPort": 9108,
  "PrometheusMetricsPath": "/metrics",
  "UsePrometheusNet": false,
  "SuppressDefaultRuntimeMetrics": true,
  "ExportMethodCounters": true,
  "ExportMethodHistograms": false,
  "MethodHistogramAllowlist": [],
  "ExportPlayerAggregateMetrics": true,
  "ExportConnectionDiagnostics": true,
  "MetricExpiryMinutes": 30,
  "MessageTypeAllowlist": [],
  "BehaviourAllowlist": [],
  "CommandAllowlist": [],
  "SeriesBudget": {
    "Plugins": 128,
    "Modules": 128,
    "Behaviours": 128,
    "MethodsPerFamily": 256,
    "Commands": 128,
    "MessageTypes": 256
  },
  "HighPingThresholdsMs": [150, 250],
  "LowFpsThresholds": [30, 45],
  "HighPacketLossRatio": 0.05,
  "DebugEndpointEnabled": false,
  "DebugEndpointListenHost": "127.0.0.1",
  "DebugEndpointListenPort": 9109,
  "DebugEndpointBearerToken": "change-me-before-enabling",
  "DebugLogging": false
}
```

Notes:

- keep `PrometheusListenHost` on `127.0.0.1` unless you have a specific private-network scrape plan
- `UsePrometheusNet` should remain `false` in this fork
- if you enable the debug endpoint, set a real bearer token

## Reload And Verify

If the server is already running after you edit the config:

```text
servermetrics.reloadcfg
servermetrics.status
```

Local verification on Linux:

```bash
curl -s http://127.0.0.1:9108/metrics | head -n 30
```

Useful sanity checks:

```bash
curl -s http://127.0.0.1:9108/metrics | rg 'rsm_exporter_build_info|rsm_server_frames_per_second|rsm_players|rsm_entities_count'
```

If you expect richer operator metrics:

```bash
curl -s http://127.0.0.1:9108/metrics | rg 'rsm_connections|rsm_network_queue_|rsm_save_|rsm_wipe_|rsm_rcon_|rsm_eac_|rsm_event_|rsm_animals_total'
```

## Prometheus

Use the bundled example:

- [res/Prometheus-Scrape.example.yml](res/Prometheus-Scrape.example.yml)

Minimal scrape job:

```yaml
scrape_configs:
  - job_name: rust_server_metrics
    metrics_path: /metrics
    sample_limit: 20000
    label_limit: 40
    static_configs:
      - targets: ["127.0.0.1:9108"]
        labels:
          server: us-main-1
    metric_relabel_configs:
      - action: labeldrop
        regex: '^(steamid|ip)$'
```

Key rule:

- assign the human-readable server identity in Prometheus target labels, not in the exporter

File-based service discovery example:

- scrape config: [res/Prometheus-Scrape.example.yml](res/Prometheus-Scrape.example.yml)
- targets file: [res/Prometheus-Targets.example.json](res/Prometheus-Targets.example.json)

## Recording Rules

Bundled rules:

- [res/Prometheus-RecordingRules.yml](res/Prometheus-RecordingRules.yml)

Example placement:

- `/etc/prometheus/rules/carbon-rsm.rules.yml`

Example `prometheus.yml` reference:

```yaml
rule_files:
  - /etc/prometheus/rules/carbon-rsm.rules.yml
```

## Grafana

Import:

- [res/Grafana-Dashboard.json](res/Grafana-Dashboard.json)
- [res/Grafana-Dashboard-Diagnostics.json](res/Grafana-Dashboard-Diagnostics.json)

Expected dashboard setup:

- datasource type: Prometheus
- `server` variable sourced from target labels
- PromQL queries using `$__rate_interval`

Recommended usage:

1. Import `res/Grafana-Dashboard.json` first.
   This is the primary day-to-day operations dashboard.
2. Import `res/Grafana-Dashboard-Diagnostics.json` second.
   This is the deeper troubleshooting dashboard for queues, hook timing, RCON/EAC, and exporter internals.

## Remote Scraping

The default exporter bind is loopback only.

If Prometheus runs on another host, choose one of these:

- bind Carbon.RSM to a private interface address
- keep Carbon.RSM on loopback and forward `/metrics` through a private reverse proxy or tunnel

Do not expose the exporter publicly by default.

## Common Problems

`curl` says connection refused:

- confirm `Enabled: true`
- confirm `PrometheusExporterEnabled: true`
- run `servermetrics.status`
- verify the host/port/path in the config

`servermetrics.reloadcfg` reports errors:

- confirm you replaced the server DLL with the latest Linux build
- remove stale old Carbon.RSM sidecar assemblies from `carbon/managed/modules`

The exporter is up but a metric family is empty:

- some counters only move when the underlying server path actually executes
- use the runbook to check the specific family and whether it depends on gameplay, RCON activity, saves, or world events

## Runbook

Operational troubleshooting lives in [RUNBOOK.md](RUNBOOK.md).
