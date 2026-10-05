using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Same frozen live incident and per-lane budget, including its previous route.
// No successful answer is supplied and no actions run in the live game.
internal static class FinisherRetentionIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest original,
        LocalSearchRequest request, LocalInstallation installation)
    {
        if (original.VerifyCandidate != null || original.RecordedReplayProbe != null ||
            original.CardGoals?.FinisherModelId == null)
            throw new InvalidOperationException("Finisher regression requires a selected-finisher search incident");
        var records = new List<object>();
        var failures = new List<string>();
        request = request with { MaxRounds = original.MaxRounds, InitialPlan = original.InitialPlan, VerifyCandidate = null,
            RecordedReplayProbe = null, Partition = 0, Partitions = 1, TimelineOrigin = 0, InitialTrace = null,
            TurnWorkPipe = null, SearchWorkPipe = null, ProgressPipe = null, MinimumLossPipe = null,
            SkipFinalVerification = false };
        installation = installation with { GameDirectory = Path.Combine(root, "game") };
        foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
        {
            var frozen = request with { Id = Guid.NewGuid().ToString("N"), SearchOrder = order };
            var result = await Task.Run(() => pool.Analyze(frozen, installation, _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, $"integration-finisher-retention-{order}-private.json"), result);
            var best = result.Best;
            var goalWins = (result.Trials ?? []).Where(t => t.Complete && t.Won && t.FinisherKills > 0).ToArray();
            var affordableGoalWins = goalWins.Where(t => original.CardGoals.HpLossThreshold is { } limit
                ? t.NetHpLoss < limit : t.NetHpLoss == 0).ToArray();
            var lowestWinLoss = (result.Trials ?? []).Where(t => t.Complete && t.Won)
                .Select(t => t.NetHpLoss).DefaultIfEmpty().Min();
            // A bounded search cannot assert that a strict HP allowance is achievable.
            // It must consider actual finishers and return the best permitted outcome.
            bool selectionCorrect = affordableGoalWins.Length > 0
                ? best?.CardGoalOutcome?.Kills >= affordableGoalWins.Max(t => t.FinisherKills) &&
                    original.CardGoals.WithinThreshold(best!)
                : best?.NetHpLoss <= lowestWinLoss;
            records.Add(new {
                algorithm = order, result.Status, result.Evaluated, result.Victories, result.Rejected,
                result.ElapsedMs, result.StoppedOnCardGoals, recoveredFailures = result.RecoveredFailures?.Length ?? 0,
                frozen.MaxNodes, frozen.BudgetSeconds, frozen.MaxRounds, frozen.MaxDepth,
                requestedWorkers = frozen.Workers, result.Workers,
                previousRouteSteps = original.InitialPlan?.Length ?? 0,
                previousRouteFirst = original.InitialPlan?.FirstOrDefault()?.ModelId,
                successful_answer_supplied = false,
                measuredFinisherWins = goalWins.Length,
                lowestMeasuredFinisherLoss = goalWins.Select(t => t.NetHpLoss).DefaultIfEmpty().Min(),
                measuredFinisherWinsWithinThreshold = affordableGoalWins.Length, selectionCorrect,
                extra_final_verification = true, result.Timing,
                best = best == null ? null : new { best.Won, best.Dead, best.StartingHp, best.Hp, best.MaxHp,
                    best.NetHpLoss, best.Rounds, steps = best.Actions.Length,
                    first = best.Actions.FirstOrDefault()?.ModelId,
                    finisherRound = best.Actions.FirstOrDefault(a => a.ModelId == original.CardGoals.FinisherModelId)?.Round,
                    kills = best.CardGoalOutcome?.Kills, consumable = best.CardGoalOutcome?.ConsumableGoals?.Finisher }
            });
            LocalWire.Write(Path.Combine(root, "integration-finisher-retention-summary.json"), new {
                version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3),
                scope = "Frozen user battle with actual Mod list, previous route and original search budget; candidate Coach in owned workers. No successful answer supplied; independent final verification added.",
                threshold = original.CardGoals.HpLossThreshold, originalWorkers = original.Workers, records });
            if (result.Status != "done" || best?.Won != true || best.Dead ||
                goalWins.Length == 0 || !selectionCorrect ||
                result.Timing?.Verifications != 1 || !LocalSearchPolicy.HasExecutionPoints(result) ||
                result.Rejected != 0 || result.Failure != null || (result.RecoveredFailures?.Length ?? 0) != 0)
                failures.Add(order + ": frozen native goal exploration or final selection regression: " + result.Message);
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
    }
}
