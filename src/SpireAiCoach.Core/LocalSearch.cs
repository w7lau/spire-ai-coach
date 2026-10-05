using System.Text.Json;

namespace SpireAiCoach.Core;

// Local IPC only: no provider calls or credentials are part of this protocol.
public sealed record LocalSearchRequest(string Id, string SnapshotId, byte[] Replay,
    string NativeHash, uint ModelHash, string[] LoadedMods, bool ContinueOptimization,
    int Partition = 0, int Partitions = 2, int MaxNodes = LocalCalculation.AttemptsPerWorker, int MaxDepth = 24,
    int BudgetSeconds = 60, string? DebugEncounter = null,
    IReadOnlyDictionary<uint, string>? TargetLabels = null, int MaxRounds = 64,
    int Workers = 0, bool IncludePotions = false, LocalHistoryStamp? History = null, string[]? ExcludedModels = null,
    LocalAction[]? InitialPlan = null, int SimulationSpeed = 8, bool DeferVerification = false,
    LocalCandidate? VerifyCandidate = null, long TimelineOrigin = 0, LocalTrace? InitialTrace = null,
    bool FastCardPresentation = true, bool FastNativeWaits = true, bool ShareSearchWork = true,
    bool FastStateSettling = true, bool FastAssetCollection = false, bool StrategicRollouts = true,
    bool ExperimentalNativeData = false, byte[]? RecordedReplayProbe = null, bool DataOnlyCombat = true,
    bool DataOnlyRun = true, bool NumericalExecution = true, bool StopOnZeroLoss = true, bool TrimWorkerOverhead = true,
    LocalSearchOrder SearchOrder = LocalSearchOrder.MonteCarlo, bool FastVerification = true,
    bool SkipFinalVerification = false, bool AdaptiveWorkers = true, bool CorrelatedRollouts = false,
    bool LeanSearchChecksums = true, string? TurnWorkPipe = null, bool ProbeChecksumListener = false,
    int? TargetVictoryRounds = null, int? TargetPotionUses = null, bool RequireKnownZeroEnemyDamage = false,
    bool EfficientTactics = true, bool LearnBuffDuration = true, bool GuideWinningRoutes = true,
    bool OwnedWinningFocus = true, string? SearchWorkPipe = null,
    bool ReuseDecisionFingerprint = true, bool AsyncProgressOutput = true, bool MemorySearchWork = true,
    bool MemoryProgress = true, string? ProgressPipe = null, bool ReuseFingerprintBuffer = true,
    LocalCardGoals? CardGoals = null, string? MinimumLossPipe = null, bool StopOnFirstWin = false,
    LocalEventEntry? EventEntry = null, bool ResumingFrontier = false, bool ReuseSnapshotMetadata = false,
    bool ReplayRootOnly = false);

// A stop belongs to one frozen request. Goal stops exclude verification;
// explicit caller cancellation also applies during verification or with goals off.
public sealed record LocalSearchStop(string Id, string SnapshotId, string NativeHash, bool Cancel = false,
    bool CardGoalsCompleted = false, bool UseWinningRoute = false)
{
    public bool Matches(LocalSearchRequest request) =>
        (Cancel || (UseWinningRoute || request.StopOnFirstWin || request.StopOnZeroLoss &&
            (request.CardGoals?.Enabled == true ? CardGoalsCompleted : !CardGoalsCompleted)) &&
            request.VerifyCandidate == null) &&
        Id == request.Id && SnapshotId == request.SnapshotId && NativeHash == request.NativeHash;
}

public sealed record LocalAction(int HandIndex, string ModelId, uint? TargetId,
    string CardName, string TargetName, string BeforeHash, int Round = 0,
    bool EndTurn = false, int Preference = 0, int? PotionSlot = null, LocalCardChoice[]? Choices = null,
    uint? CombatCardIndex = null);

// Index is relative to this exact ordered native offer, never to a display-name lookup.
public sealed record LocalCardChoice(string OfferHash, int Index, string ModelId, string Name,
    int[]? Indices = null, string Kind = "offer", int Preference = 0, bool CompleteOffer = true);

// Unknown/native-unrecorded HP changes must never establish an enemy-damage-free claim.
public sealed record LocalDamageSources(int Enemy, int Self, int Unknown, int Unattributed, bool AccountingMatches)
{
    public bool Complete => AccountingMatches && Unknown == 0 && Unattributed == 0;
}

