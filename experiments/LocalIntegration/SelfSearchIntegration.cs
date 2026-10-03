using System.Diagnostics;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Frozen-root comparisons reuse the same owned pool. No recorded player plan,
// prescribed card order or candidate seed is accepted by this experiment.
internal static class SelfSearchIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest captured,
        LocalInstallation installation, string cases)
    {
        if (captured.InitialPlan is { Length: > 0 } || captured.VerifyCandidate != null || captured.RecordedReplayProbe != null)
            throw new InvalidOperationException("Autonomous search comparison rejects answer seeds");
        var names = cases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length is < 1 or > 4 || names.Any(n => n is not ("baseline" or "correlated" or "fast" or "correlated-fast")))
            throw new InvalidOperationException("Expected one to four baseline/correlated comparisons");
        var records = new List<object>();
        for (int i = 0; i < names.Length; i++)
        {
            var request = LocalCalculation.Configure(captured, LocalSearchOrder.MonteCarlo,
                captured.Workers, captured.IncludePotions, false) with
            {
                Id = Guid.NewGuid().ToString("N"), TimelineOrigin = 0, InitialTrace = null,
                CorrelatedRollouts = names[i] is "correlated" or "correlated-fast", AdaptiveWorkers = false,
                LeanSearchChecksums = names[i] is "fast" or "correlated-fast"
            };
            var timer = Stopwatch.StartNew();
            var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, $"integration-self-search-private-{i}.json"), result);
            var best = result.Best;
            bool fallback = result.Trace?.Spans.Any(s => s.Phase == "fallback") == true;
            bool verified = result.Status == "done" && best != null && result.Rejected == 0 &&
                best.Continuation?.Length == best.Actions.Length && result.Timing?.Verifications == 1;
            double? firstSearchMs = result.Trace?.Spans.Where(s => s.Stage == "search" && s.Phase == "session")
                .Select(s => (double?)s.StartMs).Min();
            var goal = result.Trials?.Where(t => t.Complete && t.Won && t.NetHpLoss == 0 && t.PotionsUsed == 0 && t.Rounds <= 6)
                .OrderBy(t => t.FinishedMs).FirstOrDefault();
            records.Add(new
            {
                sample = i, policy = names[i], warm_pool = i > 0, manual_seed = false,
                request.MaxNodes, request.BudgetSeconds, request.MaxRounds, request.Workers,
                request.IncludePotions, request.StopOnZeroLoss, result.Status, result.Evaluated,
                result.Victories, result.Rejected, result.ElapsedMs, controller_ms = timer.ElapsedMilliseconds,
                result.Timing, result.Work, fallback, verified,
                best?.Won, best?.StartingHp, best?.Hp, best?.HpLost, best?.NetHpLoss, best?.Rounds,
                used_potions = best?.Actions.Count(a => a.PotionSlot.HasValue),
                verified_steps = best?.Continuation?.Length,
                goal_reached = goal != null,
                first_goal_from_request_ms = goal?.FinishedMs,
                first_goal_from_search_ms = goal?.FinishedMs - firstSearchMs,
                completed_trial_metrics = result.Trials?.Count(t => t.Complete) ?? 0
                ,claimed_prefixes_matched = result.Trials?.Count(t => t.ClaimedPrefixMatched == true) ?? 0
                ,claimed_prefixes_diverged = result.Trials?.Count(t => t.Complete && t.ClaimedPrefixMatched == false) ?? 0
            });
            LocalWire.Write(Path.Combine(root, "integration-self-search-summary.json"), records);
            if (!verified || fallback)
                throw new InvalidOperationException("Frozen search comparison requires a verified numerical result without fallback: " + result.Message);
        }
    }
}
