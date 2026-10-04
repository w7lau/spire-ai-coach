namespace SpireAiCoach.Core;

public sealed record LocalRecoveryAllowance(long? MaximumFurtherHpGain, string Reason = "")
{
    public static LocalRecoveryAllowance Sum(IEnumerable<LocalRecoveryAllowance> sources)
    {
        long total = 0;
        foreach (var source in sources)
        {
            if (source.MaximumFurtherHpGain is not { } gain) return source;
            if (gain < 0) throw new ArgumentOutOfRangeException(nameof(sources));
            if (gain > long.MaxValue - total) return new(null, "回复上界溢出");
            total += gain;
        }
        return new(total);
    }
}

// Kept separate from duplicate-history counters and available to both algorithms.
public sealed record LocalHealthBoundStats(int Pruned = 0, int KnownRecoveryChecks = 0,
    int UnknownRecoveryChecks = 0, int SharedIncumbentUpdates = 0, string UnknownReason = "");
