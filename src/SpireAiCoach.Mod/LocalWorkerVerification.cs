using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.addons.mega_text;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Installed only in an owned worker. Verification still builds the real scene and
// uses its native executor, choices, state notifications, effects and checksums.
internal static class LocalWorkerVerification
{
    private static readonly List<string> Boundaries = [];
    private static readonly List<string> Failures = [];
    public static bool Active { get; private set; }
    public static bool Available => Failures.Count == 0 && Boundaries.Count == 4;
    public static string? Fallback { get; set; }
    public static bool UsedFast { get; private set; }
    public static void Reset() { if (Active) throw new InvalidOperationException("Verification scope still active"); UsedFast = false; Fallback = null; }
    public static object Status() => new { active = Active, available = Available, used_fast = UsedFast, fallback = Fallback,
        boundaries = Boundaries.ToArray(), failures = Failures.ToArray() };

    public static void Install()
    {
        Patch(typeof(MegaLabel), "AdjustFontSize", nameof(Font));
        Patch(typeof(MegaRichTextLabel), "AdjustFontSize", nameof(Font));
        Patch(typeof(RunManager), nameof(RunManager.FadeIn), nameof(Fade));
        Patch(typeof(RunManager), nameof(RunManager.FadeOut), nameof(Fade));
    }

    private static void Patch(Type type, string name, string prefix)
    {
        var key = type.Name + "." + name;
        var harmony = new Harmony("SpireAiCoach.owned-worker.verify." + key);
        try
        {
            var method = AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
            if (method.ReturnType != (prefix == nameof(Font) ? typeof(void) : typeof(Task)))
                throw new InvalidOperationException("Unexpected native presentation signature");
            harmony.Patch(method, prefix: new(AccessTools.Method(typeof(LocalWorkerVerification), prefix)),
                finalizer: new(AccessTools.Method(typeof(LocalWorkerVerification), nameof(FinishTiming))));
            Boundaries.Add(key);
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(harmony.Id);
            Failures.Add(key + ": " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    public static IDisposable Begin(bool fast)
    {
        if (Active) throw new InvalidOperationException("Nested final verification scope");
        return new Scope(fast && Available && LocalWorkerVisuals.Active);
    }

    private sealed class Scope : IDisposable
    {
        private readonly bool _preload;
        private bool _disposed;
        public Scope(bool fast)
        {
            _preload = PreloadManager.Enabled;
            Active = fast;
            UsedFast |= fast;
            // This is the native supported lazy-loading path. Keep the preload calls
            // and AssetSets bookkeeping; Cache.GetAsset still loads needed resources.
            if (fast) PreloadManager.Enabled = false;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            PreloadManager.Enabled = _preload;
            Active = false;
        }
    }

    private static bool Timing(MethodBase method, bool skip, ref LocalTimeline.MethodScope state)
    {
        var name = method.DeclaringType!.Name + "." + method.Name;
        if (skip) LocalWorker.SkipMethod(name);
        else state = LocalWorker.MeasureMethod(name);
        return !skip;
    }
    // Keep SetTextAutoSize, its text assignment, dynamic card previews and node
    // lifecycle. Only omit the font-fitting binary search in this hidden scene.
    private static bool Font(MethodBase __originalMethod, ref LocalTimeline.MethodScope __state) =>
        Timing(__originalMethod, Active, ref __state);
    private static bool Fade(MethodBase __originalMethod, ref LocalTimeline.MethodScope __state, ref Task __result)
    {
        var keep = Timing(__originalMethod, Active && !TestMode.IsOn, ref __state);
        if (!keep) __result = Task.CompletedTask;
        return keep;
    }
    private static void FinishTiming(LocalTimeline.MethodScope __state) => __state.Dispose();
}
