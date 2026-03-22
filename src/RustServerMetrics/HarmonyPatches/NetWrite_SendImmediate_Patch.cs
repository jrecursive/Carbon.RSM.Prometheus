using HarmonyLib;
using Network;

namespace RustServerMetrics.HarmonyPatches
{
    [HarmonyPatch(typeof(NetWrite), nameof(NetWrite.SendImmediate))]
    public class NetWrite_SendImmediate_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(NetWrite __instance, SendInfo info)
        {
            SingletonComponent<MetricsLogger>.Instance?.OnNetWriteSend(__instance, info);
        }
    }
}
