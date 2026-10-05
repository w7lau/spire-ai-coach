using System.Reflection;
using System.Diagnostics;
using System.Runtime;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Used only after the executable and ownership marker have been verified.
// A tiny, separate trace survives a slow or failed startup before any search exists.
internal static class LocalWorkerStartup
{
    private static LocalTimeline? _timeline;
    private static string _path = "";
    private static int _index;
    private static string _generation = "";

    public static void Begin(string root)
    {
        long.TryParse(System.Environment.GetEnvironmentVariable("SPIRE_COACH_STARTUP_ORIGIN"), out var origin);
        int.TryParse(System.Environment.GetEnvironmentVariable("SPIRE_COACH_WORKER_INDEX"), out _index);
        _timeline = new(origin, capacity: 256);
        _generation = System.Environment.GetEnvironmentVariable("SPIRE_COACH_WORKER_GENERATION") ?? "standalone";
        _path = Path.Combine(root, "startup.json");
        if (origin > 0) _timeline.Add(new(_index, "prepare", "before_coach_init", "", 0, _timeline.ElapsedMs, Depth: 2));
    }

    public static IDisposable Measure(string phase)
    {
        Save(phase);
        return new Scope(_timeline!.Measure(_index, "prepare", phase, depth: 2));
    }

    public static void Install(string phase, Action action)
    {
        using var timing = Measure(phase);
        action();
    }

    public static void Ready() => Save("ready");

    public static void ProfileNative(Harmony harmony)
    {
        foreach (var method in new[] { AccessTools.Method(typeof(NGame), "InitPools"),
            AccessTools.Method(typeof(OneTimeInitialization), nameof(OneTimeInitialization.ExecuteEssential)),
            AccessTools.Method(typeof(ModManager), "CallModInitializer") })
        {
            if (method == null) continue;
            try
            {
                harmony.Patch(method, prefix: new(AccessTools.Method(typeof(LocalWorkerStartup), nameof(NativeStart))),
                    finalizer: new(AccessTools.Method(typeof(LocalWorkerStartup), nameof(NativeEnd))));
            }
            catch (Exception) { /* Timing cannot be required by game initialization. */ }
        }
    }

    private static void NativeStart(MethodBase __originalMethod, object[] __args, out IDisposable? __state) =>
        __state = _timeline?.Measure(_index, "prepare", "native_" + __originalMethod.Name,
            __args.FirstOrDefault() is Type type ? type.FullName ?? type.Name : "", depth: 2);
    private static void NativeEnd(IDisposable? __state) { __state?.Dispose(); Save("native_startup"); }

    private static void Save(string phase)
    {
        try
        {
            LocalStartupRuntime? runtime = null;
            if (phase == "ready")
            {
                using var process = Process.GetCurrentProcess();
                runtime = new(GCSettings.IsServerGC,
                    GC.GetConfigurationVariables().Where(p => p.Key.Contains("HeapCount", StringComparison.Ordinal) ||
                        p.Key.Contains("NoAffinitize", StringComparison.Ordinal)).ToDictionary(),
                    process.Threads.Count, process.PrivateMemorySize64, process.TotalProcessorTime.TotalMilliseconds,
                    GC.GetTotalPauseDuration().TotalMilliseconds,
                    ProjectSettings.GetSetting("threading/worker_pool/max_threads").AsInt32());
            }
            LocalWire.Write(_path, new LocalStartupReport(_generation,
                typeof(LocalWorker).Assembly.ManifestModule.ModuleVersionId, phase, _timeline!.Snapshot(), runtime));
        }
        // Startup diagnostics must not turn a healthy simulation into a failure.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class Scope(IDisposable timing) : IDisposable
    {
        public void Dispose() { timing.Dispose(); Save("between_phases"); }
    }
}
