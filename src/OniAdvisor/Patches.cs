using HarmonyLib;

namespace OniAdvisor
{
    /// <summary>Attach the runner once a colony is loaded.</summary>
    [HarmonyPatch(typeof(Game), "OnSpawn")]
    internal static class Game_OnSpawn_Patch
    {
        static void Postfix(Game __instance)
        {
            if (__instance.gameObject.GetComponent<AdvisorRunner>() == null)
                __instance.gameObject.AddComponent<AdvisorRunner>();
        }
    }

    /// <summary>Snapshot + screenshot after every autosave (manual saves are ignored).</summary>
    [HarmonyPatch(typeof(SaveLoader), nameof(SaveLoader.Save), typeof(string), typeof(bool), typeof(bool))]
    internal static class SaveLoader_Save_Patch
    {
        static void Postfix(bool isAutoSave)
        {
            if (isAutoSave) AdvisorRunner.Instance?.OnAutosave();
        }
    }
}
