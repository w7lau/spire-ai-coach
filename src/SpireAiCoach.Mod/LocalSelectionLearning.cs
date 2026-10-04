using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using System.Numerics;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Learn what a selector actually did; a pile screen alone does not imply
// return-to-hand (it can also put on top of the deck, exhaust, etc.).
internal sealed class LocalSelectionLearning
{
    private enum Effect { Hand, Draw, Exhaust, Other }
    private readonly record struct Slot(string Source, int Ordinal, string Kind, string Table, string Prompt);
    private sealed record Pool(PileType Origin, HashSet<string> Offered);
    private readonly Dictionary<Slot, Effect> _effects = [];
    private readonly Dictionary<string, List<Pool>> _returns = new(StringComparer.Ordinal);
    private static string Key(CardModel card) => $"{card.Id}:{card.CurrentUpgradeLevel}";
    private static Slot Id(CardModel source, int ordinal, string kind, CardSelectorPrefs? prefs) =>
        new(Key(source), ordinal, kind, prefs?.Prompt.LocTable ?? "", prefs?.Prompt.LocEntryKey ?? "");
    private static bool Prompt(CardSelectorPrefs? prefs, MegaCrit.Sts2.Core.Localization.LocString prompt) =>
        prefs?.Prompt.LocTable == prompt.LocTable && prefs?.Prompt.LocEntryKey == prompt.LocEntryKey;

    internal Func<int[], int>? Rank(CardModel source, Player player, string kind, CardModel[] cards,
        CardSelectorPrefs? prefs, int ordinal, LocalRolloutStyle style, bool efficient,
        Func<CardModel, int, int> learned)
    {
        if (kind is not ("pile" or "grid" or "offer" or "bundle") ||
            Prompt(prefs, CardSelectorPrefs.DiscardSelectionPrompt) || Prompt(prefs, CardSelectorPrefs.ExhaustSelectionPrompt)) return null;
        try
        {
            bool observed = _effects.TryGetValue(Id(source, ordinal, kind, prefs), out var effect);
            if (observed && effect == Effect.Other) return null;
            if (observed && effect == Effect.Exhaust)
                return indices => indices.Sum(i => cards[i].Type is CardType.Status or CardType.Curse ? 40 : 0);
            var pcs = player.PlayerCombatState!;
            // The source has already paid when the native command asks for a
            // selection. Do not reuse the energy from before playing it.
            int energy = (int)Math.Max(0, pcs.Energy), stars = Math.Max(0, pcs.Stars);
            var preview = LocalTacticalPreview.Capture(player, pcs.Hand.Cards.ToArray(), efficient);
            var values = cards.Select(c => preview.Followup(c, style, learned)).ToArray();
            if (observed && effect == Effect.Draw)
                // A top-decked card is not immediately playable. No assumption
                // about next turn's energy/draw order is used as a proof.
                return indices => indices.Sum(i => Math.Max(0, values[i].Value)) / 4;
            var cached = new Dictionary<BigInteger, int>();
            return indices =>
            {
                // The hint is order-independent; reuse its resource allocation
                // across ordered siblings. Native ranks/effects still keep order.
                var set = indices.Aggregate(BigInteger.Zero, (mask, i) => mask | (BigInteger.One << i));
                if (!cached.TryGetValue(set, out int value))
                    cached[set] = value = LocalFollowup.Value(indices.Select(i => values[i]), energy, stars) /
                        (observed ? 1 : 2);
                return value;
            };
        }
        catch { return null; } // Opaque Mod preview keeps neutral order and every alternative.
    }

    internal void Observe(CardModel source, IEnumerable<LocalChoices.Selection> selections)
    {
        foreach (var selection in selections)
        {
            if (selection.Selected.Length == 0) continue; // Skip is not evidence about the selector's effect.
            try
            {
                var destinations = selection.Selected.Select(c => c.Pile?.Type ?? PileType.None).Distinct().ToArray();
                bool movedToHand = destinations is [PileType.Hand] && selection.Origins.All(p => p != PileType.Hand);
                var effect = movedToHand ? Effect.Hand : destinations is [PileType.Draw] ? Effect.Draw :
                    destinations is [PileType.Exhaust] ? Effect.Exhaust : Effect.Other;
                _effects[Id(source, selection.Ordinal, selection.Kind, selection.Prefs)] = effect;
                if (!movedToHand) continue;
                if (!_returns.TryGetValue(Key(source), out var pools)) _returns[Key(source)] = pools = [];
                // Only models actually offered by this native selector can
                // supply the source-action hint. Never invent a Mod's filter.
                foreach (var origin in selection.Origins.Where(p => p is PileType.Discard or PileType.Exhaust or PileType.Draw).Distinct())
                {
                    var pool = pools.FirstOrDefault(p => p.Origin == origin);
                    if (pool == null) { pool = new(origin, new(StringComparer.Ordinal)); pools.Add(pool); }
                    foreach (var card in selection.Offered) pool.Offered.Add(Key(card));
                }
            }
            catch { /* Observed movement guides ordering only; it never changes native effects. */ }
        }
    }

    internal int SourcePriority(CardModel source, Player player, LocalTacticalPreview preview,
        LocalRolloutStyle style, Func<CardModel, int, int> learned)
    {
        if (!_returns.TryGetValue(Key(source), out var pools)) return 0;
        try
        {
            var pcs = player.PlayerCombatState!;
            int energy = (int)pcs.Energy - Math.Max(0, source.EnergyCost.GetAmountToSpend());
            int stars = pcs.Stars - Math.Max(0, source.HasStarCostX ? pcs.Stars : source.GetStarCostWithModifiers());
            int best = 0;
            foreach (var pool in pools)
                foreach (var card in pcs.AllPiles.Where(p => p.Type == pool.Origin).SelectMany(p => p.Cards))
                {
                    if (!pool.Offered.Contains(Key(card)) || card.EnergyCost.CostsX || card.HasStarCostX) continue;
                    // X previews depend on current resources; do not pretend
                    // a pre-payment X preview describes a post-payment state.
                    var value = preview.Followup(card, style, learned);
                    best = Math.Max(best, LocalFollowup.Value([value], energy, stars));
                }
            return Math.Min(80, best);
        }
        catch { return 0; }
    }
}
