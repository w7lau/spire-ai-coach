namespace SpireAiCoach.Core;

// Both product buttons use the same capture, native workers and resource limits.
public static class LocalCalculation
{
    public const int AttemptsPerWorker = 64;
    public const int SearchSeconds = 60;
    public const int Rounds = 64;

    public static string Name(LocalSearchOrder order) => order switch
    {
        LocalSearchOrder.MonteCarlo => "本地整场计算",
        LocalSearchOrder.TurnFrontier => "新算法整场计算",
        _ => throw new ArgumentOutOfRangeException(nameof(order))
    };

    public static LocalSearchRequest Configure(LocalSearchRequest captured, LocalSearchOrder order,
        int workers, bool includePotions, bool stopOnZeroLoss, bool skipFinalVerification = false,
        int targetVictoryRounds = 0)
    {
        _ = Name(order);
        return captured with
        {
            SearchOrder = order, Workers = Math.Clamp(workers, 0, 16), IncludePotions = includePotions,
            MaxNodes = AttemptsPerWorker, BudgetSeconds = SearchSeconds, MaxRounds = Rounds,
            StopOnZeroLoss = stopOnZeroLoss,
            SkipFinalVerification = skipFinalVerification,
            // Optional return target; never reduces the search horizon or budget.
            TargetVictoryRounds = targetVictoryRounds > 0 ? Math.Clamp(targetVictoryRounds, 1, Rounds) : null,
            TargetPotionUses = targetVictoryRounds > 0 ? 0 : null,
            RequireKnownZeroEnemyDamage = targetVictoryRounds > 0,
            ShareSearchWork = captured.ShareSearchWork,
            // Turn-frontier continuations are generated from the current native state.
            InitialPlan = order == LocalSearchOrder.TurnFrontier ? null : captured.InitialPlan
        };
    }
}
