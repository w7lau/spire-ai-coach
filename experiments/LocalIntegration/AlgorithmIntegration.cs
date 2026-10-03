using Godot;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// One new-mode run through the product's existing pool; no old benchmark or
// injected manual solution. Only this owned host constructs the UI controls.
internal static class AlgorithmIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest captured, LocalInstallation installation)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var overlay = new CoachOverlay(tree);
        overlay.Mount();
        var layer = tree.Root.GetNode<CanvasLayer>("SpireAiCoach");
        var oldButton = layer.FindChild("LocalBattleSearch", true, false) as Button;
        var turnButton = layer.FindChild("LocalTurnSearch", true, false) as Button;
        if (oldButton == null || turnButton == null || !oldButton.Disabled || !turnButton.Disabled ||
            oldButton.GetParent() != turnButton.GetParent())
            throw new InvalidOperationException("The two product calculation controls did not mount with native phase guards");
        // Pressing outside combat must use the common guard and create no request.
        oldButton.EmitSignal(Button.SignalName.Pressed);
        turnButton.EmitSignal(Button.SignalName.Pressed);
        layer.QueueFree();
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var request = LocalCalculation.Configure(captured, LocalSearchOrder.TurnFrontier,
            captured.Workers, captured.IncludePotions, false);
        if (!request.NumericalExecution || !request.DataOnlyCombat || !request.DataOnlyRun || !request.TrimWorkerOverhead ||
            request.InitialPlan != null || request.RecordedReplayProbe != null || request.VerifyCandidate != null)
            throw new InvalidOperationException("New-mode validation requires the ordinary unseeded numerical search");
        var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
        LocalWire.Write(Path.Combine(root, "integration-algorithm-private.json"), result);
        var best = result.Best ?? throw new InvalidOperationException("New algorithm returned no route");
        var histories = result.Trace?.Spans.Where(s => s.Stage == "search" && s.Phase == "completed-route").ToArray() ?? [];
        int duplicates = histories.Length - histories.Select(s => s.Detail).Distinct(StringComparer.Ordinal).Count();
        if (result.Status != "done" || result.Rejected != 0 || !best.Won || result.Timing?.Verifications != 1 ||
            best.Continuation?.Length != best.Actions.Length || duplicates != 0 || histories.Length == 0 ||
            result.Trace?.Dropped != 0 || result.Message.StartsWith("常规执行", StringComparison.Ordinal))
            throw new InvalidOperationException("New-mode native search/coverage/final verification failed: " + result.Message);
        LocalWire.Write(Path.Combine(root, "integration-algorithm-summary.json"), new
        {
            version = "0.7.11", ui_controls_mounted = true, outside_combat_guard_passed = true,
            request.SearchOrder, request.Workers, request.MaxNodes, request.BudgetSeconds, request.MaxRounds,
            request.IncludePotions, request.StopOnZeroLoss, request.NumericalExecution, request.TrimWorkerOverhead,
            manual_seed = false, result.Status, result.Evaluated, result.Victories, result.Rejected, result.ElapsedMs,
            result.Timing, result.TurnSearch, completed_histories = histories.Length, repeated_histories = duplicates,
            best.Won, best.StartingHp, best.Hp, best.HpLost, best.NetHpLoss, best.Rounds,
            used_potions = best.Actions.Count(a => a.PotionSlot.HasValue), verified_steps = best.Continuation!.Length
        });
    }
}
