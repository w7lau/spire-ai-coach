using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

internal sealed class LocalTacticalPreview(Player player)
{
    private readonly Dictionary<Creature, double> _threats = new();
    private double _incoming;

    public static LocalTacticalPreview Capture(Player player)
    {
        var result = new LocalTacticalPreview(player);
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
        CurrentBlock: player.Creature.Block, Hp: player.Creature.CurrentHp, EndTurn: true, Known: true));

    public int Priority(CardModel card, Creature? target)
    {
        try
        {
            var vars = card.DynamicVars.Clone(card);
            card.UpdateDynamicVarPreview(CardPreviewMode.Normal, target, vars);
            double Value(string key) => vars.TryGetValue(key, out var v) ? Math.Max(0, (double)v.PreviewValue) : 0;
            bool enemy = target != null && _threats.ContainsKey(target);
            var hand = player.PlayerCombatState!.Hand.Cards;
            var others = hand.Where(c => c != card && c.CanPlay()).ToArray();
            double baseDamage(CardModel c) => c.DynamicVars.TryGetValue("Damage", out var d) ? Math.Max(0, (double)d.BaseValue) : 0;
            int attacks = others.Count(c => baseDamage(c) > 0);
            var energyAfter = Math.Max(0, player.PlayerCombatState.Energy - card.EnergyCost.GetAmountToSpend());
            int affordable = Math.Min(attacks, energyAfter + others.Count(c => baseDamage(c) > 0 && c.EnergyCost.GetAmountToSpend() == 0));
            var followup = others.Select(baseDamage).OrderDescending().Take(affordable).Sum();
            var repeat = Math.Max(1, Value("Repeat"));
            // Built-in Armaments+ directly upgrades the hand without a selector. Ordinary Armaments
            // still requires unsupported choices; do not pretend its choice has been simulated.
            var upgrades = card is Armaments && card.IsUpgraded ? others.Count(c => c.IsUpgradable) : 0;
            bool known = vars.Keys.Any(k => k is "Damage" or "CalculatedDamage" or "Block" or "CalculatedBlock" or
                "StrengthPower" or "VulnerablePower" or "WeakPower" or "Energy" or "HpLoss") || upgrades > 0;
            return LocalTactics.Priority(new(
                Damage: enemy ? Math.Max(Value("Damage"), Value("CalculatedDamage")) * repeat : 0,
                EnemyHp: enemy ? target!.CurrentHp : 0, EnemyBlock: enemy ? target!.Block : 0,
                TargetThreat: enemy ? _threats[target!] : 0, Incoming: _incoming,
                CurrentBlock: player.Creature.Block, Block: Math.Max(Value("Block"), Value("CalculatedBlock")),
                Hp: player.Creature.CurrentHp, Strength: Value("StrengthPower"),
                Vulnerable: enemy ? Value("VulnerablePower") : 0, Weak: enemy ? Value("WeakPower") : 0,
                // "Cards" can mean discard/exhaust/selection count, so it is not a generic draw hint.
                EnergyGain: Value("Energy"), HpCost: Value("HpLoss"),
                FollowupAttacks: affordable, FollowupDamage: followup, Upgrades: upgrades, Known: known));
        }
        catch { return 0; }
    }
}
