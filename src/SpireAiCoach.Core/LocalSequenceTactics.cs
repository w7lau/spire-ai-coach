namespace SpireAiCoach.Core;

// Resource-compatible preview combinations, not future legality or a damage bound.
public static class LocalSequenceTactics
{
    public static bool BetterLowDamageSeed(LocalCandidate candidate, LocalCandidate? incumbent)
    {
        if (!candidate.Won || candidate.Dead || candidate.CombatOutcome?.Won(candidate.Dead) != true ||
            candidate.HealthChanges is not { FullyObserved: true } health || health.HpLost != candidate.HpLost ||
            health.FinalHp != candidate.Hp) return false;
        return incumbent == null || candidate.HpLost < incumbent.HpLost ||
            candidate.HpLost == incumbent.HpLost && LocalSearchPolicy.Better(candidate, incumbent);
    }

    public static LocalAction? ReconsiderEnd(LocalAction? proposed, bool canAdapt, double gap,
        IEnumerable<(LocalAction Action, int Block)> legalDefense)
    {
        if (!canAdapt || proposed?.EndTurn != true || !double.IsFinite(gap) || gap <= 0) return proposed;
        // Only current native legal plays are supplied. Preview block orders a
        // new proposal; it does not prove safety or remove the old end-turn edge.
        return legalDefense.Where(x => !x.Action.EndTurn && x.Action.PotionSlot == null && x.Block > 0)
            .OrderByDescending(x => Math.Min(gap, x.Block)).ThenByDescending(x => x.Action.Preference)
            .Select(x => x.Action).FirstOrDefault() ?? proposed;
    }

    public static int Defense(IEnumerable<LocalFollowupCard> blocks, int energy, int stars, double gap)
    {
        if (!double.IsFinite(gap) || gap <= 0) return 0;
        return (int)Math.Min(Math.Ceiling(gap), LocalFollowup.Value(blocks, energy, stars));
    }

    public static int LostDefense(int available, int afterPlay) =>
        Math.Min(80, Math.Max(0, available - afterPlay) * 4);

    public static int DelayedDamage(double added, double existing, double hp)
    {
        if (!double.IsFinite(added) || !double.IsFinite(existing) || !double.IsFinite(hp) || added <= 0 || hp <= 0)
            return 0;
        // Two declining ticks only order exploration. Native timing, cleansing,
        // extra triggers and Mod changes still decide the complete outcome.
        static double Ticks(double n) => Math.Max(0, n) + Math.Max(0, n - 1);
        double marginal = Math.Max(0, Math.Min(hp, Ticks(existing + added)) - Math.Min(hp, Ticks(existing)));
        return (int)Math.Min(40, marginal * 2);
    }
}
