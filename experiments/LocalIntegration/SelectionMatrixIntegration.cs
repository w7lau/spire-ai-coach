using System.Diagnostics;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
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
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Owned synthetic host only. Exercise shared native commands and real selection
// UI, then compare scene-free choices including native synchronization bytes.
internal static class SelectionMatrixIntegration
{
    private static readonly List<string> Synchronized = [];
    private sealed class Context(Player player) : PlayerChoiceContext
    {
        public int Begun, Ended;
        public override ulong? OwnerId => player.NetId;
        public override Task SignalPlayerChoiceBegun(Player chooser, PlayerChoiceOptions options)
        { Begun++; return Task.CompletedTask; }
        public override Task SignalPlayerChoiceEnded() { Ended++; return Task.CompletedTask; }
    }
    private static void Observe(PlayerChoiceResult result)
    {
        var writer = new PacketWriter(); result.ToNetData().Serialize(writer);
        Synchronized.Add(Convert.ToHexString(writer.Buffer.AsSpan(0, (writer.BitPosition + 7) / 8)));
    }
    public static async Task Run(string root, SceneTree tree, SerializableRun fixture)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var run = RunState.FromSerializable(fixture);
        await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
        var player = run.Players.Single();
        var previous = player.Deck.Cards.ToArray(); player.Deck.Clear(silent: true);
        foreach (var card in previous) run.RemoveCard(card);
        var ids = new[] { "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH", "ARMAMENTS", "TRUE_GRIT", "HEADBUTT", "BODY_SLAM", "BLOODLETTING", "WOUND", "DAZED" };
        foreach (var id in ids)
            await CardPileCmd.Add(run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player), player.Deck, skipVisuals: true);
        await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
        await PreloadManager.LoadActAssets(run.Acts[0]);
        RunManager.Instance.Launch(); NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
        await RunManager.Instance.GenerateMap();
        await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster,
            ModelDb.AllEncounters.Single(e => e.Id.Entry == "SLUMBERING_BEETLE_NORMAL").ToMutable(), false);
        while (!LocalCapture.Stable()) await Frame();
        foreach (var card in player.PlayerCombatState!.DrawPile.Cards.ToArray())
            await CardPileCmd.Add(card, player.PlayerCombatState.Hand);
        foreach (var card in player.PlayerCombatState.Hand.Cards.Take(2).ToArray())
            await CardPileCmd.Add(card, player.PlayerCombatState.DiscardPile);
        while (!LocalCapture.Stable()) await Frame();
        var hand = player.PlayerCombatState.Hand.Cards.ToArray();
        var deck = player.Deck.Cards.ToArray();
        var prefs = new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 2) { RequireManualConfirmation = true };
        var one = new CardSelectorPrefs(CardSelectorPrefs.ExhaustSelectionPrompt, 1) { RequireManualConfirmation = true };
        var optional = new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 0, 2) { RequireManualConfirmation = true };
        var gridAuto = new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 0, 2);
        var sorted = new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 2) {
            RequireManualConfirmation = true, Comparison = (a, b) => StringComparer.Ordinal.Compare(b.Id.Entry, a.Id.Entry) };
        var autoOne = new CardSelectorPrefs(CardSelectorPrefs.ExhaustSelectionPrompt, 1);
        var enchantment = ModelDb.Enchantment<Sharp>();
        var api = typeof(LocalWorker).Assembly;
        var mode = api.GetType("SpireAiCoach.Mod.LocalWorkerDataMode", true)!;
        mode.GetMethod("Install")!.Invoke(null, [null]);
        if (!(bool)mode.GetProperty("Available")!.GetValue(null)!) throw new InvalidOperationException("Native choice boundary installation failed");
        var current = typeof(LocalWorker).GetField("_choices", BindingFlags.NonPublic | BindingFlags.Static)!;
        var active = mode.GetProperty("Active")!;
        var harmony = new Harmony("SpireLocalIntegration.native-choice-proof");
        harmony.Patch(typeof(PlayerChoiceSynchronizer).GetMethod("SyncLocalChoice")!,
            prefix: new(typeof(SelectionMatrixIntegration).GetMethod(nameof(Observe), BindingFlags.NonPublic | BindingFlags.Static)!));
        var cases = new (string Name, Func<Context, Task<CardModel[]>> Command, bool Empty)[]
        {
            // There is no PlayCardAction owning this command-only probe. A null
            // presentation source restores holders immediately instead of waiting
            // for an ExecutionFinished event from a card that was never played.
            ("hand-multiple-ordered", async c => (await CardSelectCmd.FromHand(c, player, prefs, null, null!)).ToArray(), false),
            ("hand-discard", async c => (await CardSelectCmd.FromHandForDiscard(c, player, one, null, null!)).ToArray(), false),
            ("hand-upgrade", async c => [ (await CardSelectCmd.FromHandForUpgrade(c, player, hand[0]))! ], false),
            ("grid-manual-multiple", async c => (await CardSelectCmd.FromSimpleGrid(c, hand, player, prefs)).ToArray(), false),
            ("grid-sorted", async c => (await CardSelectCmd.FromSimpleGrid(c, hand, player, sorted)).ToArray(), false),
            ("grid-created", async c => (await CardSelectCmd.FromSimpleGridForRewards(c,
                hand.Select(card => new CardCreationResult(card)).ToList(), player, sorted)).ToArray(), false),
            ("grid-optional-empty", async c => (await CardSelectCmd.FromSimpleGrid(c, hand, player, optional)).ToArray(), true),
            ("grid-auto-max", async c => (await CardSelectCmd.FromSimpleGrid(c, hand, player, gridAuto)).ToArray(), false),
            ("offer", async c => [(await CardSelectCmd.FromChooseACardScreen(c, hand.Take(3).ToArray(), player))!], false),
            ("offer-skip", async c => (await CardSelectCmd.FromChooseACardScreen(c, hand.Take(3).ToArray(), player, true)) is { } card ? [card] : [], true),
            ("deck-generic", async c => (await CardSelectCmd.FromDeckGeneric(player, prefs)).ToArray(), false),
            ("deck-upgrade", async c => (await CardSelectCmd.FromDeckForUpgrade(player, prefs)).ToArray(), false),
            ("deck-removal", async c => (await CardSelectCmd.FromDeckForRemoval(player, prefs)).ToArray(), false),
            ("deck-transform", async c => (await CardSelectCmd.FromDeckForTransformation(player, prefs)).ToArray(), false),
            ("deck-enchant", async c => (await CardSelectCmd.FromDeckForEnchantment(player, enchantment, 2, prefs)).ToArray(), false),
            ("deck-enchant-filter", async c => (await CardSelectCmd.FromDeckForEnchantment(player, enchantment, 2, _ => true, prefs)).ToArray(), false),
            ("deck-enchant-list", async c => (await CardSelectCmd.FromDeckForEnchantment(deck.Where(enchantment.CanEnchant).ToArray(), enchantment, 2, prefs)).ToArray(), false),
            ("pile-multiple", async c => (await CardSelectCmd.FromCombatPile(c, player.PlayerCombatState.DiscardPile, player, prefs)).ToArray(), false),
            ("pile-filter", async c => (await CardSelectCmd.FromCombatPile(c, player.PlayerCombatState.DiscardPile, player, one, _ => true)).ToArray(), false),
            ("bundle", async c => (await CardSelectCmd.FromChooseABundleScreen(player,
                new IReadOnlyList<CardModel>[] { deck.Take(2).ToArray(), deck.Skip(2).Take(2).ToArray() })).ToArray(), false),
            ("empty-filter", async c => (await CardSelectCmd.FromHand(c, player, one, _ => false, null!)).ToArray(), true),
            ("autopick", async c => (await CardSelectCmd.FromSimpleGrid(c, hand.Take(1).ToArray(), player, autoOne)).ToArray(), false)
        };
        var records = new List<object>();
        try
        {
            foreach (var sample in cases)
            {
                CardModel[]? baseline = null; string[]? baselineBytes = null; LocalCardChoice[]? expected = null;
                var signals = new List<(int, int)>();
                for (int pass = 0; pass < 2; pass++)
                {
                    File.WriteAllText(Path.Combine(root, "selection-matrix-current.json"), System.Text.Json.JsonSerializer.Serialize(new { sample.Name, pass }));
                    int nextChoice = 0;
                    var choices = pass == 0 ? new LocalChoices(choose: options =>
                        sample.Empty ? options.First(c => c.Index < 0 || c.Indices is { Length: 0 }) :
                        options.FirstOrDefault(c => c.Indices is { Length: 2 } indices && indices[0] > indices[1]) ?? options.Last())
                        : new LocalChoices(choose: options =>
                        {
                            // A second command reserves a NEW native choice ID. Reuse
                            // the chosen cards/order, not the first command's checkpoint.
                            var intent = expected![nextChoice++];
                            return options.Single(o => o.Kind == intent.Kind && o.Index == intent.Index && o.ModelId == intent.ModelId &&
                                (o.Indices ?? []).SequenceEqual(intent.Indices ?? []));
                        });
                    current.SetValue(null, choices); active.SetValue(null, pass == 1);
                    Synchronized.Clear(); var context = new Context(player);
                    var started = Stopwatch.StartNew(); var task = sample.Command(context);
                    while (!task.IsCompleted)
                    {
                        if (started.Elapsed.TotalSeconds > 8) throw new InvalidOperationException(sample.Name + ": choice timeout: " + LocalChoices.PendingDescription());
                        choices.Tick(); await Frame();
                    }
                    var selected = await task; choices.Finish();
                    signals.Add((context.Begun, context.Ended));
                    if (context.Begun != context.Ended) throw new InvalidOperationException("Unbalanced native choice signals");
                    if (pass == 0) { baseline = selected; baselineBytes = Synchronized.ToArray(); expected = choices.Completed; }
                    else if (!selected.SequenceEqual(baseline!) || !Synchronized.SequenceEqual(baselineBytes!))
                        throw new InvalidOperationException(sample.Name + ": native cards/order/synchronization changed");
                    active.SetValue(null, false); current.SetValue(null, null);
                    var settle = Stopwatch.StartNew();
                    do { await Frame(); } while (settle.ElapsedMilliseconds < 400);
                }
                records.Add(new { sample.Name, selected = baseline!.Select(c => c.Id.ToString()).ToArray(),
                    choiceCount = expected!.Length, synchronizationCount = baselineBytes!.Length,
                    begun = signals[0].Item1, ended = signals[0].Item2, ordinaryAndDataMatch = true });
            }
            LocalWire.Write(Path.Combine(root, "integration-selection-matrix.json"), new {
                version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3),
                nativeModule = typeof(CardModel).Assembly.ManifestModule.ModuleVersionId,
                synchronizedChoiceIdentityPreserved = true, records,
                note = "Synthetic shared-command/UI matrix; card effects and completed-action restoration have separate native tests." });
        }
        finally { active.SetValue(null, false); current.SetValue(null, null); mode.GetMethod("FreeSelections")!.Invoke(null, null); harmony.UnpatchAll(harmony.Id); }
    }
}
