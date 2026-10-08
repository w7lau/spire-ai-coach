using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Exact frozen incident, unseeded in both algorithms. Only owned workers play.
internal static class CardGoalMinimumIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest original,
        LocalSearchRequest frozen, LocalInstallation installation)
    {
        if (original.InitialPlan is { Length: > 0 } || original.VerifyCandidate != null || original.RecordedReplayProbe != null ||
            original.CardGoals is not { Enabled: true, HpLossThreshold: null } || original.Workers != 8)
            throw new InvalidOperationException("Expected the unseeded eight-worker finite card-goal incident");
        var samples = new List<object>();
        foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
        {
            var request = frozen with { Id = Guid.NewGuid().ToString("N"), SearchOrder = order,
                MaxRounds = original.MaxRounds, Workers = original.Workers, MaxNodes = original.MaxNodes,
                BudgetSeconds = original.BudgetSeconds, SkipFinalVerification = false,
                InitialPlan = null, InitialTrace = null, VerifyCandidate = null, RecordedReplayProbe = null,
                Partition = 0, Partitions = 1, TimelineOrigin = 0, TurnWorkPipe = null, SearchWorkPipe = null,
                ProgressPipe = null, MinimumLossPipe = null };
            var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, "integration-card-goal-minimum-" + order + "-private.json"), result);
            if (result.Status != "done" || !result.StoppedEarly || !result.StoppedOnMinimum ||
                result.StoppedOnHealthTarget || result.StoppedOnFirstWin || result.StoppedOnManualVictory ||
                result.Best is not { Won: true, Dead: false, StartingHp: 60, Hp: 59 } best ||
                result.MinimumLoss is not { Confirmed: true, Certificate: { MinimumNetHpLoss: 1,
                    MinimumPotionsUsed: 0, MaximumFinalHp: 59 } certificate } ||
                !LocalSearchPolicy.CanStopAtMinimum(best, request, certificate) || !LocalSearchPolicy.HasMinimumProof(result) ||
                result.Workers != original.Workers || result.Timing?.Verifications != 1 ||
                !LocalSearchPolicy.HasExecutionPoints(result) || result.Rejected != 0 ||
                result.RecoveredFailures is { Length: > 0 } || result.HealthBounds is not { LossProofProbes: > 0, MinimumLossRequests: > 0 })
                throw new InvalidOperationException("The frozen one-loss card-goal incident did not return using a confirmed proof: " +
                    System.Text.Json.JsonSerializer.Serialize(new { result.Status, result.Message, result.Evaluated,
                        result.StoppedOnMinimum, result.MinimumLoss, result.HealthBounds, result.Best?.Hp, result.Best?.CardGoalOutcome }));
            if (LocalSearchPolicy.CanStopAtMinimum(best with { CardGoalOutcome = null }, request, certificate))
                throw new InvalidOperationException("Native proof bypassed the selected card goal");
            if (LocalSearchPolicy.FormatAdvice(result).Contains("可选目标尚未证明最优", StringComparison.Ordinal))
                throw new InvalidOperationException("Confirmed card-goal result was displayed as unfinished");
            var partials = (result.Trials ?? []).Where(t => !t.Won && !t.Complete).Take(5).Select(t =>
                new { t.Hp, t.NetHpLoss, t.Rounds, t.Complete }).ToArray();
            samples.Add(new { algorithm = order.ToString(), unseeded = true, request.Workers, request.MaxNodes,
                request.BudgetSeconds, request.MaxRounds, result.Evaluated, result.ElapsedMs, result.SearchElapsedMs,
                result.StoppedOnMinimum, minimumLoss = certificate.MinimumNetHpLoss,
                maximumFinalHp = certificate.MaximumFinalHp, minimumPotions = certificate.MinimumPotionsUsed,
                result.MinimumLoss.Confirmed, proofTrials = result.MinimumLoss.Trials, proofNodes = result.MinimumLoss.Nodes,
                best.StartingHp, best.Hp, best.MaxHp, best.CardGoalOutcome,
                steps = best.Actions.Length, result.Timing.Verifications,
                nativeStateRngHistoryVerified = true, goalEvidenceRequired = true,
                result.HealthBounds.LossProofProbes, result.HealthBounds.MinimumLossRequests,
                partialTrialExamples = partials, recoveredFailures = 0 });
        }
        LocalWire.Write(Path.Combine(root, "integration-card-goal-minimum-summary.json"), new {
            version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3), passed = true, samples,
            scope = "Same frozen native incident and actual six Mods, original eight-worker/trial/time/turn limits, no answer seed. Both algorithms with confirmed full-domain HP/potion bound and ordinary independent final replay. Goal-free and unfinished-goal candidates cannot return."
        });
    }
}
