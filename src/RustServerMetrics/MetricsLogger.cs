using HarmonyLib;
using Network;
using Newtonsoft.Json;
using Carbon;
using Facepunch;
using Facepunch.Rust;
using Facepunch.Rust.Profiling;
using RustServerMetrics.Config;
using RustServerMetrics.HarmonyPatches.Utility;
using RustServerMetrics.PrometheusMetrics;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace RustServerMetrics;

public sealed class MetricsLogger : SingletonComponent<MetricsLogger>
{
    private const string ConfigurationPath = "HarmonyMods_Data/ServerMetrics/Configuration.json";
    private static readonly FieldInfo SnapshotQueueField = typeof(BasePlayer).GetField("SnapshotQueue", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo SnapshotQueueLengthProperty = SnapshotQueueField?.FieldType.GetProperty("Length", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly FieldInfo SaveTimingField = typeof(PerformanceLogging).GetField("pendingTimings", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo EacConnectionStatusField = typeof(EACServer).GetField("connection2status", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo RconBannedAddressesField = typeof(RCon).GetField("bannedAddresses", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo RconListenerNewField = typeof(RCon).GetField("listenerNew", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo CargoShipLifetimeField = typeof(CargoShip).GetField("lifetime", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo CargoShipDockCountField = typeof(CargoShip).GetField("dockCount", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo GetBannedNetworksMethod = Type.GetType("Facepunch.Rcon.Listener, Facepunch.Rcon")?.GetMethod("GetBannedNetworks", BindingFlags.Instance | BindingFlags.Public);
    private static readonly Dictionary<string, string> WorkQueueLabelMap = new(StringComparer.Ordinal)
    {
        ["UpdateAutoTurretScanQueue"] = "autoturret_scan",
        ["UpdateAutoTurretAmmoQueue"] = "autoturret_ammo",
        ["UpdateAutoTurretTick"] = "autoturret_tick",
        ["GunTrapScanWorkQueue"] = "guntrap_scan",
        ["IndustrialProcessQueue"] = "industrial",
        ["GrowableEntityUpdateQueue"] = "growable",
        ["ChickenCoopWorkQueue"] = "chicken_coop",
        ["DischargeWorkQueue"] = "battery_discharge",
        ["SunUpdateWorkQueue"] = "solar_update",
        ["BotColliderWorkQueue"] = "bot_collider",
        ["LifeStoryWorkQueue"] = "life_story",
        ["RelationshipUpdateQueue"] = "relationship_update"
    };
    private static readonly string[] WorkQueueLabels = WorkQueueLabelMap.Values.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
    private static readonly string[] SavePhases = { "cache", "write", "disk" };
    private static readonly string[] RuntimePhases = { "servermgr_update", "net_cycle", "physics_sync", "companion_tick", "baseplayer_tick" };
    private static readonly string[] AiQueues = { "human", "animal", "pets" };
    private static readonly string[] EventActiveLabels = { "patrol_heli", "travelling_vendor", "cargo_ship", "road_bradleys" };
    private static readonly string[] EventCountLabels = { "cargo_ship", "road_bradleys" };

    private sealed class MessageTypeReference
    {
        public Message.Type Value;
    }

    private readonly Dictionary<ulong, Action> _playerStatsActions = new();
    private readonly Dictionary<ulong, uint> _perfReportDelayCounter = new();
    private readonly Dictionary<string, double> _lastPluginHookSeconds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _lastModuleHookSeconds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _pluginLastSeenUtc = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _moduleLastSeenUtc = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _lastWorkQueueExecutionSeconds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lastSaveTimingsMilliseconds = new(StringComparer.Ordinal);
    private readonly int _performanceReportRequestId = UnityEngine.Random.Range(int.MinValue, int.MaxValue);

    private ConditionalWeakTable<NetWrite, MessageTypeReference> _netWriteTypes = new();
    private PrometheusExporterHost _exporterHost;
    private DebugEndpointHost _debugEndpointHost;
    private MetricRegistry _registry;
    private MetricFactory _metricFactory;
    private MetricGuardrails _guardrails;
    private PlayerObservationStore _playerObservations = new();
    private Dictionary<TimedMetricKind, TimedMetricFamily> _timedFamilies = new();
    private readonly ExpiringSeriesTracker _pluginSeries = new();
    private readonly ExpiringSeriesTracker _moduleSeries = new();
    private readonly ExpiringSeriesTracker _networkUpdateSeries = new();
    private readonly HashSet<string> _knownPlayerConditionLabels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownPlayerPopulationKinds = new(StringComparer.Ordinal);
    private PlayerAggregateSnapshot _lastPlayerAggregateSnapshot = new();
    private Process _currentProcess;
    private bool _firstPerformanceReportGenerated;
    private bool _startupPatchesApplied;
    private long _lastGcCollections = -1;
    private long _lastNetworkBytesReceived = -1;
    private long _lastNetworkBytesSent = -1;
    private long _lastRconMessageCount = -1;
    private long _lastRconFailedAuthCount = -1;
    private string[] _currentWipeInfoLabelValues;

    private Gauge _exporterBuildInfo;
    private Counter _exporterCollectErrorsTotal;
    private Counter _exporterSeriesDroppedTotal;
    private Gauge _exporterLastSnapshotSuccessTimestampSeconds;
    private Histogram _exporterSnapshotDurationSeconds;
    private Gauge _exporterLastSaveTimestampSeconds;

    private Gauge _serverFramesPerSecond;
    private Gauge _serverFrametimeSeconds;
    private Gauge _memoryUsedBytes;
    private Counter _gcCollectionsTotal;
    private Gauge _taskQueueDepth;
    private Gauge _players;
    private Gauge _entitiesCount;
    private Counter _networkBytesTotal;
    private Gauge _networkPacketLossRatio;
    private Counter _pluginHookSecondsTotal;
    private Counter _moduleHookSecondsTotal;
    private Counter _networkUpdatesTotal;
    private Counter _networkUpdateBytesTotal;
    private Histogram _playerPingSeconds;
    private Histogram _clientFramesPerSecond;
    private Histogram _clientMemoryBytes;
    private Histogram _playerPacketLossRatio;
    private Gauge _playersConditionCount;
    private Gauge _playerObservationPopulation;
    private Counter _connectionAttemptsTotal;
    private Counter _connectionFailuresTotal;
    private Counter _authRejectionsTotal;
    private Gauge _connections;
    private Gauge _snapshotQueueDepth;
    private Gauge _networkQueueDepth;
    private Gauge _networkQueueBytes;
    private Gauge _connectionQueueDepth;
    private Gauge _saveInProgress;
    private Gauge _saveDurationSeconds;
    private Gauge _saveEntitiesCount;
    private Gauge _wipeInfo;
    private Gauge _wipeTimeRemainingSeconds;
    private Gauge _rconClients;
    private Counter _rconFailedAuthTotal;
    private Gauge _rconBannedAddresses;
    private Counter _rconMessagesTotal;
    private Gauge _eacAuthStatus;
    private Counter _eacKicksTotal;
    private Gauge _runtimePhaseSeconds;
    private Gauge _aiThinkQueueDepth;
    private Gauge _aiThinkBudgetSeconds;
    private Gauge _workQueueDepth;
    private Counter _workQueueExecutionSecondsTotal;
    private Gauge _loadBalancerDepth;
    private Gauge _loadBalancerPaused;
    private Gauge _globalNetworkEntitiesCount;
    private Gauge _globalNetworkConnections;
    private Counter _connectionKicksTotal;
    private Gauge _eventActive;
    private Gauge _eventCount;
    private Gauge _cargoShipTimeRemainingSeconds;
    private Gauge _cargoShipDockCount;
    private Gauge _hackableCrates;
    private Gauge _animalsTotal;

    internal readonly MetricsTimeStorage<MethodInfo> ServerInvokes = new(
        TimedMetricKind.Invoke,
        info => new TimedMetricLabels(info?.DeclaringType?.Name ?? "unknown", info?.Name ?? "unknown"));

    internal readonly MetricsTimeStorage<string> ServerRpcCalls = new(TimedMetricKind.Rpc, ParseTimedMethodName);
    internal readonly MetricsTimeStorage<string> WorkQueueTimes = new(TimedMetricKind.WorkQueue, ParseTimedMethodName);
    internal readonly MetricsTimeStorage<string> ServerUpdate = new(TimedMetricKind.ServerUpdate, ParseTimedMethodName);
    internal readonly MetricsTimeStorage<string> TimeWarnings = new(TimedMetricKind.TimeWarning, ParseTimedMethodName);
    internal readonly MetricsTimeStorage<string> ServerConsoleCommands = new(
        TimedMetricKind.ConsoleCommand,
        command => new TimedMetricLabels(command ?? "unknown"));

    public bool Ready { get; private set; }
    internal ConfigData Configuration { get; private set; }

    internal static void Initialize()
    {
        new GameObject().AddComponent<MetricsLogger>();
    }

    public override void Awake()
    {
        base.Awake();
        RegisterCommands();
        LoadConfiguration();
        ApplyConfiguration();
    }

    public override void OnDestroy()
    {
        StopRuntime();
    }

    internal void OnServerStarted()
    {
        if (_startupPatchesApplied)
        {
            RustServerMetricsLoader.__serverStarted = true;
            return;
        }

        RustServerMetricsLoader.__serverStarted = true;
        _startupPatchesApplied = true;

        Debug.Log("[ServerMetrics]: Applying startup patches");
        var assembly = GetType().Assembly;

        var harmonyInstance = HarmonyLoader.loadedMods.FirstOrDefault(x => x.Assembly == assembly)?.Harmony.harmonyObject;
        if (harmonyInstance == null)
        {
            RustServerMetricsLoader.__harmonyInstance ??= new Harmony("RustServerMetricsPATCH");
            harmonyInstance = RustServerMetricsLoader.__harmonyInstance;
        }

        foreach (var nestedType in assembly.GetTypes())
        {
            if (nestedType.GetCustomAttribute<DelayedHarmonyPatchAttribute>(false) == null)
            {
                continue;
            }

            var patchProcessor = new PatchClassProcessor((Harmony)harmonyInstance, nestedType);
            Debug.Log(patchProcessor.Patch() == null
                ? $"[ServerMetrics]: Failed to apply patch: {nestedType.Name}"
                : $"[ServerMetrics]: Applied startup patch: {nestedType.Name}");
        }

        if (!Ready || !Configuration.ExportPlayerAggregateMetrics)
        {
            return;
        }

        foreach (var player in BasePlayer.activePlayerList)
        {
            OnPlayerInit(player);
        }
    }

    internal void OnPlayerInit(BasePlayer player)
    {
        if (!Ready || !Configuration.ExportPlayerAggregateMetrics || player == null)
        {
            return;
        }

        var action = new Action(() => GatherPlayerSecondStats(player));

        if (_playerStatsActions.TryGetValue(player.userID, out var existingAction))
        {
            player.CancelInvoke(existingAction);
        }

        _playerStatsActions[player.userID] = action;
        player.InvokeRepeating(action, UnityEngine.Random.Range(0.5f, 1.5f), 1f);
    }

    internal void OnPlayerDisconnected(BasePlayer player)
    {
        if (player == null)
        {
            return;
        }

        if (_playerStatsActions.TryGetValue(player.userID, out var action))
        {
            player.CancelInvoke(action);
        }

        _playerStatsActions.Remove(player.userID);
        _perfReportDelayCounter.Remove(player.userID);
        _playerObservations.Remove(player.userID);
    }

    internal void OnConnectionAttempt()
    {
        if (!Ready || !Configuration.ExportConnectionDiagnostics)
        {
            return;
        }

        _connectionAttemptsTotal.Inc();
    }

    internal void OnConnectionRejected(string reason)
    {
        if (!Ready || !Configuration.ExportConnectionDiagnostics)
        {
            return;
        }

        var resolvedReason = NormalizeReasonLabel(reason);
        _connectionFailuresTotal.WithLabels(resolvedReason).Inc();
        _authRejectionsTotal.WithLabels(resolvedReason).Inc();
    }

    internal void OnConnectionKick(Connection connection, string reason)
    {
        if (!Ready)
        {
            return;
        }

        var normalizedReason = NormalizeReasonLabel(reason);
        _connectionKicksTotal.WithLabels(normalizedReason).Inc();

        if (!string.IsNullOrWhiteSpace(connection?.authStatusEAC) || (reason?.StartsWith("EAC:", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            var eacReason = !string.IsNullOrWhiteSpace(connection?.authStatusEAC)
                ? NormalizeReasonLabel(connection.authStatusEAC)
                : NormalizeReasonLabel(reason);
            _eacKicksTotal.WithLabels(eacReason).Inc();
        }
    }

    internal void OnNetWritePacketID(NetWrite write, Message.Type messageType)
    {
        if (!Ready || write == null)
        {
            return;
        }

        _netWriteTypes.Remove(write);
        _netWriteTypes.Add(write, new MessageTypeReference { Value = messageType });
    }

    internal void OnNetWriteSend(NetWrite write, SendInfo sendInfo)
    {
        if (!Ready || write == null)
        {
            return;
        }

        // TODO: Add a live-server regression test for PacketID -> Send pairing when doing a dedicated runtime test pass.
        string messageType = "unknown";

        if (_netWriteTypes.TryGetValue(write, out var reference))
        {
            messageType = reference.Value.ToString();
            _netWriteTypes.Remove(write);
        }

        var connectionCount = 0;
        if (sendInfo.connection != null)
        {
            connectionCount = 1;
        }
        else if (sendInfo.connections != null)
        {
            connectionCount = sendInfo.connections.Count;
        }

        if (connectionCount < 1)
        {
            return;
        }

        var resolvedMessageType = _guardrails.ResolveMessageType(messageType);
        var totalBytes = Convert.ToDouble(write.Length) * connectionCount;
        var nowUtc = DateTime.UtcNow;

        _networkUpdatesTotal.WithLabels(resolvedMessageType).Inc(connectionCount);
        _networkUpdateBytesTotal.WithLabels(resolvedMessageType).Inc(totalBytes);
        _networkUpdateSeries.Touch(new[] { resolvedMessageType }, nowUtc, labels =>
        {
            _networkUpdatesTotal.RemoveLabelled(labels);
            _networkUpdateBytesTotal.RemoveLabelled(labels);
        });
    }

    internal void OnOxidePluginMetrics(Dictionary<string, double> metrics)
    {
        if (!Ready || metrics == null || metrics.Count == 0)
        {
            return;
        }

        RunCollector("plugin_snapshot", () =>
        {
            var nowUtc = DateTime.UtcNow;

            foreach (var item in metrics)
            {
                var rawName = string.IsNullOrWhiteSpace(item.Key) ? "unknown" : item.Key.Trim();
                var currentTotal = item.Value;
                _pluginLastSeenUtc[rawName] = nowUtc;

                if (_lastPluginHookSeconds.TryGetValue(rawName, out var previousTotal))
                {
                    var delta = currentTotal - previousTotal;
                    if (delta > 0)
                    {
                        var label = _guardrails.ResolvePlugin(rawName);
                        _pluginHookSecondsTotal.WithLabels(label).Inc(delta);
                        _pluginSeries.Touch(new[] { label }, nowUtc, labels => _pluginHookSecondsTotal.RemoveLabelled(labels));
                    }
                }

                _lastPluginHookSeconds[rawName] = currentTotal;
            }
        });
    }

    internal void OnCarbonModuleMetrics(Dictionary<string, double> metrics)
    {
        if (!Ready || metrics == null || metrics.Count == 0)
        {
            return;
        }

        RunCollector("module_snapshot", () =>
        {
            var nowUtc = DateTime.UtcNow;

            foreach (var item in metrics)
            {
                var rawName = string.IsNullOrWhiteSpace(item.Key) ? "unknown" : item.Key.Trim();
                var currentTotal = item.Value;
                _moduleLastSeenUtc[rawName] = nowUtc;

                if (_lastModuleHookSeconds.TryGetValue(rawName, out var previousTotal))
                {
                    var delta = currentTotal - previousTotal;
                    if (delta > 0)
                    {
                        var label = _guardrails.ResolveModule(rawName);
                        _moduleHookSecondsTotal.WithLabels(label).Inc(delta);
                        _moduleSeries.Touch(new[] { label }, nowUtc, labels => _moduleHookSecondsTotal.RemoveLabelled(labels));
                    }
                }

                _lastModuleHookSeconds[rawName] = currentTotal;
            }
        });
    }

    internal bool OnClientPerformanceReport(ClientPerformanceReport clientPerformanceReport)
    {
        if (clientPerformanceReport.request_id != _performanceReportRequestId)
        {
            return false;
        }

        if (!Ready || !Configuration.ExportPlayerAggregateMetrics)
        {
            return true;
        }

        RunCollector("client_performance", () =>
        {
            var nowUtc = DateTime.UtcNow;
            var clientFps = Math.Max(0, clientPerformanceReport.fps);
            var clientMemoryBytes = Math.Max(0L, clientPerformanceReport.memory_system) * 1024L * 1024L;
            var playerId = TryParsePlayerId(clientPerformanceReport.user_id);
            var player = playerId.HasValue ? BasePlayer.FindByID(playerId.Value) : null;
            var playerName = player?.displayName ?? string.Empty;
            var playerIp = SanitizeIpAddress(player?.net?.connection?.ipaddress);

            _clientFramesPerSecond.Observe(clientFps);
            _clientMemoryBytes.Observe(clientMemoryBytes);

            if (playerId.HasValue)
            {
                _playerObservations.UpdateClientSample(
                    playerId.Value,
                    playerName,
                    playerIp,
                    clientFps,
                    clientMemoryBytes,
                    nowUtc);
            }
        });

        return true;
    }

    internal void OnPerformanceReportGenerated()
    {
        if (!Ready)
        {
            return;
        }

        PollServerSnapshot();
    }

    private void PollServerSnapshot()
    {
        if (!Ready)
        {
            return;
        }

        if (!_firstPerformanceReportGenerated)
        {
            _firstPerformanceReportGenerated = true;
        }

        RunCollector("server_snapshot", () =>
        {
            var current = Performance.current;
            var connections = Net.sv?.connections;
            var connectionCount = connections?.Count ?? 0;

            _serverFramesPerSecond.WithLabels("instant").Set(current.frameRate);
            _serverFramesPerSecond.WithLabels("average").Set(current.frameRateAverage);
            _serverFrametimeSeconds.WithLabels("instant").Set(current.frameTime / 1000d);
            _serverFrametimeSeconds.WithLabels("average").Set(current.frameTimeAverage / 1000d);
            _memoryUsedBytes.Set(GetMemoryUsageBytes(current));
            _connections.Set(connectionCount);

            ObserveGcCollections(current.memoryCollections);

            _taskQueueDepth.WithLabels("load_balancer").Set(current.loadBalancerTasks);
            _taskQueueDepth.WithLabels("invoke_handler").Set(current.invokeHandlerTasks);
            _taskQueueDepth.WithLabels("workshop_skins_queue").Set(current.workshopSkinsQueued);

            var bytesReceivedTotal = Net.sv.GetStat(null, BaseNetwork.StatTypeLong.BytesReceived);
            var bytesSentTotal = Net.sv.GetStat(null, BaseNetwork.StatTypeLong.BytesSent);
            var packetLossLastSecond = Net.sv.GetStat(null, BaseNetwork.StatTypeLong.PacketLossLastSecond);

            ObserveNetworkBytesTotal("receive", bytesReceivedTotal, ref _lastNetworkBytesReceived);
            ObserveNetworkBytesTotal("send", bytesSentTotal, ref _lastNetworkBytesSent);

            _networkPacketLossRatio.Set(ToPacketLossRatio(packetLossLastSecond));

            _players.WithLabels("connected").Set(BasePlayer.activePlayerList.Count);
            _players.WithLabels("sleeping").Set(BasePlayer.sleepingPlayerList.Count);
            _players.WithLabels("bots").Set(BasePlayer.bots.Count);
            _players.WithLabels("joining").Set(ServerMgr.Instance?.connectionQueue?.Joining ?? 0);
            _players.WithLabels("queued").Set(ServerMgr.Instance?.connectionQueue?.Queued ?? 0);
            _players.WithLabels("receiving_snapshot").Set(CountReceivingSnapshotPlayers(out var totalSnapshotQueueDepth, out var maxSnapshotQueueDepth));
            _snapshotQueueDepth.WithLabels("sum").Set(totalSnapshotQueueDepth);
            _snapshotQueueDepth.WithLabels("max").Set(maxSnapshotQueueDepth);

            _connectionQueueDepth.WithLabels("reserved").Set(ServerMgr.Instance?.connectionQueue?.ReservedCount ?? 0);
            _connectionQueueDepth.WithLabels("joining").Set(ServerMgr.Instance?.connectionQueue?.Joining ?? 0);
            _connectionQueueDepth.WithLabels("queued").Set(ServerMgr.Instance?.connectionQueue?.Queued ?? 0);

            _entitiesCount.Set(BaseNetworkable.serverEntities?.Count ?? 0);
            _networkQueueDepth.WithLabels("read").Set(((BaseNetwork)Net.sv).ReadQueueLength);
            _networkQueueDepth.WithLabels("write").Set(((BaseNetwork)Net.sv).WriteQueueLength);
            _networkQueueDepth.WithLabels("decrypt").Set(((BaseNetwork)Net.sv).DecryptQueueLength);
            _networkQueueBytes.WithLabels("read").Set(((BaseNetwork)Net.sv).ReadQueueBytes);
            _networkQueueBytes.WithLabels("write").Set(((BaseNetwork)Net.sv).WriteQueueBytes);
            _networkQueueBytes.WithLabels("decrypt").Set(((BaseNetwork)Net.sv).DecryptQueueBytes);

            _runtimePhaseSeconds.WithLabels("servermgr_update").Set(RuntimeProfiler.ServerMgr_Update.TotalSeconds);
            _runtimePhaseSeconds.WithLabels("net_cycle").Set(RuntimeProfiler.Net_Cycle.TotalSeconds);
            _runtimePhaseSeconds.WithLabels("physics_sync").Set(RuntimeProfiler.Physics_SyncTransforms.TotalSeconds);
            _runtimePhaseSeconds.WithLabels("companion_tick").Set(RuntimeProfiler.Companion_Tick.TotalSeconds);
            _runtimePhaseSeconds.WithLabels("baseplayer_tick").Set(RuntimeProfiler.BasePlayer_ServerCycle.TotalSeconds);

            _aiThinkQueueDepth.WithLabels("human").Set(AIThinkManager._processQueue.Count);
            _aiThinkQueueDepth.WithLabels("animal").Set(AIThinkManager._animalProcessQueue.Count);
            _aiThinkQueueDepth.WithLabels("pets").Set(AIThinkManager._petProcessQueue.Count);
            _aiThinkBudgetSeconds.WithLabels("human").Set(AIThinkManager.framebudgetms / 1000d);
            _aiThinkBudgetSeconds.WithLabels("animal").Set(AIThinkManager.animalframebudgetms / 1000d);
            _aiThinkBudgetSeconds.WithLabels("pets").Set(AIThinkManager.petframebudgetms / 1000d);

            _loadBalancerDepth.Set(LoadBalancer.Count());
            _loadBalancerPaused.Set(LoadBalancer.Paused ? 1 : 0);
            _globalNetworkEntitiesCount.Set(GlobalNetworkHandler.server?.serverData?.Count ?? 0);
            _globalNetworkConnections.Set(CountGlobalNetworkConnections(connections));
            _animalsTotal.Set(AnimalBrain.Count);

            UpdateWipeMetrics();
            UpdateSaveMetrics();
            UpdateRconMetrics();
            UpdateEacMetrics();
        });
    }

    private void PollHookSnapshots()
    {
        if (!Ready || !Community.IsServerInitialized)
        {
            return;
        }

        var pluginMetrics = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var plugin in Community.Runtime.Plugins.Plugins)
        {
            pluginMetrics[plugin.Name] = plugin.TotalHookTime.TotalSeconds;
        }

        OnOxidePluginMetrics(pluginMetrics);

        var moduleMetrics = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var module in Community.Runtime.ModuleProcessor.Modules)
        {
            moduleMetrics[module.Name] = module.TotalHookTime.TotalSeconds;
        }

        OnCarbonModuleMetrics(moduleMetrics);
    }

    private void SyncActivePlayers()
    {
        if (!Ready || !Configuration.ExportPlayerAggregateMetrics)
        {
            return;
        }

        var activeIds = new HashSet<ulong>();
        foreach (var player in BasePlayer.activePlayerList)
        {
            activeIds.Add(player.userID);
        }

        foreach (var player in BasePlayer.activePlayerList)
        {
            if (!_playerStatsActions.ContainsKey(player.userID))
            {
                OnPlayerInit(player);
            }
        }

        foreach (var userId in _playerStatsActions.Keys.Where(x => !activeIds.Contains(x)).ToArray())
        {
            var disconnected = BasePlayer.FindByID(userId);
            if (disconnected != null)
            {
                OnPlayerDisconnected(disconnected);
            }
            else
            {
                _playerStatsActions.Remove(userId);
                _perfReportDelayCounter.Remove(userId);
                _playerObservations.Remove(userId);
            }
        }
    }

    internal void ObserveTimedMetric(TimedMetricKind kind, TimedMetricLabels labels, double durationSeconds)
    {
        if (!Ready || durationSeconds < 0 || !_timedFamilies.TryGetValue(kind, out var family))
        {
            return;
        }

        try
        {
            family.Observe(labels, durationSeconds, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            RecordCollectorError("timed_" + kind.ToString().ToLowerInvariant(), ex);
        }
    }

    private void GatherPlayerSecondStats(BasePlayer player)
    {
        if (!Ready || !Configuration.ExportPlayerAggregateMetrics || player == null || player.net?.connection == null)
        {
            return;
        }

        RunCollector("player_snapshot", () =>
        {
            if (!player.IsReceivingSnapshot)
            {
                _perfReportDelayCounter.TryGetValue(player.userID, out var perfReportCounter);
                if (perfReportCounter < 4)
                {
                    _perfReportDelayCounter[player.userID] = perfReportCounter + 1;
                }
                else
                {
                    _perfReportDelayCounter[player.userID] = 0;
                    player.ClientRPC(RpcTarget.Player("GetPerformanceReport", player), "legacy", _performanceReportRequestId);
                }
            }

            var connection = player.net.connection;
            var pingSeconds = Math.Max(0, Net.sv.GetAveragePing(connection)) / 1000d;
            var packetLossRatio = ToPacketLossRatio(Net.sv.GetStat(connection, BaseNetwork.StatTypeLong.PacketLossLastSecond));
            var nowUtc = DateTime.UtcNow;

            _playerPingSeconds.Observe(pingSeconds);
            _playerPacketLossRatio.Observe(packetLossRatio);
            _playerObservations.UpdateNetworkSample(
                player.userID,
                player.displayName,
                SanitizeIpAddress(connection.ipaddress),
                pingSeconds,
                packetLossRatio,
                nowUtc);
        });
    }

    private void UpdatePlayerAggregateMetrics()
    {
        if (!Ready || !Configuration.ExportPlayerAggregateMetrics)
        {
            return;
        }

        var snapshot = _playerObservations.CreateSnapshot(
            DateTime.UtcNow,
            Configuration.MetricExpiry,
            Configuration.HighPingThresholdsMs,
            Configuration.LowFpsThresholds,
            Configuration.HighPacketLossRatio);

        _lastPlayerAggregateSnapshot = snapshot;

        foreach (var label in _knownPlayerConditionLabels.Except(snapshot.ConditionCount.Keys).ToArray())
        {
            _playersConditionCount.WithLabels(label).Set(0);
        }

        foreach (var item in snapshot.ConditionCount)
        {
            _knownPlayerConditionLabels.Add(item.Key);
            _playersConditionCount.WithLabels(item.Key).Set(item.Value);
        }

        foreach (var kind in _knownPlayerPopulationKinds.Except(snapshot.Population.Keys).ToArray())
        {
            _playerObservationPopulation.WithLabels(kind).Set(0);
        }

        foreach (var item in snapshot.Population)
        {
            _knownPlayerPopulationKinds.Add(item.Key);
            _playerObservationPopulation.WithLabels(item.Key).Set(item.Value);
        }
    }

    private void CleanupExpiredSeries()
    {
        if (!Ready)
        {
            return;
        }

        RunCollector("series_cleanup", () =>
        {
            var cutoffUtc = DateTime.UtcNow - Configuration.MetricExpiry;

            _pluginSeries.ExpireOlderThan(cutoffUtc);
            _moduleSeries.ExpireOlderThan(cutoffUtc);
            _networkUpdateSeries.ExpireOlderThan(cutoffUtc);

            foreach (var family in _timedFamilies.Values)
            {
                family.ExpireStale(cutoffUtc);
            }

            CleanupRawTotalCache(_lastPluginHookSeconds, _pluginLastSeenUtc, cutoffUtc);
            CleanupRawTotalCache(_lastModuleHookSeconds, _moduleLastSeenUtc, cutoffUtc);
        });
    }

    private void CleanupRawTotalCache(Dictionary<string, double> totals, Dictionary<string, DateTime> lastSeenUtc, DateTime cutoffUtc)
    {
        var expired = lastSeenUtc
            .Where(x => x.Value < cutoffUtc)
            .Select(x => x.Key)
            .ToArray();

        foreach (var key in expired)
        {
            lastSeenUtc.Remove(key);
            totals.Remove(key);
        }
    }

    private void RunCollector(string collector, Action action)
    {
        var started = Stopwatch.StartNew();

        try
        {
            action.Invoke();
            _exporterLastSnapshotSuccessTimestampSeconds.WithLabels(collector).Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        catch (Exception ex)
        {
            RecordCollectorError(collector, ex);
        }
        finally
        {
            started.Stop();
            _exporterSnapshotDurationSeconds.WithLabels(collector).Observe(started.Elapsed.TotalSeconds);
        }
    }

    private void RecordCollectorError(string collector, Exception ex)
    {
        _exporterCollectErrorsTotal.WithLabels(collector).Inc();

        if (Configuration?.DebugLogging == true && ex != null)
        {
            Debug.LogError($"[ServerMetrics]: collector '{collector}' failed");
            Debug.LogException(ex);
        }
    }

    private void RecordSeriesDrop(string family, string reason, string action)
    {
        _exporterSeriesDroppedTotal.WithLabels(family, reason, action).Inc();

        if (Configuration?.DebugLogging == true)
        {
            Debug.LogWarning($"[ServerMetrics]: series guardrail applied family={family} reason={reason} action={action}");
        }
    }

    private void ObserveGcCollections(long currentCount)
    {
        if (_lastGcCollections < 0)
        {
            _lastGcCollections = currentCount;
            return;
        }

        var delta = currentCount - _lastGcCollections;
        if (delta > 0)
        {
            _gcCollectionsTotal.Inc(delta);
        }

        _lastGcCollections = currentCount;
    }

    private int CountReceivingSnapshotPlayers(out int totalSnapshotQueueDepth, out int maxSnapshotQueueDepth)
    {
        totalSnapshotQueueDepth = 0;
        maxSnapshotQueueDepth = 0;
        var receiving = 0;

        foreach (var player in BasePlayer.activePlayerList)
        {
            if (!player.IsReceivingSnapshot)
            {
                continue;
            }

            receiving++;
            var depth = GetSnapshotQueueLength(player);
            totalSnapshotQueueDepth += depth;
            maxSnapshotQueueDepth = Math.Max(maxSnapshotQueueDepth, depth);
        }

        return receiving;
    }

    private static int GetSnapshotQueueLength(BasePlayer player)
    {
        if (player == null || SnapshotQueueField == null || SnapshotQueueLengthProperty == null)
        {
            return 0;
        }

        try
        {
            var queue = SnapshotQueueField.GetValue(player);
            if (queue == null)
            {
                return 0;
            }

            return (int)(SnapshotQueueLengthProperty.GetValue(queue) ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    private static int CountGlobalNetworkConnections(List<Connection> connections)
    {
        if (connections == null)
        {
            return 0;
        }

        var count = 0;
        foreach (var connection in connections)
        {
            if (connection != null && connection.globalNetworking)
            {
                count++;
            }
        }

        return count;
    }

    private void UpdateWipeMetrics()
    {
        var wipeTimer = WipeTimer.serverinstance;
        if (wipeTimer != null)
        {
            _wipeTimeRemainingSeconds.Set(Math.Max(0d, wipeTimer.GetTimeSpanUntilWipe().TotalSeconds));
        }

        var labelValues = new[]
        {
            World.Name ?? "unknown",
            World.Size.ToString(CultureInfo.InvariantCulture),
            World.Seed.ToString(CultureInfo.InvariantCulture),
            SaveRestore.WipeId ?? "unknown",
            World.Procedural ? "true" : "false",
            World.Networked ? "true" : "false"
        };

        if (_currentWipeInfoLabelValues != null)
        {
            _wipeInfo.RemoveLabelled(_currentWipeInfoLabelValues);
        }

        _currentWipeInfoLabelValues = labelValues;
        _wipeInfo.WithLabels(labelValues).Set(1);
    }

    private void UpdateSaveMetrics()
    {
        _saveInProgress.Set(SaveRestore.IsSaving ? 1 : 0);

        var pendingTimings = SaveTimingField?.GetValue(PerformanceLogging.server) as Dictionary<string, int>;
        if (pendingTimings == null || pendingTimings.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var phase in SavePhases)
        {
            var key = "save." + phase;
            if (!pendingTimings.TryGetValue(key, out var milliseconds))
            {
                continue;
            }

            _saveDurationSeconds.WithLabels(phase).Set(milliseconds / 1000d);

            if (!_lastSaveTimingsMilliseconds.TryGetValue(key, out var previous) || previous != milliseconds)
            {
                _lastSaveTimingsMilliseconds[key] = milliseconds;
                changed = true;
            }
        }

        if (changed)
        {
            _exporterLastSaveTimestampSeconds.Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _saveEntitiesCount.Set(BaseNetworkable.serverEntities?.Count ?? 0);
        }
    }

    private void UpdateRconMetrics()
    {
        if (RconProfiler.mode < 1)
        {
            RconProfiler.mode = 1;
        }

        var stats = RconProfiler.GetCurrentStats(false);
        _rconClients.Set(stats.ConnectionCount);

        ObserveMonotonicCounter(_rconMessagesTotal, ref _lastRconMessageCount, stats.MessageCount);
        ObserveMonotonicCounter(_rconFailedAuthTotal, ref _lastRconFailedAuthCount, stats.FailedConnectionCount);
        _rconBannedAddresses.Set(GetRconBanCount());
    }

    private static double GetRconBanCount()
    {
        var total = 0d;

        if (RconBannedAddressesField?.GetValue(null) is IEnumerable tempBans)
        {
            foreach (var item in tempBans)
            {
                var banTimeField = item.GetType().GetField("banTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (banTimeField == null)
                {
                    continue;
                }

                if ((float)banTimeField.GetValue(item) > Time.realtimeSinceStartup)
                {
                    total += 1;
                }
            }
        }

        var listener = RconListenerNewField?.GetValue(null);
        if (listener != null && GetBannedNetworksMethod != null)
        {
            if (GetBannedNetworksMethod.Invoke(listener, null) is ICollection bannedNetworks)
            {
                total += bannedNetworks.Count;
            }
        }

        return total;
    }

    private void UpdateEacMetrics()
    {
        _eacAuthStatus.WithLabels("pending").Set(0);
        _eacAuthStatus.WithLabels("local_ok").Set(0);
        _eacAuthStatus.WithLabels("remote_ok").Set(0);

        var statuses = EacConnectionStatusField?.GetValue(null) as IEnumerable;
        if (statuses == null)
        {
            return;
        }

        var pending = 0;
        var localOk = 0;
        var remoteOk = 0;

        foreach (var item in statuses)
        {
            var valueProperty = item.GetType().GetProperty("Value");
            if (valueProperty == null)
            {
                continue;
            }

            var raw = Convert.ToInt32(valueProperty.GetValue(item), CultureInfo.InvariantCulture);
            switch (raw)
            {
                case 0:
                    pending++;
                    break;
                case 1:
                    localOk++;
                    break;
                case 2:
                    remoteOk++;
                    break;
            }
        }

        _eacAuthStatus.WithLabels("pending").Set(pending);
        _eacAuthStatus.WithLabels("local_ok").Set(localOk);
        _eacAuthStatus.WithLabels("remote_ok").Set(remoteOk);
    }

    private void PollWorkQueueMetrics()
    {
        if (!Ready)
        {
            return;
        }

        RunCollector("work_queue_snapshot", () =>
        {
            foreach (var label in WorkQueueLabels)
            {
                _workQueueDepth.WithLabels(label).Set(0);
            }

            foreach (var queue in ObjectWorkQueue.All)
            {
                UpdateWorkQueueMetric(queue.Name, queue.QueueLength, queue.TotalExecutionTime.TotalSeconds);
            }

            foreach (var queue in PersistentObjectWorkQueue.All)
            {
                UpdateWorkQueueMetric(queue.Name, queue.ListLength, queue.TotalExecutionTime.TotalSeconds);
            }
        });
    }

    private void UpdateWorkQueueMetric(string queueName, int depth, double totalExecutionSeconds)
    {
        var label = ResolveWorkQueueLabel(queueName);
        if (label == null)
        {
            return;
        }

        _workQueueDepth.WithLabels(label).Set(depth);

        if (_lastWorkQueueExecutionSeconds.TryGetValue(label, out var previous))
        {
            var delta = totalExecutionSeconds - previous;
            if (delta > 0)
            {
                _workQueueExecutionSecondsTotal.WithLabels(label).Inc(delta);
            }
        }

        _lastWorkQueueExecutionSeconds[label] = totalExecutionSeconds;
    }

    private static string ResolveWorkQueueLabel(string queueName)
    {
        if (string.IsNullOrWhiteSpace(queueName))
        {
            return null;
        }

        foreach (var item in WorkQueueLabelMap)
        {
            if (queueName.Contains(item.Key, StringComparison.Ordinal))
            {
                return item.Value;
            }
        }

        return null;
    }

    private void PollWorldStateMetrics()
    {
        if (!Ready)
        {
            return;
        }

        RunCollector("world_state", () =>
        {
            var cargoShipCount = 0;
            double cargoShipTimeRemainingSeconds = 0;
            double cargoShipDockCount = 0;
            var hackingCrates = 0;
            var fullyHackedCrates = 0;

            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                if (networkable is CargoShip cargoShip)
                {
                    cargoShipCount++;

                    if (CargoShipLifetimeField != null)
                    {
                        var lifetime = Convert.ToSingle(CargoShipLifetimeField.GetValue(cargoShip), CultureInfo.InvariantCulture);
                        cargoShipTimeRemainingSeconds = Math.Max(cargoShipTimeRemainingSeconds, CargoShip.event_duration_minutes * 60d - lifetime);
                    }

                    if (CargoShipDockCountField != null)
                    {
                        cargoShipDockCount = Math.Max(cargoShipDockCount, Convert.ToInt32(CargoShipDockCountField.GetValue(cargoShip), CultureInfo.InvariantCulture));
                    }

                    continue;
                }

                if (networkable is HackableLockedCrate crate)
                {
                    if (crate.IsFullyHacked())
                    {
                        fullyHackedCrates++;
                    }

                    if (crate.IsBeingHacked())
                    {
                        hackingCrates++;
                    }
                }
            }

            _eventActive.WithLabels("patrol_heli").Set(PatrolHelicopterAI.heliInstance != null ? 1 : 0);
            _eventActive.WithLabels("travelling_vendor").Set(TravellingVendorEvent.currentVendor != null ? 1 : 0);
            _eventActive.WithLabels("cargo_ship").Set(cargoShipCount > 0 ? 1 : 0);
            _eventActive.WithLabels("road_bradleys").Set(RoadBradleys.StaticBradleyCount > 0 ? 1 : 0);

            _eventCount.WithLabels("cargo_ship").Set(cargoShipCount);
            _eventCount.WithLabels("road_bradleys").Set(RoadBradleys.StaticBradleyCount);

            _cargoShipTimeRemainingSeconds.Set(Math.Max(0d, cargoShipTimeRemainingSeconds));
            _cargoShipDockCount.Set(cargoShipDockCount);
            _hackableCrates.WithLabels("hacking").Set(hackingCrates);
            _hackableCrates.WithLabels("fully_hacked").Set(fullyHackedCrates);
        });
    }

    private void ObserveMonotonicCounter(Counter counter, ref long lastValue, long currentValue)
    {
        if (lastValue < 0)
        {
            lastValue = currentValue;
            return;
        }

        var delta = currentValue - lastValue;
        if (delta > 0)
        {
            counter.Inc(delta);
        }

        lastValue = currentValue;
    }

    private static string NormalizeReasonLabel(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "unknown";
        }

        var value = reason.Trim();

        if (value.StartsWith("EAC:", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(4).Trim();
        }
        else if (value.StartsWith("Steam:", StringComparison.OrdinalIgnoreCase))
        {
            value = "steam_auth";
        }
        else if (value.StartsWith("Packet Flooding:", StringComparison.OrdinalIgnoreCase))
        {
            value = "packet_flooding_" + value.Substring("Packet Flooding:".Length).Trim();
        }
        else if (value.StartsWith("Invalid Packet:", StringComparison.OrdinalIgnoreCase))
        {
            value = "invalid_packet_" + value.Substring("Invalid Packet:".Length).Trim();
        }
        else if (value.StartsWith("You are banned from this server", StringComparison.OrdinalIgnoreCase))
        {
            value = "banned";
        }
        else if (value.StartsWith("Wrong Steam Beta", StringComparison.OrdinalIgnoreCase))
        {
            value = "wrong_steam_beta";
        }
        else if (value.StartsWith("Wrong Connection Protocol", StringComparison.OrdinalIgnoreCase))
        {
            value = "wrong_connection_protocol";
        }

        var builder = new System.Text.StringBuilder(value.Length);
        var lastUnderscore = false;

        foreach (var ch in value)
        {
            var c = char.ToLowerInvariant(ch);
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                lastUnderscore = false;
            }
            else if (!lastUnderscore)
            {
                builder.Append('_');
                lastUnderscore = true;
            }
        }

        var normalized = builder.ToString().Trim('_');
        if (normalized.Length == 0)
        {
            return "unknown";
        }

        if (normalized.Length > 64)
        {
            normalized = normalized.Substring(0, 64).TrimEnd('_');
        }

        return normalized;
    }

    private void ObserveNetworkBytesTotal(string direction, ulong currentValue, ref long lastValue)
    {
        if (lastValue < 0)
        {
            lastValue = (long)currentValue;
            return;
        }

        var delta = (long)currentValue - lastValue;
        if (delta > 0)
        {
            _networkBytesTotal.WithLabels(direction).Inc(delta);
        }

        lastValue = (long)currentValue;
    }

    private long GetMemoryUsageBytes(Performance.Tick performanceTick)
    {
        if (performanceTick.memoryUsageSystem > 0)
        {
            return performanceTick.memoryUsageSystem * 1024L * 1024L;
        }

        _currentProcess ??= Process.GetCurrentProcess();
        _currentProcess.Refresh();
        return _currentProcess.WorkingSet64;
    }

    private static double ToPacketLossRatio(ulong packetLossValue)
    {
        return packetLossValue / 10000d;
    }

    private static ulong? TryParsePlayerId(string userId)
    {
        if (ulong.TryParse(userId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string SanitizeIpAddress(string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return string.Empty;
        }

        var trimmed = ipAddress.Trim();

        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            var end = trimmed.IndexOf(']');
            return end > 1 ? trimmed.Substring(1, end - 1) : trimmed;
        }

        var firstColon = trimmed.IndexOf(':');
        var lastColon = trimmed.LastIndexOf(':');

        if (firstColon > 0 && firstColon == lastColon)
        {
            return trimmed.Substring(0, lastColon);
        }

        return trimmed;
    }

    private void ApplyConfiguration()
    {
        StopRuntime();

        if (Configuration == null)
        {
            return;
        }

        foreach (var warning in Configuration.LegacyWarnings)
        {
            Debug.LogWarning("[ServerMetrics]: " + warning);
        }

        NormalizeConfiguration();
        SaveConfiguration();

        if (!Configuration.Enabled)
        {
            Debug.LogWarning("[ServerMetrics]: Metrics gathering is disabled in configuration");
            return;
        }

        if (Configuration.UsePrometheusNet)
        {
            Debug.LogWarning("[ServerMetrics]: prometheus-net was requested but proved incompatible with this RustDedicated runtime. Using the built-in exporter implementation.");
            Configuration.UsePrometheusNet = false;
        }

        if (RconProfiler.mode < 1)
        {
            RconProfiler.mode = 1;
        }

        InitializeMetrics();

        if (Configuration.PrometheusExporterEnabled)
        {
            _exporterHost.Start(Configuration.PrometheusListenHost, Configuration.PrometheusListenPort, Configuration.PrometheusMetricsPath);
        }

        if (Configuration.DebugEndpointEnabled)
        {
            _debugEndpointHost.Start(Configuration.DebugEndpointListenHost, Configuration.DebugEndpointListenPort, Configuration.DebugEndpointBearerToken);
        }

        StartRepeatingWork();
        Ready = true;

        if (Bootstrap.bootstrapInitRun || Community.IsServerInitialized || ServerMgr.Instance != null)
        {
            OnServerStarted();
        }

        if (Configuration.ExportPlayerAggregateMetrics)
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                OnPlayerInit(player);
            }
        }
    }

    private void NormalizeConfiguration()
    {
        if (Configuration == null)
        {
            return;
        }

        Configuration.Normalize();

        if (Configuration.DebugEndpointEnabled && string.IsNullOrWhiteSpace(Configuration.DebugEndpointBearerToken))
        {
            Debug.LogWarning("[ServerMetrics]: DebugEndpointEnabled=true without a bearer token. Disabling the debug endpoint.");
            Configuration.DebugEndpointEnabled = false;
        }

        if (Configuration.DebugEndpointEnabled &&
            string.Equals(Configuration.PrometheusListenHost, Configuration.DebugEndpointListenHost, StringComparison.OrdinalIgnoreCase) &&
            Configuration.PrometheusListenPort == Configuration.DebugEndpointListenPort)
        {
            Debug.LogWarning("[ServerMetrics]: Debug endpoint host/port collides with the metrics endpoint. Disabling the debug endpoint.");
            Configuration.DebugEndpointEnabled = false;
        }
    }

    private void InitializeMetrics()
    {
        _netWriteTypes = new ConditionalWeakTable<NetWrite, MessageTypeReference>();
        _playerObservations = new PlayerObservationStore();
        _timedFamilies = new Dictionary<TimedMetricKind, TimedMetricFamily>();
        _lastPlayerAggregateSnapshot = new PlayerAggregateSnapshot();
        _firstPerformanceReportGenerated = false;
        _startupPatchesApplied = false;
        _lastGcCollections = -1;
        _lastNetworkBytesReceived = -1;
        _lastNetworkBytesSent = -1;
        _lastRconMessageCount = -1;
        _lastRconFailedAuthCount = -1;
        _currentWipeInfoLabelValues = null;
        _lastWorkQueueExecutionSeconds.Clear();
        _lastSaveTimingsMilliseconds.Clear();

        _registry = new MetricRegistry();
        _metricFactory = new MetricFactory(_registry);
        _guardrails = new MetricGuardrails(Configuration, RecordSeriesDrop);
        _exporterHost = new PrometheusExporterHost(_registry);
        _debugEndpointHost = new DebugEndpointHost(BuildDebugPayload);

        CreateExporterMetrics();
        CreateCoreMetrics();
        CreateTimedMetrics();
        PublishBuildInfo();
    }

    private void CreateExporterMetrics()
    {
        _exporterBuildInfo = _metricFactory.CreateGauge(
            "rsm_exporter_build_info",
            "Build information for the Carbon.RSM exporter.",
            new[] { "version", "commit", "framework" });

        _exporterCollectErrorsTotal = _metricFactory.CreateCounter(
            "rsm_exporter_collect_errors_total",
            "Total collector execution errors.",
            new[] { "collector" });

        _exporterSeriesDroppedTotal = _metricFactory.CreateCounter(
            "rsm_exporter_series_dropped_total",
            "Total times exporter guardrails coalesced or dropped series.",
            new[] { "family", "reason", "action" });

        _exporterLastSnapshotSuccessTimestampSeconds = _metricFactory.CreateGauge(
            "rsm_exporter_last_snapshot_success_timestamp_seconds",
            "Unix timestamp of the last successful collector run.",
            new[] { "collector" });

        _exporterLastSaveTimestampSeconds = _metricFactory.CreateGauge(
            "rsm_exporter_last_save_timestamp_seconds",
            "Unix timestamp of the last observed successful save.");

        _exporterSnapshotDurationSeconds = _metricFactory.CreateHistogram(
            "rsm_exporter_snapshot_duration_seconds",
            "Collector execution duration in seconds.",
            new HistogramConfiguration
            {
                LabelNames = new[] { "collector" },
                Buckets = new[] { 0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1d }
            });
    }

    private void CreateCoreMetrics()
    {
        _serverFramesPerSecond = _metricFactory.CreateGauge(
            "rsm_server_frames_per_second",
            "Server frames per second.",
            new[] { "stat" });

        _serverFrametimeSeconds = _metricFactory.CreateGauge(
            "rsm_server_frametime_seconds",
            "Server frame time in seconds.",
            new[] { "stat" });

        _memoryUsedBytes = _metricFactory.CreateGauge(
            "rsm_memory_used_bytes",
            "Server memory used in bytes.");

        _gcCollectionsTotal = _metricFactory.CreateCounter(
            "rsm_gc_collections_total",
            "Garbage collection count observed from the server process.");

        _taskQueueDepth = _metricFactory.CreateGauge(
            "rsm_task_queue_depth",
            "Current task queue depth.",
            new[] { "queue" });

        _players = _metricFactory.CreateGauge(
            "rsm_players",
            "Current players by state.",
            new[] { "state" });

        _entitiesCount = _metricFactory.CreateGauge(
            "rsm_entities_count",
            "Current server entity count.");

        _connections = _metricFactory.CreateGauge(
            "rsm_connections",
            "Current network connection count.");

        _snapshotQueueDepth = _metricFactory.CreateGauge(
            "rsm_snapshot_queue_depth",
            "Current aggregate snapshot queue depth statistics.",
            new[] { "stat" });

        _networkBytesTotal = _metricFactory.CreateCounter(
            "rsm_network_bytes_total",
            "Total network bytes observed from cumulative RakNet counters.",
            new[] { "direction" });

        _networkQueueDepth = _metricFactory.CreateGauge(
            "rsm_network_queue_depth",
            "Current network queue depth by queue type.",
            new[] { "queue" });

        _networkQueueBytes = _metricFactory.CreateGauge(
            "rsm_network_queue_bytes",
            "Current network queue bytes by queue type.",
            new[] { "queue" });

        _connectionQueueDepth = _metricFactory.CreateGauge(
            "rsm_connection_queue_depth",
            "Current connection queue depth by state.",
            new[] { "state" });

        _networkPacketLossRatio = _metricFactory.CreateGauge(
            "rsm_network_packet_loss_ratio",
            "Server packet loss ratio over the last second.");

        _pluginHookSecondsTotal = _metricFactory.CreateCounter(
            "rsm_plugin_hook_seconds_total",
            "Total plugin hook time in seconds.",
            new[] { "plugin" });

        _moduleHookSecondsTotal = _metricFactory.CreateCounter(
            "rsm_module_hook_seconds_total",
            "Total module hook time in seconds.",
            new[] { "module" });

        _networkUpdatesTotal = _metricFactory.CreateCounter(
            "rsm_network_updates_total",
            "Total network updates by message type.",
            new[] { "message_type" });

        _networkUpdateBytesTotal = _metricFactory.CreateCounter(
            "rsm_network_update_bytes_total",
            "Total network update bytes by message type.",
            new[] { "message_type" });

        _playerPingSeconds = _metricFactory.CreateHistogram(
            "rsm_player_ping_seconds",
            "Observed player ping distribution in seconds.",
            new HistogramConfiguration
            {
                Buckets = new[] { 0.025, 0.05, 0.075, 0.1, 0.15, 0.2, 0.25, 0.5, 1d, 2d }
            });

        _clientFramesPerSecond = _metricFactory.CreateHistogram(
            "rsm_client_frames_per_second",
            "Observed client FPS distribution.",
            new HistogramConfiguration
            {
                Buckets = new[] { 10d, 20d, 30d, 45d, 60d, 75d, 90d, 120d, 144d, 240d }
            });

        _clientMemoryBytes = _metricFactory.CreateHistogram(
            "rsm_client_memory_bytes",
            "Observed client memory usage in bytes.",
            new HistogramConfiguration
            {
                Buckets = new[]
                {
                    256d * 1024 * 1024,
                    512d * 1024 * 1024,
                    1024d * 1024 * 1024,
                    2048d * 1024 * 1024,
                    4096d * 1024 * 1024,
                    8192d * 1024 * 1024,
                    16384d * 1024 * 1024
                }
            });

        _playerPacketLossRatio = _metricFactory.CreateHistogram(
            "rsm_player_packet_loss_ratio",
            "Observed player packet loss ratio distribution.",
            new HistogramConfiguration
            {
                Buckets = new[] { 0.001, 0.005, 0.01, 0.02, 0.05, 0.1, 0.25, 0.5, 1d }
            });

        _playersConditionCount = _metricFactory.CreateGauge(
            "rsm_players_condition_count",
            "Current number of players whose latest observation meets a threshold condition.",
            new[] { "condition" });

        _playerObservationPopulation = _metricFactory.CreateGauge(
            "rsm_player_observation_population",
            "Current number of players with a recent observation for a metric kind.",
            new[] { "kind" });

        _connectionAttemptsTotal = _metricFactory.CreateCounter(
            "rsm_connection_attempts_total",
            "Total inbound connection attempts.");

        _connectionFailuresTotal = _metricFactory.CreateCounter(
            "rsm_connection_failures_total",
            "Total connection failures recorded during authentication.",
            new[] { "reason" });

        _authRejectionsTotal = _metricFactory.CreateCounter(
            "rsm_auth_rejections_total",
            "Total authentication rejections.",
            new[] { "reason" });

        _saveInProgress = _metricFactory.CreateGauge(
            "rsm_save_in_progress",
            "Whether a world save is currently in progress.");

        _saveDurationSeconds = _metricFactory.CreateGauge(
            "rsm_save_duration_seconds",
            "Last observed world save duration in seconds by phase.",
            new[] { "phase" });

        _saveEntitiesCount = _metricFactory.CreateGauge(
            "rsm_save_entities_count",
            "Approximate entity count observed at the end of the last save.");

        _wipeInfo = _metricFactory.CreateGauge(
            "rsm_wipe_info",
            "Current wipe and world identity information.",
            new[] { "map_name", "world_size", "world_seed", "wipe_id", "procedural", "networked" });

        _wipeTimeRemainingSeconds = _metricFactory.CreateGauge(
            "rsm_wipe_time_remaining_seconds",
            "Time remaining until the next wipe in seconds.");

        _rconClients = _metricFactory.CreateGauge(
            "rsm_rcon_clients",
            "Current number of connected RCON clients.");

        _rconFailedAuthTotal = _metricFactory.CreateCounter(
            "rsm_rcon_failed_auth_total",
            "Total failed RCON authentication attempts.");

        _rconBannedAddresses = _metricFactory.CreateGauge(
            "rsm_rcon_banned_addresses",
            "Current number of RCON banned addresses or networks.");

        _rconMessagesTotal = _metricFactory.CreateCounter(
            "rsm_rcon_messages_total",
            "Total RCON messages received.");

        _eacAuthStatus = _metricFactory.CreateGauge(
            "rsm_eac_auth_status",
            "Current EAC authentication status counts.",
            new[] { "status" });

        _eacKicksTotal = _metricFactory.CreateCounter(
            "rsm_eac_kicks_total",
            "Total kicks attributed to EAC.",
            new[] { "reason" });

        _runtimePhaseSeconds = _metricFactory.CreateGauge(
            "rsm_runtime_phase_seconds",
            "Runtime phase duration in seconds.",
            new[] { "phase" });

        _aiThinkQueueDepth = _metricFactory.CreateGauge(
            "rsm_ai_think_queue_depth",
            "Current AI think queue depth by queue type.",
            new[] { "queue" });

        _aiThinkBudgetSeconds = _metricFactory.CreateGauge(
            "rsm_ai_think_budget_seconds",
            "Configured AI think budget in seconds by queue type.",
            new[] { "queue" });

        _workQueueDepth = _metricFactory.CreateGauge(
            "rsm_work_queue_depth",
            "Current selected object work queue depth.",
            new[] { "queue" });

        _workQueueExecutionSecondsTotal = _metricFactory.CreateCounter(
            "rsm_work_queue_execution_seconds_total",
            "Total execution time observed for selected work queues in seconds.",
            new[] { "queue" });

        _loadBalancerDepth = _metricFactory.CreateGauge(
            "rsm_load_balancer_depth",
            "Current load balancer backlog depth.");

        _loadBalancerPaused = _metricFactory.CreateGauge(
            "rsm_load_balancer_paused",
            "Whether the load balancer is currently paused.");

        _globalNetworkEntitiesCount = _metricFactory.CreateGauge(
            "rsm_global_network_entities_count",
            "Current number of entities tracked by the global network handler.");

        _globalNetworkConnections = _metricFactory.CreateGauge(
            "rsm_global_network_connections",
            "Current number of connections with global networking enabled.");

        _connectionKicksTotal = _metricFactory.CreateCounter(
            "rsm_connection_kicks_total",
            "Total connection kicks by normalized reason.",
            new[] { "reason" });

        _eventActive = _metricFactory.CreateGauge(
            "rsm_event_active",
            "Whether a tracked world event is currently active.",
            new[] { "event" });

        _eventCount = _metricFactory.CreateGauge(
            "rsm_event_count",
            "Current tracked world event counts.",
            new[] { "event" });

        _cargoShipTimeRemainingSeconds = _metricFactory.CreateGauge(
            "rsm_cargo_ship_time_remaining_seconds",
            "Remaining cargo ship event time in seconds for the currently observed ship.");

        _cargoShipDockCount = _metricFactory.CreateGauge(
            "rsm_cargo_ship_dock_count",
            "Current dock count for the currently observed cargo ship.");

        _hackableCrates = _metricFactory.CreateGauge(
            "rsm_hackable_crates",
            "Current hackable crate counts by state.",
            new[] { "state" });

        _animalsTotal = _metricFactory.CreateGauge(
            "rsm_animals_total",
            "Current animal brain count.");
    }

    private void CreateTimedMetrics()
    {
        var durationBuckets = new[] { 0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1d, 2d, 5d };

        _timedFamilies[TimedMetricKind.Invoke] = new TimedMetricFamily(
            _metricFactory, _guardrails, "invoke", "invoke", isCommandFamily: false,
            Configuration.ExportMethodCounters, Configuration.ExportMethodHistograms, durationBuckets);

        _timedFamilies[TimedMetricKind.Rpc] = new TimedMetricFamily(
            _metricFactory, _guardrails, "rpc", "rpc", isCommandFamily: false,
            Configuration.ExportMethodCounters, Configuration.ExportMethodHistograms, durationBuckets);

        _timedFamilies[TimedMetricKind.WorkQueue] = new TimedMetricFamily(
            _metricFactory, _guardrails, "work_queue", "work_queue", isCommandFamily: false,
            Configuration.ExportMethodCounters, Configuration.ExportMethodHistograms, durationBuckets);

        _timedFamilies[TimedMetricKind.ServerUpdate] = new TimedMetricFamily(
            _metricFactory, _guardrails, "server_update", "server_update", isCommandFamily: false,
            Configuration.ExportMethodCounters, Configuration.ExportMethodHistograms, durationBuckets);

        _timedFamilies[TimedMetricKind.TimeWarning] = new TimedMetricFamily(
            _metricFactory, _guardrails, "timewarning", "timewarning", isCommandFamily: false,
            Configuration.ExportMethodCounters, Configuration.ExportMethodHistograms, durationBuckets);

        _timedFamilies[TimedMetricKind.ConsoleCommand] = new TimedMetricFamily(
            _metricFactory, _guardrails, "console_command", "console_command", isCommandFamily: true,
            Configuration.ExportMethodCounters, exportMethodHistograms: false, durationBuckets);
    }

    private void PublishBuildInfo()
    {
        var assembly = typeof(MetricsLogger).Assembly;
        var version = assembly.GetName().Version?.ToString() ?? "unknown";
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var commit = informationalVersion?.Contains("+") == true
            ? informationalVersion.Split('+').Last()
            : "unknown";

        _exporterBuildInfo.WithLabels(version, commit, "net48").Set(1);
    }

    private void StartRepeatingWork()
    {
        CancelInvoke();
        InvokeRepeating(nameof(CleanupExpiredSeries), UnityEngine.Random.Range(5f, 10f), 60f);
        InvokeRepeating(nameof(PollServerSnapshot), UnityEngine.Random.Range(0.5f, 1.5f), 1f);
        InvokeRepeating(nameof(PollHookSnapshots), UnityEngine.Random.Range(0.5f, 1.5f), 1f);
        InvokeRepeating(nameof(PollWorldStateMetrics), UnityEngine.Random.Range(1f, 2f), 5f);
        InvokeRepeating(nameof(PollWorkQueueMetrics), UnityEngine.Random.Range(1f, 2f), 5f);

        if (Configuration.ExportPlayerAggregateMetrics)
        {
            InvokeRepeating(nameof(UpdatePlayerAggregateMetrics), UnityEngine.Random.Range(0.25f, 0.75f), 1f);
            InvokeRepeating(nameof(SyncActivePlayers), UnityEngine.Random.Range(1f, 2f), 5f);
        }
    }

    private void StopRuntime()
    {
        Ready = false;
        CancelInvoke();

        foreach (var item in _playerStatsActions.ToArray())
        {
            BasePlayer.FindByID(item.Key)?.CancelInvoke(item.Value);
        }

        _playerStatsActions.Clear();
        _perfReportDelayCounter.Clear();
        _lastPluginHookSeconds.Clear();
        _lastModuleHookSeconds.Clear();
        _pluginLastSeenUtc.Clear();
        _moduleLastSeenUtc.Clear();
        _knownPlayerConditionLabels.Clear();
        _knownPlayerPopulationKinds.Clear();

        _debugEndpointHost?.Dispose();
        _debugEndpointHost = null;

        _exporterHost?.Dispose();
        _exporterHost = null;

        _registry = null;
        _metricFactory = null;
        _guardrails = null;
        _timedFamilies.Clear();
    }

    private static TimedMetricLabels ParseTimedMethodName(string info)
    {
        if (string.IsNullOrWhiteSpace(info))
        {
            return new TimedMetricLabels("unknown", "unknown");
        }

        var separator = info.IndexOf('.');
        if (separator <= 0 || separator == info.Length - 1)
        {
            return new TimedMetricLabels("unknown", info.Trim());
        }

        return new TimedMetricLabels(
            info.Substring(0, separator).Trim(),
            info.Substring(separator + 1).Trim());
    }

    private string BuildDebugPayload()
    {
        var snapshot = _playerObservations.CreateSnapshot(
            DateTime.UtcNow,
            Configuration?.MetricExpiry ?? TimeSpan.FromMinutes(30),
            Configuration?.HighPingThresholdsMs ?? new List<int> { 150, 250 },
            Configuration?.LowFpsThresholds ?? new List<int> { 30, 45 },
            Configuration?.HighPacketLossRatio ?? 0.05d);

        return JsonConvert.SerializeObject(new
        {
            generated_timestamp_seconds = snapshot.GeneratedTimestampSeconds,
            players = snapshot.DebugPlayers
        }, Formatting.Indented);
    }

    #region Commands

    private void RegisterCommands()
    {
        const string commandPrefix = "servermetrics";
        var reloadCfgCommand = new ConsoleSystem.Command
        {
            Name = "reloadcfg",
            Parent = commandPrefix,
            FullName = commandPrefix + "." + "reloadcfg",
            ServerAdmin = true,
            Variable = false,
            Call = ReloadCfgCommand
        };

        var statusCommand = new ConsoleSystem.Command
        {
            Name = "status",
            Parent = commandPrefix,
            FullName = commandPrefix + "." + "status",
            ServerAdmin = true,
            Variable = false,
            Call = StatusCommand
        };

        ConsoleSystem.Index.Server.Dict[reloadCfgCommand.FullName] = reloadCfgCommand;
        ConsoleSystem.Index.Server.Dict[statusCommand.FullName] = statusCommand;
        ConsoleSystem.Index.All = ConsoleSystem.Index.All.Concat(new[] { reloadCfgCommand, statusCommand }).ToArray();
    }

    private void StatusCommand(ConsoleSystem.Arg arg)
    {
        var lines = new List<string>
        {
            "[ServerMetrics]: Status",
            $"Ready: {Ready}",
            $"Prometheus exporter enabled: {Configuration?.PrometheusExporterEnabled ?? false}",
            $"Prometheus exporter running: {_exporterHost?.IsRunning ?? false}",
            $"Prometheus bind: {Configuration?.PrometheusListenHost}:{Configuration?.PrometheusListenPort}{Configuration?.PrometheusMetricsPath}",
            $"Debug endpoint enabled: {Configuration?.DebugEndpointEnabled ?? false}",
            $"Debug endpoint running: {_debugEndpointHost?.IsRunning ?? false}",
            $"Tracked players: {_playerStatsActions.Count}"
        };

        arg.ReplyWith(string.Join(Environment.NewLine, lines));
    }

    private void ReloadCfgCommand(ConsoleSystem.Arg arg)
    {
        LoadConfiguration();
        ApplyConfiguration();
        arg.ReplyWith(Configuration?.Enabled == true
            ? "[ServerMetrics]: Configuration reloaded"
            : "[ServerMetrics]: Configuration reloaded, exporter remains disabled");
    }

    #endregion

    #region Configuration

    private void LoadConfiguration()
    {
        try
        {
            if (!File.Exists(ConfigurationPath))
            {
                Configuration = new ConfigData();
                SaveConfiguration();
                return;
            }

            var configString = File.ReadAllText(ConfigurationPath);
            Configuration = ConfigData.FromJson(configString);
        }
        catch (Exception ex)
        {
            Debug.LogError("[ServerMetrics]: Configuration is missing or malformed. Defaults will be written.");
            if (Configuration?.DebugLogging == true)
            {
                Debug.LogException(ex);
            }

            Configuration = new ConfigData();
        }

        SaveConfiguration();
    }

    private void SaveConfiguration()
    {
        try
        {
            var configFileInfo = new FileInfo(ConfigurationPath);
            if (configFileInfo.Directory != null && !configFileInfo.Directory.Exists)
            {
                configFileInfo.Directory.Create();
            }

            var serializedConfiguration = JsonConvert.SerializeObject(Configuration ?? new ConfigData(), Formatting.Indented);
            File.WriteAllText(ConfigurationPath, serializedConfiguration);
        }
        catch (Exception ex)
        {
            Debug.LogError("[ServerMetrics]: Failed to write configuration file");
            Debug.LogException(ex);
        }
    }

    #endregion
}
