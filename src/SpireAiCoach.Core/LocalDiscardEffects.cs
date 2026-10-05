namespace SpireAiCoach.Core;

// Native previews / observed deltas guide ordering only. These are not rules,
// legality checks, state equivalence, or bounds that can remove a branch.
public readonly record struct LocalDiscardYield(double Damage = 0, double Block = 0,
    double Energy = 0, double Stars = 0, double HandGain = 0, double Healing = 0,
    double HpCost = 0, double Setup = 0, double HandEndHpLoss = 0, double KeepValue = 0)
{
    public LocalDiscardYield Add(LocalDiscardYield other) => new(Damage + other.Damage, Block + other.Block,
        Energy + other.Energy, Stars + other.Stars, HandGain + other.HandGain, Healing + other.Healing,
        HpCost + other.HpCost, Setup + other.Setup, HandEndHpLoss + other.HandEndHpLoss, KeepValue + other.KeepValue);
    public LocalDiscardYield Scale(double scale) => new(Damage * scale, Block * scale, Energy * scale,
        Stars * scale, HandGain * scale, Healing * scale, HpCost * scale, Setup * scale,
        HandEndHpLoss * scale, KeepValue * scale);
}

public static class LocalDiscardEffects
{
    public static int Value(IEnumerable<LocalDiscardYield> effects, LocalTacticalFeatures context,
        LocalRolloutStyle style = LocalRolloutStyle.Balanced)
    {
        var values = effects.ToArray();
        if (values.Length == 0) return 0;
        var total = values.Aggregate(default(LocalDiscardYield), (sum, effect) => sum.Add(effect));
        // All selected cards share the same threat / block gap. Scoring each
        // free block separately would reward protecting against the same hit twice.
        var score = LocalTactics.Priority(context with {
            Damage = Math.Max(0, total.Damage), EnemyBlock = 0,
            Block = Math.Max(0, total.Block), EnergyGain = total.Energy,
            Draw = total.HandGain, HpCost = Math.Max(0, total.HpCost),
            HandEndHpLoss = Math.Max(0, total.HandEndHpLoss),
            EnergyCost = 0, ResourceCost = 0, Known = true, EndTurn = false,
            PersistentSetup = false
        }, style);
        double extra = Math.Min(40, Math.Max(0, total.Stars) * 12) +
            Math.Min(40, Math.Max(0, total.Healing) * 3) + Math.Min(80, Math.Max(0, total.Setup)) -
            Math.Max(0, total.KeepValue) - Math.Max(0, -total.Energy) * 12 - Math.Max(0, -total.Stars) * 12;
        return (int)Math.Clamp(score + extra, -1_000_000, 1_000_000);
    }
}
