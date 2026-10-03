using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Learn exploration hints from effects actually executed in this frozen search.
// This is not an effect mirror: native hooks still execute every proposed line.
internal sealed class LocalNativeLearning(bool trackCosts = false)
{
    internal sealed record Observation(string Card, int Round, decimal Energy, int PaidEnergy, int Hand,
        int Upgrades, int Statuses, int Buffs, int BuffAmount, int Hp, uint Played,
        IReadOnlyDictionary<uint, int>? HandCosts, bool HasHandEndEffect, IReadOnlyDictionary<uint, int> Hints,
        bool? DrawAllowed, IReadOnlyDictionary<uint, string> HintModels);
    private readonly Dictionary<string, (double Total, int Samples)> _bonuses = new(StringComparer.Ordinal);
    private Dictionary<uint, int> _hints = [];
    private Dictionary<uint, string> _hintModels = [];
    private readonly Dictionary<(string Source, string Target), int> _dependencies = new();
    private Observation? _pending;
    private readonly HashSet<string> _drawSources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _drawLocks = new(StringComparer.Ordinal);
    private static string Key(CardModel card) => $"{card.Id}:{card.CurrentUpgradeLevel}";
    private static bool? DrawAllowed(Player player)
    {
        try { return Hook.ShouldDraw(player.Creature.CombatState!, player, false, out _); }
        catch { return null; }
    }

    private static Dictionary<uint, int> Costs(Player player) => player.PlayerCombatState!.Hand.Cards
        // Spending energy makes an X card's spending amount fall; that is not a
        // reduction of its cost. Keep X cards out of this optional observation.
        .Where(c => !c.EnergyCost.CostsX)
        .ToDictionary(c => NetCombatCard.FromModel(c).CombatCardIndex, c => c.EnergyCost.GetAmountToSpend());

    public Observation? Before(CardModel card, Player player)
    {
        try
        {
            var pcs = player.PlayerCombatState!;
            return new(Key(card), player.Creature.CombatState!.RoundNumber, pcs.Energy, card.EnergyCost.GetAmountToSpend(),
                pcs.Hand.Cards.Count, pcs.AllPiles.SelectMany(p => p.Cards).Sum(c => c.CurrentUpgradeLevel),
                pcs.AllPiles.Where(p => p.Type != PileType.Exhaust).SelectMany(p => p.Cards)
                    .Count(c => c.Type is CardType.Status or CardType.Curse),
                player.Creature.Powers.Count(p => p.TypeForCurrentAmount == PowerType.Buff),
                player.Creature.Powers.Where(p => p.TypeForCurrentAmount == PowerType.Buff).Sum(p => Math.Max(0, p.Amount)),
                player.Creature.CurrentHp, NetCombatCard.FromModel(card).CombatCardIndex, trackCosts ? Costs(player) : null,
                card.HasTurnEndInHandEffect, new Dictionary<uint, int>(_hints), DrawAllowed(player), new Dictionary<uint, string>(_hintModels));
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
            if (drawn > 0) _drawSources.Add(before.Card);
            if (before.DrawAllowed == true && DrawAllowed(player) == false) _drawLocks.Add(before.Card);
            var energy = Math.Max(0, pcs.Energy - before.Energy + before.PaidEnergy);
            var upgrades = Math.Max(0, pcs.AllPiles.SelectMany(p => p.Cards).Sum(c => c.CurrentUpgradeLevel) - before.Upgrades);
            var removed = Math.Max(0, before.Statuses - pcs.AllPiles.Where(p => p.Type != PileType.Exhaust)
                .SelectMany(p => p.Cards).Count(c => c.Type is CardType.Status or CardType.Curse));
            var buffs = Math.Max(0, player.Creature.Powers.Count(p => p.TypeForCurrentAmount == PowerType.Buff) - before.Buffs);
            var buffAmount = Math.Max(0, player.Creature.Powers.Where(p => p.TypeForCurrentAmount == PowerType.Buff)
                .Sum(p => Math.Max(0, p.Amount)) - before.BuffAmount);
            var healed = Math.Max(0, player.Creature.CurrentHp - before.Hp);
            // An end-in-hand flag does not forbid an additional OnPlay cost in a Mod.
            // Correct that uncertain preview with the HP change actually observed on play.
            var handEffectPlayCost = before.HasHandEndEffect ? Math.Max(0, before.Hp - player.Creature.CurrentHp) : 0;
            double bonus = Math.Min(36, drawn * 6) + Math.Min(40, (double)energy * 12) +
                Math.Min(40, upgrades * 6) + Math.Min(24, removed * 6) + Math.Min(40, buffs * 20) + Math.Min(24, healed * 3) -
                Math.Min(40, handEffectPlayCost * 3);
            bonus += Math.Min(40, Math.Sqrt(buffAmount) * 8);
            if (before.HandCosts != null)
                bonus += Math.Min(80d, (double)LocalResourceEffects.HandCostSavings(before.HandCosts, Costs(player), before.Played) * 12);
            var old = _bonuses.GetValueOrDefault(before.Card);
            _bonuses[before.Card] = (old.Total + bonus, old.Samples + 1);
            _pending = before;
        }
        catch { /* Unknown Mod observations do not alter legal branches or final scoring. */ }
    }

