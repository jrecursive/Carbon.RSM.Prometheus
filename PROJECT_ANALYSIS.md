# Project Analysis: Carbon.RSM

## Scope

This document is based on static analysis of the repository as checked out on 2026-03-21.

I did not run the module inside a live Rust dedicated server from this workspace because:

- the repo does not include the required Rust/Carbon managed dependency folders (`deps/linux`, `deps/windows`, `carbon`)
- there is no test project in the solution
- the local environment is not set up to run a full .NET/Unity validation pass here

That means the compatibility assessment below is evidence-based, but still static.

## Executive Summary

Yes: this project is clearly intended to run on Linux Rust dedicated servers, but specifically on Rust servers running Carbon, not on a plain standalone Rust server.

Architecturally, it is a Carbon module that installs Harmony patches into Rust server methods, gathers timing and snapshot metrics in-process, serializes them directly into InfluxDB line protocol strings, buffers those strings in memory, and POSTs batched payloads to the InfluxDB v1 `/write` endpoint over HTTP.

The main collection strategy is Harmony-based instrumentation plus periodic polling of existing server state. That is reasonably efficient for a Unity/Facepunch server mod, but the implementation has several important inefficiencies and risks:

- batching is lossy on HTTP/network failure
- the send buffer uses `List<string>` front-removals, which are `O(n)`
- some shared aggregators are not thread-safe
- the code is tightly coupled to InfluxDB v1 line protocol and auth/query conventions
- the schema is intentionally high-cardinality, which makes backend substitution a design problem, not just a serializer swap

## Can It Be Used On Linux Rust Dedicated Servers?

### Short answer

Yes, that is the intended target.

### Evidence

- The README explicitly tells operators to install `Carbon.Linux.RSM.dll` into `carbon/managed/modules` on a Carbon server (`README.md:16-19`).
- The project defines separate `Linux` and `Windows` build configurations, defaults to `Linux`, and changes the assembly name to `Carbon.Linux.RSM` for that configuration (`src/RustServerMetrics/RustServerMetrics.csproj:10-18`, `src/RustServerMetrics/RustServerMetrics.csproj:25-34`).
- The build scripts fetch Linux Rust dedicated managed assemblies from Steam and place them under `deps/linux` (`update-lin-dependencies.bat:1-3`).
- The Azure pipeline builds the `Linux` configuration (`azure-pipelines.yml:13-35`).

### Important caveats

- This is not a generic Rust plugin. It depends on Carbon package/module loading and Carbon runtime APIs such as `IModulePackage`, `Carbon.Community`, and Carbon Harmony integration (`src/RustServerMetrics/RustServerMetricsLoader.cs:12-18`, `src/RustServerMetrics/RustServerMetricsLoader.cs:43-53`, `src/RustServerMetrics/HarmonyPatches/OxideMod_OnFrame_Patch.cs:63-115`).
- It targets `.NET Framework 4.8` and references Rust/Unity/Carbon managed assemblies directly (`src/RustServerMetrics/RustServerMetrics.csproj:7-22`, `src/RustServerMetrics/RustServerMetrics.csproj:53-157`).
- It is also explicitly written around InfluxDB v1.x. The README says v2+ is not compatible, and the code writes to `/write?db=...&precision=ms&u=...&p=...` (`README.md:7`, `src/RustServerMetrics/MetricsLogger.cs:61-70`).

### Practical conclusion

If you have:

- a Linux Rust dedicated server
- Carbon installed
- compatible Rust managed dependencies for the same server build
- InfluxDB v1.8 or another backend that can accept the same line protocol write API

then this module is architected to run there.

## General Architecture

At a high level the flow is:

```text
Carbon module loader
  -> Harmony patches game/server methods
  -> MetricsLogger singleton receives events/timings/snapshots
  -> metrics serialized into Influx line protocol strings
  -> ReportUploader buffers strings in memory
  -> ReportUploader batches buffered entries into HTTP POST payloads
  -> InfluxDB v1 /write endpoint
```

### Main components

#### 1. Module bootstrap and lifecycle

