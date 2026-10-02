using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

public static class ExecutionIntegration
{
    public static async Task Run(string root, SceneTree tree, LocalWorkerPool pool, StateCapture capture, Player player)
    {
        var snapshot = capture.Capture(true)!;
        var request = LocalCapture.Capture(snapshot.Fingerprint(), true) with { Workers = 1, MaxNodes = 4, BudgetSeconds = 15 };
        var installation = LocalCapture.Installation();
        var preparation = Stopwatch.StartNew();
        await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
        preparation.Stop();
        var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
        if (request.NativeHash != LocalCapture.Fingerprint() || result.Best is not { Won: true, Continuation.Length: > 1 })
            throw new InvalidOperationException("Need an unchanged host and verified winning plan");
        var executor = new LocalPlanExecutor(tree);
        LocalContinuation Plan(LocalSearchResult? r = null) => new(snapshot.CombatId, request.LoadedMods, r ?? result);
        string? CombatId() => capture.Capture(false)?.CombatId;
        int commands = 0;
        async Task Reject(LocalContinuation plan, CancellationToken token = default)
        {
            try { await executor.Execute(plan, CombatId, false, _ => commands++, token); }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
            {
                if (request.NativeHash != LocalCapture.Fingerprint() || request.History != LocalCapture.History())
                    throw new InvalidOperationException("Rejected execution changed the host");
                return;
            }
            throw new InvalidOperationException("Invalid execution was accepted");
        }
        await Reject(new LocalContinuation("wrong-combat", request.LoadedMods, result));
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); await Reject(Plan(), canceled.Token);
        var first = result.Best.Actions[0];
        await Reject(Plan(result with { Best = result.Best with { Actions = [first with { BeforeHash = "stale" }, .. result.Best.Actions.Skip(1)] } }));
        await Reject(Plan(result with { IncludePotions = true, Best = result.Best with
        { Actions = [first with { EndTurn = false, PotionSlot = 0 }, .. result.Best.Actions.Skip(1)] } }));
        var messages = new List<string>();
        using var stopAfterFirst = new CancellationTokenSource();
        try
        {
            await executor.Execute(Plan(), CombatId, false, text => { messages.Add(text); stopAfterFirst.Cancel(); }, stopAfterFirst.Token);
            throw new InvalidOperationException("Cancellation before enqueue was ignored");
        }
        catch (OperationCanceledException) { }
        if (LocalCapture.Fingerprint() != request.NativeHash) throw new InvalidOperationException("Canceled step changed host");
        messages.Clear();
        var final = await executor.Execute(Plan(), CombatId, false, messages.Add, CancellationToken.None);
        var timer = Stopwatch.StartNew();
        while (CombatManager.Instance.IsInProgress && timer.Elapsed.TotalSeconds < 10)
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        if (player.Creature.IsDead || player.Creature.CurrentHp != result.Best.Hp || messages.Count != result.Best.Actions.Length ||
            !CombatManager.Instance.IsOverOrEnding)
            throw new InvalidOperationException("Automatic execution did not match the verified victory");
        LocalWire.Write(Path.Combine(root, "integration-execution.json"), new
        { prepared_ms = preparation.ElapsedMilliseconds, analysis_startup_ms = result.Timing?.StartupMs,
            executed_steps = messages.Count, expected_steps = result.Best.Actions.Length,
            includes_end_turn = result.Best.Actions.Any(a => a.EndTurn), final_hp = player.Creature.CurrentHp,
            expected_hp = result.Best.Hp, rejected_wrong_combat = true, rejected_stale = true,
            rejected_disabled_potion = true, canceled_without_mutation = true, final });
    }
}
