using HarmonyLib;
using Network;

namespace RustServerMetrics.HarmonyPatches;

[HarmonyPatch(typeof(ConnectionAuth), nameof(ConnectionAuth.OnNewConnection))]
internal static class ConnectionAuth_OnNewConnection_Patch
{
    [HarmonyPrefix]
    public static void Prefix(Connection connection)
    {
        SingletonComponent<MetricsLogger>.Instance?.OnConnectionAttempt();
    }
}
