using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// A short native mechanism comparison, never a reduced-budget search benchmark.
internal static class ModelDisplayIntegration
{
    public static async Task Run(string root, SceneTree tree, LocalWorkerPool pool, SerializableRun fixture)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var samples = new List<object>();
        foreach (var upgraded in new[] { false, true })
        {
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
            NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control()); await Frame(); await Frame();
            var run = RunState.FromSerializable(fixture);
            await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
            var player = run.Players.Single();
            var old = player.Deck.Cards.ToArray(); player.Deck.Clear(silent: true);
            foreach (var c in old) run.RemoveCard(c);
            foreach (var id in new[] { "BOUNCING_FLASK", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD" })
            {
                var card = run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player);
                if (upgraded && id == "BOUNCING_FLASK") card.UpgradeInternal();
                await CardPileCmd.Add(card, player.Deck, skipVisuals: true);
            }
            await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
            await PreloadManager.LoadActAssets(run.Acts[0]);
            RunManager.Instance.Launch(); NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await RunManager.Instance.GenerateMap();
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster,
                ModelDb.AllEncounters.Single(e => e.Id.Entry == "SLUMBERING_BEETLE_NORMAL").ToMutable(), false);
            while (!LocalCapture.Stable()) await Frame();
            var snapshot = new StateCapture().Capture(true)!;
            var hand = player.PlayerCombatState!.Hand.Cards;
            var flask = hand.Single(c => c.Id.Entry == "BOUNCING_FLASK");
            var action = new LocalAction(hand.ToList().IndexOf(flask), flask.Id.ToString(), null, flask.Title, "",
                LocalCapture.Fingerprint(), CombatManager.Instance.DebugOnlyGetState()!.RoundNumber,
                CombatCardIndex: NetCombatCard.FromModel(flask).CombatCardIndex);
            var request = LocalCapture.Capture(snapshot.Fingerprint(), false) with {
                Workers = 1, Partitions = 1, MaxNodes = 1, MaxDepth = 1, MaxRounds = 1, BudgetSeconds = 25,
                InitialPlan = [action], ShareSearchWork = false, DeferVerification = true, SkipFinalVerification = true,
                StopOnZeroLoss = false, StopOnFirstWin = false, SearchOrder = LocalSearchOrder.MonteCarlo,
                DebugEncounter = "SLUMBERING_BEETLE_NORMAL" };
            LocalCandidate? baseline = null;
            foreach (var numerical in new[] { false, true, true })
            {
                var next = request with { Id = Guid.NewGuid().ToString("N"), DataOnlyCombat = numerical,
                    DataOnlyRun = numerical, NumericalExecution = numerical };
                var result = await Task.Run(() => pool.Analyze(next, LocalCapture.Installation() with { MinimalWorkerBootstrap = false },
                    _ => { }, CancellationToken.None));
                LocalWire.Write(Path.Combine(root, $"integration-model-display-private-{samples.Count}.json"), result);
                var best = result.Best ?? throw new InvalidOperationException(result.Message);
                if (result.Status != "done" || result.Rejected != 0 || result.Failure != null ||
                    (result.RecoveredFailures?.Length ?? 0) != 0 || result.Trace!.Spans.Any(s => s.Phase is "fallback" or "stop_failed_pass") ||
                    best.Actions.FirstOrDefault()?.ModelId != flask.Id.ToString() || !LocalSearchPolicy.HasExecutionPoints(result))
                    throw new InvalidOperationException("Native optional display did not finish cleanly: " + result.Message);
                baseline ??= best;
                if (System.Text.Json.JsonSerializer.Serialize(best.Actions) != System.Text.Json.JsonSerializer.Serialize(baseline.Actions) ||
                    System.Text.Json.JsonSerializer.Serialize(best.Continuation) != System.Text.Json.JsonSerializer.Serialize(baseline.Continuation) ||
                    (best.Hp, best.HpLost, best.EnemyHp, best.MaxHp, best.Gold, best.DamageSources) !=
                    (baseline.Hp, baseline.HpLost, baseline.EnemyHp, baseline.MaxHp, baseline.Gold, baseline.DamageSources))
                    throw new InvalidOperationException("Optional display changed poison, RNG, history or native checkpoints");
                long skipped = result.Trace.Methods?.Where(m => m.Method == "BouncingFlask.OptionalDisplay").Sum(m => m.Skipped) ?? 0;
                if (numerical && skipped != (upgraded ? 4 : 3) || !numerical && skipped != 0)
                    throw new InvalidOperationException("Expected native repeated display boundary was not exercised");
                samples.Add(new { upgraded, numerical, result.ElapsedMs, result.Timing, skipped,
                    enemyHp = best.EnemyHp, nativeStateRngHistoryAndCheckpointsMatch = true, fallback = false });
            }
        }
        LocalWire.Write(Path.Combine(root, "integration-model-display-summary.json"), new {
            version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3), passed = true,
            scope = "Native base/upgraded poison card, fixed short mechanism probe, ordinary/data/data; no full-search speed claim",
            productionBudgetsUnchanged = true, samples });
    }
}
