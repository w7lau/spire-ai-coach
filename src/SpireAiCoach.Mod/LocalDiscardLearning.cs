using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

internal sealed class LocalDiscardLearning
{
    private readonly Dictionary<(string Card, bool Auto), (LocalDiscardYield Total, int Count)> _effects = [];
    private sealed class Pool(int minimum, int maximum)
    {
        public int Minimum { get; } = minimum;
        public int Maximum { get; } = maximum;
        public HashSet<string> Offered { get; } = new(StringComparer.Ordinal);
    }
    private readonly Dictionary<string, List<Pool>> _sources = new(StringComparer.Ordinal);
    internal int HookSamples { get; private set; }
    internal int AutoSamples { get; private set; }
    internal bool RetainsBlockHint { get; set; }
    internal static string Key(CardModel card) => $"{card.Id}:{card.CurrentUpgradeLevel}";
    internal static bool IsDiscard(CardSelectorPrefs? prefs) =>
        prefs?.Prompt.LocTable == CardSelectorPrefs.DiscardSelectionPrompt.LocTable &&
        prefs?.Prompt.LocEntryKey == CardSelectorPrefs.DiscardSelectionPrompt.LocEntryKey;

    internal void Observe(string card, bool auto, LocalDiscardYield effect)
    {
        var key = (card, auto); var old = _effects.GetValueOrDefault(key);
        _effects[key] = (old.Total.Add(effect), old.Count + 1);
        if (auto) AutoSamples++; else HookSamples++;
    }
    private LocalDiscardYield Learned(CardModel card, bool auto) =>
        _effects.TryGetValue((Key(card), auto), out var effect) ? effect.Total.Scale(1d / effect.Count) : default;

    private LocalDiscardYield Hint(CardModel card, LocalTacticalPreview preview, LocalRolloutStyle style,
        Func<CardModel, int, int> learned, bool glow, int? energy = null, int? stars = null)
    {
        var hook = Learned(card, false);
        var value = hook;
        if (card.IsSlyThisTurn && glow)
        {
            // Autoplay is free. Do not treat the printed cost as a payment or
            // call CanPlay / ShouldPlay again inside an in-flight selection.
            value = value.Add(_effects.ContainsKey((Key(card), true)) && !card.EnergyCost.CostsX && !card.HasStarCostX
                ? Learned(card, true) : preview.DiscardAutoplay(card, style));
        }
        else
        {
            var playable = preview.Followup(card, style, learned);
            var pcs = card.Owner.PlayerCombatState!;
            if (playable.EnergyCost <= (energy ?? pcs.Energy) && playable.StarCost <= (stars ?? pcs.Stars))
                value = value with { KeepValue = Math.Min(30, Math.Max(0, playable.Value)) / 2d };
        }
        return value with { HandEndHpLoss = preview.DiscardHandEndLoss(card) };
    }

    internal Func<int[], int> Rank(CardModel[] cards, bool[] glows, LocalTacticalPreview preview,
        LocalRolloutStyle style, Func<CardModel, int, int> learned)
    {
        var hints = cards.Select((card, i) => {
            try { return Hint(card, preview, style, learned, glows[i]); }
            catch { return default; }
        }).ToArray();
        var context = preview.DiscardContext;
        // Native choice ranks retain order. Only this additive hint is reusable.
        var cache = new Dictionary<System.Numerics.BigInteger, int>();
        return indices =>
        {
            var mask = indices.Aggregate(System.Numerics.BigInteger.Zero,
                (sum, i) => sum | (System.Numerics.BigInteger.One << i));
            if (!cache.TryGetValue(mask, out int score))
                cache[mask] = score = LocalDiscardEffects.Value(indices.Select(i => hints[i]), context, style);
            return score;
        };
    }

    internal void ObserveSelection(CardModel source, LocalChoices.Selection selection)
    {
        if (!IsDiscard(selection.Prefs) || selection.Selected.Length == 0 ||
            selection.Origins.Any(p => p != PileType.Hand)) return;
        if (!_sources.TryGetValue(Key(source), out var pools)) _sources[Key(source)] = pools = [];
        var prefs = selection.Prefs!.Value;
        var pool = pools.FirstOrDefault(p => p.Minimum == prefs.MinSelect && p.Maximum == prefs.MaxSelect);
        if (pool == null) { pool = new(prefs.MinSelect, prefs.MaxSelect); pools.Add(pool); }
        foreach (var card in selection.Offered) pool.Offered.Add(Key(card));
    }

    internal int SourcePriority(CardModel source, Player player, LocalTacticalPreview preview,
        LocalRolloutStyle style, Func<CardModel, int, int> learned)
    {
        if (!_sources.TryGetValue(Key(source), out var pools)) return 0;
        try
        {
            if (source.EnergyCost.CostsX || source.HasStarCostX) return 0;
            int energy = player.PlayerCombatState!.Energy - Math.Max(0, source.EnergyCost.GetAmountToSpend());
            int stars = player.PlayerCombatState.Stars - Math.Max(0, source.GetStarCostWithModifiers());
            int best = 0;
            foreach (var pool in pools)
            {
                // Previously offered models are a hint, never a recreation of a
                // Mod's selection filter or a proof that this offer will reappear.
                var hand = player.PlayerCombatState!.Hand.Cards.Where(c => c != source && pool.Offered.Contains(Key(c))).ToArray();
                int count = Math.Min(pool.Maximum, hand.Length);
                if (count < pool.Minimum) continue;
                var hints = hand.Select(c => {
                    try { return Hint(c, preview, style, learned, c.IsSlyThisTurn, energy, stars); }
                    catch { return default; }
                })
                    .OrderByDescending(h => LocalDiscardEffects.Value([h], preview.DiscardContext, style)).Take(count);
                best = Math.Max(best, LocalDiscardEffects.Value(hints, preview.DiscardContext, style));
            }
            return Math.Min(80, best);
        }
        catch { return 0; }
    }
}
