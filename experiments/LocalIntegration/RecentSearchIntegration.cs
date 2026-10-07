using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Inspect a frozen real incident without an answer seed or changing its trial/time/turn limits.
// A successful simulation is not a proof that a losing sampled battle is unsolvable.
internal static class RecentSearchIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest original,
        LocalSearchRequest request, LocalInstallation installation)
    {
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_RULE_STALL_PROBE") == "1")
        {
            installation = installation with { ModDirectories = installation.ModDirectories.Append(
                Path.Combine(root, "game", "mods", "SpireLocalIntegration")).ToArray() };
            request = request with { LoadedMods = request.LoadedMods.Append(
                $"SpireLocalIntegration:0.0.1:{typeof(Entry).Assembly.ManifestModule.ModuleVersionId}").Order(StringComparer.Ordinal).ToArray() };
        }
        if (original.InitialPlan is { Length: > 0 } || original.VerifyCandidate != null || original.RecordedReplayProbe != null)
            throw new InvalidOperationException("Recent-search regression requires an unseeded frozen request");
        request = request with { MaxRounds = original.MaxRounds, InitialPlan = null, VerifyCandidate = null,
            RecordedReplayProbe = null, Partition = 0, Partitions = 1, TimelineOrigin = 0, InitialTrace = null,
            TurnWorkPipe = null, SearchWorkPipe = null, ProgressPipe = null, MinimumLossPipe = null };
        using var observing = new CancellationTokenSource();
        var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, observing.Token,
            System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_RULE_STALL_PROBE") != "1" ? null : p =>
            { if (p.Status == "failed") observing.Cancel(); }));
        LocalWire.Write(Path.Combine(root, "integration-recent-search-private.json"), result);
        var failures = result.RecoveredFailures?.Length ?? 0;
        LocalWire.Write(Path.Combine(root, "integration-recent-search-summary.json"), new
        {
            version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3),
            coachModule = typeof(LocalWorker).Assembly.ManifestModule.ModuleVersionId,
            request.SearchOrder, request.BudgetSeconds, request.MaxNodes, request.MaxRounds,
            requestedWorkers = request.Workers, originalWorkers = original.Workers, result.Workers,
            unseeded = true, request.IncludePotions, request.SkipFinalVerification,
            result.Status, result.Evaluated, result.Rejected, result.Victories, result.ElapsedMs,
            result.SearchElapsedMs, result.Timing,
            turnSearch = result.TurnSearch == null ? null : new { result.TurnSearch.Probes, result.TurnSearch.BoundPruned,
                result.TurnSearch.Offered, result.TurnSearch.DuplicateOffers, result.TurnSearch.Pending,
                result.TurnSearch.CoveredPrefixes, result.TurnSearch.CompletedHistories, result.TurnSearch.RepeatedHistories,
                result.TurnSearch.ClaimedByRound, result.TurnSearch.RolloutStyles },
            result.Work, result.HealthBounds, result.MinimumLoss, result.CardGoals, result.Evidence,
            result.HealthTarget, result.StoppedEarly, result.StoppedOnHealthTarget, result.StoppedOnMinimum,
            recoveredFailures = failures, result.Failure,
            foundVictory = result.Best?.Won == true,
            best = result.Best == null ? null : new { result.Best.Won, result.Best.Dead,
                result.Best.StartingHp, result.Best.Hp, result.Best.MaxHp, result.Best.NetHpLoss,
                result.Best.EnemyHp, result.Best.Rounds, result.Best.StopReason,
                steps = result.Best.Actions.Length, choices = result.Best.Actions.Sum(a => a.Choices?.Length ?? 0),
                potions = result.Best.Actions.Count(a => a.PotionSlot.HasValue) },
            note = "Historical baseline is reused. Sampled failure to win does not prove impossibility. Worker count override is explicit."
        });
        if (result.Evidence == null || result.Best?.Won != true && !result.Evidence.ExactRootCovered && result.Evidence.Conclusion != "unknown")
            throw new InvalidOperationException("A bounded sampled loss must retain unknown reachability");
        if (result.Status is not ("done" or "partial") || result.Best == null || result.Rejected != 0 ||
            result.Failure != null || failures != 0 || request.SkipFinalVerification && result.Timing?.Verifications != 0)
            throw new InvalidOperationException("Recent incident still has a simulation failure: " + result.Message);
    }
}
