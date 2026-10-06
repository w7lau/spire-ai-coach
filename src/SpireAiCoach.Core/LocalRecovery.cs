namespace SpireAiCoach.Core;

public sealed record LocalRecoveryAllowance(long? MaximumFurtherHpGain, string Reason = "", bool ContentScoped = false,
    int? MaximumFinalHp = null)
{
    public static LocalRecoveryAllowance Sum(IEnumerable<LocalRecoveryAllowance> sources)
    {
        long total = 0;
        bool contentScoped = false;
        foreach (var source in sources)
        {
            contentScoped |= source.ContentScoped;
            if (source.MaximumFurtherHpGain is not { } gain) return source with { ContentScoped = contentScoped };
            if (gain < 0) throw new ArgumentOutOfRangeException(nameof(sources));
            if (gain > long.MaxValue - total) return new(null, "回复上界溢出");
            total += gain;
        }
        return new(total, ContentScoped: contentScoped);
    }
}

// A return objective inferred once from the frozen player's content. It is NOT
// a recovery ceiling and must never certify optimality or prune a branch.
public sealed record LocalHealthTarget(string Scope, int StartingHp, int TargetHp, bool FullHealth,
    string Basis = "当前内容", bool Uncertain = false);

// Kept separate from duplicate-history counters and available to both algorithms.
public sealed record LocalHealthBoundStats(int Pruned = 0, int KnownRecoveryChecks = 0,
    int UnknownRecoveryChecks = 0, int SharedIncumbentUpdates = 0, string UnknownReason = "",
    int TargetAnalyses = 0, double TargetAnalysisMs = 0, int MethodBodyReads = 0, bool ContentScoped = false,
    int LossProofProbes = 0);
