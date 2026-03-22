# Runbook

## Scope

- confirmed runtime: Linux
- not validated: Windows

This runbook assumes a Linux RustDedicated + Carbon deployment.

## Fast Triage

In the Rust server console:

```text
servermetrics.status
```

On the server host:

```bash
curl -s http://127.0.0.1:9108/metrics | head -n 30
```

```bash
ss -ltn | rg 9108
```

## Core Health Checks

Exporter health:

```bash
curl -s http://127.0.0.1:9108/metrics | rg 'rsm_exporter_build_info|rsm_exporter_collect_errors_total|rsm_exporter_last_snapshot_success_timestamp_seconds'
```

Server snapshot health:

```bash
curl -s http://127.0.0.1:9108/metrics | rg 'rsm_server_frames_per_second|rsm_server_frametime_seconds|rsm_players|rsm_entities_count|rsm_connections'
```

Network health:

```bash
curl -s http://127.0.0.1:9108/metrics | rg 'rsm_network_bytes_total|rsm_network_queue_|rsm_connection_queue_depth|rsm_network_packet_loss_ratio'
```

Operator metrics:

```bash
curl -s http://127.0.0.1:9108/metrics | rg 'rsm_save_|rsm_wipe_|rsm_rcon_|rsm_eac_|rsm_runtime_phase_seconds|rsm_ai_think_|rsm_work_queue_|rsm_event_|rsm_animals_total'
```

## Prometheus Checks

Useful PromQL:

```promql
up{job="rust_server_metrics"}
```

```promql
rsm_server_frames_per_second{stat="instant"}
```

```promql
sum by (message_type) (rate(rsm_network_updates_total[5m]))
```

```promql
histogram_quantile(0.95, sum by (le) (rate(rsm_player_ping_seconds_bucket[5m])))
```

```promql
increase(rsm_exporter_collect_errors_total[15m])
```

Grafana import recommendation:

1. import `res/Grafana-Dashboard.json` for the main operations view
2. import `res/Grafana-Dashboard-Diagnostics.json` for deep troubleshooting

## Common Failures

### No Listener

Symptoms:

- `curl` gets connection refused
- `servermetrics.status` says the exporter is not running

Check:

- `Enabled`
- `PrometheusExporterEnabled`
- `PrometheusListenHost`
- `PrometheusListenPort`
- `PrometheusMetricsPath`

Then reload:

```text
servermetrics.reloadcfg
```

### Old DLL Still Deployed

Symptoms:

- reload errors mention old runtime behavior
- expected newer metrics are missing

Fix:

- stop the server
- replace `carbon/managed/modules/Carbon.Linux.RSM.dll`
- remove stale Carbon.RSM sidecar DLLs left from older builds
- restart the server

### Exporter Up, But Some Families Stay Empty

This usually means the underlying Rust path has not produced observations yet.

Examples:

- `rsm_player_ping_seconds`: needs active player polling
- `rsm_network_updates_total`: needs outbound `NetWrite` activity
- timed families such as `rsm_rpc_calls_total`: depend on Harmony timing patches and actual gameplay activity
- `rsm_save_duration_seconds`: updates after a save path runs
- `rsm_rcon_messages_total`: moves when RCON commands are received

### RCON / EAC / Save Metrics Missing

Check the activity source:

- use RCON to generate `rsm_rcon_messages_total`
- wait for or trigger a save to update `rsm_save_duration_seconds`
- EAC metrics depend on real auth activity and EAC state transitions

### Bind Host Problems

Recommended default:

- `127.0.0.1`

If you want remote Prometheus scraping:

- bind to a private interface IP, not `0.0.0.0` unless you have a clear network policy around it

## Notes

Current source-backed limitations:

- no complete `rsm_disconnects_total{reason}` family yet because disconnect reasons are spread across multiple paths and not all are normalized in one place
- player attribution remains intentionally outside the main Prometheus export

If you need player attribution for debugging:

- use the optional debug endpoint
- keep it private
- do not scrape it with Prometheus
