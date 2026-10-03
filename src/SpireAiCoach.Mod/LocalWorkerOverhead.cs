using System.Reflection;
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
using SpireAiCoach.Core;

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
    public static bool Active => Enabled && (LocalWorkerDataMode.Active && LocalWorkerDataMode.MinimalRun || LocalWorkerVerification.Active);
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
