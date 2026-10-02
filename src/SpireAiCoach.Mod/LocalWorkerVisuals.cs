using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;

namespace SpireAiCoach.Mod;

// Installed only after the owned worker executable and marker have been checked.
// Card/monster effects, hooks and action synchronization still run normally.
internal static class LocalWorkerVisuals
{
    public static bool Active { get; set; }
    public static void Install(Harmony harmony) => harmony.Patch(
        typeof(CreatureCmd).GetMethod(nameof(CreatureCmd.TriggerAnim))!,
        prefix: new HarmonyMethod(typeof(LocalWorkerVisuals).GetMethod(nameof(Animation), BindingFlags.NonPublic | BindingFlags.Static)!));

    private static bool Animation(ref Task __result)
    {
        if (!Active) return true;
        __result = Task.CompletedTask;
        return false;
    }
}
