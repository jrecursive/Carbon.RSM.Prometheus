# Carbon.RSM

Carbon.RSM is a Carbon module for Rust dedicated servers that exposes Prometheus metrics over an in-process HTTP endpoint.

It replaces the old InfluxDB push model with a pull-based exporter:

- no InfluxDB transport
- no batch uploader
- no send buffer
- no Pushgateway
- no exporter-driven remote write

## Status

This fork is currently confirmed to work on Linux only.

Windows has not been tested at all. The project still contains a `Windows` build configuration because the original module did, but this fork should be treated as Linux-only until someone verifies the full runtime behavior on a real Windows RustDedicated + Carbon deployment.

## What It Exposes

The exporter focuses on operator-facing Rust server health:

- server FPS, frametime, memory, GC, players, entities, task depth
- plugin and module hook totals
- network counters and queue health
- aggregate player experience metrics without per-player Prometheus labels
- connection, RCON, EAC, save, wipe, AI, work-queue, and event state metrics
- exporter self-observability

## Architecture

- in-process Prometheus text exposition
- dedicated internal metric registry
- default listener `127.0.0.1:9108`
- default path `/metrics`
- optional debug-only `/player-observations/` endpoint with bearer token
- target-side labels own server identity
- bounded labels and series budgets
- no steady-state `steamid` or `ip` labels in the main export
- classic histograms only

## Quick Start

Build on Linux:

```bash
./build-linux.sh
```

Deploy:

- copy `src/RustServerMetrics/bin/Linux/net48/Carbon.Linux.RSM.dll` to `carbon/managed/modules/`

Configure:

- edit `HarmonyMods_Data/ServerMetrics/Configuration.json`

Verify:

```bash
curl -s http://127.0.0.1:9108/metrics | head -n 30
```

Detailed deployment guidance is in [INSTALL.md](INSTALL.md).
Operational troubleshooting is in [RUNBOOK.md](RUNBOOK.md).

## Configuration

The module reads and writes:

- `HarmonyMods_Data/ServerMetrics/Configuration.json`

