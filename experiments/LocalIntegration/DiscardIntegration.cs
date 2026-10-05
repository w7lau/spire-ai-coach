using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
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

internal static class DiscardIntegration
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
        var attack = ModelDb.AllCards.Where(c => c.Type == CardType.Attack &&
            c.CanonicalKeywords.Contains(CardKeyword.Sly) && c.DynamicVars.TryGetValue("Damage", out var damage) &&
            damage.BaseValue > 0 && damage.BaseValue <= 12 && !c.DynamicVars.ContainsKey("Repeat"))
            .OrderBy(c => c.DynamicVars["Damage"].BaseValue).ThenBy(c => c.Id.Entry).First();
        foreach (var id in new[] { "SURVIVOR", "WOUND", "TACTICIAN", "REFLEX", attack.Id.Entry,
            "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD" })
        {
            var card = run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player);
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
        var worker = typeof(LocalWorker); const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Static;
        const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
        void Set(string name, object? value) => worker.GetField(name, hidden)!.SetValue(null, value);
        var learnerType = worker.Assembly.GetType("SpireAiCoach.Mod.LocalNativeLearning", true)!;
        var learner = Activator.CreateInstance(learnerType, [false, false])!;
        var selections = learnerType.GetProperty("Selections", instance)!.GetValue(learner)!;
        var discards = selections.GetType().GetProperty("Discards", instance)!.GetValue(selections)!;
        int Samples(string name) => (int)discards.GetType().GetProperty(name, instance)!.GetValue(discards)!;
        Set("_tree", tree); Set("_root", root); Set("_nativeLearning", learner);
        Set("_rolloutStyle", LocalRolloutStyle.Balanced); Set("_efficientTactics", true);
        Set("_fastNativeWaits", true); Set("_fastStateSettling", true); Set("_selectionCursor", new LocalSelectionCursor());
        var mode = worker.Assembly.GetType("SpireAiCoach.Mod.LocalWorkerDataMode", true)!;
        mode.GetMethod("Install")!.Invoke(null, [null]); mode.GetProperty("Active")!.SetValue(null, true);
        var observation = worker.Assembly.GetType("SpireAiCoach.Mod.LocalDiscardObservation", true)!;
        observation.GetMethod("Install", hidden)!.Invoke(null, null);
        var play = worker.GetMethod("Play", hidden)!;
        var ranks = new List<object>(); var steps = new List<LocalAction>();
        CardModel Card(string id) => pcs.AllPiles.SelectMany(p => p.Cards).First(c => c.Id.Entry == id);
        async Task Stage(params string[] ids)
        {
            var wanted = ids.Select(Card).ToArray();
            foreach (var card in pcs.Hand.Cards.ToArray()) if (!wanted.Contains(card)) await CardPileCmd.Add(card, pcs.DiscardPile);
            foreach (var card in wanted) if (!pcs.Hand.Cards.Contains(card)) await CardPileCmd.Add(card, pcs.Hand);
            // Resource setup belongs to this synthetic fixture. The real source
            // payment, discard, hooks and autoplay below remain native commands.
            var energy = pcs.GetType().GetProperty(nameof(pcs.Energy))!;
            energy.SetValue(pcs, Convert.ChangeType(1, energy.PropertyType));
            while (!LocalCapture.Stable()) await Frame();
        }
        async Task PlaySource(string expected)
        {
            var source = Card("SURVIVOR");
            var action = new LocalAction(pcs.Hand.Cards.ToList().IndexOf(source), source.Id.ToString(), null,
                source.Title, "", LocalCapture.Fingerprint(), player.Creature.CombatState!.RoundNumber,
                CombatCardIndex: NetCombatCard.FromModel(source).CombatCardIndex);
            Func<LocalCardChoice[], LocalCardChoice> choose = options => {
                if (pcs.Energy != 0) throw new InvalidOperationException("Source must pay before discard ranking");
                ranks.Add(new { remainingEnergy = pcs.Energy, offered = options.Select(o => new { o.ModelId, o.Preference }).ToArray() });
                var best = options.OrderByDescending(o => o.Preference).ThenBy(o => o.Index).First();
                if (best.ModelId != "CARD." + expected) throw new InvalidOperationException("Discard ranked an unhelpful card before " + expected);
                return best;
            };
            steps.Add(await (Task<LocalAction>)play.Invoke(null, [action, choose, null])!);
        }
        try
        {
            await Stage("SURVIVOR", "WOUND", attack.Id.Entry);
            int hpBefore = player.Creature.CombatState!.Enemies.Sum(e => e.CurrentHp);
            await PlaySource(attack.Id.Entry);
            if (pcs.Energy != 0 || player.Creature.CombatState.Enemies.Sum(e => e.CurrentHp) >= hpBefore || Samples("AutoSamples") < 1)
                throw new InvalidOperationException("Zero-energy Sly attack did not execute natively");

            await Stage("SURVIVOR", "WOUND", "TACTICIAN");
            await PlaySource("TACTICIAN");
            if (pcs.Energy < 1 || Samples("AutoSamples") < 2)
                throw new InvalidOperationException("Printed costly Sly energy card did not return real resources");
            int returnedEnergy = pcs.Energy;

            await Stage("SURVIVOR", "WOUND", "REFLEX");
            int handBefore = pcs.Hand.Cards.Count;
            await PlaySource("REFLEX");
            int handGain = pcs.Hand.Cards.Count - handBefore + 2;
            if (handGain <= 0 || Samples("AutoSamples") < 3) throw new InvalidOperationException("Sly draw not observed");

            await RelicCmd.Obtain(ModelDb.AllRelics.Single(r => r.Id.Entry == "TOUGH_BANDAGES").ToMutable(), player);
            await Stage("SURVIVOR", "WOUND");
            int blockBefore = player.Creature.Block, hookBefore = Samples("HookSamples");
            await PlaySource("WOUND");
            if (Samples("HookSamples") <= hookBefore || player.Creature.Block - blockBefore < 11)
                throw new InvalidOperationException("Non-Sly native discard hook was not retained");

            await Stage("SURVIVOR", "TACTICIAN", "REFLEX", "WOUND");
            var source = Card("SURVIVOR");
            var prefs = new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 2);
            var rank = selections.GetType().GetMethod("Rank", instance)!;
            var priority = typeof(LocalChoices).GetProperty("SelectionPriority", instance)!;
            var session = new LocalChoices(choose: options => options.OrderByDescending(o => o.Preference).ThenBy(o => o.Index).First());
            Func<CardModel, int, int> identity = (_, value) => value;
            Func<string, CardModel[], CardSelectorPrefs?, int, bool[], Func<int[], int>?> hint = (kind, cards, p, ordinal, gold) =>
                (Func<int[], int>?)rank.Invoke(selections, [source, player, kind, cards, p, ordinal, LocalRolloutStyle.Balanced, true, identity, gold]);
            priority.SetValue(session, hint); Set("_choices", session);
            var picked = (await CardSelectCmd.FromHandForDiscard(new Context(player), player, prefs, c => c != source, source)).ToArray();
            session.Finish();
            if (picked.Length != 2 || !picked.Any(c => c.Id.Entry == "TACTICIAN") || !picked.Any(c => c.Id.Entry == "REFLEX"))
                throw new InvalidOperationException("Batch should choose the resource / draw combination");
            var offer = typeof(LocalChoices).GetMethod("SelectWithoutPresentation", instance)!;
            var choices = session.Completed;
            // Reconstruct the recorded ordered selection without changing state.
            var exact = new LocalChoices(choices);
            _ = offer.Invoke(exact, ["hand", pcs.Hand.Cards.Where(c => c != source).ToArray(), prefs, false]);
            exact.Finish();
            if (exact.Completed.Single().OfferHash != choices.Single().OfferHash)
                throw new InvalidOperationException("Preference changed native selection identity");
            int goldQueries = 0;
            var countedPrefs = new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 1) {
                ShouldGlowGold = c => { goldQueries++; return c.IsSlyThisTurn; }
            };
            var counted = new LocalChoices(choose: options => options.OrderByDescending(o => o.Preference).First());
            priority.SetValue(counted, hint);
            var countedCards = pcs.Hand.Cards.Where(c => c != source).ToArray();
            string beforeHint = LocalCapture.Fingerprint();
            _ = offer.Invoke(counted, ["hand", countedCards, countedPrefs, false]);
            counted.Finish();
            if (goldQueries != countedCards.Length || LocalCapture.Fingerprint() != beforeHint)
                throw new InvalidOperationException("Discard ranking repeated permission previews or changed native state");
            int autoBefore = Samples("AutoSamples");
            using ((IDisposable)observation.GetMethod("Use", hidden)!.Invoke(null, [discards, player])!)
                await CardCmd.Discard(new Context(player), picked);
            if (Samples("AutoSamples") != autoBefore + 2) throw new InvalidOperationException("Bulk Sly order / effects not retained");

            LocalWire.Write(Path.Combine(root, "integration-discard-summary.json"), new {
                modVersion = worker.Assembly.GetName().Version!.ToString(), nativeModule = typeof(CardModel).Assembly.ManifestModule.ModuleVersionId,
                passed = true, zeroEnergySlyAttack = true, slyEnergyGain = returnedEnergy, slyHandGain = handGain,
                nativeDiscardRelic = true, batchSelection = true, exactOrderedReplay = true,
                nativeGoldQueries = goldQueries, rankedCards = countedCards.Length, rankingReadOnly = true,
                hookSamples = Samples("HookSamples"), autoSamples = Samples("AutoSamples"), nativeSteps = steps.Count,
                selections = ranks, syntheticOnly = true, fullSearchBenchmark = false });
        }
        finally { mode.GetProperty("Active")!.SetValue(null, false); Set("_choices", null); Set("_nativeLearning", null); }
    }
}
