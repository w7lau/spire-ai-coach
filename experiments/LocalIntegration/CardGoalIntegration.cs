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

// Small native acceptance cases, never a benchmark or an exhaustive-search claim.
internal static class CardGoalIntegration
{
    public static async Task Run(string root, SceneTree tree, LocalWorkerPool pool, SerializableRun fixture)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var records = new List<object>();
        foreach (var kind in new[] { "repeat-and-feed", "other-finisher", "MonteCarlo", "TurnFrontier" })
        {
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
            NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control()); await Frame(); await Frame();
            var run = RunState.FromSerializable(fixture);
            await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
            var player = run.Players.Single();
            var old = player.Deck.Cards.ToArray(); player.Deck.Clear(silent: true);
            foreach (var card in old) run.RemoveCard(card);
            foreach (var id in new[] { "DEFEND_IRONCLAD", "ANGER", "ANGER", "ANGER", "FEED" })
            {
                var card = run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player);
                if (id == "FEED") card.UpgradeInternal();
                await CardPileCmd.Add(card, player.Deck, skipVisuals: true);
            }
            foreach (var potion in player.Potions.ToArray()) potion.Discard();
            if (kind is "repeat-and-feed" or "other-finisher")
                await PotionCmd.TryToProcure(ModelDb.Potion<MegaCrit.Sts2.Core.Models.Potions.Duplicator>().ToMutable(), player, 0);
            await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
            await PreloadManager.LoadActAssets(run.Acts[0]);
            RunManager.Instance.Launch(); NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await RunManager.Instance.GenerateMap();
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster,
                ModelDb.AllEncounters.Single(e => e.Id.Entry == "SLUMBERING_BEETLE_NORMAL").ToMutable(), false);
            while (!LocalCapture.Stable()) await Frame();
            var goals = new LocalCardGoals("CARD.ANGER", "CARD.FEED", 5, "愤怒", "狂宴");
            if (kind is "repeat-and-feed" or "other-finisher")
            {
                var accountingType = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalCardGoalAccounting", true)!;
                using var ledger = (IDisposable)Activator.CreateInstance(accountingType, player, goals)!;
                async Task Play(string id)
                {
                    var card = player.PlayerCombatState!.Hand.Cards.First(c => c.Id.Entry == id);
                    var enemy = player.Creature.CombatState!.Enemies.First(e => e.IsAlive);
                    RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, card.IsValidTarget(null) ? null : enemy));
                    await Frame(); await RunManager.Instance.ActionQueueSet.BecameEmpty();
                    while (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsOverOrEnding && !LocalCapture.Stable()) await Frame();
                    // Include the real victory hooks before recording this step.
                    await Frame(); await Frame();
                    accountingType.GetMethod("CompleteStep")!.Invoke(ledger, null);
                }
                var duplicator = player.GetPotionAtSlotIndex(0)!;
                duplicator.EnqueueManualUse(duplicator.IsValidTarget(null) ? null : player.Creature);
                await Frame(); await RunManager.Instance.ActionQueueSet.BecameEmpty();
                while (!LocalCapture.Stable()) await Frame();
                accountingType.GetMethod("CompleteStep")!.Invoke(ledger, null);
                await Play("ANGER");
                var interim = (LocalCardGoalOutcome)accountingType.GetMethod("Snapshot")!.Invoke(ledger, null)!;
                if (interim.Plays != 2 || interim.Kills != 0) throw new InvalidOperationException("Native repeated play counting failed");
                var enemy = player.Creature.CombatState!.Enemies.First(e => e.IsAlive);
                // Only this synthetic direct accounting case changes an enemy HP.
                // Worker replay cases below capture untouched native battle roots.
                enemy.SetCurrentHpInternal(1);
                await Play(kind == "repeat-and-feed" ? "FEED" : "ANGER");
                var final = (LocalCardGoalOutcome)accountingType.GetMethod("Snapshot")!.Invoke(ledger, null)!;
                if (final.Kills != (kind == "repeat-and-feed" ? 1 : 0) || final.Steps.Sum(s => s.Plays) != final.Plays ||
                    final.Steps.Sum(s => s.Kills) != final.Kills)
                    throw new InvalidOperationException("Native finishing-blow source counting failed");
                records.Add(new { kind, final.Plays, final.Kills, nativeRepeatedPlayObserved = true });
                continue;
            }
            var capture = new StateCapture(); var snapshot = capture.Capture(true)!;
            var hand = player.PlayerCombatState!.Hand.Cards;
            var defense = hand.Single(c => c.Id.Entry == "DEFEND_IRONCLAD");
            var anger = hand.First(c => c.Id.Entry == "ANGER");
            LocalAction Action(CardModel card) => new(hand.ToList().IndexOf(card), card.Id.ToString(),
                card.IsValidTarget(null) ? null : player.Creature.CombatState!.Enemies[0].CombatId,
                card.Title, "enemy", LocalCapture.Fingerprint(), CombatManager.Instance.DebugOnlyGetState()!.RoundNumber,
                CombatCardIndex: NetCombatCard.FromModel(card).CombatCardIndex);
            var request = LocalCapture.Capture(snapshot.Fingerprint(), true) with { Workers = 1,
                MaxNodes = 6, MaxRounds = 8, BudgetSeconds = 12, StopOnZeroLoss = true,
                SkipFinalVerification = false, InitialPlan = [Action(defense), Action(anger)],
                SearchOrder = Enum.Parse<LocalSearchOrder>(kind), CardGoals = goals,
                DebugEncounter = "SLUMBERING_BEETLE_NORMAL" };
            var result = await Task.Run(() => pool.Analyze(request, LocalCapture.Installation(), _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, "integration-card-goals-" + kind + "-private.json"), result);
            if (request.NativeHash != LocalCapture.Fingerprint() || result.Status != "done" || result.Rejected != 0 ||
                result.StoppedEarly || result.Best is not { CardGoalOutcome: { Plays: > 0 } } best ||
                !LocalSearchPolicy.HasExecutionPoints(result) || result.CardGoals != goals ||
                best.CardGoalOutcome.Steps.Length != best.Actions.Length ||
                best.CardGoalOutcome.Plays != best.CardGoalOutcome.Steps.Sum(s => s.Plays) ||
                best.CardGoalOutcome.Kills != best.CardGoalOutcome.Steps.Sum(s => s.Kills))
                throw new InvalidOperationException(kind + ": native goal request, search or independent verification failed: " + result.Message);
            records.Add(new { kind, result.Status, result.Evaluated, result.Victories, result.StoppedEarly,
                best.Won, best.Hp, best.NetHpLoss, best.CardGoalOutcome.Plays, best.CardGoalOutcome.Kills,
                verified = result.Timing?.Verifications, hostUnchanged = true });
        }
        LocalWire.Write(Path.Combine(root, "integration-card-goals-summary.json"), new {
            version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3),
            scope = "Owned synthetic native cases; not performance or arbitrary-Mod coverage proof", records });
    }
}