public sealed record LocalCandidate(LocalAction[] Actions, int Hp, int HpLost, int EnemyHp,
    int Gold, int MaxHp, bool Won, bool Dead, bool RewardCoverageKnown,
    int Rounds = 0, string StopReason = "", LocalContinuationPoint[]? Continuation = null,
    LocalDecision[]? Decisions = null, int? StartingHp = null, bool ContinuationFromSearch = false,
    LocalDamageSources? DamageSources = null, LocalRolloutStyle RolloutStyle = LocalRolloutStyle.Balanced,
    int? InitialEnemyHp = null, double? EndTurnHpLossHint = null, LocalCardGoalOutcome? CardGoalOutcome = null,
    LocalHealthChanges? HealthChanges = null)
{
    // Gross HP costs remain useful diagnostics, but healing and victory hooks are part of the goal.
    public int? NetHpLoss => StartingHp.HasValue ? Math.Max(0, StartingHp.Value - Hp) : null;
    public int? HpChange => StartingHp.HasValue ? Hp - StartingHp.Value : null;
}

// Legal alternatives observed before a real native action. Search hints only, never instructions.
public sealed record LocalDecision(int BeforeStep, LocalAction[] Legal, LocalChoiceDecision[]? Choices = null,
    int? HpBefore = null, int? HpAfter = null,
    LocalGoalOpportunity? GoalsBefore = null, LocalGoalOpportunity? GoalsAfter = null);
public sealed record LocalChoiceDecision(int AtChoice, LocalCardChoice[] Legal);
// Bounded per-trial metrics survive truncation of detailed native event traces.
public sealed record LocalSearchTrial(int Worker, int Attempt, double FinishedMs, bool Won,
    int Hp, int? NetHpLoss, int Rounds, int PotionsUsed, bool Complete, bool? ClaimedPrefixMatched = null,
    int? GoalPlays = null, int? FinisherKills = null, int? HpChange = null);

// A peer's measured route is an exploration proposal. Keep diagnostic traces
// out of the scheduling protocol; it does not certify a result or an HP bound.
public sealed record LocalSearchSeed(string Id, string SnapshotId, string NativeHash, LocalCandidate Candidate);

public sealed record LocalSearchResult(string Id, string SnapshotId, string Status,
    string Message, int Evaluated, int Rejected, long ElapsedMs, LocalCandidate? Best,
    int Duplicates = 0, int BudgetPruned = 0, int Victories = 0, int Workers = 1,
    long WorkerMemoryBytes = 0, long SearchElapsedMs = 0, bool IncludePotions = false, LocalSearchTiming? Timing = null,
    LocalAction? BlockedAction = null, int MaxRounds = 64, LocalTrace? Trace = null, LocalWorkStats? Work = null,
    bool StoppedEarly = false, LocalTurnSearchStats? TurnSearch = null, bool VerificationSkipped = false,
    int RootBranches = 0, int WorkerLimit = 0, LocalSearchTrial[]? Trials = null,
    LocalHealthBoundStats? HealthBounds = null, LocalSimulationFailure? Failure = null,
    LocalSimulationFailure[]? RecoveredFailures = null, LocalCardGoals? CardGoals = null,
    LocalMinimumLossStatus? MinimumLoss = null, LocalSearchEvidence? Evidence = null,
    bool StoppedOnFirstWin = false, bool StoppedOnCardGoals = false, LocalSearchProgress? SearchProgress = null,
    bool StoppedOnMinimum = false, bool StoppedOnManualVictory = false);

public static class LocalSearchPolicy
{
    // Opt-out still needs complete first-pass native checkpoints. Legacy skipped
    // results cannot become executable merely by copying another plan's points.
    public static bool HasExecutionPoints(LocalSearchResult result)
    {
        if (result.Best is not { Dead: false, Actions.Length: > 0, Continuation: { } points } best ||
            points.Length != best.Actions.Length || result.VerificationSkipped && !best.ContinuationFromSearch) return false;
        for (int i = 0; i < points.Length; i++)
            if (points[i].ActionIndex != i || points[i].NativeHash != best.Actions[i].BeforeHash ||
                string.IsNullOrEmpty(points[i].NativeHash) || points[i].History == null ||
                string.IsNullOrEmpty(points[i].History.Hash) || points[i].History.Count < 0 ||
                i > 0 && points[i].History.Count <= points[i - 1].History.Count) return false;
        return true;
    }

