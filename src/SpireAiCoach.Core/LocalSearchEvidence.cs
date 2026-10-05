namespace SpireAiCoach.Core;

// Search completion and reachability are different claims. Pending is only the
// discovered frontier, never the size of the still unknown native search tree.
public sealed record LocalSearchEvidence(string Conclusion, string StopReason,
    bool ExactRootCovered, int? PendingPrefixes, int TerminalWins, int TerminalLosses,
    int RoundLimitHits, int ActionLimitHits, int TimeLimitHits, int PagedChoiceObservations,
    int PagedReplayBranches, int UnconfirmedEnds, int PrunedPrefixes, int SimulationErrors,
    bool AttemptLimitReached, bool TimeLimitReached, bool GoalStopped, bool ExcludedActions,
    bool IndependentVerification, int MaxRounds, int MaxActionsPerRound, bool IncludePotions, bool ManualStopped = false)
{
    public string Description => Conclusion switch
    {
        "verified-win" => "有解：获胜路线已通过独立复核。",
        "native-win" => "有解：原生模拟已获胜，尚未独立复核。",
        "covered-no-win" => $"当前模拟范围内无解：全部合法原生路线已覆盖（最多 {MaxRounds} 回合，每回合 {MaxActionsPerRound} 次操作，{(IncludePotions ? "含主动用药" : "不含主动用药")}）。",
        _ => "是否有解尚未确定：" + StopReason + "。"
    };

    // One fully covered, unpartitioned root suffices. Local subtree exhaustion,
    // a drained broker and missing legacy reports never establish this proof.
    public static LocalSearchEvidence Merge(LocalSearchRequest request, LocalSearchResult selected,
        IReadOnlyList<LocalSearchResult> runs, int? pending)
    {
        var reports = runs.Select(r => r.Evidence).Where(e => e != null).Cast<LocalSearchEvidence>().ToArray();
        int errors = reports.Sum(e => e.SimulationErrors) + runs.Count(r => r.Evidence == null ||
            r.Status is not ("done" or "searched") && r.Evidence.SimulationErrors == 0);
        bool excluded = request.ExcludedModels is { Length: > 0 } || reports.Any(e => e.ExcludedActions);
        bool manualStopped = selected.StoppedOnManualVictory || reports.Any(e => e.ManualStopped);
        bool covered = !manualStopped && reports.Any(e => e.ExactRootCovered && e.MaxRounds == request.MaxRounds &&
            e.MaxActionsPerRound == request.MaxDepth) && !excluded && errors == 0 && pending is null or 0;
        bool verified = !request.SkipFinalVerification && !selected.VerificationSkipped && selected.Status == "done" &&
            selected.Timing?.Verifications > 0 && LocalSearchPolicy.HasExecutionPoints(selected);
        var merged = new LocalSearchEvidence("unknown", "仍有未覆盖的合法路线", covered, pending,
            reports.Sum(e => e.TerminalWins), reports.Sum(e => e.TerminalLosses),
            reports.Sum(e => e.RoundLimitHits), reports.Sum(e => e.ActionLimitHits), reports.Sum(e => e.TimeLimitHits),
            reports.Sum(e => e.PagedChoiceObservations), reports.Sum(e => e.PagedReplayBranches),
            reports.Sum(e => e.UnconfirmedEnds), reports.Sum(e => e.PrunedPrefixes), errors,
            reports.Any(e => e.AttemptLimitReached), reports.Any(e => e.TimeLimitReached),
            !manualStopped && reports.Any(e => e.GoalStopped), excluded, verified, request.MaxRounds, request.MaxDepth, request.IncludePotions, manualStopped);
        return merged.Classify(selected.Best);
    }

    internal LocalSearchEvidence Classify(LocalCandidate? best)
    {
        string conclusion = best is { Won: true, Dead: false } ?
            IndependentVerification ? "verified-win" : "native-win" : ExactRootCovered ? "covered-no-win" : "unknown";
        var reasons = new List<string>();
        if (ManualStopped) reasons.Add("已手动停止搜索并采用胜利路线");
        if (GoalStopped) reasons.Add("已达到配置的提前返回条件");
        if (AttemptLimitReached) reasons.Add("达到每路试走次数上限");
        if (TimeLimitReached || TimeLimitHits > 0) reasons.Add("达到搜索时间预算");
        if (RoundLimitHits > 0) reasons.Add($"{RoundLimitHits} 条路线达到回合上限");
        if (ActionLimitHits > 0) reasons.Add($"{ActionLimitHits} 条路线达到单回合操作上限");
        if (SimulationErrors > 0) reasons.Add("存在未完成或失败的模拟");
        if (ExcludedActions) reasons.Add("部分动作未纳入搜索");
        if (UnconfirmedEnds > 0) reasons.Add("部分结束状态未确认胜负");
        if (PendingPrefixes > 0) reasons.Add($"仍有 {PendingPrefixes} 个已发现的待探索前缀，继续探索还可能新增");
        if (reasons.Count == 0 && !ExactRootCovered) reasons.Add("尚未完整覆盖全部合法路线与选牌组合");
        return this with { Conclusion = conclusion, StopReason = string.Join("；", reasons) };
    }

    public LocalSearchEvidence WithFailedPass(LocalCandidate? best, int failures) =>
        (this with { ExactRootCovered = false, SimulationErrors = SimulationErrors + Math.Max(1, failures) }).Classify(best);
}

// Counts consume already observed native outcomes; no extra game hook, replay,
// fingerprint capture or heuristic state equivalence is introduced for auditing.
public sealed class LocalSearchAudit
{
    public int TerminalWins { get; private set; }
    public int TerminalLosses { get; private set; }
    public int UnconfirmedEnds { get; private set; }
    public int RoundLimitHits { get; set; }
    public int ActionLimitHits { get; set; }
    public int TimeLimitHits { get; set; }
    public int PagedChoiceObservations { get; set; }
    public int PagedReplayBranches { get; set; }
    public int SimulationErrors { get; set; }

    public void Outcome(LocalCandidate candidate, bool ended)
    {
        if (candidate.Won && !candidate.Dead) TerminalWins++;
        else if (candidate.Dead) TerminalLosses++;
        else if (ended) UnconfirmedEnds++;
    }

    public LocalSearchEvidence Snapshot(LocalSearchRequest request, LocalCandidate? best,
        bool exactRootCovered, int? pending, int evaluated, bool timeReached, bool goalStopped,
        int pruned, bool verified = false, bool manualStopped = false)
    {
        // No certificate for partitioned roots, exclusions, unresolved stops or
        // optimization cuts. A completed rollout count alone is insufficient.
        bool covered = exactRootCovered && request.Partitions == 1 && request.Partition == 0 &&
            request.ExcludedModels is not { Length: > 0 } && SimulationErrors == 0 && UnconfirmedEnds == 0 &&
            RoundLimitHits == 0 && ActionLimitHits == 0 && TimeLimitHits == 0 && pruned == 0 && !goalStopped && !manualStopped;
        return new LocalSearchEvidence("unknown", "", covered, pending, TerminalWins, TerminalLosses,
            RoundLimitHits, ActionLimitHits, TimeLimitHits, PagedChoiceObservations, PagedReplayBranches,
            UnconfirmedEnds, pruned, SimulationErrors, evaluated >= request.MaxNodes, timeReached, goalStopped && !manualStopped,
            request.ExcludedModels is { Length: > 0 }, verified, request.MaxRounds, request.MaxDepth, request.IncludePotions, manualStopped).Classify(best);
    }
}