- `RustServerMetricsLoader` is the Carbon module entrypoint (`src/RustServerMetrics/RustServerMetricsLoader.cs:12-66`).
- On load, it subscribes to a Carbon runtime event and patches the assembly via Harmony (`src/RustServerMetrics/RustServerMetricsLoader.cs:43-53`).
- A transpiler on `Bootstrap.StartServer` ensures `MetricsLogger.Initialize()` is called at server startup (`src/RustServerMetrics/HarmonyPatches/Bootstrap_StartServer_Patch.cs:8-25`).
- A postfix on `ServerMgr.OpenConnection` marks the server as started and applies the delayed Harmony patches that need the server/runtime to already exist (`src/RustServerMetrics/HarmonyPatches/ServerMgr_OpenConnection_Patch.cs:5-12`, `src/RustServerMetrics/MetricsLogger.cs:81-103`).

#### 2. Metrics collection coordinator

- `MetricsLogger` is the in-process singleton that owns configuration, recurring timers, player sampling state, network counters, measurement helpers, and the uploader (`src/RustServerMetrics/MetricsLogger.cs:17-72`).
- It starts recurring aggregation flushes with `InvokeRepeating()` when configuration is valid and enabled (`src/RustServerMetrics/MetricsLogger.cs:105-135`).

#### 3. Timing aggregation helper

- `MetricsTimeStorage<TKey>` stores per-key accumulated duration totals in a dictionary and flushes one point per key once per second (`src/RustServerMetrics/HarmonyPatches/Utility/MetricsTimeStorage.cs:8-59`).
- This helper is reused for:
  - `invoke_execution`
  - `rpc_calls`
  - `work_queue`
  - `server_update`
  - `timewarnings`
  - `console_commands`

#### 4. Transport

- `ReportUploader` owns the in-memory send buffer, payload construction, retry loop, and HTTP writes using `UnityWebRequest` (`src/RustServerMetrics/ReportUploader.cs:10-180`).

## Main Method Of Metrics Collection

The dominant pattern is Harmony instrumentation of existing Rust server methods.

### Timing metrics

For timing-oriented metrics, the code patches target methods, measures elapsed time, and adds the duration to a per-key in-memory accumulator. That accumulator is flushed once per second.

Examples:

- `InvokeHandlerBase<InvokeHandler>.DoTick` is rewritten so invoked actions run through `InvokeWrapper`, which measures duration with a shared `Stopwatch` and records the action method (`src/RustServerMetrics/HarmonyPatches/Delayed/InvokeHandlerBase_DoTick_Patch.cs:50-94`).
- All `[RPC_Server]` methods in the Rust game assembly are scanned and instrumented (`src/RustServerMetrics/HarmonyPatches/Delayed/RPCServer_Attribute_Method_Patch.cs:28-80`).
- All `ObjectWorkQueue`-derived `RunJob` methods are scanned and instrumented (`src/RustServerMetrics/HarmonyPatches/Delayed/ObjectWorkQueue_RunJob_Patch.cs:28-80`).
- A hardcoded list of high-value server loop/update methods is instrumented for `server_update` (`src/RustServerMetrics/HarmonyPatches/Delayed/ServerMgr_Metrics_Patches.cs:28-77`).
- `ConsoleSystem.Internal` is timed and tagged by command name (`src/RustServerMetrics/HarmonyPatches/Delayed/ConsoleSystem_Internal_Patch.cs:24-45`).
- Arbitrary methods can also be patched dynamically through `AddModTimeWarnings()`, which feeds the `timewarnings` measurement (`src/RustServerMetrics/RustServerMetricsLoader.cs:19-35`, `src/RustServerMetrics/ModTimeWarnings.cs:12-57`).

### Snapshot and poll metrics

Not everything is measured through Harmony timings.

