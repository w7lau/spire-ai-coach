using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Replay one frozen native line containing the failed presentation boundary.
// This bounds an execution/lifecycle regression, not a product search budget.
internal static class PreparationReuseIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation, string seedPath)
    {
        var seed = LocalWire.Read<LocalSearchResult>(seedPath).Best
            ?? throw new InvalidDataException("A complete frozen native line is required");
        if (!seed.Won || seed.Actions.Length == 0 || seed.Actions[0].BeforeHash != request.NativeHash ||
            !seed.Actions.Any(a => a.ModelId == "CARD.VOID_FORM"))
            throw new InvalidDataException("The fixture must exercise the incident's native Void Form route");
        installation = installation with { MinimalWorkerBootstrap = true };
        var command = request with { Workers = 1, AdaptiveWorkers = false, MaxNodes = 1,
            SearchOrder = LocalSearchOrder.MonteCarlo, ShareSearchWork = false, SearchWorkPipe = null,
            TurnWorkPipe = null, RecordedReplayProbe = null, VerifyCandidate = null, InitialPlan = seed.Actions,
            DataOnlyCombat = true, DataOnlyRun = true, NumericalExecution = true, StopOnZeroLoss = false };
        var samples = new List<object>();
        for (int i = 0; i < 3; i++)
        {
            // Independently verify once; later samples measure normal search reuse.
            var result = await Task.Run(() => pool.Analyze(command with { Id = Guid.NewGuid().ToString("N"),
                SkipFinalVerification = i != 0 }, installation, _ => { }, CancellationToken.None));
            var best = result.Best ?? throw new InvalidOperationException(result.Message);
            LocalWire.Write(Path.Combine(root, $"integration-preparation-private-{i}.json"), result);
            if (result.Status != "done" || result.Rejected != 0 || result.Evaluated != 1 ||
                result.Trace == null || result.Trace.Spans.Any(s => s.Phase == "fallback") ||
                LocalSearchWork.Key("expand", best.Actions) != LocalSearchWork.Key("expand", seed.Actions) ||
                (best.Won, best.Hp, best.HpLost, best.MaxHp, best.Gold, best.EnemyHp, best.Rounds) !=
                (seed.Won, seed.Hp, seed.HpLost, seed.MaxHp, seed.Gold, seed.EnemyHp, seed.Rounds) ||
                best.Continuation?.Length != best.Actions.Length)
                throw new InvalidOperationException("The native incident line changed or fell back: " + result.Message);
            var resources = pool.Resources();
            if (resources.Ready != 1 || resources.Preparing != 0 || resources.Starts != 1 ||
                i > 0 && result.Trace.Spans.Any(s => s.Stage == "prepare" && s.Phase is "cold_start" or "rebuild" or "engine"))
                throw new InvalidOperationException("A healthy warm worker was rebuilt");
            double firstDecision = result.Trace.Spans.First(s => s.Stage == "search" && s.Phase == "decision").StartMs;
            samples.Add(new { cold = i == 0, independent_verification = i == 0, result.ElapsedMs,
                first_decision_ms = firstDecision, result.Timing, resources.Ready, resources.Starts,
                native_steps = best.Actions.Length, best.Rounds, best.Hp, best.HpLost,
                fallback = false, native_history_and_settlement_match = true });
        }
        LocalWire.Write(Path.Combine(root, "integration-preparation-summary.json"), new
        {
            samples, persistent_form_factories = 5, independently_verified_steps = seed.Actions.Length,
            fixed_line_only = true, product_budgets_unchanged = true, full_search_speed_unmeasured = true
        });
    }
}
