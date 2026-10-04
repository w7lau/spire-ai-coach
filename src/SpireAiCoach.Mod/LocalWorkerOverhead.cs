using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;
using SpireAiCoach.Core;
using Label = System.Reflection.Emit.Label;

namespace SpireAiCoach.Mod;

// Installed only after LocalWorker verifies its own isolated executable/marker.
// Skip presentation/file output and unused diagnostic snapshots during search.
// Keep model effects, replay events, RNG, external callbacks and native final verification.
internal static class LocalWorkerOverhead
{
    private static readonly Dictionary<MethodBase, string> Names = [];
    private static readonly List<string> Boundaries = [];
    private static readonly List<string> Failures = [];
    public static bool Enabled { get; set; }
    public static bool LeanSearchChecksums { get; set; }
    // A regular compatibility search also runs in an owned hidden process.
    // Its graphics/output are unnecessary even though its real scene is retained.
    // An explicitly slow independent replay retains the complete original path.
    public static bool Active => Enabled && (!LocalWorkerVerification.Running || LocalWorkerVerification.Active);
    public static object Status() => new { enabled = Enabled, active = Active, boundaries = Boundaries.ToArray(), failures = Failures.ToArray() };

    // Owned integration probes attach the same native event API available to Mods.
    // The normal search must retain that callback even when lean snapshots are on.
    public static IDisposable ObserveChecksums(ChecksumTracker tracker)
    {
        void Observe(NetChecksumData _, string context, NetFullCombatState state)
        { using var timing = LocalWorker.MeasureMethod("ChecksumTracker.ExternalListenerProbe"); }
        tracker.ChecksumGenerated += Observe;
        return new Subscription(() => tracker.ChecksumGenerated -= Observe);
    }
    private sealed class Subscription(Action remove) : IDisposable { public void Dispose() => remove(); }

    public static void Install()
    {
        PatchVoid(typeof(SfxCmd), ["Play", "PlayLoop", "SetParam", "PlayDamage", "PlayDeath", "PlayCardSwooshSfx"], nameof(Presentation));
        PatchVoid(typeof(NAudioManager), ["PlayOneShot", "PlayLoop", "SetParam"], nameof(Presentation));
        // Collection wrappers can enumerate caller-provided lazy sequences. Keep
        // them and skip their leaf calls, rather than suppressing that enumeration.
        PatchVoid(typeof(VfxCmd), ["PlayVfx", "PlayOnSide", "PlayOnCreature", "PlayOnCreatureCenter", "PlayFullScreenInCombat"], nameof(Presentation));
        PatchVoid(typeof(ConsoleLogPrinter), ["Print"], nameof(LogOutput));
        PatchVoid(typeof(CombatReplayWriter), ["WriteReplay"], nameof(ReplayOutput));
        Profile(typeof(ChecksumTracker), "GenerateChecksum");
        InstallLeanChecksum();
        Profile(typeof(NetFullCombatState), "FromRun");
        Profile(typeof(NetFullCombatState), "Serialize");
        Profile(typeof(PlayerCombatState), "RecalculateCardValues");
        Profile(typeof(AssetCache), "GetAsset");
        Profile(typeof(NCard), "UpdateVisuals");
        PatchVoid(typeof(NCard), ["UpdatePortrait", "UpdateTitleLabel", "UpdateEnergyCostVisuals",
            "UpdateStarCostVisuals", "UpdateEnchantmentVisuals", "UpdateTypePlaque", "SetEnchantmentStatus"], nameof(Presentation));
        InstallCardDescription();
        InstallOptionalVfxFactories();
    }