- `Performance.FPSTimer` triggers periodic emission of server-wide snapshot metrics like framerate, frametime, memory, tasks, network totals, player counts, and entity counts (`src/RustServerMetrics/HarmonyPatches/Performance_FPSTimer_Patch.cs:5-12`, `src/RustServerMetrics/MetricsLogger.cs:336-428`).
- Player metrics are sampled on a per-player repeating timer after `BasePlayer.PlayerInit` and cancelled on disconnect (`src/RustServerMetrics/HarmonyPatches/BasePlayer_PlayerInit_Patch.cs:9-35`, `src/RustServerMetrics/HarmonyPatches/BasePlayer_OnDisconnected_Patch.cs:8-30`, `src/RustServerMetrics/MetricsLogger.cs:140-158`).
- Every player second, the module writes server-observed ping/packet-loss and requests a client performance report every 5th tick (`src/RustServerMetrics/MetricsLogger.cs:243-272`).
- Client performance reports are intercepted inside `BasePlayer.PerformanceReport`, filtered by request id, and converted into `client_performance` points (`src/RustServerMetrics/HarmonyPatches/BasePlayer_PerformanceReport_Patch.cs:14-47`, `src/RustServerMetrics/MetricsLogger.cs:225-240`).
- Plugin/module hook totals are read from Carbon runtime once per second from `CarbonProcessor.Update` (`src/RustServerMetrics/HarmonyPatches/OxideMod_OnFrame_Patch.cs:72-115`).
- Network traffic by `Message.Type` is derived by pairing `NetWrite.PacketID` with `NetWrite.Send` and then flushing counters every 0.5 seconds (`src/RustServerMetrics/HarmonyPatches/NetWrite_PacketID_Patch.cs:6-13`, `src/RustServerMetrics/HarmonyPatches/NetWrite_Send_Patch.cs:6-13`, `src/RustServerMetrics/MetricsLogger.cs:160-189`, `src/RustServerMetrics/MetricsLogger.cs:275-333`).

## What Metrics It Tracks

### Aggregated timing families

- `invoke_execution`: total time spent in scheduled invoke actions, keyed by declaring type and method
- `rpc_calls`: total time spent in `[RPC_Server]` methods, keyed by type and method
- `work_queue`: total time spent in `ObjectWorkQueue` jobs, keyed by type and method
- `server_update`: total time spent in selected core server loops such as `ServerMgr.Update`, `BasePlayer.ServerCycle`, `Raknet.Server.Cycle`, etc.
- `timewarnings`: total time spent in dynamically-added time warning methods
- `console_commands`: total time spent executing console commands, keyed by full command name

### Point or snapshot families

- `framerate`
- `frametime`
- `memory`
- `tasks`
- `network`
- `players`
- `entities`
- `network_updates`
- `oxide_plugins`
- `carbon_modules`
- `client_performance`
- `connection_latency`

### Important interpretation detail

Several of these are not per-invocation latency points. They are one-second bucket totals.

For example, if `ServerMgr.Update` runs many times in one second, `server_update` stores the sum of all elapsed milliseconds for that method over that one-second window, not a single call duration, not a mean, and not a percentile.

The plugin/module hook metrics are different again: they sample Carbon's cumulative `TotalHookTime` counters once per second and write those cumulative values. The bundled Grafana dashboard then uses `derivative(1s)` to turn them into per-second rates (`src/RustServerMetrics/HarmonyPatches/OxideMod_OnFrame_Patch.cs:89-115`, `res/Grafana-Dashboard.json` around the `oxide_plugins` and `carbon_modules` panels).

## Is It Efficient?

### What it does well

- It avoids direct database drivers and instead uses simple HTTP writes to InfluxDB.
- It reuses `StringBuilder` instances in hot paths (`src/RustServerMetrics/MetricsLogger.cs:20-23`, `src/RustServerMetrics/ReportUploader.cs:17-18`).
- It aggregates many timing measurements locally before writing them, which reduces point volume.
- It randomizes initial `InvokeRepeating()` offsets to avoid synchronized spikes (`src/RustServerMetrics/MetricsLogger.cs:125-134`, `src/RustServerMetrics/MetricsLogger.cs:144-148`).
- It groups several server snapshot measurements into one queued multi-line payload string per performance tick (`src/RustServerMetrics/MetricsLogger.cs:347-428`).

### Where it is only moderately efficient

- The code builds line protocol strings at the collection site instead of collecting structured metric objects and serializing later. That is simple, but it couples collection and transport and can generate a lot of string churn.
- Per-player sampling creates one repeating invoke per player and requests client performance reports every ~5 seconds. On large-pop servers, that is materially more expensive than purely server-side counters (`src/RustServerMetrics/MetricsLogger.cs:140-148`, `src/RustServerMetrics/MetricsLogger.cs:243-256`).
- Timing instrumentation uses `DateTime.UtcNow` in several hot-path transpilers instead of a monotonic timer. That is serviceable, but it is not the cheapest possible timing primitive (`src/RustServerMetrics/ModTimeWarnings.cs:32-56`, `src/RustServerMetrics/HarmonyPatches/Delayed/RPCServer_Attribute_Method_Patch.cs:52-80`, `src/RustServerMetrics/HarmonyPatches/Delayed/ObjectWorkQueue_RunJob_Patch.cs:53-80`, `src/RustServerMetrics/HarmonyPatches/Delayed/ServerMgr_Metrics_Patches.cs:52-77`).

