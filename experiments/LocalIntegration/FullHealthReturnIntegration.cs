using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
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

// Short seeded native mechanism probe, not a route-quality or speed benchmark.
// All HP/deck edits belong to this synthetic private run, never the user's game.
internal static class FullHealthReturnIntegration
{
    public static async Task Run(string root, SceneTree tree, LocalWorkerPool pool, SerializableRun fixture)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var samples = new List<object>();
        var encounter = ModelDb.AllEncounters.Single(e => e.Id.Entry == "TOADPOLES_WEAK");
        foreach (var initialHp in new[] { 52, 100, 247 })
        {
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
            NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control()); await Frame(); await Frame();
            var run = RunState.FromSerializable(fixture);
            await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
            var player = run.Players.Single();
            player.Creature.SetMaxHpInternal(initialHp == 52 ? 66 : 250); player.Creature.SetCurrentHpInternal(initialHp);
            if (!player.Relics.Any(r => r.Id.Entry == "BURNING_BLOOD"))
                throw new InvalidOperationException("This mechanism fixture requires native Burning Blood");
            if (initialHp == 52)
                foreach (var relic in player.Relics.ToArray()) player.RemoveRelicInternal(relic, silent: true);
            foreach (var potion in player.Potions.ToArray()) potion.Discard();
            var old = player.Deck.Cards.ToArray(); player.Deck.Clear(silent: true);
            foreach (var c in old) run.RemoveCard(c);
            for (int i = 0; i < 5; i++)
            {
                var card = run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == "CLASH"), player);
                card.UpgradeInternal();
                await CardPileCmd.Add(card, player.Deck, skipVisuals: true);
            }
            await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
            await PreloadManager.LoadActAssets(run.Acts[0]);
            RunManager.Instance.Launch(); NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await RunManager.Instance.GenerateMap();
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster,
                encounter.ToMutable(), false);
            var settle = Stopwatch.StartNew();
            while (!LocalCapture.Stable())
            {
                if (settle.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Native fixture did not settle");
                await Frame();
            }
            var capture = new StateCapture(); var snapshot = capture.Capture(true)!;
            var nativeBefore = LocalCapture.Fingerprint();
            var combat = CombatManager.Instance.DebugOnlyGetState()!;
            var hand = player.PlayerCombatState!.Hand.Cards;
            var initial = hand.Take(4).Select((c, i) => new LocalAction(hand.ToList().IndexOf(c), c.Id.ToString(),
                combat.Enemies[i / 2 % combat.Enemies.Count].CombatId, c.Title, "enemy", nativeBefore, combat.RoundNumber,
                CombatCardIndex: NetCombatCard.FromModel(c).CombatCardIndex)).ToArray();
            var captured = LocalCapture.Capture(snapshot.Fingerprint(), false) with {
                Workers = 1, MaxNodes = 4, MaxRounds = 3, MaxDepth = 30, BudgetSeconds = 15,
                StopOnZeroLoss = true, StopOnFirstWin = false, IncludePotions = false,
                InitialPlan = initial, SkipFinalVerification = false,
                ShareSearchWork = true, DebugEncounter = encounter.Id.Entry };
            foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            {
                var request = captured with { Id = Guid.NewGuid().ToString("N"), SearchOrder = order };
                var result = await Task.Run(() => pool.Analyze(request, LocalCapture.Installation(), _ => { }, CancellationToken.None));
                LocalWire.Write(Path.Combine(root, $"integration-full-health-{initialHp}-{order}-private.json"), result);
                int cap = initialHp == 52 ? 66 : 250;
                int expectedHp = initialHp == 52 ? 52 : Math.Min(250, initialHp + 6);
                if (result.Best is not { Won: true, Dead: false, HpLost: 0, Rounds: 1 } best ||
                    best.Hp != expectedHp || best.MaxHp != cap || best.NetHpLoss != 0 ||
                    !result.StoppedEarly || !result.StoppedOnHealthTarget || result.HealthTarget?.TargetHp != expectedHp ||
                    result.HealthBounds?.TargetAnalyses != 1 || result.StoppedOnMinimum || LocalSearchPolicy.HasMinimumProof(result) ||
                    result.Evaluated >= captured.MaxNodes || result.Timing?.Verifications != 1 ||
                    result.Rejected != 0 || result.Failure != null || result.RecoveredFailures is { Length: > 0 } ||
                    !LocalSearchPolicy.HasExecutionPoints(result))
                    throw new InvalidOperationException("Native victory-healing/full-health return mismatch: " + result.Message);
                if (LocalCapture.Fingerprint() != nativeBefore || capture.Capture(true)!.Fingerprint() != snapshot.Fingerprint())
                    throw new InvalidOperationException("Isolated simulation changed the source combat");
                samples.Add(new { algorithm = order.ToString(), startingHp = initialHp, hp = best.Hp,
                    maxHp = best.MaxHp, netHpLoss = best.NetHpLoss, stoppedEarly = result.StoppedEarly,
                    result.Evaluated, result.Victories, result.ElapsedMs, verifications = result.Timing.Verifications,
                    result.HealthTarget, result.StoppedOnHealthTarget, result.HealthBounds,
                    nativeVictoryHealingIncluded = true, sourceCombatUnchanged = true });
                pool.DiscardSearch();
            }
        }
        LocalWire.Write(Path.Combine(root, "integration-full-health-summary.json"), new {
            version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3), passed = true,
            scope = "No healing, fixed victory healing, capped victory healing; both algorithms; one worker; seeded short mechanism probe",
            encounter = encounter.Id.Entry,
            nativeGame = true, productLimitsUnchanged = true, samples });
    }
}
