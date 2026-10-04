namespace SpireAiCoach.Core;

// Both product buttons use the same capture, native workers and resource limits.
public static class LocalCalculation
{
    public const int AttemptsPerWorker = 64;
    public const int SearchSeconds = 60;
    public const int Rounds = 64;
    public const int MaximumAttempts = 10_000;
    public const int MaximumRounds = 1_024;
    public const int MaximumSearchSeconds = 3_600;

    public static bool ValidLimits(int attempts, int rounds, int seconds) =>
        attempts is >= 1 and <= MaximumAttempts && rounds is >= 1 and <= MaximumRounds &&
        seconds is >= 1 and <= MaximumSearchSeconds;

    public static int WorkerTimeoutSeconds(int searchSeconds, int verificationActions = 0) =>
        Math.Max(180, Math.Max(searchSeconds + 120, verificationActions * 2 + 120));

    public static string Name(LocalSearchOrder order) => order switch
    {
        LocalSearchOrder.MonteCarlo => "本地整场计算",
        LocalSearchOrder.TurnFrontier => "新算法整场计算",
        _ => throw new ArgumentOutOfRangeException(nameof(order))
    };

    public static LocalSearchRequest Configure(LocalSearchRequest captured, LocalSearchOrder order,
        int workers, bool includePotions, bool stopOnZeroLoss, bool skipFinalVerification = false,
        int targetVictoryRounds = 0, int maxAttempts = AttemptsPerWorker, int maxRounds = Rounds,
        int searchSeconds = SearchSeconds)
    {
        _ = Name(order);
        if (!ValidLimits(maxAttempts, maxRounds, searchSeconds))
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "本地搜索上限超出允许范围。");
        return captured with
        {
            SearchOrder = order, Workers = Math.Clamp(workers, 0, 16), IncludePotions = includePotions,
            MaxNodes = maxAttempts, BudgetSeconds = searchSeconds, MaxRounds = maxRounds,
            StopOnZeroLoss = stopOnZeroLoss,
            SkipFinalVerification = skipFinalVerification,
            // Optional return target; never reduces the search horizon or budget.
            TargetVictoryRounds = targetVictoryRounds > 0 ? Math.Clamp(targetVictoryRounds, 1, maxRounds) : null,
            TargetPotionUses = targetVictoryRounds > 0 ? 0 : null,
            RequireKnownZeroEnemyDamage = targetVictoryRounds > 0,
            ShareSearchWork = captured.ShareSearchWork,
            // Turn-frontier continuations are generated from the current native state.
            InitialPlan = order == LocalSearchOrder.TurnFrontier ? null : captured.InitialPlan
        };
    }
}
