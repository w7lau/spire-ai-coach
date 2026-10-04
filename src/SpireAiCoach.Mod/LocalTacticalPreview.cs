using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using System.Runtime.CompilerServices;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

internal sealed class LocalTacticalPreview(Player player, bool efficient = false)
{
    private readonly Dictionary<Creature, double> _threats = new();
    private readonly Dictionary<(CardModel, Creature?), DynamicVarSet> _previews = new(new PreviewKeyComparer());
    private sealed class PreviewKeyComparer : IEqualityComparer<(CardModel, Creature?)>
    {
        public bool Equals((CardModel, Creature?) a, (CardModel, Creature?) b) =>
            ReferenceEquals(a.Item1, b.Item1) && ReferenceEquals(a.Item2, b.Item2);
        public int GetHashCode((CardModel, Creature?) key) => HashCode.Combine(
            RuntimeHelpers.GetHashCode(key.Item1), key.Item2 == null ? 0 : RuntimeHelpers.GetHashCode(key.Item2));
    }
    private double _incoming;
    private double _handEndHpLoss;
    private bool _retainsBlock;
    private CardModel[] _playable = [];

    public static LocalTacticalPreview Capture(Player player, CardModel[] playable, bool efficient = false)
    {
        var result = new LocalTacticalPreview(player, efficient);
        result._playable = playable;
        foreach (var card in player.PlayerCombatState!.Hand.Cards)
        {
            try
            {
                if (card.HasTurnEndInHandEffect && card.DynamicVars.TryGetValue("HpLoss", out var loss))
                    result._handEndHpLoss += Math.Max(0, (double)loss.PreviewValue);
            }
            catch { /* A Mod's unknown end effect remains for native execution to evaluate. */ }
        }
        try { result._retainsBlock = !Hook.ShouldClearBlock(player.Creature.CombatState!, player.Creature, out _); }
        catch { /* Unknown retention keeps the neutral prior; native execution remains authoritative. */ }
        foreach (var enemy in CombatManager.Instance.DebugOnlyGetState()!.Enemies)
        {
            if (!enemy.IsAlive) continue;
            try
            {
                var damage = enemy.Monster?.NextMove?.Intents.OfType<AttackIntent>()
                    .Sum(a => Math.Max(0, (double)a.GetSingleDamage([player.Creature], enemy)) * a.Repeats) ?? 0;
                result._threats[enemy] = damage; result._incoming += damage;
            }
            catch { /* Unknown threat does not remove any legal branch. */ }
        }
        return result;
    }

    public int EndTurnPriority => LocalTactics.Priority(new(Incoming: _incoming,
        CurrentBlock: player.Creature.Block, Hp: player.Creature.CurrentHp, EndTurn: true, Known: true,
        HandEndHpLoss: _handEndHpLoss));

    public double EndTurnHpLossHint => Math.Max(0, _incoming - player.Creature.Block) + _handEndHpLoss;

    private DynamicVarSet Preview(CardModel card, Creature? target)
    {
        if (_previews.TryGetValue((card, target), out var vars)) return vars;
        vars = card.DynamicVars.Clone(card);
        card.UpdateDynamicVarPreview(CardPreviewMode.Normal, target, vars);
        _previews.Add((card, target), vars);
        return vars;
    }

    private double PreviewDamage(CardModel card, Creature? target)
    {
        try
        {
            if (!card.DynamicVars.Keys.Any(k => k is "Damage" or "CalculatedDamage")) return 0;
            var vars = Preview(card, target);
            double Value(string key) => vars.TryGetValue(key, out var v) ? Math.Max(0, (double)v.PreviewValue) : 0;
            return Math.Max(Value("Damage"), Value("CalculatedDamage")) * Math.Max(1, Value("Repeat"));
        }
        catch { return 0; } // An opaque sibling preview must not erase known defense hints.
    }

