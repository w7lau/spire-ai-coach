using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.TestSupport;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

internal static class PassiveFactoryIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation, string seedPath)
    {
        var assembly = typeof(LocalWorker).Assembly;
        assembly.GetType("SpireAiCoach.Mod.LocalWorkerOverhead", true)!.GetMethod("Install")!.Invoke(null, null);
        var active = assembly.GetType("SpireAiCoach.Mod.LocalWorkerDataMode", true)!.GetProperty("Active")!;
        if (TestMode.IsOn || (bool)active.GetValue(null)!) throw new InvalidOperationException("Expected ordinary native host");
        bool originalSceneRequired = false;
        try { NGroundFireVfx.Create(null!); }
        catch (NullReferenceException) { originalSceneRequired = true; }
        if (!originalSceneRequired) throw new InvalidOperationException("Ordinary factory was unexpectedly suppressed");
        active.SetValue(null, true);
        try { if (NGroundFireVfx.Create(null!) != null) throw new InvalidOperationException("Numerical visual was created"); }
        finally { active.SetValue(null, false); }
        if (TestMode.IsOn) throw new InvalidOperationException("Global TestMode changed");

        var seed = LocalWire.Read<LocalSearchResult>(seedPath).Best ?? throw new InvalidOperationException("Missing measured route");
        int fire = Array.FindIndex(seed.Actions, a => a.ModelId == "CARD.FORGOTTEN_RITUAL");
        int end = Array.FindIndex(seed.Actions, fire + 1, a => a.EndTurn);
        if (fire < 0 || end < 0 || seed.Actions[0].BeforeHash != request.NativeHash)
            throw new InvalidOperationException("Expected frozen measured prefix through the failing factory");
        var prefix = seed.Actions.Take(end + 1).ToArray();
        installation = installation with { GameDirectory = Path.Combine(root, "game"), MinimalWorkerBootstrap = true };
        await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
        var worker = ((Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pool)!).GetValue(0)!;
        var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        var process = (Process)worker.GetType().GetProperty("Process")!.GetValue(worker)!;
        int pid = process.Id;
        var command = request with { Workers = 1, Partition = 0, Partitions = 1, MaxNodes = 1, MaxDepth = prefix.Length,
            MaxRounds = prefix[^1].Round, InitialPlan = prefix, VerifyCandidate = null, RecordedReplayProbe = null,
            ShareSearchWork = false, SearchWorkPipe = null, TurnWorkPipe = null, MinimumLossPipe = null, ProgressPipe = null,
            StopOnZeroLoss = false, StopOnFirstWin = false, DeferVerification = true, SkipFinalVerification = true };
        LocalCandidate? baseline = null;
        var samples = new List<object>();
        foreach (var (numerical, order) in new[] { (false, LocalSearchOrder.MonteCarlo),
            (true, LocalSearchOrder.MonteCarlo), (true, LocalSearchOrder.MonteCarlo), (true, LocalSearchOrder.TurnFrontier) })
        {
            var next = command with { Id = Guid.NewGuid().ToString("N"), DataOnlyCombat = numerical, DataOnlyRun = numerical,
                NumericalExecution = numerical, TrimWorkerOverhead = numerical, SearchOrder = order };
            var sample = await Submit(next);
            var best = sample.Best ?? throw new InvalidOperationException(sample.Message);
            if (sample.Status != "searched" || sample.Evaluated != 1 || sample.Rejected != 0 ||
                best.Actions.Length != prefix.Length || best.Continuation?.Length != prefix.Length)
                throw new InvalidOperationException("Frozen factory prefix did not settle: " + sample.Message);
            baseline ??= best;
            if (JsonSerializer.Serialize(best.Actions) != JsonSerializer.Serialize(baseline.Actions) ||
                JsonSerializer.Serialize(best.Continuation) != JsonSerializer.Serialize(baseline.Continuation) ||
                (best.Hp, best.HpLost, best.Gold, best.MaxHp, best.EnemyHp, best.Won, best.Dead, best.DamageSources, best.HealthChanges) !=
                (baseline.Hp, baseline.HpLost, baseline.Gold, baseline.MaxHp, baseline.EnemyHp, baseline.Won, baseline.Dead,
                    baseline.DamageSources, baseline.HealthChanges))
                throw new InvalidOperationException("Factory suppression changed native state/RNG/history/health");
            long skipped = sample.Trace?.Methods?.Where(m => m.Method == "NGroundFireVfx.Create").Sum(m => m.Skipped) ?? 0;
            if (numerical && skipped != 1 || !numerical && skipped != 0)
                throw new InvalidOperationException("Native ground-fire factory was not exercised");
            if (((Process)worker.GetType().GetProperty("Process")!.GetValue(worker)!).Id != pid)
                throw new InvalidOperationException("Worker was reconstructed");
            using var idle = JsonDocument.Parse(File.ReadAllText(Path.Combine(workerRoot, "idle-runtime.json")));
            using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(workerRoot, "runtime.json")));
            if (idle.RootElement.GetProperty("max_fps").GetInt32() != 10 || runtime.RootElement.GetProperty("max_fps").GetInt32() != 240 ||
                runtime.RootElement.GetProperty("overhead").GetProperty("failures").GetArrayLength() != 0)
                throw new InvalidOperationException("Idle cap leaked into search or factory installation failed");
            LocalWire.Write(Path.Combine(root, "integration-passive-private-" + samples.Count + ".json"), sample);
            samples.Add(new { numerical, order = order.ToString(), sample.ElapsedMs, sample.Timing, steps = best.Actions.Length,
                skipped, nativeStateRngHistoryAndHealthMatch = true, idleMaxFps = 10, searchMaxFps = 240, reusedWorker = samples.Count > 0 });
        }
        process.Refresh(); var beforeCpu = process.TotalProcessorTime;
        await Task.Delay(2000); process.Refresh();
        var idleCpuMs = (process.TotalProcessorTime - beforeCpu).TotalMilliseconds;
        LocalWire.Write(Path.Combine(root, "integration-passive-summary.json"), new {
            version = assembly.GetName().Version!.ToString(3), nativeModule = typeof(NGroundFireVfx).Assembly.ManifestModule.ModuleVersionId,
            fixedPreviouslyMeasuredPrefix = true, fullSearchBenchmark = false, productionBudgetsUnchanged = true,
            ordinaryFactoryPreserved = true, numericalFactorySuppressed = true, idleCpuMs, idleMeasuredWallMs = 2000,
            samples, passed = true });

        async Task<LocalSearchResult> Submit(LocalSearchRequest next)
        {
            LocalWire.Write(Path.Combine(workerRoot, "request.json"), next);
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed.TotalSeconds < 90)
            {
                await Task.Delay(100);
                if (!File.Exists(Path.Combine(workerRoot, "result.json"))) continue;
                var sample = LocalWire.Read<LocalSearchResult>(Path.Combine(workerRoot, "result.json"));
                if (sample.Id != next.Id || sample.Status == "running") continue;
                if (sample.Status is "failed" or "unsupported" or "partial") throw new InvalidOperationException(sample.Message);
                var idlePath = Path.Combine(workerRoot, "idle.json");
                if (!File.Exists(idlePath) || LocalWire.Read<LocalWorkerIdle>(idlePath).Id != next.Id) continue;
                if ((bool)worker.GetType().GetMethod("GameErrors")!.Invoke(worker, null)!)
                    throw new InvalidOperationException("Owned worker reported a runtime error");
                return sample;
            }
            throw new TimeoutException("Frozen factory prefix did not finish");
        }
    }
}
