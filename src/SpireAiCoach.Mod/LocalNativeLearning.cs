using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Learn exploration hints from effects actually executed in this frozen search.
// This is not an effect mirror: native hooks still execute every proposed line.
internal sealed class LocalNativeLearning(bool trackCosts = false, bool trackDurations = false)
{
    internal LocalSelectionLearning Selections { get; } = new();
    internal sealed record Observation(string Card, int Round, decimal Energy, int PaidEnergy, int Hand,
        int Upgrades, int Statuses, int Buffs, int BuffAmount, int Hp, uint Played,
        IReadOnlyDictionary<uint, int>? HandCosts, bool HasHandEndEffect, IReadOnlyDictionary<uint, int> Hints,
        IReadOnlyDictionary<uint, string> HintModels, IReadOnlyDictionary<PowerModel, int> BuffPowers);
    private readonly Dictionary<string, (double Total, int Samples)> _bonuses = new(StringComparer.Ordinal);
    private Dictionary<uint, int> _hints = [];
    private Dictionary<uint, string> _hintModels = [];
    private readonly Dictionary<(string Source, string Target), int> _dependencies = new();
    private Observation? _pending;
    private sealed record BuffCredit(string Card, int Round, IReadOnlyDictionary<PowerModel, int> Before,
        Dictionary<PowerModel, int> Gained, double Bonus);
    private readonly List<BuffCredit> _credits = [];
    private static readonly IEqualityComparer<PowerModel> PowerIdentity = ReferenceEqualityComparer.Instance;
    private static Dictionary<PowerModel, int> Buffs(Player player) => player.Creature.Powers
        .Where(p => p.TypeForCurrentAmount == PowerType.Buff).ToDictionary(p => p, p => Math.Max(0, p.Amount),
            PowerIdentity);
    private static double BuffBonus(IReadOnlyDictionary<PowerModel, int> before, Dictionary<PowerModel, int> gained) =>
        Math.Min(40, gained.Count(p => !before.ContainsKey(p.Key)) * 20) + Math.Min(40, Math.Sqrt(gained.Values.Sum()) * 8);
    private static string Key(CardModel card) => $"{card.Id}:{card.CurrentUpgradeLevel}";
    // A permission hook is not necessarily a pure query: Mods can consume a
    // draw-prevention charge inside ShouldDraw. Observe settled effects only;
    // the native draw command owns every call to that hook.

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
                card.HasTurnEndInHandEffect, new Dictionary<uint, int>(_hints), new Dictionary<uint, string>(_hintModels),
                trackDurations ? Buffs(player) : new Dictionary<PowerModel, int>(PowerIdentity));
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
            var buffAmount = Math.Max(0, player.Creature.Powers.Where(p => p.TypeForCurrentAmount == PowerType.Buff)
                .Sum(p => Math.Max(0, p.Amount)) - before.BuffAmount);
            var healed = Math.Max(0, player.Creature.CurrentHp - before.Hp);
            // An end-in-hand flag does not forbid an additional OnPlay cost in a Mod.
            // Correct that uncertain preview with the HP change actually observed on play.
            var handEffectPlayCost = before.HasHandEndEffect ? Math.Max(0, before.Hp - player.Creature.CurrentHp) : 0;
            double bonus = Math.Min(36, drawn * 6) + Math.Min(40, (double)energy * 12) +
                Math.Min(40, upgrades * 6) + Math.Min(24, removed * 6) + Math.Min(40, buffs * 20) + Math.Min(24, healed * 3) -
                Math.Min(40, handEffectPlayCost * 3);
            double growthBonus = Math.Min(40, buffs * 20) + Math.Min(40, Math.Sqrt(buffAmount) * 8);
            if (trackDurations)
            {
                var gained = Buffs(player).Where(p => p.Value > before.BuffPowers.GetValueOrDefault(p.Key))
                    .ToDictionary(p => p.Key, p => p.Value - before.BuffPowers.GetValueOrDefault(p.Key), PowerIdentity);
                double actualBonus = BuffBonus(before.BuffPowers, gained);
                bonus += actualBonus - Math.Min(40, buffs * 20);
                _credits.Add(new(before.Card, before.Round, before.BuffPowers, gained, actualBonus));
            }
            else bonus += growthBonus - Math.Min(40, buffs * 20);
            if (before.HandCosts != null)
                bonus += Math.Min(80d, (double)LocalResourceEffects.HandCostSavings(before.HandCosts, Costs(player), before.Played) * 12);
            var old = _bonuses.GetValueOrDefault(before.Card);
            _bonuses[before.Card] = (old.Total + bonus, old.Samples + 1);
            _pending = before;
        }
        catch { /* Unknown Mod observations do not alter legal branches or final scoring. */ }
    }

    public void ResetDecision() { _pending = null; _hints.Clear(); _hintModels.Clear(); _credits.Clear(); }
    public void SettleBuffs(Player player)
    {
        if (!trackDurations || _credits.Count == 0) return;
        try
        {
            int round = player.Creature.CombatState!.RoundNumber;
            var remaining = Buffs(player);
            foreach (var credit in _credits.Where(c => c.Round < round))
            {
                var retained = credit.Gained.Select(p => (p.Key, Amount: Math.Min(p.Value,
                    Math.Max(0, remaining.GetValueOrDefault(p.Key) - credit.Before.GetValueOrDefault(p.Key)))))
                    .Where(p => p.Amount > 0).ToDictionary(p => p.Key, p => p.Amount, PowerIdentity);
                var old = _bonuses.GetValueOrDefault(credit.Card);
                if (old.Samples > 0) _bonuses[credit.Card] = (old.Total + BuffBonus(credit.Before, retained) - credit.Bonus, old.Samples);
            }
            _credits.RemoveAll(c => c.Round < round);
        }
        catch { /* This corrects exploration hints only; native settlement remains authoritative. */ }
    }
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
