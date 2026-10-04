using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Same frozen combat, explicitly labelled existing-route reuse. This does not
// reconstruct an overwritten player run or prove unseeded search optimality.
internal static class RouteFeedbackIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest captured, LocalInstallation installation)
    {
        if (captured.InitialPlan != null || captured.VerifyCandidate != null || captured.RecordedReplayProbe != null)
            throw new InvalidOperationException("Route reuse comparison requires a frozen original root");
        var seedPath = Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT")
            ?? throw new InvalidDataException("Missing previous native result");
        var previous = LocalWire.Read<LocalSearchResult>(seedPath);
        if (previous.SnapshotId != captured.SnapshotId || previous.Best is not { Won: true, Dead: false } known ||
            previous.IncludePotions != captured.IncludePotions || !LocalSearchPolicy.HasExecutionPoints(previous) ||
            known.Actions.Length == 0 || known.Actions[0].BeforeHash != captured.NativeHash ||
            known.Continuation?.FirstOrDefault()?.History != captured.History)
            throw new InvalidDataException("Previous native route does not match the frozen state/history");
        var samples = new List<JsonElement>();
        int workers = captured.Workers;
        if (workers < 1 || workers > 16) throw new InvalidDataException("Comparison requires a fixed worker setting");
        await pool.Prepare(installation, workers, CancellationToken.None);
        bool focusedOnly = Environment.GetEnvironmentVariable("SPIRE_LOCAL_ROUTE_FEEDBACK_FOCUSED_ONLY") == "1";
        foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            foreach (bool enabled in focusedOnly ? new[] { true } : new[] { false, true })
            {
                var request = captured with { Id = Guid.NewGuid().ToString("N"), TimelineOrigin = 0, InitialTrace = null,
                    Partition = 0, Partitions = workers, SearchOrder = order, InitialPlan = enabled ? known.Actions : null,
                    DeferVerification = false };
                var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
                string label = order + (enabled ? "-reuse" : "-baseline");
                LocalWire.Write(Path.Combine(root, "integration-route-feedback-private-" + label + ".json"), result);
                var best = result.Best ?? throw new InvalidOperationException("No measured candidate: " + result.Message);
                if (result.Rejected != 0 || result.Status is "failed" or "unsupported" or "partial" ||
                    result.Message.StartsWith("常规执行", StringComparison.Ordinal))
                    throw new InvalidOperationException("Native loss feedback comparison failed: " + result.Message);
                if (enabled && (!best.Won || best.Dead || LocalSearchPolicy.Better(known, best)))
                    throw new InvalidOperationException("Existing native route was lost or worsened during reuse");
                var losses = LocalRouteFeedback.RoundLosses(best);
                samples.Add(JsonSerializer.SerializeToElement(new
                {
                    label, request.MaxNodes, request.MaxRounds, request.BudgetSeconds, request.IncludePotions,
                    request.StopOnZeroLoss, request.SkipFinalVerification, seeded = enabled, initial_plan_steps = enabled ? known.Actions.Length : 0,
                    known_hp = known.Hp, known_net_loss = known.NetHpLoss, requested_workers = workers,
                    result.Status, result.Workers, result.Evaluated, result.Victories, result.Rejected, result.ElapsedMs,
                    best.StartingHp, best.Hp, best.HpLost, best.NetHpLoss, best.Won, best.Rounds,
                    potions = best.Actions.Count(a => a.PotionSlot.HasValue),
                    round_losses = losses, result.Timing,
                    first_win_ms = result.Trials?.Where(t => t.Won).Select(t => (double?)t.FinishedMs).Min(),
                    repeated_histories = result.TurnSearch?.RepeatedHistories,
                    measured_decisions = best.Decisions?.Count(d => d.HpBefore.HasValue && d.HpAfter.HasValue)
                }));
                LocalWire.Write(Path.Combine(root, "integration-route-feedback-summary.json"), new
                {
                    scope = "existing native route reuse with captured workers/budgets/potions; one sample per case, not unseeded search optimality or stable speedup proof",
                    mod_sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(ModEntry).Assembly.Location))),
                    samples
                });
            }
    }
}