### The biggest efficiency and correctness issues

- `ReportUploader` stores buffered points in `List<string>` and repeatedly removes from the front with `RemoveAt(0)` and `RemoveRange(0, amountToTake)`. Those are `O(n)` operations and become increasingly expensive as the buffer grows (`src/RustServerMetrics/ReportUploader.cs:62-67`, `src/RustServerMetrics/ReportUploader.cs:78-99`).
- The high-cardinality schema is intentionally expensive for InfluxDB. The README explicitly tells operators to disable tag/series limits because player metrics produce large cardinality (`README.md:7-13`).
- Some shared state appears unsynchronized even though at least one source of metrics, `ObjectWorkQueue.RunJob`, likely does not guarantee single-threaded execution. `MetricsTimeStorage<TKey>.dict` is a plain `Dictionary<TKey,double>` with no locking (`src/RustServerMetrics/HarmonyPatches/Utility/MetricsTimeStorage.cs:10-12`, `src/RustServerMetrics/HarmonyPatches/Utility/MetricsTimeStorage.cs:21-33`).

### Overall assessment

For a server-side Harmony mod, the design is pragmatic and probably "good enough" on many servers, but I would not call it especially robust or especially efficient under sustained high load. It is optimized more for implementation simplicity than for hard guarantees around correctness, losslessness, or concurrency.

## How It Aggregates Data Points

There are three different aggregation styles in the codebase.

### 1. One-second duration accumulation

`MetricsTimeStorage<TKey>` does:

- key lookup in a dictionary
- `dict[key] += milliseconds`
- once per second, emit one line per key
- clear the dictionary

So a one-second bucket might produce:

```text
rpc_calls,server=myserver,behaviour="BasePlayer",method="RPC_Auth" duration=84.5 1710000000000
```

That means "84.5 ms total spent in this method during the last flush window", not "84.5 ms for one call".

### 2. Counter flushes

`network_updates` accumulates per-message-type count and bytes in mutable objects and flushes all of them into one point every 0.5 seconds, then zeros the counters (`src/RustServerMetrics/MetricsLogger.cs:275-333`).

This is one of the more write-efficient parts of the design because it uses fields instead of many separate tagged points.

### 3. Immediate point creation

Some sources just create points directly as events or snapshots:

- `client_performance`
- `connection_latency`
- `oxide_plugins`
- `carbon_modules`
- the snapshot group emitted by `OnPerformanceReportGenerated()`

## How It Batches And Submits To The Database

### Actual submission path

There is no direct DB driver in this repo. The module submits metrics by HTTP POST to the InfluxDB v1 write endpoint:

```text
/write?db=<db>&precision=ms&u=<user>&p=<password>
```

That URI is assembled in `MetricsLogger.BaseUri` and on config reload (`src/RustServerMetrics/MetricsLogger.cs:61-70`, `src/RustServerMetrics/MetricsLogger.cs:604-624`).

### Buffering behavior

- Every metric is converted to one string payload entry and appended to `_sendBuffer`
- `_sendBuffer` is capped at 100,000 entries
- if the buffer is full, the oldest entry is dropped
- if the uploader is idle, adding an entry starts the send coroutine

This is implemented in `ReportUploader.AddToSendBuffer()` (`src/RustServerMetrics/ReportUploader.cs:62-71`).

### Batch formation

`SendBufferLoop()`:

- takes up to `BatchSize` buffered entries
- concatenates them with newline separators
- converts the concatenated text to UTF-8 bytes
- POSTs that byte array via `UnityWebRequest`

See `src/RustServerMetrics/ReportUploader.cs:73-99`.

### Important batching detail

`BatchSize` is the number of buffered string entries, not necessarily the number of individual metric lines.

That matters because some queued entries already contain multiple line-protocol records separated by `\n`. `OnPerformanceReportGenerated()` is the clearest example: it builds one queued string containing multiple measurements (`framerate`, `frametime`, `memory`, `tasks`, `network`, `players`, `entities`) (`src/RustServerMetrics/MetricsLogger.cs:347-428`).

So the README's wording that the batch size controls the exact number of "individual statistics records" is only approximately true (`README.md:65-70`).

### Retry and loss semantics

This is the most important operational behavior in the entire repo:

