using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RustServerMetrics.PrometheusMetrics;

internal sealed class PlayerObservationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, PlayerObservation> _observations = new();

    public void Remove(ulong userId)
    {
        lock (_gate)
        {
            _observations.Remove(userId);
        }
    }

    public void UpdateNetworkSample(ulong userId, string displayName, string ipAddress, double pingSeconds, double packetLossRatio, DateTime observedAtUtc)
    {
        lock (_gate)
        {
            var observation = GetOrCreate(userId);
            observation.DisplayName = displayName;
            observation.IpAddress = ipAddress;
            observation.LastObservedUtc = observedAtUtc;
            observation.PingSeconds = pingSeconds;
            observation.HasPing = true;
            observation.PacketLossRatio = packetLossRatio;
            observation.HasPacketLoss = true;
        }
    }

    public void UpdateClientSample(ulong userId, string displayName, string ipAddress, double clientFramesPerSecond, long clientMemoryBytes, DateTime observedAtUtc)
    {
        lock (_gate)
        {
            var observation = GetOrCreate(userId);
            observation.DisplayName = displayName;
            observation.IpAddress = ipAddress;
            observation.LastObservedUtc = observedAtUtc;
            observation.ClientFramesPerSecond = clientFramesPerSecond;
            observation.HasClientFramesPerSecond = true;
            observation.ClientMemoryBytes = clientMemoryBytes;
            observation.HasClientMemoryBytes = true;
        }
    }

    public PlayerAggregateSnapshot CreateSnapshot(DateTime nowUtc, TimeSpan ttl, IReadOnlyList<int> highPingThresholdsMs, IReadOnlyList<int> lowFpsThresholds, double highPacketLossRatio)
    {
        lock (_gate)
        {
            var stale = _observations
                .Where(x => nowUtc - x.Value.LastObservedUtc > ttl)
                .Select(x => x.Key)
                .ToArray();

            foreach (var userId in stale)
            {
                _observations.Remove(userId);
            }

            var snapshot = new PlayerAggregateSnapshot();

            foreach (var thresholdMs in highPingThresholdsMs)
            {
                snapshot.ConditionCount[FormatPingCondition(thresholdMs)] = 0;
            }

            foreach (var threshold in lowFpsThresholds)
            {
                snapshot.ConditionCount[FormatLowFpsCondition(threshold)] = 0;
            }

            snapshot.ConditionCount[FormatPacketLossCondition(highPacketLossRatio)] = 0;
            snapshot.Population["ping"] = 0;
            snapshot.Population["fps"] = 0;
            snapshot.Population["memory"] = 0;
            snapshot.Population["packet_loss"] = 0;

            foreach (var observation in _observations.Values)
            {
                if (observation.HasPing)
                {
                    snapshot.Population["ping"] += 1;

                    foreach (var thresholdMs in highPingThresholdsMs)
                    {
                        if (observation.PingSeconds > thresholdMs / 1000d)
                        {
                            snapshot.ConditionCount[FormatPingCondition(thresholdMs)] += 1;
                        }
                    }
                }

                if (observation.HasClientFramesPerSecond)
                {
                    snapshot.Population["fps"] += 1;

                    foreach (var threshold in lowFpsThresholds)
                    {
                        if (observation.ClientFramesPerSecond < threshold)
                        {
                            snapshot.ConditionCount[FormatLowFpsCondition(threshold)] += 1;
                        }
                    }
                }

                if (observation.HasClientMemoryBytes)
                {
                    snapshot.Population["memory"] += 1;
                }

                if (observation.HasPacketLoss)
                {
                    snapshot.Population["packet_loss"] += 1;

                    if (observation.PacketLossRatio > highPacketLossRatio)
                    {
                        snapshot.ConditionCount[FormatPacketLossCondition(highPacketLossRatio)] += 1;
                    }
                }

                snapshot.DebugPlayers.Add(new PlayerObservationDebugRecord
                {
                    SteamId = observation.UserId.ToString(CultureInfo.InvariantCulture),
                    DisplayName = observation.DisplayName,
                    IpAddress = observation.IpAddress,
                    PingSeconds = observation.HasPing ? observation.PingSeconds : (double?)null,
                    PacketLossRatio = observation.HasPacketLoss ? observation.PacketLossRatio : (double?)null,
                    ClientFramesPerSecond = observation.HasClientFramesPerSecond ? observation.ClientFramesPerSecond : (double?)null,
                    ClientMemoryBytes = observation.HasClientMemoryBytes ? observation.ClientMemoryBytes : (long?)null,
                    LastObservedTimestampSeconds = new DateTimeOffset(observation.LastObservedUtc).ToUnixTimeSeconds()
                });
            }

            snapshot.GeneratedTimestampSeconds = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
            return snapshot;
        }
    }

    private PlayerObservation GetOrCreate(ulong userId)
    {
        if (_observations.TryGetValue(userId, out var observation))
        {
            return observation;
        }

        observation = new PlayerObservation
        {
            UserId = userId
        };
        _observations.Add(userId, observation);
        return observation;
    }

    private static string FormatPingCondition(int milliseconds)
    {
        var wholeSeconds = milliseconds / 1000;
        var remainderMilliseconds = milliseconds % 1000;
        return $"ping_gt_{wholeSeconds}_{remainderMilliseconds:000}s";
    }

    private static string FormatLowFpsCondition(int framesPerSecond)
    {
        return "fps_lt_" + framesPerSecond.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatPacketLossCondition(double ratio)
    {
        return "packet_loss_gt_" + FormatRatio(ratio);
    }

    private static string FormatRatio(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', '_');
    }
}

internal sealed class PlayerAggregateSnapshot
{
    public long GeneratedTimestampSeconds;
    public Dictionary<string, double> ConditionCount = new(StringComparer.Ordinal);
    public Dictionary<string, double> Population = new(StringComparer.Ordinal);
    public List<PlayerObservationDebugRecord> DebugPlayers = new();
}

internal sealed class PlayerObservationDebugRecord
{
    public string SteamId { get; set; }
    public string DisplayName { get; set; }
    public string IpAddress { get; set; }
    public double? PingSeconds { get; set; }
    public double? PacketLossRatio { get; set; }
    public double? ClientFramesPerSecond { get; set; }
    public long? ClientMemoryBytes { get; set; }
    public long LastObservedTimestampSeconds { get; set; }
}

internal sealed class PlayerObservation
{
    public ulong UserId;
    public string DisplayName;
    public string IpAddress;
    public DateTime LastObservedUtc;
    public double PingSeconds;
    public bool HasPing;
    public double PacketLossRatio;
    public bool HasPacketLoss;
    public double ClientFramesPerSecond;
    public bool HasClientFramesPerSecond;
    public long ClientMemoryBytes;
    public bool HasClientMemoryBytes;
}
