using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;

namespace SpireAiCoach.Mod;

internal static class LocalWorkerResources
{
    // Native unloading schedules Dispose and can race a later branch loading the same paths.
    // Retain resource objects within one search; scene/model state is still rebuilt per branch.
    public static bool Retain { get; set; }
    public static bool FastCollection { get; set; }
    public static void Install(Harmony harmony)
    {
        var prefix = new HarmonyMethod(typeof(LocalWorkerResources).GetMethod(nameof(Unload), BindingFlags.NonPublic | BindingFlags.Static)!);
        harmony.Patch(typeof(AssetCache).GetMethod(nameof(AssetCache.UnloadAssets))!, prefix: prefix);
        harmony.Patch(typeof(AssetCache).GetMethod(nameof(AssetCache.UnloadMissedCacheAssets))!, prefix: prefix);
        // These full collections normally follow asset eviction. In this worker the assets
        // are deliberately retained; forcing a full heap scan after each room reload still
        // visits the same live resources. Natural .NET collections remain enabled.
        foreach (var name in new[] { nameof(PreloadManager.LoadRunAssets), nameof(PreloadManager.LoadActAssets), "LoadRoomAssets" })
        {
            var method = typeof(PreloadManager).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            var moveNext = method.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType
                .GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance)!;
            harmony.Patch(moveNext, transpiler: new(typeof(LocalWorkerResources).GetMethod(nameof(Collection), BindingFlags.NonPublic | BindingFlags.Static)!));
        }
    }
    private static bool Unload() => !Retain;
    private static IEnumerable<CodeInstruction> Collection(IEnumerable<CodeInstruction> instructions)
    {
        var original = typeof(GC).GetMethod(nameof(GC.Collect), Type.EmptyTypes)!;
        var replacement = typeof(LocalWorkerResources).GetMethod(nameof(CollectAfterPreload), BindingFlags.NonPublic | BindingFlags.Static)!;
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original)) { instruction.opcode = OpCodes.Call; instruction.operand = replacement; replaced++; }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("Unexpected native preload collection boundary");
    }
    private static void CollectAfterPreload()
    {
        using var timing = LocalWorker.TracePreloadCollection();
        if (!Retain || !FastCollection) GC.Collect();
    }
}
