using HarmonyLib;
using Network;

namespace RustServerMetrics.HarmonyPatches;

[HarmonyPatch(typeof(ConnectionAuth), nameof(ConnectionAuth.Reject))]
internal static class ConnectionAuth_Reject_Patch
{
    [HarmonyPrefix]
    public static void Prefix(Connection connection, string strReason, string strReasonPrivate = null)
    {
        SingletonComponent<MetricsLogger>.Instance?.OnConnectionRejected(string.IsNullOrWhiteSpace(strReasonPrivate) ? strReason : strReasonPrivate);
    }
}
