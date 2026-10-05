using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.TestSupport;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// A measured incident prefix through death, four summons and their first turn.
// This compares native execution modes, never search speed or search quality.
internal static class AnimationPresentationIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation, string seedPath)
    {
        var assembly = typeof(LocalWorker).Assembly;
        assembly.GetType("SpireAiCoach.Mod.LocalModelDisplay", true)!.GetMethod("Install")!.Invoke(null, null);
        var active = assembly.GetType("SpireAiCoach.Mod.LocalWorkerDataMode", true)!.GetProperty("Active")!;
        var deferred = typeof(InfestedPower).GetMethod("RevealWrigglersAfterDeathAnim", BindingFlags.NonPublic | BindingFlags.Static)!;
        if (TestMode.IsOn || (bool)active.GetValue(null)!) throw new InvalidOperationException("Expected ordinary native host");
        bool ordinaryMissingScene = false;
        try { await (Task)deferred.Invoke(null, [new List<Creature>(), 0f])!; }
        catch (NullReferenceException) { ordinaryMissingScene = true; }
        if (!ordinaryMissingScene) throw new InvalidOperationException("Ordinary deferred presentation was unexpectedly suppressed");
        active.SetValue(null, true);
        try { await (Task)deferred.Invoke(null, [new List<Creature>(), 0f])!; }
        finally { active.SetValue(null, false); }
        if (TestMode.IsOn) throw new InvalidOperationException("Global TestMode changed");

        var seed = LocalWire.Read<LocalSearchResult>(seedPath).Best ?? throw new InvalidOperationException("No measured route seed");
        // Include the next complete turn: its checkpoints cover the summons' turn,
        // while both search modes receive the same complete turn boundary.
        var prefix = seed.Actions.Take(16).ToArray();
        if (prefix.Length != 16 || !prefix[11].EndTurn || prefix[12].Round != 4 || !prefix[^1].EndTurn ||
            prefix[^1].Round != 4 || prefix[0].BeforeHash != request.NativeHash)
            throw new InvalidOperationException("Expected measured incident prefix through its first summon turn");
        installation = installation with { GameDirectory = Path.Combine(root, "game"), MinimalWorkerBootstrap = true };
        await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
        var worker = ((Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pool)!).GetValue(0)!;
        var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        int pid = ((Process)worker.GetType().GetProperty("Process")!.GetValue(worker)!).Id;
        if (!File.Exists(Path.Combine(workerRoot, ".coach-worker"))) throw new InvalidOperationException("Not an owned worker");
        var command = request with { Workers = 1, Partition = 0, Partitions = 1, MaxNodes = 1, MaxDepth = prefix.Length,
            MaxRounds = 4, InitialPlan = prefix, VerifyCandidate = null, RecordedReplayProbe = null,
            ShareSearchWork = false, SearchWorkPipe = null, TurnWorkPipe = null, MinimumLossPipe = null, ProgressPipe = null,
            StopOnZeroLoss = false, StopOnFirstWin = false, DeferVerification = true, SkipFinalVerification = true };
        LocalCandidate? baseline = null;
        var samples = new List<object>();
        foreach (var (numerical, order) in new[] { (false, LocalSearchOrder.MonteCarlo),
            (true, LocalSearchOrder.MonteCarlo), (true, LocalSearchOrder.MonteCarlo), (true, LocalSearchOrder.TurnFrontier) })
        {
            var next = command with { Id = Guid.NewGuid().ToString("N"), DataOnlyCombat = numerical, DataOnlyRun = numerical,
                NumericalExecution = numerical, SearchOrder = order };
            var sample = await Submit(next);
            var best = sample.Best ?? throw new InvalidOperationException(sample.Message);
            if (sample.Status != "searched" || sample.Evaluated != 1 || sample.Rejected != 0 ||
                best.Actions.Length != prefix.Length || best.Continuation?.Length != prefix.Length)
                throw new InvalidOperationException("Native incident prefix did not settle: " + sample.Message);
            baseline ??= best;
            if (JsonSerializer.Serialize(best.Actions) != JsonSerializer.Serialize(baseline.Actions) ||
                JsonSerializer.Serialize(best.Continuation) != JsonSerializer.Serialize(baseline.Continuation) ||
                (best.Hp, best.HpLost, best.Gold, best.MaxHp, best.EnemyHp, best.Won, best.Dead, best.DamageSources, best.HealthChanges) !=
                (baseline.Hp, baseline.HpLost, baseline.Gold, baseline.MaxHp, baseline.EnemyHp, baseline.Won, baseline.Dead,
                    baseline.DamageSources, baseline.HealthChanges))
                throw new InvalidOperationException("Optional presentation changed native state/RNG/history/health");
            int targets = best.Decisions?.Where(d => d.BeforeStep == 10).SelectMany(d => d.Legal)
                .Where(a => a.TargetName == "扭动虫").Select(a => a.TargetId).Distinct().Count() ?? 0;
            if (targets != 4) throw new InvalidOperationException("Native four-wriggler summon was not preserved");
            long skipped = sample.Trace?.Methods?.Where(m => m.Method == "InfestedPower.OptionalDisplay").Sum(m => m.Skipped) ?? 0;
            if (numerical && skipped != 1 || !numerical && skipped != 0)
                throw new InvalidOperationException("Deferred display boundary was not exercised");
            if (((Process)worker.GetType().GetProperty("Process")!.GetValue(worker)!).Id != pid)
                throw new InvalidOperationException("Owned worker was reconstructed");
            LocalWire.Write(Path.Combine(root, "integration-animation-private-" + samples.Count + ".json"), sample);
            samples.Add(new { numerical, order = order.ToString(), sample.ElapsedMs, sample.Timing, steps = best.Actions.Length,
                skipped, summonedTargets = targets, nativeStateRngHistoryAndHealthMatch = true, reusedWorker = samples.Count > 0 });
        }
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(workerRoot, "runtime.json")));
        var boundaries = runtime.RootElement.GetProperty("model_display").GetProperty("boundaries")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        if (!boundaries.Contains("InfestedPower.RevealWrigglersAfterDeathAnim") || !boundaries.Contains("BygoneEffigy.WakeMove"))
            throw new InvalidOperationException("Deferred power and monster display guards were not installed");
        LocalWire.Write(Path.Combine(root, "integration-animation-summary.json"), new {
            version = assembly.GetName().Version!.ToString(3), nativeModule = typeof(InfestedPower).Assembly.ManifestModule.ModuleVersionId,
            fixedPreviouslyMeasuredPrefix = true, fullSearchBenchmark = false, productionBudgetsUnchanged = true,
            ordinaryDeferredPresentationPreserved = true, numericalDeferredPresentationCompleted = true,
            boundaries, samples, passed = true });

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
                var idle = Path.Combine(workerRoot, "idle.json");
                if (!File.Exists(idle) || LocalWire.Read<LocalWorkerIdle>(idle).Id != next.Id) continue;
                if ((bool)worker.GetType().GetMethod("GameErrors")!.Invoke(worker, null)!)
                    throw new InvalidOperationException("Owned native comparison reported a runtime error");
                return sample;
            }
            throw new TimeoutException("Owned incident prefix did not finish");
        }
    }
}
