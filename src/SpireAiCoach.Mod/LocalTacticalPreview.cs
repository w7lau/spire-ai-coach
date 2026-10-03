using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

internal sealed class LocalTacticalPreview(Player player)
{
    private readonly Dictionary<Creature, double> _threats = new();
    private double _incoming;
    private double _handEndHpLoss;
    private bool _retainsBlock;
    private CardModel[] _playable = [];

    public static LocalTacticalPreview Capture(Player player, CardModel[] playable)
    {
        var result = new LocalTacticalPreview(player);
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

    public int Priority(CardModel card, Creature? target)
    {
        try
        {
            var vars = card.DynamicVars.Clone(card);
            card.UpdateDynamicVarPreview(CardPreviewMode.Normal, target, vars);
            double Value(string key) => vars.TryGetValue(key, out var v) ? Math.Max(0, (double)v.PreviewValue) : 0;
            bool enemy = target != null && _threats.ContainsKey(target);
            var others = _playable.Where(c => c != card).ToArray();
            double baseDamage(CardModel c) => c.DynamicVars.TryGetValue("Damage", out var d) ? Math.Max(0, (double)d.BaseValue) : 0;
            int attacks = others.Count(c => baseDamage(c) > 0);
            var energyAfter = Math.Max(0, player.PlayerCombatState!.Energy - card.EnergyCost.GetAmountToSpend());
            int affordable = Math.Min(attacks, energyAfter + others.Count(c => baseDamage(c) > 0 && c.EnergyCost.GetAmountToSpend() == 0));
            var followup = others.Select(baseDamage).OrderDescending().Take(affordable).Sum();
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
                HandEndHpLoss: handEndHpLoss));
        }
        catch { return 0; }
    }
}