    public void ResetDecision() { _pending = null; _hints.Clear(); _hintModels.Clear(); }
    public void ObserveHints(Player player, Dictionary<uint, int> current)
    {
        // Reuse previews already computed by native enumeration. No extra card
        // execution or hypothetical mechanics: compare the same remaining
        // instances after the preceding real play (upgrades, strength, costs,
        // and block-driven damage can all improve a following card).
        var pending = _pending; _pending = null; _hints = current;
        _hintModels = player.PlayerCombatState!.Hand.Cards.Where(c => current.ContainsKey(NetCombatCard.FromModel(c).CombatCardIndex))
            .ToDictionary(c => NetCombatCard.FromModel(c).CombatCardIndex, Key);
        if (pending == null || player.Creature.CombatState?.RoundNumber != pending.Round) return;
        int gains = current.Where(p => p.Key != pending.Played && pending.Hints.ContainsKey(p.Key))
            .Sum(p => Math.Max(0, p.Value - pending.Hints[p.Key]));
        var old = _bonuses.GetValueOrDefault(pending.Card);
        if (old.Samples > 0) _bonuses[pending.Card] = (old.Total + Math.Min(80, gains * .75), old.Samples);
        foreach (var pair in current)
            if (pair.Key != pending.Played && pending.Hints.TryGetValue(pair.Key, out int before) &&
                pair.Value > before && pending.HintModels.TryGetValue(pair.Key, out string? model) && model != pending.Card)
            {
                var key = (pending.Card, model);
                _dependencies[key] = Math.Max(_dependencies.GetValueOrDefault(key), pair.Value - before);
            }
    }

    public int Priority(CardModel card, int nativePreview)
    {
        string key = Key(card);
        int priority = _bonuses.TryGetValue(key, out var learned) ? nativePreview + (int)(learned.Total / learned.Samples) : nativePreview;
        // Observe the native draw gate rather than identifying a particular card
        // or mirroring its power. Generation can also enlarge a hand, so these
        // are scheduling hints only; every outcome still runs the actual hooks.
        if (_drawLocks.Contains(key) && card.Owner.PlayerCombatState!.Hand.Cards.Any(c => c != card &&
            _drawSources.Contains(Key(c)) && _hints.ContainsKey(NetCombatCard.FromModel(c).CombatCardIndex)))
            priority -= 80;
        return priority;
    }

    public void OrderDependencies(Player player, List<LocalAction> actions)
    {
        var original = actions.ToArray();
        var cards = player.PlayerCombatState!.Hand.Cards;
        decimal energy = player.PlayerCombatState.Energy;
        for (int i = 0; i < actions.Count; i++)
        {
            var source = cards[original[i].HandIndex];
            foreach (var targetAction in original)
            {
                var target = cards[targetAction.HandIndex];
                if (source == target || source.EnergyCost.GetAmountToSpend() + target.EnergyCost.GetAmountToSpend() > energy) continue;
                if (_dependencies.TryGetValue((Key(source), Key(target)), out int gain) && gain > 0)
                    actions[i] = actions[i] with { Preference = Math.Max(actions[i].Preference,
                        targetAction.Preference + Math.Min(50, gain) + 1) };
            }
        }
    }
}