Minimal recommended Linux config:

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
  "ExportPlayerAggregateMetrics": true,
  "ExportConnectionDiagnostics": true,
  "MetricExpiryMinutes": 30,
  "DebugEndpointEnabled": false,
  "DebugLogging": false
}
```

Notes:

- `Enabled` is the master switch.
- `PrometheusExporterEnabled` controls whether the metrics listener is started.
- `UsePrometheusNet` remains as a compatibility flag only. This fork uses the built-in exporter implementation because `prometheus-net` was not runtime-compatible with the RustDedicated Mono runtime used here.
- legacy InfluxDB keys are tolerated on load, ignored at runtime, and rewritten out in the Prometheus-oriented config.

## Label Policy

Steady-state labels intentionally allowed by default:

- `action`
- `collector`
- `command`
- `condition`
- `direction`
- `event`
- `family`
- `framework`
- `kind`
- `message_type`
- `method`
- `module`
- `phase`
- `plugin`
- `queue`
- `reason`
- `state`
- `stat`
- `status`

Steady-state labels intentionally forbidden from the main exporter:

- `steamid`
- `ip`

## Full Metric Dictionary

All metrics use the `rsm_` prefix.

Histograms are classic Prometheus histograms and therefore emit:

- `_bucket`
- `_sum`
- `_count`

### Exporter Self Metrics

| Metric | Type | Labels | Notes |
| --- | --- | --- | --- |
| `rsm_exporter_build_info` | gauge | `version`, `commit`, `framework` | Always `1` for the running build. |
| `rsm_exporter_collect_errors_total` | counter | `collector` | Incremented when a collector poll/update fails. |
| `rsm_exporter_series_dropped_total` | counter | `family`, `reason`, `action` | Guardrail/coalescing telemetry. |
| `rsm_exporter_last_snapshot_success_timestamp_seconds` | gauge | `collector` | Last successful collector run timestamp. |
| `rsm_exporter_last_save_timestamp_seconds` | gauge | none | Last observed successful save timestamp. |
| `rsm_exporter_snapshot_duration_seconds` | histogram | `collector` | Collector execution duration. |

### Server Snapshot Metrics

| Metric | Type | Labels | Notes |
| --- | --- | --- | --- |
| `rsm_server_frames_per_second` | gauge | `stat` | `instant`, `average`. |
| `rsm_server_frametime_seconds` | gauge | `stat` | `instant`, `average`. |
| `rsm_memory_used_bytes` | gauge | none | Server memory usage in bytes. |
| `rsm_gc_collections_total` | counter | none | Monotonic process GC collections. |
| `rsm_task_queue_depth` | gauge | `queue` | `load_balancer`, `invoke_handler`, `workshop_skins_queue`. |
| `rsm_players` | gauge | `state` | `connected`, `sleeping`, `bots`, `joining`, `queued`, `receiving_snapshot`. |
| `rsm_entities_count` | gauge | none | Current `BaseNetworkable.serverEntities.Count`. |
| `rsm_connections` | gauge | none | Current `Net.sv.connections.Count`. |
| `rsm_snapshot_queue_depth` | gauge | `stat` | `sum`, `max` across players currently receiving snapshots. |

### Network Metrics

| Metric | Type | Labels | Notes |
| --- | --- | --- | --- |
| `rsm_network_bytes_total` | counter | `direction` | `receive`, `send`, backed by cumulative RakNet totals. |
| `rsm_network_queue_depth` | gauge | `queue` | `read`, `write`, `decrypt`. |
| `rsm_network_queue_bytes` | gauge | `queue` | `read`, `write`, `decrypt`. |
| `rsm_connection_queue_depth` | gauge | `state` | `reserved`, `joining`, `queued`. |
| `rsm_network_packet_loss_ratio` | gauge | none | Last-second RakNet packet loss ratio. |
| `rsm_network_updates_total` | counter | `message_type` | Outbound update/message counter. |
| `rsm_network_update_bytes_total` | counter | `message_type` | Outbound update/message bytes. |
| `rsm_global_network_entities_count` | gauge | none | `GlobalNetworkHandler.serverData.Count`. |
| `rsm_global_network_connections` | gauge | none | Connections with `globalNetworking` enabled. |

### Plugin / Module Metrics

| Metric | Type | Labels | Notes |
| --- | --- | --- | --- |
| `rsm_plugin_hook_seconds_total` | counter | `plugin` | Monotonic total hook time per plugin. |
| `rsm_module_hook_seconds_total` | counter | `module` | Monotonic total hook time per module. |

### Player Aggregate Metrics

| Metric | Type | Labels | Notes |
| --- | --- | --- | --- |
| `rsm_player_ping_seconds` | histogram | none | Aggregate player ping distribution. |
| `rsm_client_frames_per_second` | histogram | none | Aggregate client FPS distribution. |
| `rsm_client_memory_bytes` | histogram | none | Aggregate client memory distribution. |
| `rsm_player_packet_loss_ratio` | histogram | none | Aggregate player packet loss distribution. |
| `rsm_players_condition_count` | gauge | `condition` | Current affected-player counts by threshold condition. |
| `rsm_player_observation_population` | gauge | `kind` | `ping`, `fps`, `memory`, `packet_loss`. |

### Connection / Auth / Kick Metrics

| Metric | Type | Labels | Notes |
| --- | --- | --- | --- |
| `rsm_connection_attempts_total` | counter | none | New inbound connection attempts. |
| `rsm_connection_failures_total` | counter | `reason` | Authentication-stage failures/rejections. |
| `rsm_auth_rejections_total` | counter | `reason` | Authentication rejection reasons. |
| `rsm_connection_kicks_total` | counter | `reason` | Normalized kick reasons across the network path. |
| `rsm_eac_auth_status` | gauge | `status` | `pending`, `local_ok`, `remote_ok`. |
| `rsm_eac_kicks_total` | counter | `reason` | EAC-attributed kick reasons. |

### RCON Metrics

| Metric | Type | Labels | Notes |
| --- | --- | --- | --- |
| `rsm_rcon_clients` | gauge | none | Connected RCON client count. |
| `rsm_rcon_failed_auth_total` | counter | none | Failed RCON auth attempts. |
| `rsm_rcon_banned_addresses` | gauge | none | Current temporary + persistent banned address/network count. |
| `rsm_rcon_messages_total` | counter | none | Total RCON messages received. |

### Save / Wipe Metrics

| Metric | Type | Labels | Notes |
| --- | --- | --- | --- |
| `rsm_save_in_progress` | gauge | none | `1` while `SaveRestore.IsSaving` is true. |
| `rsm_save_duration_seconds` | gauge | `phase` | `cache`, `write`, `disk`, from the server’s save timings. |
| `rsm_save_entities_count` | gauge | none | Approximate entity count observed when save timings update. |
| `rsm_wipe_info` | gauge | `map_name`, `world_size`, `world_seed`, `wipe_id`, `procedural`, `networked` | Always `1` for the active world identity. |
| `rsm_wipe_time_remaining_seconds` | gauge | none | Time until next wipe, if `WipeTimer` is present. |

### Runtime / Queue Metrics

| Metric | Type | Labels | Notes |
| --- | --- | --- | --- |
| `rsm_runtime_phase_seconds` | gauge | `phase` | `servermgr_update`, `net_cycle`, `physics_sync`, `companion_tick`, `baseplayer_tick`. |
| `rsm_ai_think_queue_depth` | gauge | `queue` | `human`, `animal`, `pets`. |
| `rsm_ai_think_budget_seconds` | gauge | `queue` | `human`, `animal`, `pets`. |
| `rsm_work_queue_depth` | gauge | `queue` | Selected object/persistent work queues only. |
| `rsm_work_queue_execution_seconds_total` | counter | `queue` | Selected work queue cumulative execution time. |
| `rsm_load_balancer_depth` | gauge | none | Current `LoadBalancer.Count()`. |
| `rsm_load_balancer_paused` | gauge | none | `1` if paused. |
| `rsm_animals_total` | gauge | none | `AnimalBrain.Count`. |

The currently exported `rsm_work_queue_depth` / `rsm_work_queue_execution_seconds_total` queue labels are:

- `autoturret_ammo`
- `autoturret_scan`
- `autoturret_tick`
- `battery_discharge`
- `bot_collider`
- `chicken_coop`
- `growable`
- `guntrap_scan`
- `industrial`
- `life_story`
- `relationship_update`
- `solar_update`

### World / Event Metrics

| Metric | Type | Labels | Notes |
| --- | --- | --- | --- |
| `rsm_event_active` | gauge | `event` | `patrol_heli`, `travelling_vendor`, `cargo_ship`, `road_bradleys`. |
| `rsm_event_count` | gauge | `event` | Currently `cargo_ship`, `road_bradleys`. |
| `rsm_cargo_ship_time_remaining_seconds` | gauge | none | Remaining event time for the observed cargo ship. |
| `rsm_cargo_ship_dock_count` | gauge | none | Current cargo ship dock count. |
| `rsm_hackable_crates` | gauge | `state` | `hacking`, `fully_hacked`. |

### Timed Method Families

All of the following families are classic histograms at the coarse label set, plus counters for calls and cumulative duration:

#### Invoke

| Metric | Type | Labels |
| --- | --- | --- |
| `rsm_invoke_duration_seconds` | histogram | `behaviour` |
| `rsm_invoke_calls_total` | counter | `behaviour`, `method` |
| `rsm_invoke_duration_seconds_total` | counter | `behaviour`, `method` |
| `rsm_invoke_method_duration_seconds` | histogram | `behaviour`, `method` |

#### RPC

| Metric | Type | Labels |
| --- | --- | --- |
| `rsm_rpc_duration_seconds` | histogram | `behaviour` |
| `rsm_rpc_calls_total` | counter | `behaviour`, `method` |
| `rsm_rpc_duration_seconds_total` | counter | `behaviour`, `method` |
| `rsm_rpc_method_duration_seconds` | histogram | `behaviour`, `method` |

#### Work Queue

| Metric | Type | Labels |
| --- | --- | --- |
| `rsm_work_queue_duration_seconds` | histogram | `behaviour` |
| `rsm_work_queue_calls_total` | counter | `behaviour`, `method` |
| `rsm_work_queue_duration_seconds_total` | counter | `behaviour`, `method` |
| `rsm_work_queue_method_duration_seconds` | histogram | `behaviour`, `method` |

#### Server Update

| Metric | Type | Labels |
| --- | --- | --- |
| `rsm_server_update_duration_seconds` | histogram | `behaviour` |
| `rsm_server_update_calls_total` | counter | `behaviour`, `method` |
| `rsm_server_update_duration_seconds_total` | counter | `behaviour`, `method` |
| `rsm_server_update_method_duration_seconds` | histogram | `behaviour`, `method` |

#### Time Warning

| Metric | Type | Labels |
| --- | --- | --- |
| `rsm_timewarning_duration_seconds` | histogram | `behaviour` |
| `rsm_timewarning_calls_total` | counter | `behaviour`, `method` |
| `rsm_timewarning_duration_seconds_total` | counter | `behaviour`, `method` |
| `rsm_timewarning_method_duration_seconds` | histogram | `behaviour`, `method` |

#### Console Command

| Metric | Type | Labels |
| --- | --- | --- |
| `rsm_console_command_duration_seconds` | histogram | `command` |
| `rsm_console_command_calls_total` | counter | `command` |
| `rsm_console_command_duration_seconds_total` | counter | `command` |

Notes:

- the `*_method_duration_seconds` histograms are disabled by default and appear only when `ExportMethodHistograms` is enabled
- the non-command timed families always use `behaviour` for coarse histograms and `behaviour,method` for fine counters

## Commands

- `servermetrics.reloadcfg`
- `servermetrics.status`

## Security Notes

- default bind is loopback only
- prefer Prometheus target labels for server identity
- keep the optional debug endpoint private
- do not expose the exporter publicly without network controls

## Related Files

- deployment guide: [INSTALL.md](INSTALL.md)
- operational troubleshooting: [RUNBOOK.md](RUNBOOK.md)
- Prometheus scrape example: [res/Prometheus-Scrape.example.yml](res/Prometheus-Scrape.example.yml)
- Prometheus file_sd example: [res/Prometheus-Targets.example.json](res/Prometheus-Targets.example.json)
- recording rules: [res/Prometheus-RecordingRules.yml](res/Prometheus-RecordingRules.yml)
- Grafana operations dashboard: [res/Grafana-Dashboard.json](res/Grafana-Dashboard.json)
- Grafana diagnostics dashboard: [res/Grafana-Dashboard-Diagnostics.json](res/Grafana-Dashboard-Diagnostics.json)
