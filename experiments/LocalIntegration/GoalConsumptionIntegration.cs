using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
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

// Native synthetic actions in the owned test host, never the player's game.
internal static class GoalConsumptionIntegration
{
    public static async Task Run(string root, SceneTree tree, SerializableRun fixture)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control()); await Frame(); await Frame();
        var run = RunState.FromSerializable(fixture);
        await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
        var player = run.Players.Single();
        var old = player.Deck.Cards.ToArray(); player.Deck.Clear(silent: true);
        foreach (var card in old) run.RemoveCard(card);
        foreach (var id in new[] { "FEED", "FEED", "TRUE_GRIT", "STOKE", "DEFEND_IRONCLAD" })
        {
            var card = run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player);
            card.UpgradeInternal(); await CardPileCmd.Add(card, player.Deck, skipVisuals: true);
        }
        foreach (var potion in player.Potions.ToArray()) potion.Discard();
        await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
        await PreloadManager.LoadActAssets(run.Acts[0]);
        RunManager.Instance.Launch(); NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(run));
        await RunManager.Instance.GenerateMap();
        await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster,
            ModelDb.AllEncounters.Single(e => e.Id.Entry == "TWO_TAILED_RATS_NORMAL").ToMutable(), false);
        while (!LocalCapture.Stable()) await Frame();
        var pcs = player.PlayerCombatState!;
        foreach (var card in pcs.DrawPile.Cards.ToArray()) await CardPileCmd.Add(card, pcs.Hand);
        var feed = pcs.Hand.Cards.First(c => c.Id.Entry == "FEED");
        var grit = pcs.Hand.Cards.Single(c => c.Id.Entry == "TRUE_GRIT");
        var stoke = pcs.Hand.Cards.Single(c => c.Id.Entry == "STOKE");
        var goals = new LocalCardGoals("CARD.FEED", "CARD.FEED", 100);
        var ledgerType = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalCardGoalAccounting", true)!;
        using var ledger = (IDisposable)Activator.CreateInstance(ledgerType, player, goals)!;
        LocalGoalOpportunity Stock() => (LocalGoalOpportunity)ledgerType.GetMethod("Opportunity")!.Invoke(ledger, null)!;
        int Penalty(CardModel card) => (int)ledgerType.GetMethod("ExhaustSelectionPenalty", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [card])!;
        var records = new List<object>();
        var initial = Stock();
        if (initial.Finisher is not { Available: 2, Completed: 0, Target: 2 } || Penalty(feed) <= 0 || Penalty(grit) != 0)
            throw new InvalidOperationException("Native root copies or exhaustion-selection reservation changed");
        // This test Mod moves cards through native piles: discard/draw remain usable,
        // while exhaust/removal are observed irrespective of which caller caused them.
        await CardPileCmd.Add(feed, pcs.DiscardPile);
        var discarded = Stock();
        await CardPileCmd.Add(feed, pcs.DrawPile);
        var drawn = Stock();
        if (LocalGoalOpportunity.LossPenalty(initial, discarded, goals) != 0 || drawn != initial)
            throw new InvalidOperationException("Temporary native pile movement lost a goal copy");
        await CardPileCmd.Add(feed, PileType.Exhaust.GetPile(player));
        var externallyExhausted = Stock();
        if (LocalGoalOpportunity.LossPenalty(drawn, externallyExhausted, goals) <= 0)
            throw new InvalidOperationException("Test Mod's native exhaustion was missed");
        await CardPileCmd.Add(feed, pcs.Hand);
        if (Stock() != initial) throw new InvalidOperationException("Recovered native copy stayed unavailable");
        records.Add(new { kind = "native-pile-movement-and-recovery", initial, discarded, drawn, externallyExhausted, recovered = Stock() });
        async Task Play(CardModel card, Creature? target = null)
        {
            if (!card.CanPlay() || target != null && !card.IsValidTarget(target))
                throw new InvalidOperationException($"Native synthetic action was not legal: {card.Id}; energy={pcs.Energy}; target={target?.IsAlive}");
            var choices = new LocalChoices(choose: options => {
                var selected = options.First(c => c.ModelId == "CARD.FEED");
                if (!options.Any(c => c.ModelId != "CARD.FEED" && c.Preference > selected.Preference))
                    throw new InvalidOperationException("Explicit native exhaust selection ignored the reserved goal copy");
                return selected; // Deliberately exercise the still-legal sacrifice.
            });
            RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, target));
            await Frame(); var pending = RunManager.Instance.ActionQueueSet.BecameEmpty();
            while (!pending.IsCompleted) { choices.Tick(); await Frame(); }
            await pending; choices.Finish();
            while (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsOverOrEnding && !LocalCapture.Stable()) await Frame();
            ledgerType.GetMethod("CompleteStep")!.Invoke(ledger, null);
        }
        var enemy = player.Creature.CombatState!.Enemies.First(e => e.IsAlive);
        enemy.SetCurrentHpInternal(40);
        var beforeSelf = Stock(); await Play(feed, enemy); var afterSelf = Stock();
        if (enemy.IsDead || LocalGoalOpportunity.LossPenalty(beforeSelf, afterSelf, goals) <= 0 || afterSelf.Finisher!.Completed != 0)
            throw new InvalidOperationException("Native non-finishing self-exhaustion was missed or credited as success");
        var playGoals = new LocalCardGoals(PlayModelId: "CARD.FEED");
        var beforeUse = new LocalGoalOpportunity("CARD.FEED", null, beforeSelf.Play, null);
        var afterUse = new LocalGoalOpportunity("CARD.FEED", null, afterSelf.Play, null);
        if (beforeUse.Play is not { Target: 2, Available: 2, Completed: 0 } ||
            afterUse.Play is not { Available: 1, Completed: 1, Missing: 0 } ||
            LocalGoalOpportunity.LossPenalty(beforeUse, afterUse, playGoals) != 0)
            throw new InvalidOperationException("Successful native selected play was penalized for its exhaustion: " +
                System.Text.Json.JsonSerializer.Serialize(new { beforeUse, afterUse, energy = pcs.Energy }));
        records.Add(new { kind = "native-successful-play-consumption", beforeUse, afterUse });
        await CardPileCmd.Add(feed, pcs.Hand);
        if (Stock().Finisher != initial.Finisher) throw new InvalidOperationException("Recovery after self-exhaustion was ignored");
        records.Add(new { kind = "native-non-finishing-self-exhaustion", beforeSelf, afterSelf, recovered = Stock() });
        var beforeChoice = Stock(); await Play(grit); var afterChoice = Stock();
        if (LocalGoalOpportunity.LossPenalty(beforeChoice, afterChoice, goals) <= 0 || afterChoice.Finisher!.Completed != 0)
            throw new InvalidOperationException("Native selected exhaustion supplied no loss or false success");
        var consumed = PileType.Exhaust.GetPile(player).Cards.First(c => c.Id.Entry == "FEED");
        await CardPileCmd.Add(consumed, pcs.Hand);
        if (Stock().Finisher != initial.Finisher) throw new InvalidOperationException("Recovery after selected exhaustion was ignored");
        records.Add(new { kind = "native-selected-exhaustion", beforeChoice, afterChoice, recovered = Stock() });
        var beforeMass = Stock(); await Play(stoke); var afterMass = Stock();
        if (LocalGoalOpportunity.LossPenalty(beforeMass, afterMass, goals) <= 0 || afterMass.Finisher!.Completed != 0)
            throw new InvalidOperationException("Native mass exhaustion was missed");
        records.Add(new { kind = "native-mass-exhaustion", beforeMass, afterMass });
        LocalWire.Write(Path.Combine(root, "integration-goal-consumption-summary.json"), new {
            version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3),
            scope = "Owned synthetic native self/selected/mass exhaustion, successful play and native pile movement/recovery; original finishing tests separately cover successful finishing consumption. No arbitrary private Mod guarantee.", records });
    }
}
