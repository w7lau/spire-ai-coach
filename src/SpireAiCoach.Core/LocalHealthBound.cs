namespace SpireAiCoach.Core;

// A bound is about a completed victory from this frozen root, not another partial
// turn with more HP. Arbitrary Mod healing remains unknown, never assumed absent.
public sealed record LocalHealthEnvelope(string Root, int StartingHp, int Hp, int PotionsUsed,
    long? MaximumFurtherHpGain = null);

public sealed record LocalWinningBound(string Root, int StartingHp, int NetHpLoss, int PotionsUsed,
    int? FinalHp = null)
{
    // A legacy zero-loss record does not say how far above the starting HP it healed.
    public int? ActualHp => FinalHp ?? (NetHpLoss > 0 ? StartingHp - NetHpLoss : null);
    public static LocalWinningBound? From(string root, LocalCandidate? candidate, bool requireFullHealthForZeroLoss = false) =>
        candidate is { Won: true, Dead: false, StartingHp: not null, NetHpLoss: not null }
            && (!requireFullHealthForZeroLoss || candidate.NetHpLoss != 0 || LocalSearchPolicy.FullHealthVictory(candidate))
            ? new(root, candidate.StartingHp.Value, candidate.NetHpLoss.Value,
                candidate.Actions.Count(a => a.PotionSlot.HasValue), candidate.Hp) : null;
}

public static class LocalHealthBound
{
    public static int? MaximumFinalHp(LocalHealthEnvelope branch)
    {
        if (branch.PotionsUsed < 0 || branch.Hp < 0 || branch.MaximumFurtherHpGain < 0)
            throw new ArgumentOutOfRangeException(nameof(branch));
        if (branch.MaximumFurtherHpGain is not { } gain) return null;
        // Native current HP is an int. Saturate the optimistic sum before adding;
        // overflow must never turn unlimited recovery into a low HP ceiling.
        return gain >= int.MaxValue - (long)branch.Hp ? int.MaxValue : branch.Hp + (int)gain;
    }
    public static int? MinimumHpLoss(LocalHealthEnvelope branch) =>
        MaximumFinalHp(branch) is { } hp ? branch.StartingHp - hp : null;

    public static long MinimumNetHpLoss(LocalHealthEnvelope branch)
    {
        return Math.Max(0, MinimumHpLoss(branch) ?? 0);
    }
    public static LocalWinningBound? Better(LocalWinningBound? current, LocalWinningBound? proposed)
    {
        if (proposed == null) return current;
        if (current == null) return proposed;
        if (current.Root != proposed.Root || current.StartingHp != proposed.StartingHp) return current;
        if (proposed.ActualHp is not { } proposedHp || current.ActualHp is not { } currentHp) return current;
        return proposedHp > currentHp || proposedHp == currentHp &&
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
        if (MaximumFinalHp(branch) is not { } maximumHp || incumbent.ActualHp is not { } winningHp) return false;
        if (maximumHp < winningHp) return true;
        // Potion expense breaks equal final-HP ties only. Unknown recovery may
        // still improve HP, even when a no-loss, no-potion victory already exists.
        return maximumHp == winningHp && branch.PotionsUsed > incumbent.PotionsUsed;
    }
}