    public int CardGoalPriority(CardModel card, Creature? target, LocalCardGoals goals)
    {
        int hint = card.Id.ToString() == goals.PlayModelId ? 30 : 0;
        if (target?.IsEnemy != true || PreviewDamage(card, target) < target.CurrentHp + target.Block) return hint;
        if (card.Id.ToString() == goals.FinisherModelId) return hint + 120;
        // Reserve a potential finishing blow only if that model is actually playable.
        // Unknown effects remain native candidates; this is never a legality filter.
        if (_playable.Any(c => c.Id.ToString() == goals.FinisherModelId)) hint -= 60;
        return hint;
    }

    public int Priority(CardModel card, Creature? target, LocalRolloutStyle style = LocalRolloutStyle.Balanced)
    {
        try
        {
            var vars = Preview(card, target);
            double Value(string key) => vars.TryGetValue(key, out var v) ? Math.Max(0, (double)v.PreviewValue) : 0;
            bool enemy = target != null && _threats.ContainsKey(target);
            var others = _playable.Where(c => c != card).ToArray();
            // Follow-up damage must use the same target's native modifiers too.
            // Raw card values overstate shield breaking under damage caps/reduction.
            // This decision-local cache never crosses a native action or state change.
            var attacks = others.Select(c => (Card: c, Damage: PreviewDamage(c, target))).Where(a => a.Damage > 0).ToArray();
            var energyAfter = Math.Max(0, player.PlayerCombatState!.Energy - card.EnergyCost.GetAmountToSpend());
            int affordable = Math.Min(attacks.Length, energyAfter + attacks.Count(a => a.Card.EnergyCost.GetAmountToSpend() == 0));
            var followup = attacks.Select(a => a.Damage).OrderDescending().Take(affordable).Sum();
            var repeat = Math.Max(1, Value("Repeat"));
            // HpLoss alone does not identify a payment made by OnPlay. The native
            // in-hand trigger flag places this preview in the turn-end risk instead.
            // No model/name allowlist or effect replacement is involved.
            var handEndHpLoss = card.HasTurnEndInHandEffect ? Value("HpLoss") : 0;
            bool known = vars.Keys.Any(k => k is "Damage" or "CalculatedDamage" or "Block" or "CalculatedBlock" or
                "StrengthPower" or "VulnerablePower" or "WeakPower" or "Energy" or "HpLoss");
            return LocalTactics.Priority(new(
                Damage: enemy ? Math.Max(Value("Damage"), Value("CalculatedDamage")) * repeat : 0,
                EnemyHp: enemy ? target!.CurrentHp : 0, EnemyBlock: enemy ? target!.Block : 0,
                TargetThreat: enemy ? _threats[target!] : 0, Incoming: _incoming,
                // Modal cards can carry variables for several inactive effects.
                // Use the native capability, not the existence of a Block variable.
                CurrentBlock: player.Creature.Block, Block: card.GainsBlock ? Math.Max(Value("Block"), Value("CalculatedBlock")) : 0,
                Hp: player.Creature.CurrentHp, Strength: Value("StrengthPower"),
                Vulnerable: enemy ? Value("VulnerablePower") : 0, Weak: enemy ? Value("WeakPower") : 0,
                // "Cards" can mean discard/exhaust/selection count, so it is not a generic draw hint.
                EnergyGain: Value("Energy"), HpCost: card.HasTurnEndInHandEffect ? 0 : Value("HpLoss"),
                FollowupAttacks: affordable, FollowupDamage: followup, Known: known, RetainsBlock: _retainsBlock,
                HandEndHpLoss: handEndHpLoss, PersistentSetup: card.Type == CardType.Power,
                EnergyCost: card.EnergyCost.GetAmountToSpend(),
                ResourceCost: efficient ? Math.Max(0, card.EnergyCost.GetAmountToSpend()) +
                    Math.Max(0, card.HasStarCostX ? player.PlayerCombatState!.Stars : card.GetStarCostWithModifiers()) : null), style);
        }
        catch { return 0; }
    }
}
