using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Numerics;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// General native selection flows. Effects remain game-owned; native handlers preserve IDs and replay.
public sealed class LocalChoices
{
    private readonly LocalCardChoice[] _expected;
    private readonly Func<LocalCardChoice[], LocalCardChoice>? _choose;
    private readonly List<LocalCardChoice> _completed = [];
    private readonly HashSet<object> _handled = new(ReferenceEqualityComparer.Instance);
    private readonly LocalSelectionCursor _cursor;
    public LocalCardChoice[] Completed => _completed.ToArray();
    public LocalChoices(LocalCardChoice[]? expected = null, Func<LocalCardChoice[], LocalCardChoice>? choose = null,
        LocalSelectionCursor? cursor = null)
    { _expected = expected ?? []; _choose = choose; _cursor = cursor ?? new(); }

    private static FieldInfo FindField(object owner, string name)
    {
        for (var type = owner.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field) return field;
        throw new InvalidOperationException("游戏选牌接口已变化：" + name);
    }
    private static T Field<T>(object owner, string name) => (T)FindField(owner, name).GetValue(owner)!;
    private static void Invoke(object owner, string name, params object?[] arguments)
    {
        for (var type = owner.GetType(); type != null; type = type.BaseType)
            if (type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .SingleOrDefault(m => m.Name == name && m.GetParameters().Length == arguments.Length) is { } method)
            { method.Invoke(owner, arguments); return; }
        throw new InvalidOperationException("游戏选牌接口已变化：" + name);
    }

