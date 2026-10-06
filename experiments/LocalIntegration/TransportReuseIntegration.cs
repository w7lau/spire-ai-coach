using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// A six-second producer stall occurs only in a marker/executable-guarded owned
// test worker, after it acquires its publication lease. No game effect is patched.
internal static class TransportPressureProbe
{
    private static string _root = "";
    private static string? _last;
    private static readonly FieldInfo Active = typeof(LocalWorker).GetField("_activeRequest", BindingFlags.Static | BindingFlags.NonPublic)!;
    public static void Install(string root)
    {
        if (!File.Exists(Path.Combine(root, ".coach-worker")) ||
            !string.Equals(OS.GetExecutablePath().Replace('/', '\\'), Path.Combine(root, "game", "SlayTheSpire2.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Transport pressure requires the exact owned worker executable");
        _root = root;
        new Harmony("SpireLocalIntegration.owned-transport-pressure").Patch(
            AccessTools.Method(typeof(LocalWire), nameof(LocalWire.WriteJson)),
            prefix: new(AccessTools.Method(typeof(TransportPressureProbe), nameof(Publication))));
    }
    private static void Publication(string path, string json, ref Func<string, IDisposable?>? measure)
    {
        if (path != Path.Combine(_root, "result.json") ||
            Active.GetValue(null) is not LocalSearchRequest { VerifyCandidate: null, ReplayRootOnly: false } request ||
            request.Id == _last || !json.Contains("\"Status\":\"running\"", StringComparison.Ordinal)) return;
        _last = request.Id;
        var original = measure;
        measure = operation =>
        {
            if (operation == "Write")
            {
                var timer = Stopwatch.StartNew(); Thread.Sleep(6000);
                File.WriteAllText(Path.Combine(_root, "transport-pressure.json"),
                    JsonSerializer.Serialize(new { request.Id, heldMs = timer.ElapsedMilliseconds }));
            }
            return original?.Invoke(operation);
        };
    }
}

internal static class TransportReuseIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest frozen, LocalInstallation installation)
    {
        var observer = Path.Combine(root, "game", "mods", "SpireLocalIntegration");
        var ownedInstallation = installation with { MinimalWorkerBootstrap = true,
            ModDirectories = installation.ModDirectories.Append(observer).ToArray() };
        frozen = frozen with { LoadedMods = frozen.LoadedMods.Append(
            $"SpireLocalIntegration:0.0.1:{typeof(Entry).Assembly.ManifestModule.ModuleVersionId}").Order(StringComparer.Ordinal).ToArray() };
        var samples = new List<object>();
        var workers = (Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
        var worker = workers.GetValue(0)!;
        int Pid() => ((Process)worker.GetType().GetProperty("Process")!.GetValue(worker)!).Id;
        string WorkerRoot() => (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        await pool.Prepare(ownedInstallation, 1, CancellationToken.None);
        int pid = Pid(); string generation = (string)worker.GetType().GetProperty("Generation")!.GetValue(worker)!;
        foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier, LocalSearchOrder.MonteCarlo })
        {
            var request = frozen with { Id = Guid.NewGuid().ToString("N"), Workers = 1, Partitions = 1, Partition = 0,
                SearchOrder = order, MaxNodes = 2, BudgetSeconds = 20, MaxRounds = frozen.MaxRounds,
                StopOnZeroLoss = false, StopOnFirstWin = false, InitialPlan = null, VerifyCandidate = null,
                DataOnlyCombat = true, DataOnlyRun = true, NumericalExecution = true,
                ShareSearchWork = false, TurnWorkPipe = null, SearchWorkPipe = null, MinimumLossPipe = null,
                ContinueOptimization = false, DeferVerification = false, SkipFinalVerification = false, FastVerification = false };
            var timer = Stopwatch.StartNew();
            var result = await pool.Analyze(request, ownedInstallation, _ => { }, CancellationToken.None);
            LocalWire.Write(Path.Combine(root, $"integration-transport-{samples.Count}-private.json"), result);
            if (result.Status != "done" || result.Failure != null || result.RecoveredFailures is { Length: > 0 } ||
                result.Rejected != 0 || result.Best?.Continuation?.Length != result.Best?.Actions.Length || result.Best == null ||
                result.Trace?.Spans.Any(s => s.Phase is "retire" or "fallback" or "rebuild" or "cold_start") == true ||
                Pid() != pid || (string)worker.GetType().GetProperty("Generation")!.GetValue(worker)! != generation)
                throw new InvalidOperationException("Transport pressure interrupted native search/verification or recreated the owned process");
            var pressure = LocalWire.Read<JsonElement>(Path.Combine(WorkerRoot(), "transport-pressure.json"));
            if (pressure.GetProperty("Id").GetString() != request.Id || pressure.GetProperty("heldMs").GetInt64() < 5900)
                throw new InvalidOperationException("Publication stall was not exercised");
            samples.Add(new { algorithm = order.ToString(), result.Evaluated, steps = result.Best.Actions.Length,
                result.Best.Won, result.Best.Dead, result.Best.Hp, elapsedMs = timer.ElapsedMilliseconds,
                heldMs = pressure.GetProperty("heldMs").GetInt64(), samePidAndGeneration = true,
                independentOrdinaryReplay = result.Timing?.Verifications == 1,
                nativeCheckpointsMatch = true, fallbackOrRebuild = false });
            LocalWire.Write(Path.Combine(root, "integration-transport-summary.json"), new {
                version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3), samples,
                scope = "Frozen incident; one owned resident worker; bounded search; six-second publication pressure; independent normal replay",
                passed = true });
        }
    }
}