    // Read the final native HP cap after victory hooks. Recovering only the
    // starting HP is not full health, including when a Mod raises the cap.
    public static bool FullHealthVictory(LocalCandidate? candidate) =>
        candidate is { Won: true, Dead: false, Hp: > 0, MaxHp: > 0 } && candidate.Hp == candidate.MaxHp;
    public static bool PrefersFullHealth(LocalSearchRequest request) =>
        request.StopOnZeroLoss && !request.StopOnFirstWin && request.CardGoals?.Enabled != true;

    public static bool CanStop(LocalCandidate? candidate, bool stopOnZeroLoss, int? targetRounds = null,
        int? targetPotions = null, bool requireKnownZeroEnemyDamage = false) =>
        stopOnZeroLoss && candidate is { NetHpLoss: 0 } && FullHealthVictory(candidate) &&
        (!targetRounds.HasValue || candidate.Rounds <= targetRounds.Value) &&
        (!targetPotions.HasValue || candidate.Actions.Count(a => a.PotionSlot.HasValue) <= targetPotions.Value) &&
        (!requireKnownZeroEnemyDamage || candidate.DamageSources is { Complete: true, Enemy: 0 });

    // First-win mode is an explicit product choice, independent of loss and
    // potion targets. Only a complete victory from this frozen root qualifies.
    public static bool WinningRouteFrom(LocalCandidate? candidate, LocalSearchRequest request) =>
        request.VerifyCandidate == null &&
        candidate is { Won: true, Dead: false, Hp: > 0, Actions.Length: > 0 } &&
        candidate.Actions[0].BeforeHash == request.NativeHash;
    public static bool CanStopAtFirstWin(LocalCandidate? candidate, LocalSearchRequest request) =>
        request.StopOnFirstWin && WinningRouteFrom(candidate, request);
    public static bool CanStopAfterVictory(LocalCandidate? candidate, LocalSearchRequest request,
        LocalMinimumLossCertificate? certificate = null) => request.VerifyCandidate == null &&
        (CanStop(candidate, request) || CanStopOnCardGoals(candidate, request) || CanStopAtMinimum(candidate, request, certificate));
    public static bool CanStopOnCardGoals(LocalCandidate? candidate, LocalSearchRequest request) =>
        request.StopOnZeroLoss && !request.StopOnFirstWin && request.VerifyCandidate == null &&
        request.CardGoals is { Enabled: true } goals &&
        candidate is { Won: true, Dead: false, Hp: > 0, Actions.Length: > 0,
            CardGoalOutcome: { ConsumableGoals: { } consumable } outcome } &&
        candidate.Actions[0].BeforeHash == request.NativeHash && consumable.Complete(goals, outcome) &&
        (goals.HpLossThreshold.HasValue ? goals.WithinThreshold(candidate) :
            candidate.NetHpLoss == 0 && FullHealthVictory(candidate)) &&
        (!request.TargetVictoryRounds.HasValue || candidate.Rounds <= request.TargetVictoryRounds.Value) &&
        (!request.TargetPotionUses.HasValue || candidate.Actions.Count(a => a.PotionSlot.HasValue) <= request.TargetPotionUses.Value) &&
        (!request.RequireKnownZeroEnemyDamage || candidate.DamageSources is { Complete: true, Enemy: 0 });
    public static bool HasSpecificGoal(LocalSearchRequest request) => !request.StopOnFirstWin &&
        (request.TargetVictoryRounds.HasValue || request.TargetPotionUses.HasValue || request.RequireKnownZeroEnemyDamage ||
            request.CardGoals?.Enabled == true);
    public static bool CanStop(LocalCandidate? candidate, LocalSearchRequest request) =>
        request.StopOnFirstWin ? CanStopAtFirstWin(candidate, request) :
        request.CardGoals?.Enabled != true && CanStop(candidate, request.StopOnZeroLoss,
            request.TargetVictoryRounds, request.TargetPotionUses, request.RequireKnownZeroEnemyDamage);
    public static bool CanStopAtMinimum(LocalCandidate? candidate, LocalSearchRequest request,
        LocalMinimumLossCertificate? certificate) => request.StopOnZeroLoss && !request.StopOnFirstWin && !HasSpecificGoal(request) &&
        candidate is { Won: true, Dead: false, Hp: > 0, StartingHp: not null, Actions.Length: > 0 } && certificate != null &&
        candidate.Actions[0].BeforeHash == request.NativeHash &&
        certificate.Scope == LocalMinimumLossProof.Scope(request) && certificate.StartingHp == candidate.StartingHp &&
        ReachesHealthProof(candidate, certificate) &&
        certificate.MinimumPotionsUsed == candidate.Actions.Count(a => a.PotionSlot.HasValue);
    public static bool RequiresMinimumConfirmation(LocalCandidate? candidate, LocalSearchRequest request,
        LocalMinimumLossCertificate? certificate) => !CanStop(candidate, request) && !CanStopOnCardGoals(candidate, request) &&
        CanStopAtMinimum(candidate, request, certificate);
    private static bool ReachesHealthProof(LocalCandidate candidate, LocalMinimumLossCertificate proof) =>
        proof.MinimumNetHpLoss == candidate.NetHpLoss &&
        (proof.MaximumFinalHp is { } maximum ? maximum == candidate.Hp : proof.MinimumNetHpLoss > 0);
    public static bool HasMinimumProof(LocalSearchResult result) => result.CardGoals?.Enabled != true &&
        result.Status == "done" && !result.StoppedOnFirstWin && !result.StoppedOnManualVictory &&
        result.Best is { Won: true, Dead: false, Hp: > 0, StartingHp: not null } best &&
        result.MinimumLoss is { Confirmed: true, Certificate: { } proof } &&
        proof.StartingHp == best.StartingHp && ReachesHealthProof(best, proof) &&
        proof.MinimumPotionsUsed == best.Actions.Count(a => a.PotionSlot.HasValue);
    public static bool MeetsGoal(LocalCandidate? candidate, LocalSearchRequest request) =>
        CanStop(candidate, true, request.TargetVictoryRounds, request.TargetPotionUses, request.RequireKnownZeroEnemyDamage);
    public static bool BetterForGoal(LocalCandidate candidate, LocalCandidate? prior, LocalSearchRequest request)
    {
        if (request.CardGoals?.Enabled != true && prior != null &&
            (PrefersFullHealth(request) || HasSpecificGoal(request)) && MeetsGoal(candidate, request) != MeetsGoal(prior, request))
            return MeetsGoal(candidate, request);
        return Better(candidate, prior, request.StopOnFirstWin ? null : request.CardGoals);
    }

