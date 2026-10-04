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
public sealed record LocalCardGoalOutcome(string? PlayModelId, string? FinisherModelId,
    int Plays, int Kills, LocalCardGoalStep[] Steps)
{
    public LocalCardGoalOutcome Remaining(int completed)
    {
        var steps = Steps.Skip(completed).ToArray();
        return this with { Plays = steps.Sum(s => s.Plays), Kills = steps.Sum(s => s.Kills), Steps = steps };
    }
}
