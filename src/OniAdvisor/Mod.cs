using HarmonyLib;
using KMod;

namespace OniAdvisor
{
    /// <summary>Entry point. The base OnLoad applies every [HarmonyPatch] in this assembly.</summary>
    public sealed class OniAdvisorMod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            base.OnLoad(harmony);
            ModOptions.Self = mod;
            Log.Info("loaded; output folder: " + Paths.LiveDir);
        }
    }

    internal static class Log
    {
        public static void Info(string msg) => Debug.Log("[OniAdvisor] " + msg);
        public static void Warn(string msg) => Debug.LogWarning("[OniAdvisor] " + msg);
    }
}