    // An unfinished horizon supplies a search hint, never a predicted victory or
    // a dominance proof. Keep health needed to continue as well as kill progress.
    // The preview covers observed attacks/hand effects, not every possible Mod hook.
    public static double UnfinishedQuality(LocalCandidate candidate, int initialEnemyHp)
    {
        if (candidate.Dead) return 0;
        var risk = Math.Max(0, candidate.EndTurnHpLossHint ?? 0);
        var survival = Math.Clamp((candidate.Hp - risk) /
            Math.Max(1d, candidate.StartingHp ?? candidate.MaxHp), 0, 1);
        var progress = Math.Clamp(1d - (double)candidate.EnemyHp / Math.Max(1, initialEnemyHp), 0, 1);
        // Progress matters while healthy. Damage dealt cannot compensate for a
        // depleted ability to survive; a completed win still outranks all hints.
        return survival * (.75 + .25 * progress);
    }

    // Same root, completed native victory: net HP loss first, potions are a reserve resource.
    public static bool Better(LocalCandidate candidate, LocalCandidate? prior, LocalCardGoals? cardGoals = null)
    {
        if (prior == null) return true;
        if (candidate.Won != prior.Won) return candidate.Won;
        if (candidate.Dead != prior.Dead) return !candidate.Dead;
        if (candidate.Won && cardGoals?.Enabled == true)
        {
            bool eligible = cardGoals.WithinThreshold(candidate), priorEligible = cardGoals.WithinThreshold(prior);
            if (eligible != priorEligible) return eligible;
            if (eligible && cardGoals.Compare(candidate, prior) is var goalRank && goalRank != 0) return goalRank > 0;
        }
        if (!candidate.Won)
        {
            // Compare both hints against the same root. Legacy results without
            // root metadata use a common denominator rather than different scales.
            var initialEnemyHp = candidate.InitialEnemyHp ?? prior.InitialEnemyHp ??
                Math.Max(candidate.EnemyHp, prior.EnemyHp);
            var quality = UnfinishedQuality(candidate, initialEnemyHp);
            var priorQuality = UnfinishedQuality(prior, initialEnemyHp);
            if (quality != priorQuality) return quality > priorQuality;
        }
        // Completed native final HP includes damage, healing and victory hooks.
        // Do not flatten all gains to zero before comparing potion expense.
        if (candidate.Hp != prior.Hp) return candidate.Hp > prior.Hp;
        if (candidate.Won && cardGoals?.Enabled == true)
        {
            int goalRank = cardGoals.Compare(candidate, prior);
            if (goalRank != 0) return goalRank > 0;
        }
        var potions = candidate.Actions.Count(a => a.PotionSlot.HasValue);
        var priorPotions = prior.Actions.Count(a => a.PotionSlot.HasValue);
        if (potions != priorPotions) return potions < priorPotions;
        if (candidate.Hp != prior.Hp) return candidate.Hp > prior.Hp;
        if (candidate.MaxHp != prior.MaxHp) return candidate.MaxHp > prior.MaxHp;
        if (candidate.Gold != prior.Gold) return candidate.Gold > prior.Gold;
        if (candidate.EnemyHp != prior.EnemyHp) return candidate.EnemyHp < prior.EnemyHp;
        if (candidate.Rounds != prior.Rounds) return candidate.Rounds < prior.Rounds;
        return candidate.Actions.Length < prior.Actions.Length;
    }

