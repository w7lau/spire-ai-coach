using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Replay only the incident's first enemy turn, in one owned worker. Compare
// ordinary native scene execution with scene-free execution, not search quality.
internal static class SummonPresentationIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request, LocalInstallation installation)
    {
        var mode = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalWorkerDataMode", true)!;
        mode.GetMethod("Install")!.Invoke(null, [null]);
        var boundaries = (string[])mode.GetProperty("SummonPresentationBoundaries")!.GetValue(null)!;
        if (boundaries.Length != 4) throw new InvalidOperationException("Native optional summon boundary coverage changed");
        installation = installation with { MinimalWorkerBootstrap = true };
        await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
        var worker = ((Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pool)!).GetValue(0)!;
        var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        var process = (Process)worker.GetType().GetProperty("Process")!.GetValue(worker)!;
        int pid = process.Id;
        if (!File.Exists(Path.Combine(workerRoot, ".coach-worker"))) throw new InvalidOperationException("Not an owned worker");
        var command = request with { Workers = 1, Partition = 0, Partitions = 1, MaxNodes = 1, MaxDepth = 1, MaxRounds = 1,
            InitialPlan = [new(-1, "", null, "", "", "", 1, EndTurn: true)],
            VerifyCandidate = null, RecordedReplayProbe = null, SearchOrder = LocalSearchOrder.MonteCarlo,
            ShareSearchWork = false, SearchWorkPipe = null, TurnWorkPipe = null, MinimumLossPipe = null, ProgressPipe = null,
            StopOnZeroLoss = false, StopOnFirstWin = false, DeferVerification = true, SkipFinalVerification = true };
        LocalCandidate? baseline = null;
        var samples = new List<object>();
        foreach (var numerical in new[] { false, true, true })
        {
            var next = command with { Id = Guid.NewGuid().ToString("N"), DataOnlyCombat = numerical, DataOnlyRun = numerical,
                NumericalExecution = numerical };
            var sample = await Submit(next);
            var best = sample.Best ?? throw new InvalidOperationException(sample.Message);
            if (sample.Status != "searched" || sample.Evaluated != 1 || sample.Rejected != 0 ||
                best.Actions.Length != 1 || !best.Actions[0].EndTurn || best.Continuation?.Length != 1)
                throw new InvalidOperationException("Incident first enemy turn did not settle: " + sample.Message);
            baseline ??= best;
            if (JsonSerializer.Serialize(best.Actions) != JsonSerializer.Serialize(baseline.Actions) ||
                JsonSerializer.Serialize(best.Continuation) != JsonSerializer.Serialize(baseline.Continuation) ||
                (best.Hp, best.HpLost, best.Gold, best.MaxHp, best.EnemyHp, best.Won, best.Dead, best.DamageSources) !=
                (baseline.Hp, baseline.HpLost, baseline.Gold, baseline.MaxHp, baseline.EnemyHp, baseline.Won, baseline.Dead, baseline.DamageSources))
                throw new InvalidOperationException("Optional node initialization changed native summon state/RNG/history");
            long skipped = sample.Trace?.Methods?.Where(m => m.Method == "Summon.OptionalNodeInitialization").Sum(m => m.Skipped) ?? 0;
            if (numerical && skipped < 2 || !numerical && skipped != 0)
                throw new InvalidOperationException("Native two-bot summon was not exercised through the intended display boundary");
            if (((Process)worker.GetType().GetProperty("Process")!.GetValue(worker)!).Id != pid)
                throw new InvalidOperationException("Short native comparison reconstructed its owned worker");
            LocalWire.Write(Path.Combine(root, "integration-summon-private-" + samples.Count + ".json"), sample);
            samples.Add(new { numerical, sample.ElapsedMs, sample.Timing, steps = best.Actions.Length, skipped,
                completeNativeCheckpoints = true, nativeStateRngAndHistoryMatch = true, reusedWorker = samples.Count > 0 });
        }
        LocalWire.Write(Path.Combine(root, "integration-summon-summary.json"), new {
            version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3),
            nativeModule = typeof(MegaCrit.Sts2.Core.Combat.CombatManager).Assembly.ManifestModule.ModuleVersionId,
            fixedIncidentFirstEnemyTurn = true, fullSearchBenchmark = false, productionBudgetsUnchanged = true,
            boundaries, samples, passed = true });

        async Task<LocalSearchResult> Submit(LocalSearchRequest next)
        {
            LocalWire.Write(Path.Combine(workerRoot, "request.json"), next);
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed.TotalSeconds < 40)
            {
                await Task.Delay(100);
                if (!File.Exists(Path.Combine(workerRoot, "result.json"))) continue;
                var sample = LocalWire.Read<LocalSearchResult>(Path.Combine(workerRoot, "result.json"));
                if (sample.Id != next.Id || sample.Status == "running") continue;
                if (sample.Status is "failed" or "unsupported" or "partial") throw new InvalidOperationException(sample.Message);
                var idle = Path.Combine(workerRoot, "idle.json");
                if (!File.Exists(idle) || LocalWire.Read<LocalWorkerIdle>(idle).Id != next.Id) continue;
                if ((bool)worker.GetType().GetMethod("GameErrors")!.Invoke(worker, null)!)
                    throw new InvalidOperationException("Owned native comparison reported a runtime error");
                return sample;
            }
            throw new TimeoutException("Owned first enemy turn did not finish");
        }
    }
}
