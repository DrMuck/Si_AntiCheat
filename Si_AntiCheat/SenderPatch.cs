using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace Si_AntiCheat
{
    /// <summary>
    /// The damage packet handler never learns who sent the packet. NetworkLayer.ProcessMessage (private static) gets
    /// the sender as "remoteID"; this prefix parks it in a static for the duration of the call so the damage handler
    /// can check that the sender owns the instigator it names. Nothing else is changed.
    /// </summary>
    public static class SenderPatch
    {
        public static bool Applied;
        public static bool HaveSender;
        public static NetworkID Current;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo? m = AccessTools.Method(typeof(NetworkLayer), "ProcessMessage");
                if (m == null)
                {
                    MelonLogger.Warning("[AC] NetworkLayer.ProcessMessage not found: sender check disabled");
                    return;
                }
                harmony.Patch(m,
                    prefix: new HarmonyMethod(typeof(SenderPatch), nameof(Prefix)),
                    postfix: new HarmonyMethod(typeof(SenderPatch), nameof(Postfix)));
                Applied = true;
                MelonLogger.Msg("[AC] sender check active (NetworkLayer.ProcessMessage prefix)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[AC] sender check patch failed: " + ex.Message);
            }
        }

        static void Prefix(NetworkID remoteID, bool isServer)
        {
            if (!AntiCheatMod.Armed) return;
            Current = remoteID;
            HaveSender = isServer;
        }

        static void Postfix()
        {
            HaveSender = false;
        }
    }
}
