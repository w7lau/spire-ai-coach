using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Recreate the frozen event only in an owned host, then capture the game's actual
// event history. The two fixture choices are never injected as a search answer.
internal static class EventEntryIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest frozen, LocalInstallation installation)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var settled = typeof(LocalCapture).GetMethod("ExecutionSettled", BindingFlags.Static | BindingFlags.NonPublic)!;
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
        while (NAssetLoader.Instance.IsProcessing()) await Frame();
        await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
        await PreloadManager.LoadActAssets(run.Acts[run.CurrentActIndex]);
        // The fixture has real event visuals. Instant mode turns its repeated
        // zero-effect punching animation into a synchronous infinite loop.
        SaveManager.Instance.PrefsSave.FastMode = FastModeType.Normal;
        manager.Launch();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
        await Frame(); await Frame();
        await manager.GenerateMap();
        await manager.LoadIntoLatestMapCoord(null);
        if (run.CurrentRoom is not EventRoom room || room.CanonicalEvent.Id.Entry != "PUNCH_OFF")
            throw new InvalidOperationException("Frozen preparation incident no longer enters the expected native event");
        var sync = manager.EventSynchronizer;
        foreach (var key in new[] { "PUNCH_OFF.pages.INITIAL.options.I_CAN_TAKE_THEM", "PUNCH_OFF.pages.I_CAN_TAKE_THEM.options.FIGHT" })
        {
            var offered = sync.GetLocalEvent().CurrentOptions;
            int index = offered.Select((o, i) => (o, i)).Single(x => x.o.TextKey == key && !x.o.IsLocked).i;
            sync.ChooseLocalOption(index);
            await sync.AwaitPendingOptionTasks();
        }
        var wait = Stopwatch.StartNew();
        while (!CombatManager.Instance.IsInProgress || !LocalCapture.Stable() || !(bool)settled.Invoke(null, null)!)
        {
            if (wait.Elapsed.TotalSeconds > 12) throw new TimeoutException("Owned event fixture did not reach its first decision");
            await Frame();
        }
        var captured = LocalCapture.Capture("owned-event-root", false);
        LocalWire.Write(Path.Combine(root, "integration-event-capture-private.json"), captured);
        if (captured.EventEntry is not { Choices.Length: 2 } || captured.NativeHash != frozen.NativeHash || captured.History != frozen.History ||
            captured.ModelHash != frozen.ModelHash ||
            !captured.LoadedMods.Where(m => !m.StartsWith("SpireLocalIntegration:", StringComparison.Ordinal) &&
                !m.StartsWith("SpireNativeProbe:", StringComparison.Ordinal)).SequenceEqual(frozen.LoadedMods))
            throw new InvalidOperationException("Native event choices did not recreate the exact frozen combat root/history");
        manager.CleanUp();
        SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
        NGame.Instance.RootSceneContainer.SetCurrentScene(new Control());
        await Frame(); await Frame();
        var preparation = Stopwatch.StartNew();
        await Task.Run(() => pool.Prepare(installation with { MinimalWorkerBootstrap = true }, 1, CancellationToken.None));
        long preparationMs = preparation.ElapsedMilliseconds;
        var worker = ((Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pool)!).GetValue(0)!;
        var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        int pid = ((Process)worker.GetType().GetProperty("Process")!.GetValue(worker)!).Id;
        // Replay the original frozen packet, not the fixture host's new recorder.
        // Only add the entry history just captured from the recreated native run.
        var command = frozen with { EventEntry = captured.EventEntry,
            MaxNodes = 1, MaxDepth = 1, MaxRounds = 1, Workers = 1, Partition = 0, Partitions = 1,
            InitialPlan = [new(-1, "", null, "", "", frozen.NativeHash, 1, EndTurn: true)],
            SearchOrder = LocalSearchOrder.MonteCarlo, VerifyCandidate = null, RecordedReplayProbe = null,
            DeferVerification = true, SkipFinalVerification = true, StopOnZeroLoss = false, StopOnFirstWin = false,
            ShareSearchWork = false, SearchWorkPipe = null, TurnWorkPipe = null, MinimumLossPipe = null, ProgressPipe = null };
        LocalCandidate? control = null;
        var samples = new List<object>();
        foreach (var (numerical, algorithm) in new[] { (true, LocalSearchOrder.MonteCarlo), (false, LocalSearchOrder.MonteCarlo),
            (true, LocalSearchOrder.MonteCarlo), (true, LocalSearchOrder.TurnFrontier) })
        {
            var result = await Submit(command with { Id = Guid.NewGuid().ToString("N"), DataOnlyCombat = numerical,
                DataOnlyRun = numerical, NumericalExecution = numerical, SearchOrder = algorithm });
            var best = result.Best ?? throw new InvalidOperationException(result.Message);
            if (result.Status != "searched" || result.Evaluated != 1 || result.Rejected != 0 ||
                best.Actions.Length != 1 || !best.Actions[0].EndTurn || best.Continuation?.Length != 1)
                throw new InvalidOperationException("Event combat did not restore and settle its first enemy turn");
            control ??= best;
            if (JsonSerializer.Serialize(best.Actions) != JsonSerializer.Serialize(control.Actions) ||
                JsonSerializer.Serialize(best.Continuation) != JsonSerializer.Serialize(control.Continuation) ||
                (best.Hp, best.HpLost, best.EnemyHp, best.Gold, best.MaxHp, best.Won, best.Dead,
                    best.StartingHp, best.DamageSources, best.HealthChanges, best.CardGoalOutcome) !=
                (control.Hp, control.HpLost, control.EnemyHp, control.Gold, control.MaxHp, control.Won, control.Dead,
                    control.StartingHp, control.DamageSources, control.HealthChanges, control.CardGoalOutcome))
                throw new InvalidOperationException("Event restoration changed native decisions/history/settlement");
            samples.Add(new { numerical, algorithm = algorithm.ToString(), result.ElapsedMs, result.Timing, exactRootAndHistory = true,
                completeNativeCheckpoints = true, sameNativeOutcome = true, reusedWorker = samples.Count > 0 });
        }
        var rejected = await Submit(command with { Id = Guid.NewGuid().ToString("N"), EventEntry = null });
        if (rejected.Status != "failed" || rejected.Evaluated != 0 || rejected.Failure?.Category != "local_event_entry")
            throw new InvalidOperationException("Missing event entry did not fail explicitly before waiting for gameplay");
        LocalWire.Write(Path.Combine(root, "integration-event-summary.json"), new {
            version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3),
            nativeModule = typeof(CombatManager).Assembly.ManifestModule.ModuleVersionId,
            preparationMs, preparationIncludedInSamples = false,
            exactFrozenRootRecreated = true, capturedNativeEntryChoices = captured.EventEntry.Choices.Length,
            samples, missingEntry = new { rejected.ElapsedMs, rejected.Timing, rejected.Status, rejected.Failure!.Category },
            fullSearchBenchmark = false, productionBudgetsUnchanged = true, passed = true });

        async Task<LocalSearchResult> Submit(LocalSearchRequest next)
        {
            LocalWire.Write(Path.Combine(workerRoot, "request.json"), next);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 30)
            {
                await Task.Delay(100);
                if (!File.Exists(Path.Combine(workerRoot, "result.json"))) continue;
                var result = LocalWire.Read<LocalSearchResult>(Path.Combine(workerRoot, "result.json"));
                if (result.Id != next.Id || result.Status == "running") continue;
                if (result.Status != "failed")
                {
                    var idle = Path.Combine(workerRoot, "idle.json");
                    if (!File.Exists(idle) || LocalWire.Read<LocalWorkerIdle>(idle).Id != next.Id) continue;
                }
                if (((Process)worker.GetType().GetProperty("Process")!.GetValue(worker)!).Id != pid)
                    throw new InvalidOperationException("Event fixture rebuilt its owned worker");
                if ((bool)worker.GetType().GetMethod("GameErrors")!.Invoke(worker, null)!)
                    throw new InvalidOperationException("Owned event fixture reported a native runtime error");
                LocalWire.Write(Path.Combine(root, "integration-event-result-private-" + samples.Count + ".json"), result);
                return result;
            }
            throw new TimeoutException("Owned event restoration did not complete");
        }
    }
}
