using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
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

// Tiny synthetic native fight. No full search, answer seed, real save or API.
internal static class FollowupIntegration
{
    private sealed class Context(Player player) : PlayerChoiceContext
    {
        public override ulong? OwnerId => player.NetId;
        public override Task SignalPlayerChoiceBegun(Player chooser, PlayerChoiceOptions options) => Task.CompletedTask;
        public override Task SignalPlayerChoiceEnded() => Task.CompletedTask;
    }
    public static async Task Run(string root, SceneTree tree, SerializableRun fixture)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var run = RunState.FromSerializable(fixture);
        await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
        var player = run.Players.Single();
        var old = player.Deck.Cards.ToArray(); player.Deck.Clear(silent: true);
        foreach (var card in old) run.RemoveCard(card);
        foreach (var id in new[] { "HOLOGRAM", "CLAW", "BLUDGEON", "WOUND", "HEADBUTT", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD" })
        {
            var card = run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player);
            if (id == "HOLOGRAM") card.UpgradeInternal();
            await CardPileCmd.Add(card, player.Deck, skipVisuals: true);
        }
        await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
        await PreloadManager.LoadActAssets(run.Acts[0]);
        RunManager.Instance.Launch(); NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
        await RunManager.Instance.GenerateMap();
        await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster,
            ModelDb.AllEncounters.Single(e => e.Id.Entry == "SLUMBERING_BEETLE_NORMAL").ToMutable(), false);
        while (!LocalCapture.Stable()) await Frame();
        var pcs = player.PlayerCombatState!;
        foreach (var card in pcs.DrawPile.Cards.ToArray()) await CardPileCmd.Add(card, pcs.Hand);
        foreach (var card in pcs.Hand.Cards.Where(c => c.Id.Entry is "CLAW" or "BLUDGEON" or "WOUND").ToArray())
            await CardPileCmd.Add(card, pcs.DiscardPile);
        while (!LocalCapture.Stable()) await Frame();

