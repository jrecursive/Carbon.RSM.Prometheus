using HarmonyLib;
using Network;

namespace RustServerMetrics.HarmonyPatches;

[HarmonyPatch(typeof(Facepunch.Network.Raknet.Server), nameof(Facepunch.Network.Raknet.Server.Kick))]
internal static class RaknetServer_Kick_Patch
{
    [HarmonyPrefix]
    public static void Prefix(Connection cn, string message, bool logfile)
    {
        SingletonComponent<MetricsLogger>.Instance?.OnConnectionKick(cn, message);
    }
}
