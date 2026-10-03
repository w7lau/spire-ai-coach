namespace SpireAiCoach.Core;

// A bound is about a completed victory from this frozen root, not another partial
// turn with more HP. Arbitrary Mod healing remains unknown, never assumed absent.
public sealed record LocalHealthEnvelope(string Root, int StartingHp, int Hp, int PotionsUsed,
    long? MaximumFurtherHpGain = null);

public sealed record LocalWinningBound(string Root, int StartingHp, int NetHpLoss, int PotionsUsed)
{
    public static LocalWinningBound? From(string root, LocalCandidate? candidate) =>
        candidate is { Won: true, Dead: false, StartingHp: not null, NetHpLoss: not null }
            ? new(root, candidate.StartingHp.Value, candidate.NetHpLoss.Value,
                candidate.Actions.Count(a => a.PotionSlot.HasValue)) : null;
}

public static class LocalHealthBound
{
    public static bool CannotImprove(LocalHealthEnvelope branch, LocalWinningBound? incumbent)
    {
        if (branch.PotionsUsed < 0 || branch.MaximumFurtherHpGain < 0)
            throw new ArgumentOutOfRangeException(nameof(branch));
        if (incumbent == null || branch.Root != incumbent.Root || branch.StartingHp != incumbent.StartingHp)
            return false;
        // Unknown recovery can restore all HP. Do not cap by today's MaxHp: a
        // generated card, relic or Mod can increase it during the continuation.
        long deficit = (long)branch.StartingHp - branch.Hp;
        long lowerLoss = branch.MaximumFurtherHpGain is { } gain && deficit > gain ? deficit - gain : 0;
        if (lowerLoss > incumbent.NetHpLoss) return true;
        // Used-potion count is a monotone expense in the current objective.
        // Strict inequality preserves HP, gold, max-HP and shorter-route ties.
        return lowerLoss == incumbent.NetHpLoss && branch.PotionsUsed > incumbent.PotionsUsed;
    }
}
