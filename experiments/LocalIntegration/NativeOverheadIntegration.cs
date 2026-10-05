using System.Diagnostics;
using System.Reflection;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// A bounded transport/execution check of the identical native route. It does
// not shorten product search settings or measure whole-search solution quality.
internal static class NativeOverheadIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation, string seedPath)
    {
        var seed = LocalWire.Read<LocalSearchResult>(seedPath).Best
            ?? throw new InvalidDataException("A frozen native candidate is required");
        if (seed.Actions.Length == 0 || seed.Actions[0].BeforeHash != request.NativeHash)
            throw new InvalidDataException("Native route and root differ");
        await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
        var worker = ((Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pool)!).GetValue(0)!;
        var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        if (!File.Exists(Path.Combine(workerRoot, ".coach-worker"))) throw new InvalidOperationException("Not an owned worker");
        var command = request with { Workers = 1, Partitions = 1, Partition = 0, MaxNodes = 1,
            SearchOrder = LocalSearchOrder.MonteCarlo, ShareSearchWork = false, SearchWorkPipe = null,
            TurnWorkPipe = null, RecordedReplayProbe = null, VerifyCandidate = null, InitialPlan = seed.Actions,
            DataOnlyCombat = true, DataOnlyRun = true, NumericalExecution = true, DeferVerification = true, StopOnZeroLoss = false };
        LocalCandidate? baseline = null, optimized = null;
        var samples = new List<object>();
        int sample = 0;
        foreach (var (fast, warm) in new[] { (false, true), (true, true), (true, false), (false, false) })
        {
            var result = await Submit(command with { ReuseDecisionFingerprint = fast, AsyncProgressOutput = fast });
            var best = result.Best ?? throw new InvalidOperationException(result.Message);
            if (result.Status != "searched" || result.Evaluated != 1 || result.Rejected != 0 ||
                LocalSearchWork.Key("expand", best.Actions) != LocalSearchWork.Key("expand", seed.Actions))
                throw new InvalidOperationException("Native execution diverged: " + result.Message);
            if (baseline == null) baseline = best;
            if ((best.Hp, best.HpLost, best.MaxHp, best.Gold, best.EnemyHp, best.Won, best.Dead, best.DamageSources) !=
                (baseline.Hp, baseline.HpLost, baseline.MaxHp, baseline.Gold, baseline.EnemyHp, baseline.Won, baseline.Dead, baseline.DamageSources))
                throw new InvalidOperationException("Execution shortcuts changed native settlement");
            var methods = result.Trace?.Methods ?? throw new InvalidDataException("Missing native method timings");
            var reused = methods.Where(m => m.Method == "LocalCapture.ReusedDecisionFingerprint").Sum(m => m.Skipped);
            if (fast && reused == 0) throw new InvalidOperationException("Decision reuse was not exercised");
            if (fast) optimized = best;
            LocalWire.Write(Path.Combine(root, $"integration-native-overhead-private-{sample++}.json"), result);
            samples.Add(new { fast, warm, result.ElapsedMs, steps = best.Actions.Length, reused,
                copies = methods.Where(m => m.Method == "NetFullCombatState.FromRun").Sum(m => m.Calls),
                fingerprint_ms = methods.Where(m => m.Method == "LocalCapture.Fingerprint").Sum(m => m.TotalMs),
                progress_enqueue_ms = methods.Where(m => m.Method == "LocalWorker.Progress").Sum(m => m.TotalMs),
                progress_write_ms = methods.Where(m => m.Method == "LocalWorker.ProgressWrite").Sum(m => m.TotalMs),
                best.Hp, best.HpLost, best.NetHpLoss, best.Won, best.Rounds });
        }
        var verified = await Submit(command with { InitialPlan = null, VerifyCandidate = optimized,
            DeferVerification = false, SkipFinalVerification = false, ReuseDecisionFingerprint = true });
        if (verified.Status != "done" || verified.Best?.Continuation?.Length != optimized!.Actions.Length ||
            verified.Trace!.Methods!.Any(m => m.Method == "LocalCapture.ReusedDecisionFingerprint" && m.Calls > 0))
            throw new InvalidOperationException("Independent native verification changed or reused a decision: " + verified.Message);
        LocalWire.Write(Path.Combine(root, "integration-native-overhead-verification-private.json"), verified);
        // Exercise the actual parent/worker wiring with two owned native lanes.
        // One attempt per lane bounds this mechanism check; product limits stay unchanged.
        var shared = await Task.Run(() => pool.Analyze(command with { Id = Guid.NewGuid().ToString("N"),
            Workers = 2, AdaptiveWorkers = false, ShareSearchWork = true, MemorySearchWork = true,
            MaxNodes = 1, SkipFinalVerification = true, InitialPlan = seed.Actions }, installation, _ => { }, CancellationToken.None));
        LocalWire.Write(Path.Combine(root, "integration-native-overhead-shared-private.json"), shared);
        var participants = shared.Trace?.Spans.Where(s => s.Stage == "search" && s.Phase == "session")
            .Select(s => s.Worker).Distinct().Order().ToArray() ?? [];
        if (shared.Status != "done" || shared.Workers != 2 || shared.Evaluated != 2 || shared.Rejected != 0 ||
            shared.Work is not { Claimed: >= 2, Completed: >= 2 } || !participants.SequenceEqual(new[] { 0, 1 }) ||
            shared.Best is not { } sharedBest || sharedBest.Continuation?.Length != sharedBest.Actions.Length)
            throw new InvalidOperationException("Owned native shared transport did not complete both lanes: " + shared.Message);
        LocalWire.Write(Path.Combine(root, "integration-native-overhead-summary.json"), new
        {
            samples, equivalent_native_history_and_settlement = true,
            independent_verified_steps = verified.Best.Continuation.Length,
            verified.Best.Hp, verification_ms = verified.ElapsedMs,
            shared_transport = new { shared.Workers, shared.Evaluated, shared.Rejected, shared.Work,
                participants, native_checkpoints = shared.Best!.Continuation!.Length },
            product_limits_unchanged = true, whole_search_speed_unmeasured = true
        });

        async Task<LocalSearchResult> Submit(LocalSearchRequest next)
        {
            next = next with { Id = Guid.NewGuid().ToString("N") };
            LocalWire.Write(Path.Combine(workerRoot, "request.json"), next);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < 120)
            {
                await Task.Delay(100);
                if (!File.Exists(Path.Combine(workerRoot, "result.json"))) continue;
                var result = LocalWire.Read<LocalSearchResult>(Path.Combine(workerRoot, "result.json"));
                if (result.Id != next.Id || result.Status == "running") continue;
                // Wait for writer drain, broker retirement and cleanup before reusing it.
                var idlePath = Path.Combine(workerRoot, "idle.json");
                if (File.Exists(idlePath) && LocalWire.Read<LocalWorkerIdle>(idlePath).Id == next.Id)
                {
                    if ((bool)worker.GetType().GetMethod("GameErrors")!.Invoke(worker, null)!)
                        throw new InvalidOperationException("Owned native worker reported a runtime error");
                    return result;
                }
                if (result.Status is "failed" or "unsupported" or "partial") throw new InvalidOperationException(result.Message);
            }
            throw new TimeoutException("Owned native overhead check did not finish");
        }
    }
}
