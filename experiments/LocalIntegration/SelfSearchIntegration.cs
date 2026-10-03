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
        string[] allowed = ["baseline", "correlated", "fast", "correlated-fast", "turn-fast", "refine-fast", "turn-goal",
            "efficient-turn-goal", "efficient-fast-goal", "duration-turn-goal", "efficient-duration-turn-goal", "release-turn-goal"];
        if (names.Length is < 1 or > 4 || names.Any(n => !allowed.Contains(n)))
            throw new InvalidOperationException("Expected one to four supported frozen search comparisons: " + string.Join(", ", allowed));
        var records = new List<object>();
        LocalCandidate? discovered = null;
        long accumulatedMs = 0;
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i] == "refine-fast" && discovered == null)
                throw new InvalidOperationException("Continued optimization needs a preceding autonomous result from this frozen request");
            bool goalCase = names[i].EndsWith("-goal", StringComparison.Ordinal);
            bool releaseCase = names[i] == "release-turn-goal";
            var request = LocalCalculation.Configure(captured, names[i] == "turn-fast" || names[i].EndsWith("turn-goal", StringComparison.Ordinal)
                ? LocalSearchOrder.TurnFrontier : LocalSearchOrder.MonteCarlo,
                captured.Workers, captured.IncludePotions, goalCase) with
            {
                Id = Guid.NewGuid().ToString("N"), TimelineOrigin = 0, InitialTrace = null,
                CorrelatedRollouts = names[i] is "correlated" or "correlated-fast", AdaptiveWorkers = releaseCase,
                LeanSearchChecksums = names[i] is not ("baseline" or "correlated"),
                InitialPlan = names[i] == "refine-fast" ? discovered!.Actions : null,
                TargetVictoryRounds = goalCase ? 6 : null,
                TargetPotionUses = goalCase ? 0 : null,
                EfficientTactics = releaseCase || names[i].StartsWith("efficient-", StringComparison.Ordinal),
                LearnBuffDuration = releaseCase || names[i].Contains("duration", StringComparison.Ordinal)
            };
            var timer = Stopwatch.StartNew();
            var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, $"integration-self-search-private-{i}.json"), result);
            var best = result.Best;
            if (best != null && LocalSearchPolicy.Better(best, discovered)) discovered = best;
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
                computer_seed_from_prior_sample = names[i] == "refine-fast",
                request.MaxNodes, request.BudgetSeconds, request.MaxRounds, request.Workers,
                request.IncludePotions, request.StopOnZeroLoss, request.TargetVictoryRounds, request.TargetPotionUses,
                request.EfficientTactics, request.LearnBuffDuration, request.AdaptiveWorkers,
                actual_workers = result.Workers,
                result.Status, result.Evaluated,
                result.Victories, result.Rejected, result.ElapsedMs, controller_ms = timer.ElapsedMilliseconds,
                result.Timing, result.Work, fallback, verified,
                best?.Won, best?.StartingHp, best?.Hp, best?.HpLost, best?.NetHpLoss, best?.Rounds,
                used_potions = best?.Actions.Count(a => a.PotionSlot.HasValue),
                verified_steps = best?.Continuation?.Length,
                goal_reached = goal != null,
                first_goal_from_request_ms = goal?.FinishedMs,
                first_goal_from_search_ms = goal?.FinishedMs - firstSearchMs,
                first_goal_accumulated_controller_ms = goal?.FinishedMs + accumulatedMs,
                completed_trial_metrics = result.Trials?.Count(t => t.Complete) ?? 0,
                claimed_prefixes_matched = result.Trials?.Count(t => t.ClaimedPrefixMatched == true) ?? 0,
                claimed_prefixes_diverged = result.Trials?.Count(t => t.Complete && t.ClaimedPrefixMatched == false) ?? 0
            });
            LocalWire.Write(Path.Combine(root, "integration-self-search-summary.json"), records);
            accumulatedMs += timer.ElapsedMilliseconds;
            if (!verified || fallback)
                throw new InvalidOperationException("Frozen search comparison requires a verified numerical result without fallback: " + result.Message);
        }
    }
}
