using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;

namespace SpireAiCoach.Mod;

internal static class LocalWorkerResources
{
    // Native unloading schedules Dispose and can race a later branch loading the same paths.
    // Retain resource objects within one search; scene/model state is still rebuilt per branch.
    public static bool Retain { get; set; }
    public static void Install(Harmony harmony)
    {
        var prefix = new HarmonyMethod(typeof(LocalWorkerResources).GetMethod(nameof(Unload), BindingFlags.NonPublic | BindingFlags.Static)!);
        harmony.Patch(typeof(AssetCache).GetMethod(nameof(AssetCache.UnloadAssets))!, prefix: prefix);
        harmony.Patch(typeof(AssetCache).GetMethod(nameof(AssetCache.UnloadMissedCacheAssets))!, prefix: prefix);
    }
    private static bool Unload() => !Retain;
}
