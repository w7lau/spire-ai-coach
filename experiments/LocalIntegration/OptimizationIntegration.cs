using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Runs;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

public static class OptimizationIntegration
{
    public static async Task Run(string root, SceneTree tree, LocalWorkerPool pool, StateCapture capture, Player player)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var snapshot = capture.Capture(true)!;
        var request = LocalCapture.Capture(snapshot.Fingerprint(), true) with { Workers = 1, MaxNodes = 4, BudgetSeconds = 15 };
        var installation = LocalCapture.Installation();
        var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
        var unchanged = request.NativeHash == LocalCapture.Fingerprint();
        if (!unchanged || result.Best?.Continuation is not { Length: > 1 } || result.Timing is not { Actions: > 0, Restores: > 0 })
            throw new InvalidOperationException("Missing verified continuation/timing or changed synthetic host");
        var warm = await Task.Run(() => pool.Analyze(request with { Id = Guid.NewGuid().ToString("N") }, installation, _ => { }, CancellationToken.None));
        if (request.NativeHash != LocalCapture.Fingerprint() || warm.Best?.Continuation is not { Length: > 1 })
            throw new InvalidOperationException("Warm search did not preserve the host or produce a verified route");
        var continuation = new LocalContinuation(snapshot.CombatId, request.LoadedMods, result);
        if (continuation.Advance(snapshot.CombatId, LocalCapture.LoadedMods(), LocalCapture.Fingerprint(), LocalCapture.History()) == null)
            throw new InvalidOperationException("Root history differs");
        int consumed = 0;
        // Follow enough of the verified plan to cover a card and an enemy-turn boundary.
        foreach (var action in result.Best.Actions.Take(result.Best.Actions.Length - 1))
        {
            if (LocalCapture.Fingerprint() != action.BeforeHash) throw new InvalidOperationException("Actual synthetic state diverged");
            if (action.EndTurn)
            {
                var round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                PlayerCmd.EndTurn(player, canBackOut: false);
                while (CombatManager.Instance.DebugOnlyGetState()!.RoundNumber <= round) await Frame();
            }
            else
            {
                var target = action.TargetId == null ? null : player.Creature.CombatState!.Creatures.Single(c => c.CombatId == action.TargetId);
                RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(player.PlayerCombatState!.Hand.Cards[action.HandIndex], target));
                await Frame(); await RunManager.Instance.ActionQueueSet.BecameEmpty();
            }
            while (!LocalCapture.Stable()) await Frame();
            await Frame();
            consumed++;
            var next = continuation.Advance(snapshot.CombatId, LocalCapture.LoadedMods(), LocalCapture.Fingerprint(), LocalCapture.History());
            if (next?.Best?.Actions.Length != result.Best.Actions.Length - consumed)
                throw new InvalidOperationException("Continuation did not match actual card/turn history");
            if (action.EndTurn) break;
        }
        if (consumed == 0) throw new InvalidOperationException("No continuation tested");
        var mismatch = new LocalHistoryStamp(LocalCapture.History().Count, "deliberate-mismatch");
        if (continuation.Advance(snapshot.CombatId, LocalCapture.LoadedMods(), LocalCapture.Fingerprint(), mismatch) != null)
            throw new InvalidOperationException("Changed history accepted");
        LocalWire.Write(Path.Combine(root, "integration-optimization.json"), new { unchanged_during_search = unchanged,
            continued_steps = consumed, history_mismatch_rejected = true, result, warm_result = warm });
    }
}
