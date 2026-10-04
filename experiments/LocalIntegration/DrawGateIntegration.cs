using System.Diagnostics;
using System.Reflection;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// One prescribed route, then native verification. Budgets here bound a private
// regression fixture; they do not replace any player's search configuration.
internal static class DrawGateIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation, string seedPath, string reportPath)
    {
        var seed = LocalWire.Read<LocalSearchResult>(seedPath).Best!;
        var actual = LocalWire.Read<LocalExecutionReport>(reportPath);
        var observed = LocalWire.Read<LocalExecutionReport>(Path.Combine(root, "integration-execution-replay-private.json"));
        if (actual.Stage != "checkpoint" || actual.ActualHash == actual.ExpectedHash || actual.SettledActions == 0 ||
            actual.RequestId != request.Id && actual.Plan[0].BeforeHash != request.NativeHash)
            throw new InvalidDataException("A matching frozen execution divergence is required");
        if (observed.Stage != actual.Stage || observed.SettledActions != actual.SettledActions || observed.ActualHash != actual.ActualHash)
            throw new InvalidOperationException("Learning observations still changed the actual native draw restriction");
        File.Copy(Path.Combine(root, "integration-execution-replay-private.json"), Path.Combine(root, "integration-draw-gate-original-private.json"));
        File.Copy(Path.Combine(root, "integration-execution-draw-private.json"), Path.Combine(root, "integration-draw-gate-original-draw-private.json"));
        await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
        var worker = ((Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pool)!).GetValue(0)!;
        var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        if (!File.Exists(Path.Combine(workerRoot, ".coach-worker"))) throw new InvalidOperationException("Not an owned worker");
        var command = request with { Workers = 1, Partition = 0, Partitions = 1, MaxNodes = 1,
            MaxRounds = 20, BudgetSeconds = 60, SearchOrder = LocalSearchOrder.MonteCarlo,
            ShareSearchWork = false, SearchWorkPipe = null, TurnWorkPipe = null,
            InitialPlan = seed.Actions, VerifyCandidate = null, RecordedReplayProbe = null,
            DeferVerification = true, SkipFinalVerification = true, StopOnZeroLoss = false,
            DataOnlyCombat = true, DataOnlyRun = true, NumericalExecution = true };
        var sample = await Submit(command);
        LocalWire.Write(Path.Combine(root, "integration-draw-gate-search-private.json"), sample);
        var best = sample.Best ?? throw new InvalidOperationException(sample.Message);
        int prefix = actual.SettledActions;
        if (sample.Status != "searched" || sample.Evaluated != 1 || sample.Rejected != 0 || !best.Won ||
            best.Actions.Length <= prefix || best.Continuation?.Length != best.Actions.Length ||
            !best.Actions.Take(prefix).Zip(seed.Actions.Take(prefix)).All(p =>
                p.First.BeforeHash == p.Second.BeforeHash && LocalSearchWork.Key("action", [p.First]) == LocalSearchWork.Key("action", [p.Second])) ||
            best.Actions[prefix].BeforeHash != actual.ActualHash)
            throw new InvalidOperationException("Numeric search did not preserve the actual native draw restriction/checkpoint: " + sample.Message);

        var verified = await Submit(command with { InitialPlan = null, VerifyCandidate = best,
            DeferVerification = false, SkipFinalVerification = false, FastVerification = false });
        LocalWire.Write(Path.Combine(root, "integration-draw-gate-verified-private.json"), verified);
        if (verified.Status != "done" || verified.Best?.Continuation?.Length != best.Actions.Length ||
            verified.Best.Hp != best.Hp || verified.Best.Won != best.Won)
            throw new InvalidOperationException("Corrected draw route failed native verification: " + verified.Message);

        // Execute the unverified search checkpoints in the regular scene too.
        // No learning is injected into this real-executor pass.
        System.Environment.SetEnvironmentVariable("SPIRE_LOCAL_EXECUTION_LEARN", null);
        await ExecutionReplayIntegration.Run(root, request, Path.Combine(root, "integration-draw-gate-search-private.json"));
        var executed = LocalWire.Read<LocalExecutionReport>(Path.Combine(root, "integration-execution-replay-private.json"));
        if (!executed.BattleEnded || executed.SettledActions != best.Actions.Length || executed.ActualState?.Hp != best.Hp)
            throw new InvalidOperationException("First-pass corrected route did not execute to its predicted victory");
        LocalWire.Write(Path.Combine(root, "integration-draw-gate-summary.json"), new
        {
            searches = 1, sample.Evaluated, preserved_prefix_steps = prefix,
            blocked_draw_checkpoint_matches_actual = true, numerical_steps = best.Actions.Length,
            best.Rounds, best.Hp, best.NetHpLoss, native_verified_steps = verified.Best.Continuation.Length,
            executed_steps = executed.SettledActions, executed.BattleEnded,
            sample.ElapsedMs, final_verification_is_optional_in_product = true,
        });

        async Task<LocalSearchResult> Submit(LocalSearchRequest next)
        {
            next = next with { Id = Guid.NewGuid().ToString("N"), TimelineOrigin = 0, InitialTrace = null };
            LocalWire.Write(Path.Combine(workerRoot, "request.json"), next);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < 120)
            {
                await Task.Delay(100);
                var file = Path.Combine(workerRoot, "result.json");
                if (!File.Exists(file)) continue;
                var result = LocalWire.Read<LocalSearchResult>(file);
                if (result.Id != next.Id || result.Status == "running") continue;
                var idle = Path.Combine(workerRoot, "idle.json");
                if (File.Exists(idle) && LocalWire.Read<LocalWorkerIdle>(idle).Id == next.Id)
                {
                    if ((bool)worker.GetType().GetMethod("GameErrors")!.Invoke(worker, null)!)
                        throw new InvalidOperationException("Owned native worker reported a runtime error");
                    return result;
                }
                if (result.Status is "failed" or "unsupported" or "partial") return result;
            }
            throw new TimeoutException("Owned draw-gate check did not finish");
        }
    }
}
