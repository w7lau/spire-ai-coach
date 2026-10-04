using Godot;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// One new-mode run through the product's existing pool; no old benchmark or
// injected manual solution. Only this owned host constructs the UI controls.
internal static class AlgorithmIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest captured, LocalInstallation installation,
        bool goalTest = false)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var overlay = new CoachOverlay(tree);
        overlay.Mount();
        var layer = tree.Root.GetNode<CanvasLayer>("SpireAiCoach");
        var oldButton = layer.FindChild("LocalBattleSearch", true, false) as Button;
        var turnButton = layer.FindChild("LocalTurnSearch", true, false) as Button;
        var targetRounds = layer.FindChild("LocalTargetVictoryRounds", true, false) as SpinBox;
        if (oldButton == null || turnButton == null || !oldButton.Disabled || !turnButton.Disabled ||
            oldButton.GetParent() != turnButton.GetParent() || targetRounds is not { MinValue: 0, MaxValue: LocalCalculation.Rounds })
            throw new InvalidOperationException("The two product calculation controls did not mount with native phase guards");
        if (goalTest) targetRounds.Value = 6;
        int requestedTargetRounds = (int)targetRounds.Value;
        // Pressing outside combat must use the common guard and create no request.
        oldButton.EmitSignal(Button.SignalName.Pressed);
        turnButton.EmitSignal(Button.SignalName.Pressed);
        layer.QueueFree();
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var request = LocalCalculation.Configure(captured, LocalSearchOrder.TurnFrontier,
            captured.Workers, captured.IncludePotions, goalTest, targetVictoryRounds: goalTest ? requestedTargetRounds : 0);
        if (goalTest) request = request with { Id = Guid.NewGuid().ToString("N"), TimelineOrigin = 0,
            StopOnZeroLoss = true };
        if (!request.NumericalExecution || !request.DataOnlyCombat || !request.DataOnlyRun || !request.TrimWorkerOverhead ||
            request.InitialPlan != null || request.RecordedReplayProbe != null || request.VerifyCandidate != null)
            throw new InvalidOperationException("New-mode validation requires the ordinary unseeded numerical search");
        var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
        LocalWire.Write(Path.Combine(root, "integration-algorithm-private.json"), result);
        var best = result.Best ?? throw new InvalidOperationException("New algorithm returned no route");
        var histories = result.Trace?.Spans.Where(s => s.Stage == "search" && s.Phase == "completed-route").ToArray() ?? [];
        int duplicates = histories.Length - histories.Select(s => s.Detail).Distinct(StringComparer.Ordinal).Count();
        int completed = result.TurnSearch?.CompletedHistories ?? 0;
        if (result.Status != "done" || result.Rejected != 0 || !best.Won || result.Timing?.Verifications != 1 ||
            best.Continuation?.Length != best.Actions.Length || duplicates != 0 || completed == 0 ||
            result.TurnSearch?.RepeatedHistories != 0 || result.Message.StartsWith("常规执行", StringComparison.Ordinal))
            throw new InvalidOperationException("New-mode native search/coverage/final verification failed: " + result.Message);
        if (goalTest && (!result.StoppedEarly || !LocalSearchPolicy.MeetsGoal(best, request)))
            throw new InvalidOperationException("New-mode target was not autonomously discovered and verified");
        var goal = result.TurnSearch!.Outcomes?.Where(o => o.Won && best.StartingHp is { } initial && o.Hp >= initial &&
            o.Potions == 0 && o.Rounds <= 6 && o.DamageSources is { Complete: true, Enemy: 0 })
            .OrderBy(o => o.CompletedMs).FirstOrDefault();
        double? started = result.Trace?.Spans.Where(s => s.Stage == "search" && s.Phase == "session")
            .Select(s => (double?)s.StartMs).Min();
        LocalWire.Write(Path.Combine(root, "integration-algorithm-summary.json"), new
        {
            version = typeof(ModEntry).Assembly.GetName().Version!.ToString(), ui_controls_mounted = true, outside_combat_guard_passed = true,
            request.SearchOrder, requested_workers = request.Workers, request.MaxNodes, request.BudgetSeconds, request.MaxRounds,
            request.IncludePotions, request.StopOnZeroLoss, request.NumericalExecution, request.TrimWorkerOverhead,
            request.TargetVictoryRounds, request.TargetPotionUses, request.RequireKnownZeroEnemyDamage,
            manual_seed = false, request.ShareSearchWork, result.Status, result.Workers, result.WorkerLimit,
            result.Evaluated, result.Victories, result.Rejected, result.ElapsedMs,
            result.Timing, result.TurnSearch, completed_histories = completed, repeated_histories = result.TurnSearch!.RepeatedHistories,
            workers_with_completed_trials = result.TurnSearch.Outcomes?.Select(o => o.Worker).Distinct().Count(),
            retained_history_digests = histories.Length, dropped_trace_entries = result.Trace?.Dropped,
            best.Won, best.StartingHp, best.Hp, best.HpLost, best.NetHpLoss, best.Rounds, best.DamageSources,
            used_potions = best.Actions.Count(a => a.PotionSlot.HasValue), verified_steps = best.Continuation!.Length
            ,first_goal_from_request_ms = goal?.CompletedMs, first_goal_from_search_ms = goal?.CompletedMs - started
        });
    }

    public static async Task RunGoal(string root, LocalWorkerPool pool, LocalSearchRequest captured, LocalInstallation installation)
    {
        var records = new List<JsonElement>();
        for (int sample = 0; sample < 2; sample++)
        {
            await Run(root, pool, captured, installation, true);
            File.Copy(Path.Combine(root, "integration-algorithm-private.json"),
                Path.Combine(root, $"integration-algorithm-goal-private-{sample}.json"), true);
            using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "integration-algorithm-summary.json")));
            records.Add(summary.RootElement.Clone());
            LocalWire.Write(Path.Combine(root, "integration-algorithm-goal-summary.json"), records);
        }
    }
}
