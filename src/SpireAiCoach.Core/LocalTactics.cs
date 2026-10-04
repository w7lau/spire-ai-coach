namespace SpireAiCoach.Core;

public enum LocalRolloutStyle { Balanced, Preparation, Attack, Correlated }

// Preview values are hints for exploration, never a substitute for native execution or a prune.
public sealed record LocalTacticalFeatures(double Damage = 0, double EnemyHp = 0, double EnemyBlock = 0,
    double TargetThreat = 0, double Incoming = 0, double CurrentBlock = 0, double Block = 0,
    double Hp = 80, double Strength = 0, double Vulnerable = 0, double Weak = 0,
    double EnergyGain = 0, double Draw = 0, double HpCost = 0,
    int FollowupAttacks = 0, double FollowupDamage = 0, int Upgrades = 0,
    bool EndTurn = false, bool Known = false, bool RetainsBlock = false, double HandEndHpLoss = 0,
    bool PersistentSetup = false, int EnergyCost = -1, double? ResourceCost = null);

public static class LocalTactics
{
    public static int Priority(LocalTacticalFeatures f, LocalRolloutStyle style = LocalRolloutStyle.Balanced)
    {
        if (!f.Known) return style == LocalRolloutStyle.Preparation ?
            f.PersistentSetup ? 100 : f.EnergyCost == 0 ? 3 : 0 : 0;
        var gap = Math.Max(0, f.Incoming - f.CurrentBlock);
        // A native HpLoss turn-end effect is a separate risk from incoming attacks.
        // Block must not make retaining the harmful card look harmless.
        var endLoss = gap + Math.Max(0, f.HandEndHpLoss);
        if (f.EndTurn) return endLoss >= f.Hp ? -40 : endLoss > 0 ? -15 : 0;
        var effective = Math.Max(0, f.Damage - f.EnemyBlock);
        double score = Math.Min(f.EnemyHp, effective) * 2;
        // A shield hit may enable another hit; never label it impossible or remove the branch.
        if (f.Damage > 0 && effective == 0 && f.FollowupDamage + f.Damage > f.EnemyBlock)
            score += Math.Min(f.Damage, f.EnemyBlock) * .5;
        if (f.EnemyHp > 0 && effective >= f.EnemyHp)
            score += 65 + Math.Min(20, f.TargetThreat * 2);
        var saved = Math.Min(gap, f.Block);
        score += saved * 4;
        if (saved > 0 && gap >= f.Hp && gap - saved < f.Hp) score += 60;
        if (f.RetainsBlock) score += Math.Max(0, f.Block - saved) * 2;
        else if (f.Block > 0 && gap == 0) score -= 12;
        if (f.Strength > 0 && f.FollowupAttacks > 0)
            score += 15 + Math.Min(40, f.Strength * f.FollowupAttacks * 4);
        if (f.Vulnerable > 0 && f.FollowupDamage > 0)
            score += 12 + Math.Min(35, f.FollowupDamage * 1.5);
        if (f.Weak > 0 && f.TargetThreat > 0) score += Math.Min(30, f.TargetThreat * 2);
        score += Math.Min(40, Math.Max(0, f.EnergyGain) * 12) + Math.Min(24, Math.Max(0, f.Draw) * 6);
        if (f.Upgrades > 0) score += 15 + Math.Min(30, f.Upgrades * 6);
        score -= Math.Max(0, f.HpCost) * 3;
        // This is an exploration hint for removing an in-hand effect, not damage
        // dealt by playing the card. The native completed fight still scores the line.
        score += Math.Max(0, f.HandEndHpLoss) * 4;
        // A separate preparation rollout preserves absolute combination
        // benefits. Applying single-action efficiency to every strategy hid
        // costly setup whose value is realized by later native plays.
        if (f.ResourceCost.HasValue && style != LocalRolloutStyle.Preparation)
        {
            // Benefit per actual currently spendable resource orders proposals.
            // A present lethal remains urgent; unknown branches stay available.
            bool lethal = f.EnemyHp > 0 && effective >= f.EnemyHp;
            if (!lethal && score > 0) score /= 1 + Math.Max(0, f.ResourceCost.Value);
            // Free block with no known payment is not an energy sacrifice. It
            // may trigger a native relic/Mod effect even when direct block is
            // currently unnecessary. The complete native outcome judges it.
            if (f.ResourceCost == 0 && f.Block > 0 && f.HpCost == 0 && score < 0) score = 0;
        }
        if (style == LocalRolloutStyle.Preparation)
        {
            // A second complete rollout strategy tests persistent setup early.
            // Native card type and observed resource previews supply hints only;
            // the actual full combat still decides whether the setup pays off.
            score += f.PersistentSetup ? 100 : 0;
            score += Math.Max(0, f.EnergyGain) * 8 + Math.Max(0, f.Draw) * 4 + f.Upgrades * 4;
            if (f.RetainsBlock) score += Math.Max(0, f.Block - saved) * 2;
            // Free native plays may trigger draw, cost reductions, exhaust or
            // other opaque hooks. A separate rollout tests that possibility
            // before ending; it does not certify their value or prune siblings.
            if (f.EnergyCost == 0) score = Math.Max(3, score);
        }
        else if (style == LocalRolloutStyle.Attack)
            score += Math.Min(f.EnemyHp, effective) * 2 + Math.Max(0, f.Strength) * f.FollowupAttacks * 2;
        // Preserve marginal gains in strong attacks. A low hard ceiling hid
        // the effect of block, strength and upgrades on a following attack.
        return (int)Math.Clamp(score, -40, 1_000_000);
    }
}