    private static void InstallOptionalVfxFactories()
    {
        var disabled = AccessTools.PropertyGetter(typeof(TestMode), nameof(TestMode.IsOn));
        // Native factories already promise that presentation may be absent. Extend
        // only that leading null guard, never the global TestMode or model hooks.
        // Form visuals have nullable power-owned receivers. Do not apply this to
        // card-flight factories: those nodes also own pile completion callbacks.
        foreach (var type in typeof(NCard).Assembly.GetTypes().Where(t =>
            t.Namespace == "MegaCrit.Sts2.Core.Nodes.Vfx.Forms" &&
            typeof(Node).IsAssignableFrom(t)))
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "Create" && typeof(Node).IsAssignableFrom(m.ReturnType) &&
                LocalPresentationGuard.OptionalNullFactory(m, disabled)))
        {
            string name = type.Name + ".Create";
            var harmony = new Harmony("SpireAiCoach.owned-worker.overhead.factory." + type.FullName + "." + method.MetadataToken);
            try
            {
                Names[method] = name;
                harmony.Patch(method, transpiler: new(AccessTools.Method(typeof(LocalWorkerOverhead), nameof(OptionalFactory))));
                Boundaries.Add(name);
            }
            catch (Exception ex)
            {
                harmony.UnpatchAll(harmony.Id);
                Failures.Add(name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    private static IEnumerable<CodeInstruction> OptionalFactory(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var code = instructions.ToList();
        var disabled = AccessTools.PropertyGetter(typeof(TestMode), nameof(TestMode.IsOn));
        int index = code.FindIndex(c => c.opcode != OpCodes.Nop);
        if (index < 0 || !code[index].Calls(disabled)) throw new InvalidOperationException("Native optional visual guard changed");
        var name = new CodeInstruction(OpCodes.Ldstr, Names[original]);
        name.labels.AddRange(code[index].labels); name.blocks.AddRange(code[index].blocks);
        code[index] = new(OpCodes.Call, AccessTools.Method(typeof(LocalWorkerOverhead), nameof(OptionalVisualsDisabled)));
        code.Insert(index, name);
        return code;
    }

    private static bool OptionalVisualsDisabled(string name)
    {
        bool native = TestMode.IsOn;
        bool skip = LocalWorkerDataMode.Active || Active;
        if (!native && skip) LocalWorker.SkipMethod(name);
        return native || skip;
    }

    private static void InstallCardDescription()
    {
        var harmony = new Harmony("SpireAiCoach.owned-worker.overhead.card-description");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(NCard), nameof(NCard.UpdateVisuals)),
                transpiler: new(AccessTools.Method(typeof(LocalWorkerOverhead), nameof(CardDescription))));
            Boundaries.Add("NCard.DescriptionPresentation");
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(harmony.Id);
            Failures.Add("NCard.DescriptionPresentation: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // Keep the original DisplayingPile assignment, dynamic previews and their Mod
    // hooks. Bypass only the following description formatting/text-layout block.
    private static IEnumerable<CodeInstruction> CardDescription(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var code = instructions.ToList();
        var forcePreview = AccessTools.Field(typeof(NCard), "_forceUnpoweredPreview");
        int index = code.FindIndex(c => c.opcode == OpCodes.Ldfld && Equals(c.operand, forcePreview));
        if (index < 0 || index + 1 >= code.Count || code[index + 1].opcode != OpCodes.Brtrue_S && code[index + 1].opcode != OpCodes.Brtrue ||
            code[index + 1].operand is not Label label) throw new InvalidOperationException("Native card preview boundary changed");
        int boundary = code.FindIndex(c => c.labels.Contains(label));
        var preview = AccessTools.Method(typeof(CardModel), "UpdateDynamicVarPreview");
        if (boundary <= index || code[boundary].opcode != OpCodes.Ldarg_2 || code.Any(c => c.blocks.Count != 0) ||
            code.Take(boundary).Count(c => c.Calls(preview)) != 2 || code.Skip(boundary).Any(c => c.Calls(preview)))
            throw new InvalidOperationException("Native card description no longer follows complete previews");
        var render = generator.DefineLabel();
        var guard = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LocalWorkerOverhead), nameof(RenderCardDescription)));
        guard.labels.AddRange(code[boundary].labels);
        code[boundary].labels.Clear(); code[boundary].labels.Add(render);
        code.InsertRange(boundary, [guard, new(OpCodes.Brtrue, render), new(OpCodes.Ret)]);
        return code;
    }

    private static bool RenderCardDescription()
    {
        if (!Active) return true;
        LocalWorker.SkipMethod("NCard.DescriptionPresentation");
        return false;
    }

    private static readonly FieldInfo? ChecksumListeners = AccessTools.Field(typeof(ChecksumTracker), "ChecksumGenerated");
    private static void InstallLeanChecksum()
    {
        var harmony = new Harmony("SpireAiCoach.owned-worker.overhead.lean-checksum");
        var method = AccessTools.Method(typeof(ChecksumTracker), "GenerateChecksum", [typeof(string), typeof(MegaCrit.Sts2.Core.GameActions.GameAction)]);
        if (method == null || ChecksumListeners == null) { Failures.Add("LeanChecksum: native boundary unavailable"); return; }
        try
        {
            // Harmony state belongs to the declaring patch class. Keep the bool
            // scope separate from this file's MethodScope profiling patches.
            harmony.Patch(method, prefix: new(AccessTools.Method(typeof(ChecksumBoundary), nameof(ChecksumBoundary.Pause))),
                finalizer: new(AccessTools.Method(typeof(ChecksumBoundary), nameof(ChecksumBoundary.Restore))));
            Boundaries.Add("ChecksumTracker.LeanSearch");
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(harmony.Id);
            Failures.Add("LeanChecksum: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static class ChecksumBoundary
    {
        public static void Pause(ChecksumTracker __instance, MethodBase __originalMethod, ref bool __state)
        {
            if (!LeanSearchChecksums || !Active || LocalWorkerVerification.Active || !__instance.IsEnabled ||
                MegaCrit.Sts2.Core.Runs.RunManager.Instance.NetService.Type != NetGameType.Singleplayer) return;
            // Recheck diagnostic subscribers on every call: a Mod can attach one
            // during a play. External callbacks and patches keep normal execution.
            if (ChecksumListeners!.GetValue(__instance) is Delegate listeners && listeners.GetInvocationList()
                .Any(d => d.Method.DeclaringType != typeof(CombatReplayWriter))) return;
            var patches = Harmony.GetPatchInfo(__originalMethod);
            if (patches != null && patches.Owners.Any(owner => !owner.StartsWith("SpireAiCoach.owned-worker.overhead.", StringComparison.Ordinal))) return;
            __state = true;
            __instance.IsEnabled = false;
            LocalWorker.SkipMethod("ChecksumTracker.SearchSnapshotSuppressed");
        }

        public static void Restore(ChecksumTracker __instance, bool __state)
        {
            if (__state) __instance.IsEnabled = true;
        }
    }

    private static void PatchVoid(Type type, string[] names, string prefix)
    {
        foreach (var name in names) InstallBoundary(type, name, prefix, requireVoid: true);
    }
    private static void Profile(Type type, string name) => InstallBoundary(type, name, nameof(Timing), requireVoid: false);
    private static void InstallBoundary(Type type, string name, string prefix, bool requireVoid)
    {
        var harmony = new Harmony("SpireAiCoach.owned-worker.overhead." + type.Name + "." + name);
        try
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == name && !m.IsGenericMethod && (!requireVoid || m.ReturnType == typeof(void))).ToArray();
            if (methods.Length == 0) throw new MissingMethodException(type.FullName, name);
            foreach (var method in methods)
            {
                Names[method] = type.Name + "." + name;
                harmony.Patch(method, prefix: new(AccessTools.Method(typeof(LocalWorkerOverhead), prefix)),
                    finalizer: new(AccessTools.Method(typeof(LocalWorkerOverhead), nameof(FinishTiming))));
            }
            Boundaries.Add(type.Name + "." + name);
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(harmony.Id);
            Failures.Add(type.Name + "." + name + ": " + ex.GetType().Name + ": " + ex.Message);
            Godot.GD.Print("[SpireAiCoach] Optional worker boundary unavailable: " + type.Name + "." + name + ": " + ex.Message);
        }
    }
    private static bool Begin(MethodBase method, bool skip, ref LocalTimeline.MethodScope state)
    {
        if (skip) LocalWorker.SkipMethod(Names[method]);
        else state = LocalWorker.MeasureMethod(Names[method]);
        return !skip;
    }
    private static bool Presentation(MethodBase __originalMethod, ref LocalTimeline.MethodScope __state) =>
        Begin(__originalMethod, Active, ref __state);
    private static bool LogOutput(LogLevel logLevel, MethodBase __originalMethod, ref LocalTimeline.MethodScope __state) =>
        Begin(__originalMethod, Active && logLevel is not (LogLevel.Warn or LogLevel.Error), ref __state);
    private static bool ReplayOutput(CombatReplayWriter __instance, bool stopRecording, MethodBase __originalMethod,
        ref LocalTimeline.MethodScope __state)
    {
        // Preserve native disabled/not-started behavior, including its exception.
        if (!Active || !__instance.IsEnabled || !__instance.IsRecordingReplay)
            return Begin(__originalMethod, false, ref __state);
        Begin(__originalMethod, true, ref __state);
        if (stopRecording) __instance.StopRecording();
        return false;
    }
    private static void Timing(MethodBase __originalMethod, ref LocalTimeline.MethodScope __state) =>
        __state = LocalWorker.MeasureMethod(Names[__originalMethod]);
    // Finalizer observes both success and exception and never replaces/swallows it.
    private static void FinishTiming(LocalTimeline.MethodScope __state) => __state.Dispose();
}
