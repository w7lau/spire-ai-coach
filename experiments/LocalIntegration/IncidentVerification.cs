using System.Diagnostics;
using System.Reflection;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Replay the saved final candidate, not an entire new search. Input/output stay
// in the owned private workspace, and the actual parent error gate is exercised.
internal static class IncidentVerification
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation, string seedFile)
    {
        var seed = LocalWire.Read<LocalSearchResult>(seedFile).Best ?? throw new InvalidOperationException("Missing incident candidate");
        if (seed.Actions.Length == 0 || seed.Actions[0].BeforeHash != request.NativeHash)
            throw new InvalidOperationException("Incident candidate has a different native root");
        await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
        var worker = ((Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pool)!).GetValue(0)!;
        var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        if (!File.Exists(Path.Combine(workerRoot, ".coach-worker"))) throw new InvalidOperationException("Not an owned worker");
        var command = request with { Partition = 0, Partitions = 1, Workers = 1, VerifyCandidate = seed,
            InitialPlan = null, RecordedReplayProbe = null, ShareSearchWork = false, DeferVerification = false,
            FastVerification = true, SkipFinalVerification = false };
        var valid = await Submit(command);
        bool errors = (bool)worker.GetType().GetMethod("GameErrors")!.Invoke(worker, null)!;
        if (valid.Status != "done" || valid.Best?.Continuation?.Length != seed.Actions.Length || errors ||
            valid.Best.Hp != seed.Hp || valid.Best.HpLost != seed.HpLost || valid.Best.Won != seed.Won ||
            !valid.Trace!.Methods!.Any(m => m.Method == "ChecksumTracker.GenerateChecksum" && m.Calls > 0 && m.Skipped == 0))
            throw new InvalidOperationException("Saved candidate still fails the native verification/error gate");
        LocalWire.Write(Path.Combine(root, "integration-incident-private.json"), valid);
        var invalid = await Submit(command with { VerifyCandidate = seed with { Hp = seed.Hp - 1 } });
        if (invalid.Status != "failed" || invalid.Best != null)
            throw new InvalidOperationException("A wrong settlement escaped verification");
        LocalWire.Write(Path.Combine(root, "integration-incident-rejection-private.json"), invalid);
        LocalWire.Write(Path.Combine(root, "integration-incident-summary.json"), new
        {
            valid.Status, valid.ElapsedMs, seed.Rounds, steps = seed.Actions.Length,
            checked_steps = valid.Best.Continuation.Length, valid.Best.Won, valid.Best.StartingHp,
            valid.Best.Hp, valid.Best.NetHpLoss, parent_error_gate_passed = !errors,
            wrong_settlement_rejected = invalid.Status == "failed", searches = 0,
            methods = valid.Trace.Methods,
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
                if (result.Id == next.Id && result.Status != "running") return result;
            }
            throw new TimeoutException("Incident verification did not finish");
        }
    }
}