- the uploader removes entries from the buffer before the HTTP request succeeds (`src/RustServerMetrics/ReportUploader.cs:80-87`)
- network failures are retried up to two more times (`src/RustServerMetrics/ReportUploader.cs:115-135`)
- HTTP failures are not retried (`src/RustServerMetrics/ReportUploader.cs:137-152`)
- failed batches are not requeued

That means any batch that ultimately fails is simply lost.

The module is therefore lossy in two different situations:

- when the send buffer overflows
- when an HTTP/network submission fails after removal from the queue

## General Form Of Adding A New Metric

There are three realistic extension patterns.

### Pattern 1: Add a new timed method family

Use this when you want "total time spent in method X per second".

1. Add a new `MetricsTimeStorage<TKey>` field to `MetricsLogger`.
2. Register its `SerializeToStringBuilder` flush in `StartLoggingMetrics()`.
3. Create a Harmony patch that measures elapsed time around the target method(s).
4. Call `YourStorage.LogTime(key, elapsedMs)` from the postfix/wrapper.
5. Choose a stable serializer that maps the key into tags.

This is the pattern used by `ServerInvokes`, `ServerRpcCalls`, `WorkQueueTimes`, `ServerUpdate`, `TimeWarnings`, and `ServerConsoleCommands`.

### Pattern 2: Add a direct event/snapshot metric

Use this when you already have the values and just need to emit a point.

1. Decide where the metric should be observed.
2. Hook or poll that location.
3. Call `MetricsLogger.UploadPacket("measurement_name", data, serializer)`.
4. In the serializer, append tags and fields to the provided `StringBuilder`.

This is the pattern used by `client_performance`, `connection_latency`, `oxide_plugins`, and `carbon_modules`.

### Pattern 3: Extend the periodic server snapshot batch

Use this when the new metric belongs with the once-per-tick server snapshot group.

1. Add another measurement block in `OnPerformanceReportGenerated()`.
2. Append the new line to the shared `_stringBuilder`.
3. Let the existing batched enqueue call send it with the rest of the performance snapshot payload.

### What you have to be careful about

- preserve valid Influx line protocol formatting
- preserve tag stability and cardinality expectations
- decide whether the metric should be a cumulative counter, a one-second bucket total, or a direct point
- do not add high-frequency metrics as one-line-per-call unless you really want the resulting write volume

## Can It Be Generified For Another Backend?

### Short answer

Yes, but not by a tiny adapter. It needs a moderate refactor.

### Why it is not generic today

The repo is coupled to InfluxDB in several places:

- config names are explicitly Influx-specific (`Influx Database Url`, `Influx Database Name`, etc.) (`src/RustServerMetrics/Config/ConfigData.cs:16-35`)
- the write URI is hardcoded to the InfluxDB v1 `/write` API (`src/RustServerMetrics/MetricsLogger.cs:61-70`)
- every collector builds raw Influx line protocol strings directly
- batching is defined in terms of string payload entries, not abstract metric points
- the tag-heavy schema assumes a backend that tolerates high cardinality

### What a real generalization would look like

I would split the design into these layers:

1. `MetricPoint`
   - measurement name
   - timestamp
   - tags
   - fields

2. `IMetricSink`
   - `Enqueue(MetricPoint point)`
   - maybe `EnqueueBatch(IEnumerable<MetricPoint>)`

3. `IMetricBatchWriter`
   - turns buffered `MetricPoint` objects into backend-specific payloads
   - examples: Influx line protocol, JSON over HTTP, OpenTelemetry exporter, SQL batch insert, Kafka producer

4. `ITransport`
   - HTTP transport, DB transport, or file transport

5. backend-specific config objects
   - keep auth/endpoint/retention/backend concerns out of collectors

### What would stay reusable

- almost all Harmony patches
- almost all observation points
- the one-second aggregation concept in `MetricsTimeStorage<TKey>`
- the high-level lifecycle around `MetricsLogger`

### What would need redesign, not just refactoring

- the metric schema if the target backend dislikes high-cardinality tags
- batching rules, if the backend wants payload size limits instead of point-count limits
- auth/transport, since the current implementation embeds credentials in the query string

### Practical answer

If the target backend is "another line-protocol-over-HTTP time-series system", the refactor is straightforward.

If the target backend is something like Prometheus, Mimir, CloudWatch, or a relational DB, the data model itself should be reconsidered first. This project currently behaves more like an event/time-series writer than a Prometheus-style metrics exporter.

