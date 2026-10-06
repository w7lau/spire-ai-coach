using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
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

// Actual native power-card removal and victory callbacks in the owned fixture.
// Seeded stopping acceptance; no unseeded optimizer or arbitrary Mod claim.
internal static class FiniteCardGoalIntegration
{
    public static async Task Run(string root, SceneTree tree, LocalWorkerPool pool, SerializableRun fixture)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var samples = new List<object>();
        var accounting = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalCardGoalAccounting", true)!;
        foreach (string kind in new[] { "one-power", "two-powers", "fixed-recovery", "allowed-loss", "repeatable" })
        {
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
            NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control()); await Frame(); await Frame();
            var run = RunState.FromSerializable(fixture);
            await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
            var player = run.Players.Single();
            player.Creature.SetMaxHpInternal(66); player.Creature.SetCurrentHpInternal(52);
            foreach (var relic in player.Relics.ToArray())
                if (kind != "fixed-recovery" || relic.Id.Entry != "BURNING_BLOOD") player.RemoveRelicInternal(relic, silent: true);
            if (kind == "fixed-recovery" && !player.Relics.Any(r => r.Id.Entry == "BURNING_BLOOD"))
                throw new InvalidOperationException("This native fixture requires Burning Blood");
            // Native first-attack damage lets the three remaining attacks finish
            // the two-power fixture in one turn. Captured before combat/replay.
            await RelicCmd.Obtain(ModelDb.AllRelics.Single(r => r.Id.Entry == "AKABEKO").ToMutable(), player);
            foreach (var potion in player.Potions.ToArray()) potion.Discard();
            var old = player.Deck.Cards.ToArray(); player.Deck.Clear(silent: true);
            foreach (var card in old) run.RemoveCard(card);
            int copies = kind == "one-power" ? 1 : 2;
            for (int i = 0; i < 5; i++)
            {
                var card = run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == (i < copies ? "ROYALTIES" : "CLASH")), player);
                card.UpgradeInternal(); await CardPileCmd.Add(card, player.Deck, skipVisuals: true);
            }
            await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
            await PreloadManager.LoadActAssets(run.Acts[0]);
            RunManager.Instance.Launch(); NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await RunManager.Instance.GenerateMap();
            const string encounter = "TOADPOLES_WEAK";
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster,
                ModelDb.AllEncounters.Single(e => e.Id.Entry == encounter).ToMutable(), false);
            while (!LocalCapture.Stable()) await Frame();
            var goals = new LocalCardGoals(PlayModelId: kind == "repeatable" ? "CARD.CLASH" : "CARD.ROYALTIES",
                HpLossThreshold: kind == "allowed-loss" ? 5 : null);
            var capture = new StateCapture(); var snapshot = capture.Capture(true)!;
            var request = LocalCapture.Capture(snapshot.Fingerprint(), true) with
            {
                Workers = 1, MaxNodes = 2, MaxRounds = 4, BudgetSeconds = 8, StopOnZeroLoss = true,
                SkipFinalVerification = false, ShareSearchWork = false, CardGoals = goals, DebugEncounter = encounter
            };
            LocalWire.Write(Path.Combine(root, "integration-finite-card-" + kind + "-root-private.json"), request);
            var plan = new List<LocalAction>();
            using (var ledger = (IDisposable)Activator.CreateInstance(accounting, player, goals)!)
            {
                int steps = 0;
                while (!CombatManager.Instance.IsOverOrEnding && !player.Creature.IsDead)
                {
                    if (++steps > 30) throw new InvalidOperationException("Native finite-card fixture did not finish");
                    var hand = player.PlayerCombatState!.Hand.Cards;
                    var legal = hand.Where(c => c.CanPlay()).ToArray();
                    var card = legal.FirstOrDefault(c => c.Id.Entry == "ROYALTIES") ?? legal.FirstOrDefault(c => c.Id.Entry == "CLASH");
                    if (card == null) throw new InvalidOperationException("Native finite-card fixture has no playable card");
                    var enemy = player.Creature.CombatState!.Enemies.Where(e => e.IsAlive).OrderByDescending(e => e.CurrentHp).First();
                    plan.Add(new(hand.ToList().IndexOf(card), card.Id.ToString(), card.IsValidTarget(null) ? null : enemy.CombatId,
                        card.Title, "enemy", LocalCapture.Fingerprint(), CombatManager.Instance.DebugOnlyGetState()!.RoundNumber,
                        CombatCardIndex: NetCombatCard.FromModel(card).CombatCardIndex));
                    RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, card.IsValidTarget(null) ? null : enemy));
                    await Frame(); await RunManager.Instance.ActionQueueSet.BecameEmpty().WaitAsync(TimeSpan.FromSeconds(15));
                    while (!CombatManager.Instance.IsOverOrEnding && !player.Creature.IsDead && !LocalCapture.Stable()) await Frame();
                    await Frame(); await Frame();
                    accounting.GetMethod("CompleteStep")!.Invoke(ledger, null);
                    if (card.Type == CardType.Power)
                    {
                        var observed = (LocalCardGoalOutcome)accounting.GetMethod("Snapshot")!.Invoke(ledger, null)!;
                        if (card.Keywords.Contains(CardKeyword.Exhaust) || card.Pile != null)
                            throw new InvalidOperationException("Power card did not natively leave combat");
                        if (kind != "repeatable")
                        {
                            var stock = observed.ConsumableGoals?.Play;
                            int played = plan.Count(a => a.ModelId == goals.PlayModelId);
                            if (stock == null || stock.Copies != copies || stock.CompletedCopies != played ||
                                stock.RemovedCopies != played || stock.ExhaustedCopies != 0 || stock.Complete != (played == copies))
                                throw new InvalidOperationException("Power-copy removal/completion accounting failed: " + System.Text.Json.JsonSerializer.Serialize(observed));
                        }
                    }
                }
            }
            int hp = player.Creature.CurrentHp, maxHp = player.Creature.MaxHp, gold = player.Gold;
            int target = kind == "fixed-recovery" ? 58 : 52;
            if (player.Creature.IsDead || hp != target || hp >= maxHp)
                throw new InvalidOperationException("Native health fixture failed: " + kind + ", hp=" + hp);
            foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            {
                var frozen = request with { Id = Guid.NewGuid().ToString("N"), SearchOrder = order, InitialPlan = plan.ToArray() };
                var result = await Task.Run(() => pool.Analyze(frozen, LocalCapture.Installation(), _ => { }, CancellationToken.None));
                LocalWire.Write(Path.Combine(root, "integration-finite-card-" + kind + "-" + order + "-private.json"), result);
                bool expectedStop = kind != "repeatable";
                if (result.Status != "done" || result.StoppedEarly != expectedStop || result.StoppedOnCardGoals != expectedStop ||
                    result.Evaluated != (expectedStop ? 1 : 2) || result.Best is not { Won: true, Dead: false } best ||
                    best.Hp != hp || best.MaxHp != maxHp || result.Timing?.Verifications != 1 || !LocalSearchPolicy.HasExecutionPoints(result) ||
                    result.RecoveredFailures is { Length: > 0 } || result.StoppedOnMinimum || result.StoppedOnHealthTarget ||
                    player.Creature.CurrentHp != hp || player.Creature.MaxHp != maxHp || player.Gold != gold)
                    throw new InvalidOperationException("Native finite-card stop/replay failed: " + kind + "/" + order + ": " +
                        System.Text.Json.JsonSerializer.Serialize(new { result.Status, result.Message, result.Evaluated, result.StoppedEarly,
                            result.StoppedOnCardGoals, result.HealthTarget, result.Best?.Hp, result.Best?.CardGoalOutcome }));
                if (expectedStop && (best.CardGoalOutcome?.ConsumableGoals?.Play is not { Complete: true, ExhaustedCopies: 0 } stock ||
                    stock.Copies != copies || stock.RemovedCopies != copies || best.CardGoalOutcome.Plays != copies))
                    throw new InvalidOperationException("Independent native replay changed root-copy completion");
                if (kind is not ("allowed-loss" or "repeatable") && result.HealthTarget?.TargetHp != target)
                    throw new InvalidOperationException("Combined finite goal did not reuse the content health target");
                samples.Add(new { kind, algorithm = order.ToString(), copies, result.Evaluated, result.ElapsedMs,
                    result.StoppedOnCardGoals, best.Hp, best.MaxHp, result.HealthTarget,
                    progress = best.CardGoalOutcome?.ConsumableGoals?.Play,
                    verifications = result.Timing.Verifications, checkpoints = best.Actions.Length,
                    targetAnalyses = result.HealthBounds?.TargetAnalyses, seeded = true, hostUnchanged = true });
            }
            LocalWire.Write(Path.Combine(root, "integration-finite-card-summary.json"), new
            {
                version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3), passed = true, samples,
                scope = "Seeded native power removal with one/two actual copies; no-recovery/fixed-recovery/strict allowance and repeatable negative cases; both algorithms independently replayed. No unseeded optimizer or arbitrary Mod proof."
            });
        }
    }
}
