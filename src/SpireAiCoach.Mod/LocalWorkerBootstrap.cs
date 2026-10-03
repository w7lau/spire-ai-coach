using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace SpireAiCoach.Mod;

// The game already supports starting without a main menu. Keep essential initialization,
// profiles, models and mods; load native common/run/room assets when restoring the battle.
internal static class LocalWorkerBootstrap
{
    public static void Install(Harmony harmony)
    {
        if (System.Environment.GetEnvironmentVariable("SPIRE_COACH_MINIMAL_BOOTSTRAP") != "1") return;
        // Mod initialization can occur inside the first GameStartup invocation.
        if (NGame.Instance != null) NGame.Instance.StartOnMainMenu = false;
        harmony.Patch(typeof(NGame).GetMethod("GameStartup", BindingFlags.NonPublic | BindingFlags.Instance)!,
            prefix: new(typeof(LocalWorkerBootstrap).GetMethod(nameof(Start), BindingFlags.NonPublic | BindingFlags.Static)!));
    }
    private static void Start(NGame __instance) => __instance.StartOnMainMenu = false;
}