## Commentary: Pertinent Findings, Quirks, And Improvement Ideas

### 1. The uploader is intentionally lossy

The current design drops data on both buffer overflow and failed upload. If you want operational reliability, this is the first place to change.

Suggested improvement:

- use a real queue or ring buffer
- remove items only after confirmed success
- optionally requeue failed batches with backoff

### 2. `connection_latency` appears to produce malformed line protocol

`UploadPacket()` already appends the separator before the timestamp (`src/RustServerMetrics/MetricsLogger.cs:445-458`), but the `connection_latency` serializer also appends a trailing space after `packet_loss` (`src/RustServerMetrics/MetricsLogger.cs:267-271`).

That yields two spaces between the field set and timestamp. Influx line protocol is whitespace-sensitive and defines the second unescaped space as the timestamp delimiter, so this formatting appears invalid or at least fragile:

- official reference: https://docs.influxdata.com/influxdb/v1/write_protocols/line_protocol_reference/

This is the single clearest bug candidate I found.

### 3. The buffer data structure is the wrong one for the workload

`List<string>` plus front-removal is a poor fit for queue semantics at this scale. A `Queue<string>`, custom ring buffer, or `Channel<T>`-style structure would be materially better.

### 4. Thread-safety is questionable

`MetricsTimeStorage<TKey>` uses a normal dictionary with no synchronization. That is probably fine for purely main-thread metrics, but `ObjectWorkQueue.RunJob` is suspicious enough that I would not assume single-threaded safety without verifying the underlying Rust implementation.

Suggested improvement:

- switch to thread-safe accumulation or explicit main-thread marshaling before writes

### 5. Network update attribution is fragile

`NetWrite.PacketID` writes to one shared `_lastMessageType`, then `NetWrite.Send` reads that shared field later (`src/RustServerMetrics/MetricsLogger.cs:160-189`).

If multiple `NetWrite` instances interleave, attribution can be wrong. It would be safer to associate the message type with the `NetWrite` instance instead of global mutable state.

### 6. Tag escaping is incomplete

The code manually builds line protocol, but user-provided or runtime-provided tag values are not consistently escaped.

Examples:

- `serverTag`
- `ip`
- console command names
- dynamically generated method names

Plugin/module names are "sanitized" by stripping underscores and non-word characters instead of escaping them (`src/RustServerMetrics/MetricsLogger.cs:20`, `src/RustServerMetrics/MetricsLogger.cs:196-221`), which can cause collisions between distinct names.

### 7. Credentials are put in the URI query string

This is simple, but it is not ideal. It increases the chance of credentials leaking through logs, proxies, or diagnostics.

### 8. The repo has CI but no actual tests

The pipeline runs `VSTest`, but the solution contains only one project and no test project (`azure-pipelines.yml:36-39`, `RustServerMetrics.sln`).

That means regression safety currently depends on manual/runtime validation.

### 9. The naming is slightly misleading in a few places

- `OxideMod_OnFrame_Patch` is really Carbon-oriented and targets `CarbonProcessor.Update`, not an Oxide frame hook.
- `_requestedClientPerf` exists but is only removed from; it is not actually used to gate requests/responses (`src/RustServerMetrics/MetricsLogger.cs:54`, `src/RustServerMetrics/MetricsLogger.cs:239`).

### 10. The architecture is simple in a good way

Even with the flaws above, the repo is easy to understand once you find the flow:

- patch
- observe
- aggregate
- stringify
- buffer
- POST

That simplicity is also why it is a reasonable candidate for cleanup and backend abstraction. The hard part is not the control flow. The hard part is decoupling the current Influx-first data model and making the buffering reliable.

## Bottom Line

This project is a Linux-capable Carbon module for Rust dedicated servers that gathers metrics primarily through Harmony-based in-process instrumentation and periodic polling, aggregates many timing metrics into one-second buckets, and ships them to InfluxDB v1 over HTTP as line protocol.

It is workable and fairly direct, but it is tightly bound to InfluxDB, intentionally high-cardinality, and currently has some real reliability/correctness issues around buffering, formatting, and shared mutable state. If you wanted to keep using InfluxDB v1, I would treat it as salvageable with a focused hardening pass. If you wanted a truly generic backend architecture, I would refactor toward structured metric points and a pluggable sink/writer layer before adding any new backend.
