namespace SpireAiCoach.Core;

// Model identities include all copies and upgrade levels; labels are display only.
public sealed record LocalCardGoals(string? PlayModelId = null, string? FinisherModelId = null,
    int? HpLossThreshold = null, string? PlayName = null, string? FinisherName = null)
{
    public bool Enabled => !string.IsNullOrEmpty(PlayModelId) || !string.IsNullOrEmpty(FinisherModelId);

    public void Validate()
    {
        if (HpLossThreshold is < 1) throw new ArgumentOutOfRangeException(nameof(HpLossThreshold));
    }

    public bool WithinThreshold(LocalCandidate candidate) => Enabled && HpLossThreshold is { } limit &&
        candidate.Won && !candidate.Dead && candidate.NetHpLoss is { } loss && loss < limit;

    public (int Plays, int Kills) Counts(LocalCandidate candidate) => candidate.CardGoalOutcome is { } outcome
        ? (!string.IsNullOrEmpty(PlayModelId) && outcome.PlayModelId == PlayModelId ? outcome.Plays : 0,
            !string.IsNullOrEmpty(FinisherModelId) && outcome.FinisherModelId == FinisherModelId ? outcome.Kills : 0) : (0, 0);

    // Finishing blows are discrete opportunities; plays break ties between them.
    public int Compare(LocalCandidate a, LocalCandidate b)
    {
        var left = Counts(a); var right = Counts(b);
        int kills = left.Kills.CompareTo(right.Kills);
        return kills != 0 ? kills : left.Plays.CompareTo(right.Plays);
    }

    // Bounded exploration hint, not the final lexicographic ranking or a proof.
    public double Quality(LocalCandidate candidate)
    {
        var (plays, kills) = Counts(candidate);
        return (!string.IsNullOrEmpty(FinisherModelId) ? .75 * kills / (1d + kills) : 0) +
            (!string.IsNullOrEmpty(PlayModelId) ? .25 * plays / (4d + plays) : 0);
    }
}

// These are observed native events, not counts inferred from proposed actions.
// Steps permit subtraction when only the remaining suffix is displayed/reused.
public sealed record LocalCardGoalStep(int Plays = 0, int Kills = 0);

// A currently legal finite finishing opportunity, observed in this decision.
// Priority is a preview hint, never evidence of an actual kill.
public sealed record LocalFinisherHint(LocalAction Action, int Priority);

// Ordering hints only. Native effects decide kills, and every legal sibling
// remains available, including sacrificing a goal card to survive.
public static class LocalCardGoalTactics
{
    // Search around an achieved goal even when its current HP cost is too high.
    // This is an exploration seed only; final selection keeps the strict HP gate.
    public static bool BetterExplorationSeed(LocalCandidate candidate, LocalCandidate? incumbent,
        LocalCardGoals? goals)
    {
        if (!candidate.Won || candidate.Dead) return false;
        if (incumbent == null) return true;
        if (goals?.Enabled == true)
        {
            int progress = goals.Compare(candidate, incumbent);
            if (progress != 0) return progress > 0;
        }
        return LocalSearchPolicy.Better(candidate, incumbent, goals);
    }

    public static LocalAction? AdaptSoftContinuation(LocalAction? proposed,
        IReadOnlyList<LocalFinisherHint> hints, bool canAdapt)
    {
        if (!canAdapt || proposed == null || proposed.PotionSlot.HasValue) return proposed;
        var ready = hints.Where(h => h.Priority > 0)
            .OrderByDescending(h => h.Action.TargetId == proposed.TargetId)
            .ThenByDescending(h => h.Action.Preference).FirstOrDefault();
        if (ready != null) return ready.Action;
        // Defer this old proposal; the complete legal set still contains it.
        return hints.Any(h => h.Action == proposed && h.Priority < 0) ? null : proposed;
    }

    public static int FinisherPriority(bool consumable, double? damage, double hp, double block = 0,
        bool rewardEligible = true)
    {
        // A denied reward is still a spent finite opportunity, even on a lethal hit.
        // Callers keep this neutral when no eligible enemy remains in the decision.
        if (!rewardEligible) return consumable ? -180 : 0;
        if (!Known(damage, hp, block)) return 0;
        return damage >= hp + block ? 120 : consumable ? -180 : 0;
    }

    public static int SetupPriority(double? damage, double hp, double block, double? finisherDamage)
    {
        if (!Known(damage, hp, block) || !double.IsFinite(finisherDamage ?? double.NaN) || finisherDamage <= 0)
            return 0;
        // A damage-only setup leaves the enemy alive but in the reserved card's
        // preview range. This is not credit for a kill or a legality assertion.
        return damage > 0 && damage < hp + block && hp + block - damage <= finisherDamage ? 50 : 0;
    }

    private static bool Known(double? damage, double hp, double block) => damage is { } value &&
        double.IsFinite(value) && value >= 0 && double.IsFinite(hp) && hp > 0 &&
        double.IsFinite(block) && block >= 0;
}

// A product stopping target, not an upper bound on arbitrary Mod-generated,
// recovered or repeated plays. CompletedCopies counts distinct successful root
// copies. Victory closes their opportunities even when cleanup omits exhaustion.
// Finishers are capped by the current living enemies eligible for Fatal rewards;
// playing the other goal still needs every available root copy once.
public sealed record LocalConsumableGoalProgress(int Copies, int CompletedCopies, int ExhaustedCopies,
    int? LivingEnemies = null, bool BattleEnded = false)
{
    public int Target => LivingEnemies.HasValue ? Math.Min(Copies, LivingEnemies.Value) : Copies;
    public bool Complete => Copies > 0 && Target > 0 && CompletedCopies >= Target &&
        CompletedCopies <= Copies && ExhaustedCopies >= 0 && ExhaustedCopies <= Copies &&
        (BattleEnded || ExhaustedCopies >= CompletedCopies);
}
public sealed record LocalConsumableCardGoals(LocalConsumableGoalProgress? Play,
    LocalConsumableGoalProgress? Finisher, string Scope = "current-consumable-copies-v1")
{
    public bool Complete(LocalCardGoals goals, LocalCardGoalOutcome outcome) =>
        Scope == "current-consumable-copies-v1" && goals.Enabled &&
        outcome.PlayModelId == goals.PlayModelId && outcome.FinisherModelId == goals.FinisherModelId &&
        (string.IsNullOrEmpty(goals.PlayModelId) || Play is { Complete: true, LivingEnemies: null } && outcome.Plays >= Play.Target) &&
        (string.IsNullOrEmpty(goals.FinisherModelId) || Finisher is { Complete: true } && outcome.Kills >= Finisher.Target);
}
public sealed record LocalCardGoalOutcome(string? PlayModelId, string? FinisherModelId,
    int Plays, int Kills, LocalCardGoalStep[] Steps, LocalConsumableCardGoals? ConsumableGoals = null)
{
    public LocalCardGoalOutcome Remaining(int completed)
    {
        var steps = Steps.Skip(completed).ToArray();
        return this with { Plays = steps.Sum(s => s.Plays), Kills = steps.Sum(s => s.Kills), Steps = steps,
            ConsumableGoals = completed > 0 ? null : ConsumableGoals };
    }
}