    public static string Format(LocalSearchResult result)
    {
        if (result.Best is not { } best) return result.Evidence?.Description ?? result.Message;
        var lines = new List<string> { best.Won ? "本地整场战斗 · 已找到获胜路线" : "本地整场战斗 · 尚未找到获胜路线", result.Message,
            $"启用 {result.Workers} 路，评估 {result.Evaluated} 条路线，其中 {result.Victories} 条获胜，不支持 {result.Rejected}。",
            best.StartingHp is { } initial ?
                $"{(best.Won ? "预测战后生命" : "已模拟到的生命")} {initial} → {best.Hp}/{best.MaxHp}；生命净变化 {best.HpChange:+0;-0;0}{(best.Won ? "（包含战中、战后回血）" : "（战斗尚未完成）")}。" :
                $"{(best.Won ? "预测战后生命" : "已模拟到的生命")} {best.Hp}/{best.MaxHp}。",
            $"过程累计扣血 {best.HpLost}" + (best.HealthChanges is { } health ? $"，已恢复或增加生命 {health.HpGained}" :
                best.StartingHp is { } start ? $"，已恢复或增加生命 {Math.Max(0, best.Hp - start + best.HpLost)}" : "") +
                $"；敌人剩余生命合计 {best.EnemyHp}。" };
        if (result.Evidence is { } evidence)
        {
            lines.Insert(1, evidence.Description);
            lines.Add($"原生终局：获胜 {evidence.TerminalWins}，死亡 {evidence.TerminalLosses}；回合上限 {evidence.RoundLimitHits}，操作上限 {evidence.ActionLimitHits}，时间中断 {evidence.TimeLimitHits}。" +
                $"分页选牌观察 {evidence.PagedChoiceObservations} 次，重放中补交 {evidence.PagedReplayBranches} 个选牌前缀（提交数含去重前重复，不代表已完成搜索）。");
        }
        if (result.SearchProgress is { } saved)
            lines.Insert(1, $"{(saved.Resumed ? "已接着上次进度搜索" : "搜索进度已保留")} · 第 {saved.Batch} 批，累计评估 {saved.TotalEvaluated} 条路线、获胜 {saved.TotalVictories} 条。" +
                (saved.CanContinue ? $"剩余 {saved.Pending} 个待探索前缀，可点击「继续搜索」。" : "当前已发现的待探索队列为空。"));
        if (result.VerificationSkipped)
            lines.Insert(1, HasExecutionPoints(result) ?
                "已跳过最终复核：可点击执行方案，执行时逐步核对首次模拟记录，偏离即停止。" :
                "已跳过最终复核，但未取得完整逐步记录；目前仅供手动查看，暂不能自动执行。");
        if (result.StoppedOnManualVictory)
            lines.Add("已手动停止搜索并采用当前胜利路线；尚未证明最优。");
        if (result.StoppedOnFirstWin && best is { Won: true, Dead: false })
            lines.Add("已按「找到获胜路线即返回」停止搜索，未继续优化损失或用药。");
        if (HasMinimumProof(result)) lines.Add(MinimumProofDescription(best));
        if (best.DamageSources is { } damage)
            lines.Add($"伤害来源：敌方 {damage.Enemy}，自身 {damage.Self}，来源未明 {damage.Unknown + damage.Unattributed}" +
                (damage.AccountingMatches ? "。" : "（来源记录与扣血统计不一致）。"));
        if (best.HealthChanges is { } hpChanges)
        {
            if (hpChanges.MaxHpGained > 0 || hpChanges.MaxHpLost > 0)
                lines.Add($"生命上限变化：增加 {hpChanges.MaxHpGained}，减少 {hpChanges.MaxHpLost}。");
            if (!hpChanges.FullyObserved)
                lines.Add("部分生命变化绕过了事件接口，已核对最终生命；中途扣血与回血次数可能不完整。");
        }
        lines.AddRange(CardGoalAdvice(result));
        if (result.CardGoals?.Enabled != true && best.Won && best.NetHpLoss == 0 && !HasMinimumProof(result))
            lines.Add(FullHealthVictory(best) ?
                "已达到战后满血目标；其他收益和最短路线未证明最优。" :
                $"战后净损失为 0，但仍差 {Math.Max(0, best.MaxHp - best.Hp)} 点生命才满血；不满足满血提前返回条件。");
        if (!best.Won) lines.Add("以下仅为已模拟的部分路线，不代表能打赢本次战斗。停止原因：" + best.StopReason);
        if (best.Dead) lines.Add("注意：目前找到的路线仍会死亡，不能保证存活。");
        if (result.Work is { } work) lines.Add($"分支分工：领取 {work.Claimed} 项任务，合并 {work.DuplicateOffers} 次重复提交。");
        if (result.WorkerLimit > 0) lines.Add($"本次使用 {result.Workers} 路计算，并发上限 {result.WorkerLimit}。");
        if (result.Work is { CoveredJobs: > 0 } covered) lines.Add($"跳过 {covered.CoveredJobs} 项已经完成的相同路线任务。");
        if (result.TurnSearch is { } turns) lines.Add($"另探查 {turns.Probes} 个回合组合，按可证明的界限剪枝 {turns.BoundPruned} 次，跳过 {turns.CoveredPrefixes} 个已评估前缀，仍待搜索 {turns.Pending} 个操作前缀。");
        if (result.TurnSearch is { LossProofProbes: > 0 } proofTurns)
            lines.Add($"其中 {proofTurns.LossProofProbes} 次只检查局部损失下界，达到界限后即结束该次探查。");
        if (result.MinimumLoss is { Certificate.MaximumFinalHp: { } ceiling } && !HasMinimumProof(result))
            lines.Add($"已确认的战后生命上界 {ceiling}，当前候选生命 {best.Hp}；尚未达到已证明的最优。");
        else if (result.MinimumLoss is { Certificate: { } floor } && best.NetHpLoss is > 0 && !HasMinimumProof(result))
            lines.Add($"已确认的净损失下界 {floor.MinimumNetHpLoss}，当前候选净损失 {best.NetHpLoss}；尚未达到已证明的最优。");
        if (result.HealthBounds is { } bounds)
        {
            if (result.TurnSearch == null) lines.Add($"按可证明的界限剪枝 {bounds.Pruned} 次。");
            if (result.Victories == 0 && bounds.KnownRecoveryChecks == 0 && bounds.UnknownRecoveryChecks == 0 &&
                bounds.SharedIncumbentUpdates == 0) lines.Add("尚未取得完整获胜基准，暂未进行收益界限剪枝。");
            lines.Add($"回复上界可判定 {bounds.KnownRecoveryChecks} 次，未知 {bounds.UnknownRecoveryChecks} 次；采用共享获胜基准 {bounds.SharedIncumbentUpdates} 次。");
            if (bounds.UnknownRecoveryChecks > 0 && bounds.UnknownReason.Length > 0)
                lines.Add($"部分分支保留搜索：{bounds.UnknownReason}。");
        }
        lines.Add($"计算用时 {result.ElapsedMs / 1000d:F1} 秒。");
        int round = -1;
        for (var i = 0; i < best.Actions.Length; i++)
        {
            var action = best.Actions[i];
            if (action.Round != round) { round = action.Round; lines.Add($"—— 第 {round} 回合 ——"); }
            lines.Add($"{i + 1}. " + Describe(action));
        }
        if (best.Won) lines.Add("模拟结果：战斗获胜。");
        lines.Add($"最多规划 {result.MaxRounds} 轮；{(result.IncludePotions ? "已纳入主动使用药水，同等战后生命优先保留药水" : "未纳入主动使用药水（自动触发仍按游戏结算）")}；选牌组合受搜索预算限制。实际状态偏离时请重新计算。" );
        return string.Join("\n", lines);
    }

