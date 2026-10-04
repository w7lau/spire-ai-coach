using System.Diagnostics;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Entry verifies the exact owned executable and private APPDATA before reaching this fixture.
// Replays a frozen incident in that test host, never in the user's running game.
public static class ExecutionReplayIntegration
{
    public static async Task Run(string root, LocalSearchRequest frozen, string resultPath)
    {
        if (!File.Exists(Path.Combine(root, ".spire-native-probe-owner")) ||
            !string.Equals(OS.GetExecutablePath().Replace('/', '\\'), Path.Combine(root, "game", "SlayTheSpire2.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Execution replay requires an owned host");
        var tree = (SceneTree)Engine.GetMainLoop();
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var notificationField = typeof(CombatStateTracker).GetField("_combatStateChangedDeferredTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task Settle(int? afterRound = null)
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                var executing = RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                var notification = notificationField.GetValue(CombatManager.Instance.StateTracker) as Task;
                if (executing.IsCompletedSuccessfully && (notification == null || notification.IsCompletedSuccessfully) &&
                    (CombatManager.Instance.IsOverOrEnding || LocalCapture.Stable() &&
                        (!afterRound.HasValue || CombatManager.Instance.DebugOnlyGetState()!.RoundNumber > afterRound))) return;
                if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException("Owned native execution did not settle");
                await Frame();
            }
        }
        // Restore already-completed native selections only in this isolated test host.
        var replayChoices = typeof(ModEntry).Assembly.GetType("SpireAiCoach.Mod.LocalReplayChoices")!;
        var harmonyType = Type.GetType("HarmonyLib.Harmony, 0Harmony", throwOnError: true)!;
        var harmony = Activator.CreateInstance(harmonyType, "SpireLocalIntegration.owned-execution-replay")!;
        replayChoices.GetMethod("Install")!.Invoke(null, [harmony]);
        async Task<Player> Restore()
        {
            if (RunManager.Instance.IsInProgress)
            {
                RunManager.Instance.CleanUp();
                NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control());
                await Frame(); await Frame();
            }
            var reader = new PacketReader(); reader.Reset(frozen.Replay);
            var replay = reader.Read<CombatReplay>();
            var run = RunState.FromSerializable(replay.serializableRun);
            var manager = RunManager.Instance;
            await manager.SetUpSavedSingleplayer(run, replay.serializableRun);
            typeof(RunManager).GetProperty(nameof(RunManager.ShouldSave))!.SetValue(manager, false);
            manager.ActionQueueSet.FastForwardNextActionId(replay.nextActionId);
            manager.ActionQueueSynchronizer.FastForwardHookId(replay.nextHookId);
            manager.PlayerChoiceSynchronizer.FastForwardChoiceIds(replay.choiceIds);
            manager.RewardsSetSynchronizer.FastForwardRewardIds(replay.rewardIds);
            using var historical = (IDisposable)Activator.CreateInstance(replayChoices, [replay.events])!;
            await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
            await PreloadManager.LoadActAssets(run.Acts[run.CurrentActIndex]);
            manager.Launch();
            NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await Frame(); await Frame();
            await manager.GenerateMap(); await manager.LoadIntoLatestMapCoord(null);
            var player = LocalContext.GetMe(run)!;
            await Settle();
            foreach (var item in replay.events)
            {
                if (item.eventType is CombatReplayEventType.HookAction or CombatReplayEventType.PlayerChoice or CombatReplayEventType.ResumeAction) continue;
                if (item.eventType != CombatReplayEventType.GameAction || item.action == null)
                    throw new InvalidOperationException("Unknown incident replay event");
                var action = item.action.ToGameAction(player);
                if (action is ReadyToBeginEnemyTurnAction) continue;
                var round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                if (action is UsePotionAction use)
                {
                    var target = use.TargetId == null ? null : player.Creature.CombatState!.Creatures.Single(c => c.CombatId == use.TargetId);
                    player.GetPotionAtSlotIndex((int)use.PotionIndex)!.EnqueueManualUse(target);
                }
                else manager.ActionQueueSet.EnqueueWithoutSynchronizing(action);
                await Frame(); await Settle(action is EndPlayerTurnAction ? round : null);
            }
            replayChoices.GetMethod("Finish")!.Invoke(historical, null);
            if (LocalCapture.Fingerprint() != frozen.NativeHash || LocalCapture.History() != frozen.History)
                throw new InvalidOperationException("Owned scene did not reproduce the exact incident root");
            return player;
        }

        var result = LocalWire.Read<LocalSearchResult>(resultPath) with { VerificationSkipped = true };
        if (!LocalSearchPolicy.HasExecutionPoints(result) || result.Best is not { Won: true } best)
            throw new InvalidOperationException("A complete frozen first-pass winner is required");
        var player = await Restore();
        var capture = new StateCapture();
        LocalContinuation Plan(LocalSearchResult r) => new(capture.Capture(false)!.CombatId, LocalCapture.LoadedMods(), r);
        string? CombatId() => capture.Capture(false)?.CombatId;
        var executor = new LocalPlanExecutor(tree);

        // Queue-empty with a pending native notification is not ready to execute.
        var tracker = CombatManager.Instance.StateTracker;
        var previous = notificationField.GetValue(tracker);
        var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationField.SetValue(tracker, delayed.Task);
        using var stop = new CancellationTokenSource();
        int dispatches = 0;
        var waiting = executor.Execute(Plan(result), CombatId, true, _ => { dispatches++; stop.Cancel(); }, stop.Token);
        await Frame(); await Frame(); await Frame();
        if (dispatches != 0 || waiting.IsCompleted || LocalCapture.Fingerprint() != frozen.NativeHash)
            throw new InvalidOperationException("Executor dispatched before the deferred notification completed");
        delayed.SetResult();
        try { await waiting; throw new InvalidOperationException("Pre-enqueue cancellation was ignored"); }
        catch (OperationCanceledException) { }
        notificationField.SetValue(tracker, previous);
        if (LocalCapture.Fingerprint() != frozen.NativeHash || LocalCapture.History() != frozen.History)
            throw new InvalidOperationException("Canceled execution changed the frozen test host");

        // A truncated winning route must produce a mismatch, never a completed-plan message.
        var truncated = result with { Best = best with { Actions = [best.Actions[0]], Continuation = [best.Continuation![0]] } };
        try { await executor.Execute(Plan(truncated), CombatId, true, _ => { }, CancellationToken.None);
            throw new InvalidOperationException("Truncated victory was silently accepted"); }
        catch (InvalidOperationException) when (executor.Report?.Stage == "victory-mismatch") { }
        if (executor.Report is not { SettledActions: 1, PlannedActions: 1, BattleEnded: false })
            throw new InvalidOperationException("Truncated execution lost its stop evidence");
        LocalWire.Write(Path.Combine(root, "integration-truncated-execution-private.json"), executor.Report);

        player = await Restore();
        var messages = new List<string>();
        var final = await executor.Execute(Plan(result), CombatId, true, messages.Add, CancellationToken.None);
        if (player.Creature.IsDead || player.Creature.CurrentHp != best.Hp || messages.Count != best.Actions.Length ||
            executor.Report is not { BattleEnded: true } report || report.SettledActions != best.Actions.Length)
            throw new InvalidOperationException("Frozen native route did not execute to the predicted victory");
        LocalWire.Write(Path.Combine(root, "integration-execution-replay-private.json"), executor.Report);
        LocalWire.Write(Path.Combine(root, "integration-execution-replay-summary.json"), new {
            exact_incident_root = true, frozen_history_entries = frozen.History!.Count,
            withheld_dispatch_until_notification_completed = true, canceled_before_enqueue = true,
            rejected_truncated_victory = true, executed_steps = messages.Count, expected_steps = best.Actions.Length,
            final_hp = player.Creature.CurrentHp, expected_hp = best.Hp, battle_ended = true,
            complete_first_pass_route_without_independent_verification = true, final });
    }
}
