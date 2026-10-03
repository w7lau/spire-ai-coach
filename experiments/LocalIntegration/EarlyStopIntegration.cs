using System.Reflection;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

internal static class EarlyStopIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation, string seedPath)
    {
        var seed = LocalWire.Read<LocalSearchResult>(seedPath).Best
            ?? throw new InvalidOperationException("No winning seed");
        if (!LocalSearchPolicy.CanStop(seed, true)) throw new InvalidOperationException("Seed does not meet the goal");
        await Task.Run(() => pool.Prepare(installation, 2, CancellationToken.None));
        var summaries = new List<object>();
        foreach (var enabled in new[] { false, true })
        {
            var command = request with { Id = Guid.NewGuid().ToString("N"), Workers = 2, Partitions = 2,
                VerifyCandidate = null, RecordedReplayProbe = null, InitialPlan = seed.Actions, DeferVerification = false,
                ShareSearchWork = true, ContinueOptimization = true, StopOnZeroLoss = enabled, AdaptiveWorkers = false,
                MaxNodes = enabled ? 32 : 2, BudgetSeconds = 60, TimelineOrigin = 0, InitialTrace = null };
            var result = await Task.Run(() => pool.Analyze(command, installation, _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, $"integration-early-stop-{enabled}-private.json"), result);
            if (result.Status != "done" || !LocalSearchPolicy.CanStop(result.Best, true) ||
                result.Best!.Continuation?.Length != result.Best.Actions.Length || result.Timing?.Verifications != 1)
                throw new InvalidOperationException("The final native route was not verified: " + result.Message);
            if (result.StoppedEarly != enabled || (!enabled && result.Evaluated != 4) || enabled && result.Evaluated >= 32)
                throw new InvalidOperationException("Search did not obey the explicit early-stop switch");
            var stops = result.Trace!.Spans.Count(s => s.Phase == "stop_search");
            if (enabled && stops == 0) throw new InvalidOperationException("Peer stopping was not exercised");
            var workers = (Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
            var peerResults = new List<object>();
            for (int i = 0; i < 2; i++)
            {
                var worker = workers.GetValue(i)!;
                var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
                var terminal = LocalWire.Read<LocalSearchResult>(Path.Combine(workerRoot, "result.json"));
                if (terminal.Status == "running") throw new InvalidOperationException("A peer kept searching after Analyze returned");
                peerResults.Add(new { worker = i, terminal.Status, terminal.Evaluated, terminal.StoppedEarly });
            }
            summaries.Add(new { enabled, result.Status, result.StoppedEarly, result.Evaluated, result.ElapsedMs,
                result.SearchElapsedMs, result.Timing, result.Best.Hp, result.Best.NetHpLoss,
                result.Best.RewardCoverageKnown, steps = result.Best.Actions.Length, stop_signals = stops, peers = peerResults });
        }
        LocalWire.Write(Path.Combine(root, "integration-early-stop-summary.json"), summaries);
    }
}