    // The default advice contains decisions and outcomes. Full diagnostics keep
    // search accounting and native identities in Format above.
    public static string FormatAdvice(LocalSearchResult result)
    {
        if (result.Best is not { } best) return result.Evidence?.Description ?? result.Message;
        var lines = new List<string>
        {
            best.Won ? $"预计获胜 · {best.Rounds} 回合" : "战斗尚未打完，以下是部分路线。",
            $"{(best.Won ? "预计战后生命" : "当前模拟生命")} {best.Hp}/{best.MaxHp}" +
                (best.HpChange is { } change ? $" · 生命净变化 {change:+0;-0;0}（含回血）" : "")
        };
        if (result.Evidence is { } evidence) lines.Insert(0, evidence.Description);
        if (best.Dead) lines.Add("注意：这条路线会死亡，不能保证存活。");
        if (result.StoppedOnManualVictory)
            lines.Add("已手动停止搜索并采用当前胜利路线；尚未证明最优。");
        if (result.StoppedOnFirstWin && best is { Won: true, Dead: false })
            lines.Add("已按「找到获胜路线即返回」停止搜索，未继续优化损失或用药。");
        if (HasMinimumProof(result)) lines.Add(MinimumProofDescription(best));
        if (!best.Won) lines.Add("尚未找到能打赢的路线，请继续优化或重新计算。");
        if (result.Status == "partial") lines.Add("部分搜索未完成，显示当前取得的路线。");
        lines.AddRange(CardGoalAdvice(result));
        if (!HasExecutionPoints(result)) lines.Add("这条路线暂不能自动执行，可手动参考。");
        var potions = best.Actions.Count(a => a.PotionSlot.HasValue);
        if (potions > 0) lines.Add($"这条路线会使用 {potions} 瓶药水。");
        int round = -1;
        for (int i = 0; i < best.Actions.Length; i++)
        {
            var action = best.Actions[i];
            if (action.Round != round) { round = action.Round; lines.Add($"—— 第 {round} 回合 ——"); }
            lines.Add($"{i + 1}. " + Describe(action, includeNativeTarget: false));
        }
        return string.Join("\n", lines);
    }

