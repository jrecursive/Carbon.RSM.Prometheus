using HarmonyLib;
using Network;

namespace RustServerMetrics.HarmonyPatches
{
    [HarmonyPatch(typeof(NetWrite), nameof(NetWrite.PacketID))]
    public class NetWrite_PacketID_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(NetWrite __instance, Message.Type val)
        {
            SingletonComponent<MetricsLogger>.Instance?.OnNetWritePacketID(__instance, val);
        }
    }
}
