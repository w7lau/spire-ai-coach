using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;

namespace SpireAiCoach.Mod;

// Learn exploration hints from effects actually executed in this frozen search.
// This is not an effect mirror: native hooks still execute every proposed line.
internal sealed class LocalNativeLearning
{
    internal sealed record Observation(string Card, int Round, decimal Energy, int PaidEnergy, int Hand,
        int Upgrades, int Statuses, int Buffs, int Hp);
    private readonly Dictionary<string, (double Total, int Samples)> _bonuses = new(StringComparer.Ordinal);
    private static string Key(CardModel card) => $"{card.Id}:{card.CurrentUpgradeLevel}";

    public Observation? Before(CardModel card, Player player)
    {
        try
        {
            var pcs = player.PlayerCombatState!;
            return new(Key(card), player.Creature.CombatState!.RoundNumber, pcs.Energy, card.EnergyCost.GetAmountToSpend(),
                pcs.Hand.Cards.Count, pcs.AllPiles.SelectMany(p => p.Cards).Sum(c => c.CurrentUpgradeLevel),
                pcs.AllPiles.Where(p => p.Type != PileType.Exhaust).SelectMany(p => p.Cards)
                    .Count(c => c.Type is CardType.Status or CardType.Curse),
                player.Creature.Powers.Count(p => p.TypeForCurrentAmount == PowerType.Buff), player.Creature.CurrentHp);
        }
        catch { return null; }
    }

    public void After(Observation? before, Player player)
    {
        if (before == null) return;
        try
        {
            var pcs = player.PlayerCombatState!;
            // Exclude turn changes and victory hooks from a card's learned hint.
            if (player.Creature.CombatState!.RoundNumber != before.Round || CombatManager.Instance.IsOverOrEnding) return;
            var drawn = Math.Max(0, pcs.Hand.Cards.Count - before.Hand + 1);
            var energy = Math.Max(0, pcs.Energy - before.Energy + before.PaidEnergy);
            var upgrades = Math.Max(0, pcs.AllPiles.SelectMany(p => p.Cards).Sum(c => c.CurrentUpgradeLevel) - before.Upgrades);
            var removed = Math.Max(0, before.Statuses - pcs.AllPiles.Where(p => p.Type != PileType.Exhaust)
                .SelectMany(p => p.Cards).Count(c => c.Type is CardType.Status or CardType.Curse));
            var buffs = Math.Max(0, player.Creature.Powers.Count(p => p.TypeForCurrentAmount == PowerType.Buff) - before.Buffs);
            var healed = Math.Max(0, player.Creature.CurrentHp - before.Hp);
            double bonus = Math.Min(36, drawn * 6) + Math.Min(40, (double)energy * 12) +
                Math.Min(40, upgrades * 6) + Math.Min(24, removed * 6) + Math.Min(40, buffs * 20) + Math.Min(24, healed * 3);
            var old = _bonuses.GetValueOrDefault(before.Card);
            _bonuses[before.Card] = (old.Total + bonus, old.Samples + 1);
        }
        catch { /* Unknown Mod observations do not alter legal branches or final scoring. */ }
    }

    public int Priority(CardModel card, int nativePreview) => _bonuses.TryGetValue(Key(card), out var learned)
        ? nativePreview + (int)(learned.Total / learned.Samples) : nativePreview;
}