    private static string OfferHash(string kind, IReadOnlyList<CardModel> cards, int minimum, int maximum)
    {
        var writer = new PacketWriter(); writer.WriteInt(minimum); writer.WriteInt(maximum); writer.WriteInt(cards.Count);
        foreach (var card in cards) card.ToSerializable().Serialize(writer);
        var identity = Convert.ToHexString(writer.Buffer.AsSpan(0, (writer.BitPosition + 7) / 8));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(LocalCapture.Fingerprint() + kind + identity)));
    }

    private LocalCardChoice Select(LocalCardChoice[] options, object identity)
    {
        LocalCardChoice selected;
        if (_choose != null) selected = _choose(options);
        else
        {
            if (_completed.Count >= _expected.Length) throw new InvalidOperationException("出现了方案之外的选择，已停止执行。");
            var expected = _expected[_completed.Count];
            selected = options.SingleOrDefault(c => c.Index == expected.Index && c.ModelId == expected.ModelId &&
                c.OfferHash == expected.OfferHash && c.Kind == expected.Kind && (c.Indices ?? []).SequenceEqual(expected.Indices ?? []))
                ?? throw new InvalidOperationException("选择内容与模拟不一致，已停止执行。");
        }
        if (!options.Contains(selected)) throw new InvalidOperationException("无效的选择分支。");
        _handled.Add(identity); _completed.Add(selected); return selected;
    }

    private LocalCardChoice SelectCards(string kind, CardModel[] cards, CardSelectorPrefs prefs, object identity, bool clampCount = false)
    {
        var minimum = clampCount ? Math.Min(cards.Length, prefs.MinSelect) : prefs.MinSelect;
        var maximum = clampCount ? Math.Min(cards.Length, prefs.MaxSelect) : prefs.MaxSelect;
        maximum = Math.Min(cards.Length, maximum);
        // Grid confirmation buttons do not permit intermediate counts unless
        // manual confirmation is requested. Hand selectors do permit them.
        int[]? sizes = kind is "grid" or "pile" && !prefs.RequireManualConfirmation
            ? (minimum == 0 && maximum > 0 ? [0, maximum] : [maximum])
            : kind == "deck-upgrade" ? [maximum] : null;
        var space = new LocalSelectionSpace(cards.Length, minimum, maximum, sizes: sizes);
        var hash = OfferHash(kind + ":ordered-v2:" + string.Join(",", sizes ?? []), cards, minimum, maximum);
        // The native selector explicitly describes discard/exhaust, independent of the
        // source card's name or Mod. These are exploration hints, never eliminated choices.
        var prompt = prefs.Prompt;
        bool discards = prompt.LocTable == CardSelectorPrefs.DiscardSelectionPrompt.LocTable &&
            prompt.LocEntryKey == CardSelectorPrefs.DiscardSelectionPrompt.LocEntryKey;
        bool exhausts = prompt.LocTable == CardSelectorPrefs.ExhaustSelectionPrompt.LocTable &&
            prompt.LocEntryKey == CardSelectorPrefs.ExhaustSelectionPrompt.LocEntryKey;
        int Priority(CardModel card)
        {
            int score = 0;
            if ((discards || exhausts) && card.Type is CardType.Status or CardType.Curse) score += 40;
            try
            {
                if (prefs.ShouldGlowGold?.Invoke(card) == true) score += 20;
            }
            catch { /* Unknown native preview retains all alternatives. */ }
            return score;
        }
        var priorities = cards.Select(Priority).ToArray();
        bool complete = space.Count <= 128;
        var ranks = _cursor.Page(hash, space).ToHashSet();
        // Explore native glow/status hints early without deleting any alternative.
        // Stable ranks depend only on the offer, never on changing preference scores.
        foreach (var size in sizes ?? Enumerable.Range(minimum, maximum - minimum + 1).ToArray())
        {
            var indices = Enumerable.Range(0, cards.Length).OrderByDescending(i => priorities[i]).ThenBy(i => i).Take(size).ToArray();
            ranks.Add(space.Rank(indices)); ranks.Add(space.Rank(indices.Reverse().ToArray()));
        }
        var expected = _expected.ElementAtOrDefault(_completed.Count);
        if (expected?.Kind == kind && expected.OfferHash == hash && expected.Indices != null)
            ranks.Add(space.Rank(expected.Indices));
        LocalCardChoice Make(BigInteger rank)
        {
            var indexes = space.At(rank);
            return new LocalCardChoice(hash, rank <= int.MaxValue ? (int)rank : -2,
                string.Join(";", indexes.Select(n => cards[n].Id)),
                string.Join("、", indexes.Select(n => $"{n + 1}. {cards[n].Title}")), indexes, kind,
                Preference: indexes.Sum(n => priorities[n]), CompleteOffer: complete);
        }
        var options = ranks.Order().Select(Make).ToArray();
        return Select(options, identity);
    }

    // Used only by owned workers replacing presentation. Native CardSelectCmd still reserves
    // and synchronizes choice IDs, logs the result, and applies the original card effects.
    internal IEnumerable<CardModel> SelectWithoutPresentation(string kind, CardModel[] cards, CardSelectorPrefs prefs, bool clampCount = false)
    {
        var selected = SelectCards(kind, cards, prefs, new object(), clampCount);
        return selected.Indices!.Select(i => cards[i]).ToArray();
    }

    internal IEnumerable<CardModel> OfferWithoutPresentation(IReadOnlyList<CardModel> cards, bool canSkip)
    {
        var hash = OfferHash("offer", cards, canSkip ? 0 : 1, 1);
        var options = cards.Select((c, i) => new LocalCardChoice(hash, i, c.Id.ToString(), c.Title)).ToList();
        if (canSkip) options.Add(new(hash, -1, "", "跳过"));
        var selected = Select(options.ToArray(), new object());
        return selected.Index < 0 ? [] : [cards[selected.Index]];
    }

    private LocalCardChoice SelectBundle(IReadOnlyList<IReadOnlyList<CardModel>> bundles, object identity)
    {
        // Include bundle boundaries, not only the flattened cards: [A,B]/[C]
        // and [A]/[B,C] are different native offers with different effects.
        var hash = OfferHash("bundle:" + string.Join(",", bundles.Select(b => b.Count)), bundles.SelectMany(b => b).ToArray(), 1, 1);
        return Select(bundles.Select((b, i) => new LocalCardChoice(hash, i,
            string.Join(";", b.Select(c => c.Id)), string.Join("、", b.Select(c => c.Title)), Kind: "bundle")).ToArray(), identity);
    }
    internal IEnumerable<IReadOnlyList<CardModel>> BundleWithoutPresentation(IReadOnlyList<IReadOnlyList<CardModel>> bundles) =>
        [bundles[SelectBundle(bundles, new object()).Index]];

    internal static bool SupportedGrid(Type type) => type == typeof(NSimpleCardSelectScreen) ||
        type == typeof(NCombatPileCardSelectScreen) || type == typeof(NDeckCardSelectScreen) ||
        type == typeof(NDeckUpgradeSelectScreen) || type == typeof(NDeckTransformSelectScreen) || type == typeof(NDeckEnchantSelectScreen);
    internal static string GridKind(Type type) => type == typeof(NCombatPileCardSelectScreen) ? "pile" :
        type == typeof(NDeckCardSelectScreen) ? "deck" : type == typeof(NDeckUpgradeSelectScreen) ? "deck-upgrade" :
        type == typeof(NDeckTransformSelectScreen) ? "deck-transform" : type == typeof(NDeckEnchantSelectScreen) ? "deck-enchant" : "grid";

    public void Tick(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var top = NOverlayStack.Instance?.Peek();
        if (top is NChooseACardSelectionScreen screen && top.GetType() == typeof(NChooseACardSelectionScreen))
        {
            if (!screen.IsNodeReady() || _handled.Contains(screen) || Field<bool>(screen, "_screenComplete")) return;
            // The click debounce is a UI delay, unrelated to native choice legality or effects.
            // Preserve it in the real executor. Only an owned simulation can bypass it.
            if (LocalWorkerVisuals.Active) FindField(screen, "_openedTicks").SetValue(screen, 0UL);
            if (Godot.Time.GetTicksMsec() - Field<ulong>(screen, "_openedTicks") <= 350) return;
            var cards = Field<IReadOnlyList<CardModel>>(screen, "_cards");
            var canSkip = Field<bool>(screen, "_canSkip");
            var hash = OfferHash("offer", cards, canSkip ? 0 : 1, 1);
            var options = cards.Select((c, i) => new LocalCardChoice(hash, i, c.Id.ToString(), c.Title)).ToList();
            if (canSkip) options.Add(new(hash, -1, "", "跳过"));
            var selected = Select(options.ToArray(), screen);
            token.ThrowIfCancellationRequested();
            if (selected.Index < 0) screen.Call("OnSkipButtonReleased", default(Variant));
            else
            {
                var row = Field<Control>(screen, "_cardRow");
                var holder = row.GetChildren().OfType<NCardHolder>().Single(h => ReferenceEquals(h.CardModel, cards[selected.Index]));
                screen.Call("SelectHolder", holder);
            }
            return;
        }
        if (top is NChooseABundleSelectionScreen bundleScreen && top.GetType() == typeof(NChooseABundleSelectionScreen))
        {
            if (!bundleScreen.IsNodeReady() || _handled.Contains(bundleScreen)) return;
            var bundles = Field<IReadOnlyList<IReadOnlyList<CardModel>>>(bundleScreen, "_bundles");
            var selected = SelectBundle(bundles, bundleScreen);
            token.ThrowIfCancellationRequested();
            var row = Field<Control>(bundleScreen, "_bundleRow");
            var node = row.GetChildren().OfType<NCardBundle>().Single(n => ReferenceEquals(n.Bundle, bundles[selected.Index]));
            Invoke(bundleScreen, "OnBundleClicked", node);
            Invoke(bundleScreen, "ConfirmSelection", new object?[] { null });
            return;
        }
        if (top is NCardGridSelectionScreen grid && SupportedGrid(top.GetType()))
        {
            if (!grid.IsNodeReady() || _handled.Contains(grid)) return;
            bool pileSelection = top.GetType() == typeof(NCombatPileCardSelectScreen);
            var cards = pileSelection ? Field<CardPile>(grid, "_pile").Cards
                .Where(Field<Func<CardModel, bool>?>(grid, "_filter") ?? (_ => true)).ToArray() :
                Field<IReadOnlyList<CardModel>>(grid, "_cards").ToArray();
            var prefs = Field<CardSelectorPrefs>(grid, "_prefs");
            var kind = GridKind(top.GetType());
            var selected = SelectCards(kind, cards, prefs, grid, pileSelection);
            token.ThrowIfCancellationRequested();
            foreach (var index in selected.Indices!) Invoke(grid, "OnCardClicked", cards[index]);
            if (!Field<TaskCompletionSource<IEnumerable<CardModel>>>(grid, "_completionSource").Task.IsCompleted)
            {
                if (kind is "grid" or "pile") Invoke(grid, "CompleteSelection");
                else if (kind == "deck-transform")
                {
                    // Preserve native preview construction before confirming; its
                    // callback is supplied by the caller, potentially a Mod.
                    if (selected.Indices.Length < prefs.MaxSelect) Invoke(grid, "ConfirmSelection", new object?[] { null });
                    if (!Field<TaskCompletionSource<IEnumerable<CardModel>>>(grid, "_completionSource").Task.IsCompleted)
                        Invoke(grid, "CompleteSelection", new object?[] { null });
                }
                else Invoke(grid, "CheckIfSelectionComplete");
            }
            return;
        }
        if (NPlayerHand.Instance is { IsInCardSelection: true } hand)
        {
            var completion = Field<TaskCompletionSource<IEnumerable<CardModel>>>(hand, "_selectionCompletionSource");
            if (completion.Task.IsCompleted || _handled.Contains(completion)) return;
            var prefs = Field<CardSelectorPrefs>(hand, "_prefs");
            var filter = Field<Func<CardModel, bool>?>(hand, "_currentSelectionFilter");
            var cards = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState()!)!.PlayerCombatState!.Hand.Cards
                .Where(filter ?? (_ => true)).ToArray();
            // Native selection mode can become visible before restored card holders
            // finish entering the hand. Defer the click, not the native command.
            if (cards.Any(c => hand.GetCardHolder(c) is not NHandCardHolder)) return;
            var selected = SelectCards("hand", cards, prefs, completion);
            token.ThrowIfCancellationRequested();
            foreach (var index in selected.Indices!)
            {
                var holder = hand.GetCardHolder(cards[index]);
                if (holder is not NHandCardHolder) throw new InvalidOperationException("手牌选择实例已变化。");
                Invoke(hand, hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect ? "SelectCardInUpgradeMode" : "SelectCardInSimpleMode", holder);
            }
            Invoke(hand, "OnSelectModeConfirmButtonPressed", new object?[] { null });
        }
    }

    public void Finish()
    {
        if (_choose == null && _completed.Count != _expected.Length)
            throw new InvalidOperationException("选择次数与记录不一致，已停止执行。");
    }
    public static string PendingDescription() => NOverlayStack.Instance?.Peek() is { } screen ? screen.GetType().Name :
        NPlayerHand.Instance?.IsInCardSelection == true ? "手牌选择" : "无选择流程";
}
