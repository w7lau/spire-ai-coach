namespace SpireAiCoach.Core;

// A bound is about a completed victory from this frozen root, not another partial
// turn with more HP. Arbitrary Mod healing remains unknown, never assumed absent.
public sealed record LocalHealthEnvelope(string Root, int StartingHp, int Hp, int PotionsUsed,
    long? MaximumFurtherHpGain = null);

public sealed record LocalWinningBound(string Root, int StartingHp, int NetHpLoss, int PotionsUsed)
{
    public static LocalWinningBound? From(string root, LocalCandidate? candidate, bool requireFullHealthForZeroLoss = false) =>
        candidate is { Won: true, Dead: false, StartingHp: not null, NetHpLoss: not null }
            && (!requireFullHealthForZeroLoss || candidate.NetHpLoss != 0 || LocalSearchPolicy.FullHealthVictory(candidate))
            ? new(root, candidate.StartingHp.Value, candidate.NetHpLoss.Value,
                candidate.Actions.Count(a => a.PotionSlot.HasValue)) : null;
}

public static class LocalHealthBound
{
    public static long MinimumNetHpLoss(LocalHealthEnvelope branch)
    {
        if (branch.PotionsUsed < 0 || branch.MaximumFurtherHpGain < 0)
            throw new ArgumentOutOfRangeException(nameof(branch));
        long deficit = (long)branch.StartingHp - branch.Hp;
        return branch.MaximumFurtherHpGain is { } gain && deficit > gain ? deficit - gain : 0;
    }
    public static LocalWinningBound? Better(LocalWinningBound? current, LocalWinningBound? proposed)
    {
        if (proposed == null) return current;
        if (current == null) return proposed;
        if (current.Root != proposed.Root || current.StartingHp != proposed.StartingHp) return current;
        return proposed.NetHpLoss < current.NetHpLoss || proposed.NetHpLoss == current.NetHpLoss &&
            proposed.PotionsUsed < current.PotionsUsed ? proposed : current;
    }

    public static bool CannotImprove(LocalHealthEnvelope branch, LocalWinningBound? incumbent, LocalCardGoals? cardGoals = null)
    {
        if (branch.PotionsUsed < 0 || branch.MaximumFurtherHpGain < 0)
            throw new ArgumentOutOfRangeException(nameof(branch));
        if (incumbent == null || branch.Root != incumbent.Root || branch.StartingHp != incumbent.StartingHp)
            return false;
        // Unknown recovery can restore all HP. Do not cap by today's MaxHp: a
        // generated card, relic or Mod can increase it during the continuation.
        long lowerLoss = MinimumNetHpLoss(branch);
        if (cardGoals?.Enabled == true)
        {
            // No upper bound exists for plays/finishing blows, especially for Mods.
            // Preserve every branch able to reach the allowed HP tier, even with
            // more potion use or less HP than the current optional-goal incumbent.
            if (cardGoals.HpLossThreshold is { } limit && incumbent.NetHpLoss < limit)
                return lowerLoss >= limit;
            return lowerLoss > incumbent.NetHpLoss;
        }
        if (lowerLoss > incumbent.NetHpLoss) return true;
        // Used-potion count is a monotone expense in the current objective.
        // Strict inequality preserves HP, gold, max-HP and shorter-route ties.
        return lowerLoss == incumbent.NetHpLoss && branch.PotionsUsed > incumbent.PotionsUsed;
    }
}