    public static string MinimumProofDescription(LocalCandidate best) => best.NetHpLoss > 0 ?
        $"已证明最低净损失为 {best.NetHpLoss}；同等战后生命下用药也已达下界。" :
        $"已证明最高战后生命为 {best.Hp}（生命净变化 {best.HpChange:+0;-0;0}）；同等战后生命下用药也已达下界。";

    private static IEnumerable<string> CardGoalAdvice(LocalSearchResult result)
    {
        if (result.StoppedOnFirstWin || result.CardGoals is not { Enabled: true } goals || result.Best is not { } best) yield break;
        var (plays, kills) = goals.Counts(best);
        if (!string.IsNullOrEmpty(goals.PlayModelId)) yield return $"「{goals.PlayName ?? goals.PlayModelId}」预计使用 {plays} 次。";
        if (!string.IsNullOrEmpty(goals.FinisherModelId)) yield return $"「{goals.FinisherName ?? goals.FinisherModelId}」预计补刀 {kills} 次。";
        if (goals.HpLossThreshold is { } limit)
            yield return goals.WithinThreshold(best) ? $"净损血小于 {limit}，已优先比较补刀及使用次数。" :
                $"尚未找到净损血小于 {limit} 的获胜路线，显示目前损血较少的方案。";
        yield return result.StoppedOnCardGoals ?
            "当前消耗出牌及补刀目标已完成，损血符合设置，已停止搜索；补刀目标以当前活敌人数为上限，未继续寻找回收、复制或额外生成后的次数。" :
            "可选目标尚未证明最优；仅无伤或耗尽目标牌不会提前返回。";
    }

    public static string Describe(LocalAction action) => Describe(action, includeNativeTarget: true);