        var worker = typeof(LocalWorker);
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Static;
        void Set(string name, object? value) => worker.GetField(name, fields)!.SetValue(null, value);
        var learningType = worker.Assembly.GetType("SpireAiCoach.Mod.LocalNativeLearning", true)!;
        var learning = Activator.CreateInstance(learningType, [false, false])!;
        Set("_tree", tree); Set("_root", root); Set("_nativeLearning", learning);
        Set("_rolloutStyle", LocalRolloutStyle.Balanced); Set("_efficientTactics", true);
        Set("_fastNativeWaits", true); Set("_fastStateSettling", true); Set("_selectionCursor", new LocalSelectionCursor());
        var mode = worker.Assembly.GetType("SpireAiCoach.Mod.LocalWorkerDataMode", true)!;
        mode.GetMethod("Install")!.Invoke(null, [null]);
        mode.GetProperty("Active")!.SetValue(null, true);
        var play = worker.GetMethod("Play", fields)!;
        var records = new List<object>();
        var steps = new List<LocalAction>();
        async Task<LocalAction> Play(CardModel card)
        {
            var state = CombatManager.Instance.DebugOnlyGetState()!;
            var target = card.IsValidTarget(null) ? null : state.Enemies.First(c => c.IsAlive && card.IsValidTarget(c));
            var action = new LocalAction(pcs.Hand.Cards.ToList().IndexOf(card), card.Id.ToString(), target?.CombatId,
                card.Title, target?.Name ?? "", LocalCapture.Fingerprint(), state.RoundNumber,
                CombatCardIndex: NetCombatCard.FromModel(card).CombatCardIndex);
            Func<LocalCardChoice[], LocalCardChoice> choose = options =>
            {
                records.Add(new { offered = options.Select(o => new { o.ModelId, o.Preference, o.Index }).ToArray(),
                    remainingEnergy = pcs.Energy, remainingStars = pcs.Stars });
                return options.OrderByDescending(c => c.Preference).ThenBy(c => c.Index).First();
            };
            var result = await (Task<LocalAction>)play.Invoke(null, [action, choose, null])!;
            steps.Add(result);
            return result;
        }
        try
        {
            foreach (var defense in pcs.Hand.Cards.Where(c => c.Id.Entry == "DEFEND_IRONCLAD").ToArray()) await Play(defense);
            var hologram = pcs.Hand.Cards.Single(c => c.Id.Entry == "HOLOGRAM");
            decimal before = pcs.Energy;
            var returned = await Play(hologram);
            if (returned.Choices is not [{ ModelId: "CARD.CLAW" }] || pcs.Energy != 0 || before != 1)
                throw new InvalidOperationException("Retrieval must select the free attack after paying the source's last energy");
            var claw = pcs.Hand.Cards.Single(c => c.Id.Entry == "CLAW");
            int enemyBefore = player.Creature.CombatState!.Enemies.Sum(e => e.CurrentHp);
            await Play(claw);

            // The same generic ranking serves generated offers and bundles.
            // These native commands are not per-card effect substitutes.
            var selectionLearner = learningType.GetProperty("Selections", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(learning)!;
            var rank = selectionLearner.GetType().GetMethod("Rank", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var priorityProperty = typeof(LocalChoices).GetProperty("SelectionPriority", BindingFlags.NonPublic | BindingFlags.Instance)!;
            LocalChoices Choices()
            {
                var session = new LocalChoices(choose: options => options.OrderByDescending(c => c.Preference).ThenBy(c => c.Index).First());
                Func<CardModel, int, int> identity = (_, value) => value;
                Func<string, CardModel[], CardSelectorPrefs?, int, bool[], Func<int[], int>?> priority = (kind, cards, prefs, ordinal, gold) =>
                    (Func<int[], int>?)rank.Invoke(selectionLearner,
                        [hologram, player, kind, cards, prefs, ordinal, LocalRolloutStyle.Balanced, true, identity, gold]);
                priorityProperty.SetValue(session, priority); Set("_choices", session);
                return session;
            }
            CardModel Created(string id) => run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player);
            var createdFree = Created("CLAW"); var createdCostly = Created("BLUDGEON");
            var generatedSession = Choices();
            var generated = await CardSelectCmd.FromChooseACardScreen(new Context(player), [createdCostly, createdFree], player);
            if (!ReferenceEquals(generated, createdFree)) throw new InvalidOperationException("Generated offer must use the remaining energy budget");
            generatedSession.Finish();
            var bundleSession = Choices();
            var bundles = new IReadOnlyList<CardModel>[] { [createdCostly, Created("BLUDGEON")], [createdFree, Created("DEFEND_IRONCLAD")] };
            var bundle = await CardSelectCmd.FromChooseABundleScreen(player, bundles);
            if (bundle.Count() != bundles[1].Count || !bundle.Zip(bundles[1]).All(pair => ReferenceEquals(pair.First, pair.Second)))
                throw new InvalidOperationException("Generated bundle must share one resource budget");
            bundleSession.Finish(); Set("_choices", null);
            if (!pcs.DiscardPile.Cards.Contains(claw) || player.Creature.CombatState.Enemies.Sum(e => e.CurrentHp) >= enemyBefore)
                throw new InvalidOperationException("The returned instance must be legally playable and deal native damage");

            // A second native round allows another source to select from the
            // same pile. Its true destination is draw, not hand.
            int round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
            RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new EndPlayerTurnAction(player, pcs.TurnNumber));
            do { await Frame(); } while (CombatManager.Instance.DebugOnlyGetState()!.RoundNumber <= round || !LocalCapture.Stable());
            foreach (var card in pcs.AllPiles.SelectMany(p => p.Cards).Where(c => c.Id.Entry is "HEADBUTT" or "HOLOGRAM").ToArray())
                if (!pcs.Hand.Cards.Contains(card)) await CardPileCmd.Add(card, pcs.Hand);
            foreach (var card in pcs.AllPiles.SelectMany(p => p.Cards).Where(c => c.Id.Entry is "CLAW" or "BLUDGEON" or "WOUND").ToArray())
                if (!pcs.DiscardPile.Cards.Contains(card)) await CardPileCmd.Add(card, pcs.DiscardPile);
            while (!LocalCapture.Stable()) await Frame();
            var headbutt = pcs.Hand.Cards.Single(c => c.Id.Entry == "HEADBUTT");
            var topdeck = await Play(headbutt);
            if (topdeck.Choices is not [{ ModelId: "CARD.CLAW" }] || claw.Pile?.Type != PileType.Draw)
                throw new InvalidOperationException("Native topdeck selector must retain its draw-pile destination");
            await CardPileCmd.Add(claw, pcs.DiscardPile);
            var repeated = await Play(hologram);
            if (repeated.Choices is not [{ ModelId: "CARD.CLAW" }] || !pcs.Hand.Cards.Contains(claw))
                throw new InvalidOperationException("Learned return-to-hand and topdeck semantics must stay separate");
            await Play(claw);

            // Priority changes cannot alter offer identity or the chosen rank.
            // Replay the exact intent in the same frozen native offer below.
            var choices = new LocalChoices(choose: options => options.OrderByDescending(c => c.Preference).First());
            var offerMethod = typeof(LocalChoices).GetMethod("SelectWithoutPresentation", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var prefs = new MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs(
                MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs.ExhaustSelectionPrompt, 1);
            var offered = pcs.DiscardPile.Cards.ToArray();
            _ = offerMethod.Invoke(choices, ["pile", offered, prefs, false]);
            var exact = new LocalChoices(choices.Completed);
            _ = offerMethod.Invoke(exact, ["pile", offered, prefs, false]); exact.Finish();
            if (choices.Completed.Single().OfferHash != exact.Completed.Single().OfferHash)
                throw new InvalidOperationException("Selection identity changed");

            LocalWire.Write(Path.Combine(root, "integration-followup-summary.json"), new {
                modVersion = worker.Assembly.GetName().Version!.ToString(),
                nativeModule = typeof(CardModel).Assembly.ManifestModule.ModuleVersionId,
                passed = true, freeReturnAfterLastEnergy = true, returnedInstancePlayedTwice = true,
                topdeckAndHandSeparate = true, exactOfferReplay = true,
                generatedOfferBudget = true, generatedBundleBudget = true, nativeSelectionCommands = 2,
                nativeSteps = steps.Count, selections = records, steps = steps.Select(s => new { s.ModelId, s.Round,
                    choices = s.Choices?.Select(c => new { c.ModelId, c.Preference }).ToArray() }).ToArray(),
                syntheticOnly = true, fullSearchBenchmark = false });
        }
        finally { mode.GetProperty("Active")!.SetValue(null, false); Set("_choices", null); Set("_nativeLearning", null); }
    }
}
