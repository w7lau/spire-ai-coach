using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.Combat;
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
    public LocalCardChoice[] Completed => _completed.ToArray();
    public LocalChoices(LocalCardChoice[]? expected = null, Func<LocalCardChoice[], LocalCardChoice>? choose = null)
    { _expected = expected ?? []; _choose = choose; }

    private static FieldInfo FindField(object owner, string name)
    {
        for (var type = owner.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field) return field;
        throw new InvalidOperationException("游戏选牌接口已变化：" + name);
    }
    private static T Field<T>(object owner, string name) => (T)FindField(owner, name).GetValue(owner)!;
    private static void Invoke(object owner, string name, params object?[] arguments) => owner.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(owner, arguments);

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
        var hash = OfferHash(kind, cards, minimum, maximum);
        var options = LocalSelectionBranches.Generate(cards.Length, minimum, maximum).Select((indexes, i) =>
            new LocalCardChoice(hash, i, string.Join(";", indexes.Select(n => cards[n].Id)),
                string.Join("、", indexes.Select(n => $"{n + 1}. {cards[n].Title}")), indexes, kind)).ToArray();
        return Select(options, identity);
    }

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
        if (top is NCardGridSelectionScreen grid && (top.GetType() == typeof(NSimpleCardSelectScreen) ||
            top.GetType() == typeof(NCombatPileCardSelectScreen)))
        {
            if (!grid.IsNodeReady() || _handled.Contains(grid)) return;
            bool pileSelection = top.GetType() == typeof(NCombatPileCardSelectScreen);
            var cards = pileSelection ? Field<CardPile>(grid, "_pile").Cards
                .Where(Field<Func<CardModel, bool>?>(grid, "_filter") ?? (_ => true)).ToArray() :
                Field<IReadOnlyList<CardModel>>(grid, "_cards").ToArray();
            var prefs = Field<CardSelectorPrefs>(grid, "_prefs");
            var selected = SelectCards(pileSelection ? "pile" : "grid", cards, prefs, grid, pileSelection);
            token.ThrowIfCancellationRequested();
            foreach (var index in selected.Indices!) Invoke(grid, "OnCardClicked", cards[index]);
            if (prefs.RequireManualConfirmation || selected.Indices!.Length == 0) Invoke(grid, "CompleteSelection");
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