    public static string Describe(LocalAction action, bool includeNativeTarget) => (action.EndTurn ? "结束回合，结算敌方行动。" :
        (action.PotionSlot is { } slot ? $"使用药水「{action.CardName}」（药水槽 {slot + 1}）" :
            $"打出「{action.CardName}」（当时手牌第 {action.HandIndex + 1} 张）") +
        (action.TargetId is null ? "。" : $" → {action.TargetName}" + (includeNativeTarget ? $"［目标 {action.TargetId}］。" : "。"))) +
        string.Concat((action.Choices ?? []).Select(c => c.Kind != "offer" ?
            (c.Indices is { Length: 0 } ? " 不选择卡牌。" : $" 选牌时选择「{c.Name}」。") :
            c.Index < 0 ? " 选牌时跳过。" : $" 选择第 {c.Index + 1} 张「{c.Name}」。"));

    public static int WorkerCount(int processors, ulong availableMemory, int configured) => configured > 0
        ? Math.Clamp(configured, 1, 16)
        : Math.Clamp(Math.Min(Math.Max(1, processors / 2),
            (int)Math.Max(1, ((double)availableMemory / (1024 * 1024 * 1024) - 3) / 1.5)), 1, 8);
}

// Exact action-prefix deduplication is safe even for unknown Mods. Native-state hash merging is not.
public sealed class LocalFrontier(int capacity)
{
    private readonly Dictionary<string, (LocalAction[] Actions, double Priority, long Order)> _pending = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private long _order;
    public int Duplicates { get; private set; }
    public int BudgetPruned { get; private set; }
    public int Count => _pending.Count;
    public static string Key(IEnumerable<LocalAction> actions) => string.Join("/", actions.Select(a =>
        $"{a.BeforeHash}:{a.Round}:{a.EndTurn}:{a.PotionSlot}:{a.HandIndex}:{a.ModelId}:{a.TargetId}:" +
        string.Join(",", (a.Choices ?? []).Select(c => $"{c.OfferHash}:{c.Index}:{c.ModelId}"))));
    public void MarkVisited(LocalAction[] actions) { var key = Key(actions); _seen.Add(key); _pending.Remove(key); }
    public void Add(LocalAction[] actions, double priority)
    {
        var key = Key(actions);
        if (!_seen.Add(key)) { Duplicates++; return; }
        _pending.Add(key, (actions, priority, _order++));
        if (_pending.Count <= capacity) return;
        var worst = _pending.OrderBy(p => p.Value.Priority).ThenByDescending(p => p.Value.Order).First();
        _pending.Remove(worst.Key); BudgetPruned++;
    }
    public bool TryTake(out LocalAction[] actions)
    {
        if (_pending.Count == 0) { actions = []; return false; }
        var best = _pending.OrderByDescending(p => p.Value.Priority).ThenBy(p => p.Value.Order).First();
        _pending.Remove(best.Key); actions = best.Value.Actions; return true;
    }
}

public static class LocalWire
{
    public static T Read<T>(string path)
    {
        using var lease = new FileLease(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Local worker returned empty data");
    }
    public static void Write<T>(string path, T value, Func<string, IDisposable?>? measure = null)
    {
        string json;
        using (measure?.Invoke("Serialize")) json = JsonSerializer.Serialize(value);
        WriteJson(path, json, measure);
    }

    public static void WriteJson(string path, string json, Func<string, IDisposable?>? measure = null)
    {
        FileLease lease;
        using (measure?.Invoke("Lock")) lease = new FileLease(path);
        using var ownership = lease;
        // Never reuse a just-retired staging path. On Windows it can still be held by
        // an external reader/scanner after replacement. ReplaceFile preserves an
        // already-published document atomically instead of deleting its directory entry.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (measure?.Invoke("Write")) File.WriteAllText(temporary, json);
            using (measure?.Invoke("Replace"))
            {
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Windows replacement can race with an open reader even with FileShare.Delete.
    // Serialize only access to this one IPC file; never hold the lock during game execution.
    private sealed class FileLease : IDisposable
    {
        private readonly Mutex _mutex;
        public FileLease(string path)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
            _mutex = new Mutex(false, "SpireAiCoach-ipc-" + Convert.ToHexString(hash));
            bool acquired;
            try { acquired = _mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { _mutex.Dispose(); throw new IOException("Local IPC file is busy"); }
        }
        public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
    }
}
