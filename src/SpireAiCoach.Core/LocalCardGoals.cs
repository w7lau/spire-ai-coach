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

// A product stopping target, not an upper bound on arbitrary Mod-generated,
// recovered or repeated plays. CompletedCopies counts distinct successful root
// copies. Victory closes their opportunities even when cleanup omits exhaustion.
// Finishers are capped by the current living enemies;
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
