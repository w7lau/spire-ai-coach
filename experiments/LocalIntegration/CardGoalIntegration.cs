using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
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
        foreach (var kind in new[] { "repeat-and-feed", "other-finisher", "feed-not-lethal", "feed-capped", "feed-two-copies", "MonteCarlo", "TurnFrontier", "goal-stop" })
        {
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
            NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control()); await Frame(); await Frame();
            var run = RunState.FromSerializable(fixture);
            await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
            var player = run.Players.Single();
            if (kind == "goal-stop")
            { player.Creature.SetMaxHpInternal(800); player.Creature.SetCurrentHpInternal(800); }
            var old = player.Deck.Cards.ToArray(); player.Deck.Clear(silent: true);
            foreach (var card in old) run.RemoveCard(card);
            foreach (var id in kind == "feed-two-copies" ?
                new[] { "DEFEND_IRONCLAD", "ANGER", "ANGER", "FEED", "FEED" } :
                new[] { "DEFEND_IRONCLAD", "ANGER", "ANGER", "ANGER", "FEED" })
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
            if (kind is not ("MonteCarlo" or "TurnFrontier" or "goal-stop"))
            {
                if (kind.StartsWith("feed-", StringComparison.Ordinal)) goals = goals with { PlayModelId = null, PlayName = null };
                if (kind == "feed-two-copies")
                    foreach (var extra in player.Creature.CombatState!.Enemies.Skip(1)) extra.SetCurrentHpInternal(0);
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
                if (kind is "repeat-and-feed" or "other-finisher")
                {
                    player.PlayerCombatState!.Hand.Cards.First(c => c.Id.Entry == "ANGER").InvokeExecutionFinished();
                    var idle = (LocalCardGoalOutcome)accountingType.GetMethod("Snapshot")!.Invoke(ledger, null)!;
                    if (idle.Plays != 0) throw new InvalidOperationException("Non-play execution notification counted as playing a card");
                    var duplicator = player.GetPotionAtSlotIndex(0)!;
                    duplicator.EnqueueManualUse(duplicator.IsValidTarget(null) ? null : player.Creature);
                    await Frame(); await RunManager.Instance.ActionQueueSet.BecameEmpty();
                    while (!LocalCapture.Stable()) await Frame();
                    accountingType.GetMethod("CompleteStep")!.Invoke(ledger, null);
                    await Play("ANGER");
                    var interim = (LocalCardGoalOutcome)accountingType.GetMethod("Snapshot")!.Invoke(ledger, null)!;
                    if (interim.Plays != 2 || interim.Kills != 0) throw new InvalidOperationException("Native repeated play counting failed");
                }
                var enemy = player.Creature.CombatState!.Enemies.First(e => e.IsAlive);
                // Only this synthetic direct accounting case changes an enemy HP.
                // Worker replay cases below capture untouched native battle roots.
                if (kind != "feed-not-lethal") enemy.SetCurrentHpInternal(kind == "feed-capped" ? 5 : 1);
                if (kind == "feed-capped")
                    await PowerCmd.Apply<IntangiblePower>(new ThrowingPlayerChoiceContext(), enemy, 1, enemy, null);
                await Play(kind == "other-finisher" ? "ANGER" : "FEED");
                var final = (LocalCardGoalOutcome)accountingType.GetMethod("Snapshot")!.Invoke(ledger, null)!;
                bool killed = kind is "repeat-and-feed" or "feed-two-copies";
                if (final.Kills != (killed ? 1 : 0) || final.Steps.Sum(s => s.Plays) != final.Plays ||
                    final.Steps.Sum(s => s.Kills) != final.Kills)
                    throw new InvalidOperationException(kind + ": native finishing-blow source counting failed: " +
                        System.Text.Json.JsonSerializer.Serialize(new { final, enemyHp = enemy.CurrentHp, enemy.IsDead,
                            enemy.IsAlive, ending = CombatManager.Instance.IsOverOrEnding, playerHp = player.Creature.CurrentHp }));
                if (kind == "feed-capped" && enemy.CurrentHp != 4)
                    throw new InvalidOperationException("Native damage cap was not applied");
                var stock = final.ConsumableGoals?.Finisher;
                if (stock == null || stock.Copies != (kind == "feed-two-copies" ? 2 : 1) ||
                    stock.CompletedCopies != (killed ? 1 : 0) ||
                    kind != "other-finisher" && stock.ExhaustedCopies != 1 && !stock.BattleEnded ||
                    stock.Complete != killed)
                    throw new InvalidOperationException("Native consumable copy completion/exhaustion accounting failed: " + System.Text.Json.JsonSerializer.Serialize(final));
                records.Add(new { kind, final.Plays, final.Kills, consumable = stock, enemyHp = enemy.CurrentHp });
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
                SearchOrder = kind == "goal-stop" ? LocalSearchOrder.MonteCarlo : Enum.Parse<LocalSearchOrder>(kind), CardGoals = goals,
                DebugEncounter = "SLUMBERING_BEETLE_NORMAL" };
            if (kind == "goal-stop")
            {
                // Produce one exact native winning plan, then prove the new stop
                // and independent recount on its frozen root. This is seeded
                // acceptance, not an unseeded optimizer quality/speed benchmark.
                var plan = new List<LocalAction>();
                request = request with { CardGoals = new(null, "CARD.FEED", 1000), MaxRounds = 12, BudgetSeconds = 20,
                    ShareSearchWork = false };
                LocalWire.Write(Path.Combine(root, "integration-card-goal-root-private.json"), request);
                var previewType = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalTacticalPreview", true)!;
                var damagePreview = previewType.GetMethod("PreviewDamage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var reservedFeed = player.PlayerCombatState!.AllPiles.SelectMany(p => p.Cards).First(c => c.Id.Entry == "FEED");
                var accountingType = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalCardGoalAccounting", true)!;
                using var manualLedger = (IDisposable)Activator.CreateInstance(accountingType, player, request.CardGoals)!;
                int moves = 0;
                while (!CombatManager.Instance.IsOverOrEnding && !player.Creature.IsDead)
                {
                    if (++moves > 150 || CombatManager.Instance.DebugOnlyGetState()!.RoundNumber > 12)
                        throw new InvalidOperationException("Synthetic native Feed plan did not finish");
                    var alive = player.Creature.CombatState!.Enemies.Where(e => e.IsAlive).ToArray();
                    var enemy = alive[0];
                    var legal = player.PlayerCombatState!.Hand.Cards.Where(c => c.CanPlay()).ToArray();
                    var preview = previewType.GetMethod("Capture")!.Invoke(null, [player, legal, true])!;
                    double feedDamage = (double)damagePreview.Invoke(preview, [reservedFeed, enemy])!;
                    bool fed = plan.Any(a => a.ModelId == "CARD.FEED");
                    var card = legal.FirstOrDefault(c => c.Id.Entry == "FEED" && enemy.CurrentHp + enemy.Block <= feedDamage) ??
                        legal.FirstOrDefault(c => c.Id.Entry == "DEFEND_IRONCLAD") ??
                        legal.FirstOrDefault(c => c.Id.Entry == "ANGER" && (fed || enemy.CurrentHp + enemy.Block > feedDamage));
                    if (card != null)
                    {
                        plan.Add(new(player.PlayerCombatState.Hand.Cards.ToList().IndexOf(card), card.Id.ToString(),
                            card.IsValidTarget(null) ? null : enemy.CombatId, card.Title, "enemy", LocalCapture.Fingerprint(),
                            CombatManager.Instance.DebugOnlyGetState()!.RoundNumber,
                            CombatCardIndex: NetCombatCard.FromModel(card).CombatCardIndex));
                        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, card.IsValidTarget(null) ? null : enemy));
                        await Frame(); await RunManager.Instance.ActionQueueSet.BecameEmpty().WaitAsync(TimeSpan.FromSeconds(15));
                    }
                    else
                    {
                        int round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                        plan.Add(new(-1, "", null, "", "", LocalCapture.Fingerprint(), round, EndTurn: true));
                        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new EndPlayerTurnAction(player, player.PlayerCombatState.TurnNumber));
                        do { await Frame(); } while (!CombatManager.Instance.IsOverOrEnding && !player.Creature.IsDead &&
                            CombatManager.Instance.DebugOnlyGetState()!.RoundNumber == round);
                    }
                    while (!CombatManager.Instance.IsOverOrEnding && !player.Creature.IsDead && !LocalCapture.Stable()) await Frame();
                    await Frame(); await Frame();
                    accountingType.GetMethod("CompleteStep")!.Invoke(manualLedger, null);
                }
                var manualCounts = (LocalCardGoalOutcome)accountingType.GetMethod("Snapshot")!.Invoke(manualLedger, null)!;
                if (player.Creature.IsDead || manualCounts.Kills != 1)
                    throw new InvalidOperationException("Synthetic seeded plan did not win after using Feed: hp=" + player.Creature.CurrentHp);
                int hostHp = player.Creature.CurrentHp, hostMaxHp = player.Creature.MaxHp, hostGold = player.Gold;
                LocalWire.Write(Path.Combine(root, "integration-card-goal-plan-private.json"), plan);
                foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
                {
                    var stopped = await Task.Run(() => pool.Analyze(request with { Id = Guid.NewGuid().ToString("N"),
                        SearchOrder = order, InitialPlan = plan.ToArray() }, LocalCapture.Installation(), _ => { }, CancellationToken.None));
                    LocalWire.Write(Path.Combine(root, "integration-card-goal-stop-" + order + "-private.json"), stopped);
                    if (stopped.Status != "done" || !stopped.StoppedEarly || !stopped.StoppedOnCardGoals ||
                        stopped.Evaluated != 1 || stopped.Best is not { Won: true, CardGoalOutcome.Kills: 1 } ||
                        stopped.Timing?.Verifications != 1 || !LocalSearchPolicy.HasExecutionPoints(stopped) ||
                        player.Creature.CurrentHp != hostHp || player.Creature.MaxHp != hostMaxHp || player.Gold != hostGold)
                        throw new InvalidOperationException(order + ": seeded native goal stop/verification failed: " +
                            System.Text.Json.JsonSerializer.Serialize(new { stopped.Message, stopped.Evaluated, stopped.StoppedEarly,
                                stopped.StoppedOnCardGoals, stopped.Best?.CardGoalOutcome }));
                    records.Add(new { kind = "goal-stop-" + order, stopped.Status, stopped.Evaluated, stopped.ElapsedMs,
                        stopped.StoppedOnCardGoals, stopped.Best.Hp, stopped.Best.NetHpLoss, stopped.Best.CardGoalOutcome,
                        verifications = stopped.Timing.Verifications, seeded = true, hostUnchanged = true });
                }
                continue;
            }
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
