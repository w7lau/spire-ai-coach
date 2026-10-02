namespace SpireAiCoach.Core;

// Preview values are hints for exploration, never a substitute for native execution or a prune.
public sealed record LocalTacticalFeatures(double Damage = 0, double EnemyHp = 0, double EnemyBlock = 0,
    double TargetThreat = 0, double Incoming = 0, double CurrentBlock = 0, double Block = 0,
    double Hp = 80, double Strength = 0, double Vulnerable = 0, double Weak = 0,
    double EnergyGain = 0, double Draw = 0, double HpCost = 0,
    int FollowupAttacks = 0, double FollowupDamage = 0, int Upgrades = 0,
    bool EndTurn = false, bool Known = false);

public static class LocalTactics
{
    public static int Priority(LocalTacticalFeatures f)
    {
        if (!f.Known) return 0;
        var gap = Math.Max(0, f.Incoming - f.CurrentBlock);
        if (f.EndTurn) return gap >= f.Hp ? -40 : gap > 0 ? -15 : 0;
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
        if (f.Block > 0 && gap == 0) score -= 12;
        if (f.Strength > 0 && f.FollowupAttacks > 0)
            score += 15 + Math.Min(40, f.Strength * f.FollowupAttacks * 4);
        if (f.Vulnerable > 0 && f.FollowupDamage > 0)
            score += 12 + Math.Min(35, f.FollowupDamage * 1.5);
        if (f.Weak > 0 && f.TargetThreat > 0) score += Math.Min(30, f.TargetThreat * 2);
        score += Math.Min(40, Math.Max(0, f.EnergyGain) * 12) + Math.Min(24, Math.Max(0, f.Draw) * 6);
        if (f.Upgrades > 0) score += 15 + Math.Min(30, f.Upgrades * 6);
        score -= Math.Max(0, f.HpCost) * 3;
        return (int)Math.Clamp(score, -40, 100);
    }
}
