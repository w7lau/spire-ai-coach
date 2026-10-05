using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// A fixed native incident route, not a search-quality or exhaustive-search benchmark.
internal static class VisualFactoryIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation, string seedFile)
    {
        var seed = LocalWire.Read<LocalSearchResult>(seedFile).Best ?? throw new InvalidOperationException("Missing native incident route");
        if (!seed.Won || seed.Actions.Length == 0 || seed.Actions[0].BeforeHash != request.NativeHash ||
            !seed.Actions.Any(a => a.ModelId == "CARD.VOID_FORM"))
            throw new InvalidOperationException("This regression requires the frozen Void Form incident");
        var samples = new List<object>();
        for (int pass = 0; pass < 2; pass++)
        {
            var sample = await Task.Run(() => pool.Analyze(request with
            {
                Id = Guid.NewGuid().ToString("N"), Workers = 1, MaxNodes = 1,
                SearchOrder = LocalSearchOrder.MonteCarlo, ContinueOptimization = false,
                InitialPlan = seed.Actions, ShareSearchWork = false, SkipFinalVerification = true,
                DataOnlyCombat = true, DataOnlyRun = true, NumericalExecution = true,
                ExperimentalNativeData = true, DeferVerification = false
            }, installation with { MinimalWorkerBootstrap = true }, _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, $"integration-visual-factory-private-{pass}.json"), sample);
            var best = sample.Best ?? throw new InvalidOperationException(sample.Message);
            if (sample.Status != "done" || sample.Rejected != 0 || !best.Won || best.Dead || !sample.VerificationSkipped ||
                best.Hp != seed.Hp || best.HpLost != seed.HpLost || best.MaxHp != seed.MaxHp || best.Gold != seed.Gold ||
                best.Rounds != seed.Rounds || best.EnemyHp != seed.EnemyHp || best.DamageSources != seed.DamageSources ||
                !LocalSearchPolicy.HasExecutionPoints(sample) ||
                // The historical search has learned scores; a fixed one-route replay
                // has not. Compare every native action field, excluding only its hint.
                JsonSerializer.Serialize(best.Actions.Select(a => a with { Preference = 0 })) !=
                JsonSerializer.Serialize(seed.Actions.Select(a => a with { Preference = 0 })) ||
                sample.Trace?.Spans.Any(s => s.Phase is "fallback" or "retire") == true)
                throw new InvalidOperationException("Scene-free visual regression changed the native incident route or fell back: " + sample.Message);
            var factories = sample.Trace?.Methods?.Where(m => m.Skipped > 0 && m.Method.EndsWith("FormVfx.Create", StringComparison.Ordinal)).ToArray();
            if (factories == null || !factories.Any(m => m.Method == "NVoidFormVfx.Create"))
                throw new InvalidOperationException("Native incident did not exercise the suppressed Void Form factory");
            samples.Add(new { pass, sample.ElapsedMs, sample.Timing, best.Rounds, steps = best.Actions.Length,
                best.StartingHp, best.Hp, best.NetHpLoss, best.DamageSources,
                skipped_form_factories = factories, native_step_fingerprints_match = true,
                actions_and_selections_match = true, search_preference_scores_excluded = true, complete_execution_points = true,
                regular_fallbacks = 0, reused_worker = pass > 0 });
        }
        LocalWire.Write(Path.Combine(root, "integration-visual-factory-summary.json"), new
        {
            version = typeof(ModEntry).Assembly.GetName().Version?.ToString(3),
            fixed_incident_route = true, original_attempt_limit = request.MaxNodes,
            original_time_limit = request.BudgetSeconds, original_round_limit = request.MaxRounds,
            production_budgets_unchanged = true, final_verification_skipped = true, samples
        });
    }
}
