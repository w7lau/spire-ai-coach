using System.Diagnostics;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Controlled proc-state changes only in Entry's already-verified owned native host.
// The live incident's frozen bytes and hashes remain unchanged on disk.
internal static class ModReplayIntegration
{
    public static async Task Run(string root, SceneTree tree, LocalSearchRequest before, string resultPath, string afterPath)
    {
        var incident = LocalWire.Read<LocalSearchResult>(resultPath);
        var after = LocalWire.Read<LocalSearchRequest>(afterPath);
        var prefix = incident.Best!.Actions.Take(4).ToArray();
        if (before.History?.Count != 0 || after.History?.Count != 4 || prefix.Last().ModelId != "CARD.HIDDEN_GEM")
            throw new InvalidDataException("Expected the frozen four-step Hidden Gem incident");
        string NativeHash() => (string)typeof(LocalCapture).GetMethod("NativeFingerprint", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        async Task Settle()
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                var executor = RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                var task = typeof(CombatStateTracker).GetField("_combatStateChangedDeferredTask", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(CombatManager.Instance.StateTracker) as Task;
                if (executor.IsCompletedSuccessfully && (task == null || task.IsCompletedSuccessfully) && LocalCapture.Stable()) return;
                if (watch.Elapsed.TotalSeconds > 30) throw new TimeoutException("Owned prefix did not settle");
                await Frame();
            }
        }
        RelicModel Rune(Player p) => p.Relics.Single(r => r.GetType().FullName == "HextechRunes.HiddenGemUpgradeRune");
        FieldInfo Ordinal(RelicModel r) => r.GetType().GetField("_localUpgradedPlayOrdinal", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task<Player> RestoreHost(int ordinal)
        {
            var manager = RunManager.Instance;
            if (manager.IsInProgress)
            {
                manager.CleanUp(); NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control());
                await Frame(); await Frame();
            }
            var reader = new PacketReader(); reader.Reset(before.Replay);
            var replay = reader.Read<CombatReplay>();
            var run = RunState.FromSerializable(replay.serializableRun);
            var player = run.Players.Single(); var rune = Rune(player);
            Ordinal(rune).SetValue(rune, ordinal); // Synthetic prior-combat history, in this owned fixture only.
            await manager.SetUpSavedSingleplayer(run, replay.serializableRun);
            typeof(RunManager).GetProperty(nameof(RunManager.ShouldSave))!.SetValue(manager, false);
            manager.ActionQueueSet.FastForwardNextActionId(replay.nextActionId);
            manager.ActionQueueSynchronizer.FastForwardHookId(replay.nextHookId);
            manager.PlayerChoiceSynchronizer.FastForwardChoiceIds(replay.choiceIds);
            manager.RewardsSetSynchronizer.FastForwardRewardIds(replay.rewardIds);
            await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
            await PreloadManager.LoadActAssets(run.Acts[run.CurrentActIndex]);
            manager.Launch(); NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await Frame(); await Frame(); await manager.GenerateMap(); await manager.LoadIntoLatestMapCoord(null);
            await Settle();
            if (NativeHash() != before.NativeHash || LocalCapture.History() != before.History)
                throw new InvalidOperationException("Owned scene did not restore the frozen pre-execution native root");
            return player;
        }
        async Task PlayPrefix(Player player)
        {
            foreach (var action in prefix)
            {
                if (NativeHash() != action.BeforeHash) throw new InvalidOperationException("Prefix differed before its audited Mod effect");
                var card = player.PlayerCombatState!.Hand.Cards[action.HandIndex];
                if (card.Id.ToString() != action.ModelId || NetCombatCard.FromModel(card).CombatCardIndex != action.CombatCardIndex)
                    throw new InvalidOperationException("Frozen native card identity changed");
                RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, null));
                await Frame(); await Settle();
            }
        }
        var player = await RestoreHost(0); await PlayPrefix(player);
        string baseline = NativeHash();
        if (baseline != incident.Best.Actions[4].BeforeHash) throw new InvalidOperationException("Native baseline did not reproduce the old worker prediction");
        player = await RestoreHost(1);
        var initial = LocalCapture.Capture("owned-mod-replay", false);
        if (initial.ModReplay is not { Initial.Length: 1, Current.Length: 1 } || initial.ModReplay.Initial[0].Value != 1)
            throw new InvalidOperationException("Read-only replay observation missed the real initial ordinal");
        await PlayPrefix(player);
        string reproduced = NativeHash();
        if (reproduced != after.NativeHash || LocalCapture.History() != after.History)
            throw new InvalidOperationException("Prior-combat ordinal did not reproduce the exact live divergence");
        var captured = LocalCapture.Capture("owned-mod-replay", false);
        if (captured.ModReplay!.Initial[0].Value != 1 || captured.ModReplay.Current[0].Value != 2)
            throw new InvalidOperationException("Current ordinal overwrote the original replay root");
        var nativeClone = RelicModel.FromSerializable(Rune(player).ToSerializable());
        if ((int)Ordinal(nativeClone).GetValue(nativeClone)! != 0) throw new InvalidOperationException("The native serialization gap disappeared");
        try
        {
            typeof(ModEntry).Assembly.GetType("SpireAiCoach.Mod.LocalModReplay")!.GetMethod("Restore")!
                .Invoke(null, [captured.ModReplay, (RunState)player.RunState]);
            throw new InvalidOperationException("Supplemental restoration was callable outside an owned worker");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException denied &&
            denied.Message.Contains("owned worker", StringComparison.Ordinal)) { }
        LocalWire.Write(Path.Combine(root, "integration-mod-replay-request-private.json"), captured);
        var installation = LocalCapture.Installation() with { MinimalWorkerBootstrap = false };
        using var pool = new LocalWorkerPool(Path.Combine(root, "mod-replay-pool"));
        var lane = ((Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!).GetValue(0)!;
        async Task Ensure() => await (Task)lane.GetType().GetMethod("Ensure")!.Invoke(lane,
            [Path.Combine(root, "mod-replay-workers"), 0, installation, CancellationToken.None, null])!;
        Process Process() => (Process)lane.GetType().GetProperty("Process")!.GetValue(lane)!;
        string WorkerRoot() => (string)lane.GetType().GetProperty("Root")!.GetValue(lane)!;
        async Task<LocalSearchResult> Send(LocalSearchRequest command, bool requireIdle = true)
        {
            File.Delete(Path.Combine(WorkerRoot(), "result.json")); File.Delete(Path.Combine(WorkerRoot(), "idle.json"));
            LocalWire.Write(Path.Combine(WorkerRoot(), "request.json"), command);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 90 && !Process().HasExited)
            {
                if (File.Exists(Path.Combine(WorkerRoot(), "result.json")))
                {
                    var result = LocalWire.Read<LocalSearchResult>(Path.Combine(WorkerRoot(), "result.json"));
                    if (result.Id == command.Id && result.Status != "running")
                    {
                        var generation = (string)lane.GetType().GetProperty("Generation")!.GetValue(lane)!;
                        if (requireIdle && !await LocalWorkerSession.WaitForIdle(WorkerRoot(), command, generation, () => !Process().HasExited, TimeSpan.FromSeconds(5)))
                            throw new InvalidOperationException("Owned Mod replay worker did not clean up");
                        return result;
                    }
                }
                await Task.Delay(30);
            }
            throw new TimeoutException("Owned Mod replay worker did not finish");
        }
        await Ensure(); int pid = Process().Id;
        var probe = captured with { ReplayRootOnly = true, Workers = 1, Partitions = 1, History = after.History };
        foreach (bool numeric in new[] { true, false })
        {
            var restored = await Send(probe with { Id = Guid.NewGuid().ToString("N"), DataOnlyCombat = numeric, DataOnlyRun = numeric });
            if (restored is not { Status: "restored", Best: null } || restored.Timing?.Actions != 0)
                throw new InvalidOperationException("Supplemental Mod state did not restore the exact incident root");
        }
        if (Process().Id != pid || LocalCapture.Fingerprint() != captured.NativeHash || LocalCapture.History() != captured.History)
            throw new InvalidOperationException("Worker checks changed the host or broke native PID reuse");
        var searched = await pool.Analyze(captured with { Id = Guid.NewGuid().ToString("N"), Workers = 1, Partitions = 1,
            MaxNodes = 12, BudgetSeconds = 20, StopOnFirstWin = true, StopOnZeroLoss = false, CardGoals = null,
            SkipFinalVerification = false }, installation, _ => { }, CancellationToken.None);
        if (searched.Best is not { Won: true } || !LocalSearchPolicy.HasExecutionPoints(searched))
            throw new InvalidOperationException("Restored Mod root produced no independently verified native winner");
        var capture = new StateCapture();
        var plan = new LocalContinuation(capture.Capture(false)!.CombatId, LocalCapture.LoadedMods(), searched);
        var executor = new LocalPlanExecutor(tree);
        await executor.Execute(plan, () => capture.Capture(false)?.CombatId, captured.IncludePotions, _ => { }, CancellationToken.None);
        if (executor.Report is not { BattleEnded: true, Stage: "battle-ended" } report ||
            report.SettledActions != searched.Best.Actions.Length || player.Creature.CurrentHp != searched.Best.Hp || player.Creature.IsDead)
            throw new InvalidOperationException("Mod replay checkpoints diverged during ordinary native plan execution");
        LocalWire.Write(Path.Combine(root, "integration-mod-replay-execution-private.json"), executor.Report);
        // Failed roots are retired by the product pool, rather than required to
        // acknowledge a reusable idle. Keep that contract in negative fixtures.
        var changed = await Send(probe with { Id = Guid.NewGuid().ToString("N"),
            ModReplay = captured.ModReplay with { Initial = [captured.ModReplay.Initial[0] with { Value = 0 }] } }, requireIdle: false);
        lane.GetType().GetMethod("Stop")!.Invoke(lane, ["隔离负例完成，退役失败实例", null, 0]); await Ensure();
        var omitted = await Send(probe with { Id = Guid.NewGuid().ToString("N"), ModReplay = null }, requireIdle: false);
        if (omitted.Failure?.Category != "local_mod_replay" || changed.Failure?.Category != "local_mod_replay" || omitted.Best != null || changed.Best != null)
            throw new InvalidOperationException("A missing or incorrect Mod checkpoint was accepted");
        LocalWire.Write(Path.Combine(root, "integration-mod-replay-summary.json"), new {
            exact_live_divergence_reproduced = true, baseline_native_hash = baseline, actual_native_hash = reproduced,
            initial_ordinal = 1, current_ordinal = 2, native_clone_ordinal = 0,
            ordinary_and_numeric_root_restored = true, missing_or_changed_checkpoint_rejected = true,
            root_checks_kept_same_pid = true, isolated_host_unchanged_by_worker_checks = true,
            restoration_outside_owned_worker_rejected = true,
            independently_verified_winner = true, executed_steps = executor.Report.SettledActions,
            expected_steps = searched.Best.Actions.Length, final_hp = player.Creature.CurrentHp, expected_hp = searched.Best.Hp,
            ordinary_native_execution_completed = true,
        });
    }
}
