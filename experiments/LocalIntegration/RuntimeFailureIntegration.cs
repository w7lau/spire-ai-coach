using System.Diagnostics;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Emit one original native log error after a real settled action. This observer
// exists only in the exact marker/executable-guarded private test process.
internal static class RuntimeFailureProbe
{
    private static string _root = "";
    private static string? _last;
    private static readonly FieldInfo Active = typeof(LocalWorker).GetField("_activeRequest", BindingFlags.Static | BindingFlags.NonPublic)!;
    public static void Install(string root)
    {
        if (!File.Exists(Path.Combine(root, ".coach-worker")) ||
            !string.Equals(OS.GetExecutablePath().Replace('/', '\\'), Path.Combine(root, "game", "SlayTheSpire2.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Runtime error probe requires the exact owned worker executable");
        _root = root;
        new Harmony("SpireLocalIntegration.owned-runtime-error").Patch(AccessTools.Method(typeof(LocalWorker), "Play"),
            postfix: new(AccessTools.Method(typeof(RuntimeFailureProbe), nameof(AfterPlay))));
    }
    private static void AfterPlay(ref Task<LocalAction> __result) => __result = Observe(__result);
    private static async Task<LocalAction> Observe(Task<LocalAction> task)
    {
        var action = await task;
        if (Active.GetValue(null) is LocalSearchRequest { VerifyCandidate: null } request && request.Id != _last &&
            (request.Id.StartsWith("runtime-all-", StringComparison.Ordinal) ||
                request.Id.StartsWith("runtime-peer-", StringComparison.Ordinal) && request.Partition == 1))
        {
            _last = request.Id;
            LocalWire.Write(Path.Combine(_root, "runtime-error-probe.json"), new { request.Id, timestamp = LocalTimeline.Timestamp });
            Log.Error("Owned runtime failure probe: original native error after settled action");
        }
        return action;
    }
}

internal static class RuntimeFailureIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest frozen, LocalInstallation installation)
    {
        var observer = Path.Combine(root, "game", "mods", "SpireLocalIntegration");
        var owned = installation with { MinimalWorkerBootstrap = true, ModDirectories = installation.ModDirectories.Append(observer).ToArray() };
        frozen = frozen with { LoadedMods = frozen.LoadedMods.Append(
            $"SpireLocalIntegration:0.0.1:{typeof(Entry).Assembly.ManifestModule.ModuleVersionId}").Order(StringComparer.Ordinal).ToArray() };
        var workers = (Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
        object Worker(int lane) => workers.GetValue(lane)!;
        Process? ProcessAt(int lane) => (Process?)Worker(lane).GetType().GetProperty("Process")!.GetValue(Worker(lane));
        string Generation(int lane) => (string)Worker(lane).GetType().GetProperty("Generation")!.GetValue(Worker(lane))!;
        string WorkerRoot(int lane) => (string)Worker(lane).GetType().GetProperty("Root")!.GetValue(Worker(lane))!;
        LocalSearchRequest Request(string prefix, LocalSearchOrder order, int count = 2) => frozen with {
            Id = prefix + Guid.NewGuid().ToString("N"), TimelineOrigin = 0, InitialTrace = null,
            Workers = count, Partitions = count, Partition = 0, SearchOrder = order, MaxNodes = 2, BudgetSeconds = 20,
            StopOnZeroLoss = false, StopOnFirstWin = false, InitialPlan = null, VerifyCandidate = null,
            DataOnlyCombat = true, DataOnlyRun = true, NumericalExecution = true, ShareSearchWork = false,
            TurnWorkPipe = null, SearchWorkPipe = null, MinimumLossPipe = null, ContinueOptimization = false,
            DeferVerification = false, SkipFinalVerification = false, FastVerification = false };
        var samples = new List<object>();
        void Save() => LocalWire.Write(Path.Combine(root, "integration-runtime-summary.json"), new {
            version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3), samples, passed = true,
            scope = "Frozen incident; bounded owned native search; native log injection after a real action; independent ordinary replay" });
        void Valid(LocalSearchResult result)
        {
            if (result.Best is not { Won: true, Dead: false } candidate || candidate.Continuation?.Length != candidate.Actions.Length ||
                result.Timing?.Verifications != 1 || result.Trace!.Spans.Any(s => s.Phase == "fallback"))
                throw new InvalidOperationException("Healthy native candidate or independent ordinary replay was lost");
        }
        await pool.Prepare(owned, 2, CancellationToken.None);
        foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
        {
            await pool.Prepare(owned, 2, CancellationToken.None);
            int healthy = ProcessAt(0)!.Id; string generation = Generation(0);
            int faulty = ProcessAt(1)!.Id;
            var result = await pool.Analyze(Request("runtime-peer-", order), owned, _ => { }, CancellationToken.None);
            LocalWire.Write(Path.Combine(root, $"integration-runtime-{order}-failure-private.json"), result);
            Valid(result);
            var failures = result.RecoveredFailures ?? [];
            if (result.Status != "partial" || failures.Length != 1 || failures[0].Category != "local_runtime" ||
                failures[0].Worker != 1 || !failures[0].Message.Contains("Owned runtime failure probe") ||
                !failures[0].Stack.Contains(nameof(RuntimeFailureProbe)) || ProcessAt(1) != null ||
                ProcessAt(0)!.Id != healthy || Generation(0) != generation ||
                result.Trace!.Spans.Count(s => s.Phase == "retire") != 1)
                throw new InvalidOperationException("Logged native error was not isolated to its exact owner with original evidence");
            var emission = LocalWire.Read<System.Text.Json.JsonElement>(Path.Combine(WorkerRoot(1), "runtime-error-probe.json"));
            double loggedMs = (emission.GetProperty("timestamp").GetInt64() - result.Trace.OriginTimestamp) * 1000d / Stopwatch.Frequency;
            double detectionMs = result.Trace.Spans.Single(s => s.Phase == "runtime_error").StartMs - loggedMs;
            if (detectionMs < 0 || detectionMs > 5000)
                throw new InvalidOperationException("Native error was left running until the complete search budget");
            var stored = LocalWire.Read<LocalSearchResult>(Path.Combine(WorkerRoot(1), "last-data-failure.json"));
            if (stored.Failure != failures[0] || stored.Best != null)
                throw new InvalidOperationException("Persistent native failure evidence was incomplete");
            var next = await pool.Analyze(Request("runtime-healthy-", order), owned, _ => { }, CancellationToken.None);
            LocalWire.Write(Path.Combine(root, $"integration-runtime-{order}-recovery-private.json"), next);
            Valid(next);
            if (next.Status != "done" || next.RecoveredFailures is { Length: > 0 } || ProcessAt(0)!.Id != healthy ||
                Generation(0) != generation || ProcessAt(1)!.Id == faulty || next.Trace!.Spans.Count(s => s.Phase == "rebuild") != 1)
                throw new InvalidOperationException("The following search reconstructed a healthy native resource");
            samples.Add(new { algorithm = order.ToString(), failedOwner = 1, failureCount = 1, detectionMs,
                healthyPidAndGenerationRetained = true, onlyFailedOwnerRebuilt = true,
                failedSteps = result.Best!.Actions.Length, recoveredSteps = next.Best!.Actions.Length,
                ordinaryNativeReplays = 2, fallback = false }); Save();
        }
        var lone = Request("runtime-all-", LocalSearchOrder.MonteCarlo, 1);
        await pool.Prepare(owned, 1, CancellationToken.None);
        int starts = pool.Resources().Starts;
        try { await pool.Analyze(lone, owned, _ => { }, CancellationToken.None); throw new InvalidOperationException("Logged native failure became usable"); }
        catch (CoachException error) when (error.Category == "local_runtime")
        {
            var failures = error.Data["local_failures"] as LocalSimulationFailure[] ?? [];
            if (failures.Length != 1 || failures[0].Category != "local_runtime" || failures[0].Worker != 0 ||
                !failures[0].Stack.Contains(nameof(RuntimeFailureProbe)) || pool.Resources().Starts != starts ||
                ((LocalTrace)error.Data["local_trace"]!).Spans.Any(s => s.Phase == "fallback"))
                throw new InvalidOperationException("Lone native error lost its evidence or launched an automatic compatibility search");
            samples.Add(new { loneFailureRejected = true, failureCount = 1, newCompatibilityProcesses = 0 }); Save();
        }
    }
}
