using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

public sealed record LocalPoolResources(int Ready, int Preparing, int Starts, string LastChange);

public sealed class LocalWorkerPool(string directory) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Worker[] _workers = Enumerable.Range(0, 16).Select(_ => new Worker()).ToArray();
    private bool _disposed;
    private LocalTrace? _lastPreparation;
    private readonly object _resumeGate = new();
    private LocalSearchSession? _pausedSearch;
    private long _resumeGeneration;
    private readonly LocalReplayFailureMemo _replayFailures = new();

    public bool CanResume(LocalSearchRequest request)
    { lock (_resumeGate) return _pausedSearch is { Pending: > 0 } saved && saved.Matches(request); }

    public void DiscardSearch()
    {
        LocalSearchSession? old;
        lock (_resumeGate) { _resumeGeneration++; old = _pausedSearch; _pausedSearch = null; }
        old?.Dispose();
    }

    private LocalSearchResult WithProgress(LocalSearchResult result)
    {
        lock (_resumeGate) return _pausedSearch is { } saved && saved.LastId == result.Id ?
            result with { SearchProgress = saved.Progress(saved.Batches > 1, result.Evaluated) } : result;
    }

    public LocalPoolResources Resources()
    {
        var states = _workers.Select(w => w.ResourceState()).ToArray();
        return new(states.Count(w => w.Ready), states.Count(w => w.Preparing), states.Sum(w => w.Starts),
            states.OrderByDescending(w => w.Changed).First().Detail);
    }

    private int Count(int configured, bool adaptive = true)
    {
        if (configured > 0) return LocalConcurrency.Limit(0, 0, configured);
        var memory = new MemoryStatus();
        if (!GlobalMemoryStatusEx(memory)) throw new IOException("Cannot determine available memory for local workers");
        ulong reusable = (ulong)_workers.Sum(w => w.MemoryBytes);
        return adaptive ? LocalConcurrency.Limit(Environment.ProcessorCount, memory.AvailablePhysical + reusable, configured)
            : LocalSearchPolicy.WorkerCount(Environment.ProcessorCount, memory.AvailablePhysical + reusable, configured);
    }

    public async Task Prepare(LocalInstallation installation, int configured, CancellationToken token)
    {
        var timeline = new LocalTimeline(capacity: 65536);
        Task[] preparation;
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Manual concurrency is already an explicit resource choice. Start
            // those preparations together; automatic mode still warms only one.
            preparation = Enumerable.Range(0, LocalConcurrency.PrewarmCount(configured))
                .Select(index => _workers[index].Ensure(directory, index, installation, token, timeline)).ToArray();
        }
        finally { _gate.Release(); }
        // Ensure owns and reuses a lane's in-flight preparation. A calculation
        // may join it immediately instead of queuing behind the entire warmup.
        try { await Task.WhenAll(preparation); }
        finally { _lastPreparation = timeline.Snapshot(); }
    }

    public async Task<LocalSearchResult> Analyze(LocalSearchRequest request, LocalInstallation installation,
        Action<string> progress, CancellationToken cancellation, Action<LocalProgress>? simulationProgress = null,
        LocalVictoryReturn? victoryReturn = null)
    {
        if (victoryReturn != null && !victoryReturn.Matches(request))
            throw new ArgumentException("The manual return belongs to another calculation", nameof(victoryReturn));
        var origin = new LocalTimeline(request.TimelineOrigin);
        request = request with { TimelineOrigin = origin.Origin };
        string installationScope = JsonSerializer.Serialize(installation);
        // Compatibility workers use another physical request ID under this same
        // frozen calculation, just like the existing progress projection.
        void ObserveWinner(LocalSearchResult result) => victoryReturn?.Observe(result with { Id = request.Id });
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (_replayFailures.Get(request, installationScope, DateTimeOffset.UtcNow) is { } known)
            {
                origin.Add(new(-1, "main", "replay_failure_reused", "相同冻结输入的起点核对已失败", origin.ElapsedMs, 0));
                var error = new CoachException("local_replay_mismatch",
                    "当前战斗起点已在两种执行方式下核对失败，已省去重复启动。战斗状态变化后可重新计算，也可使用 AI 分析。");
                error.Data["local_failures"] = known;
                error.Data["local_trace"] = origin.Snapshot();
                throw error;
            }
            return WithProgress(await AnalyzePass(request, installation, progress, cancellation, simulationProgress, victoryReturn, ObserveWinner));
        }
        catch (CoachException ex) when (victoryReturn?.Requested != true && request.DataOnlyCombat && ex.Category is "local_data_unavailable" or "local_failed")
        {
            // A Mod can depend on an actual UI node. Repeat through the regular native
            // execution instead of removing that card or weakening replay validation.
            // Final verification already retries the same candidate normally;
            // a verification failure must not restart all full-budget searches.
            origin.Import(ex.Data["local_trace"] as LocalTrace);
            var failures = ex.Data["local_failures"] as LocalSimulationFailure[] ?? [];
            if (failures.FirstOrDefault(f => f.Category == "local_event_entry") is { } entryFailure)
            {
                var error = new CoachException("local_event_entry", entryFailure.Message);
                foreach (var key in ex.Data.Keys) error.Data[key] = ex.Data[key];
                throw error;
            }
            bool validateRoot = LocalSearchRecovery.NeedsReplayValidation(failures);
            progress(validateRoot ? "正在用一个实例核对战斗起点…" : "首次计算未完成，正在重新计算…");
            origin.Add(new(-1, "main", "fallback", ex.Category + ": " + ex.Message, origin.ElapsedMs, 0));
            var regular = request with { Id = request.Id + "-regular", DataOnlyCombat = false, DataOnlyRun = false,
                ExperimentalNativeData = false, InitialTrace = origin.Snapshot() };
            LocalSearchResult result;
            try
            {
                result = await AnalyzePass(regular, installation with { MinimalWorkerBootstrap = false }, progress, cancellation,
                    // A compatibility pass restarts each worker's sequence; keep it above
                    // the first pass's search/refinement/verification offsets in the UI.
                    simulationProgress == null ? null : p => simulationProgress(p with { Id = request.Id, Sequence = p.Sequence + 4_000_000 }),
                    victoryReturn, ObserveWinner, validateRoot);
            }
            catch (CoachException failed)
            {
                var regularFailures = failed.Data["local_failures"] as LocalSimulationFailure[] ?? [];
                if (failed.Data["local_replay_probe"] is LocalSearchResult probe)
                    _replayFailures.Remember(request, installationScope, failures, probe, DateTimeOffset.UtcNow);
                failed.Data["local_failures"] = failures.Concat(regularFailures).ToArray();
                throw;
            }
            var evidence = result.Evidence?.WithFailedPass(result.Best, failures.Length);
            return WithProgress(result) with { Id = request.Id, Message = "常规执行完成。" + result.Message, Evidence = evidence,
                RecoveredFailures = failures.Concat(result.RecoveredFailures ?? []).ToArray() };
        }
        finally { victoryReturn?.CloseSearch(); }
    }

    private async Task<LocalSearchResult> AnalyzePass(LocalSearchRequest request, LocalInstallation installation,
        Action<string> progress, CancellationToken cancellation, Action<LocalProgress>? simulationProgress = null,
        LocalVictoryReturn? victoryReturn = null, Action<LocalSearchResult>? observeWinner = null,
        bool validateRoot = false)
    {
        var timeline = new LocalTimeline(request.TimelineOrigin, 65536);
        timeline.Import(request.InitialTrace);
        request = request with { TimelineOrigin = timeline.Origin, InitialTrace = null };
        double queueStart = timeline.ElapsedMs;
        using (timeline.Measure(-1, "main", "queue")) await _gate.WaitAsync(cancellation);
        LocalSearchSession? session = null;
        long sessionGeneration = 0;
        bool retainSession = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            LocalSearchSession? obsolete;
            lock (_resumeGate)
            {
                sessionGeneration = ++_resumeGeneration;
                session = request.ContinueOptimization && _pausedSearch is { Pending: > 0 } saved && saved.Matches(request)
                    ? saved : LocalSearchSession.Supported(request) ? new LocalSearchSession(request) : null;
                obsolete = ReferenceEquals(session, _pausedSearch) ? null : _pausedSearch;
                _pausedSearch = null;
            }
            obsolete?.Dispose();
            request = request with { ResumingFrontier = session?.Batches > 0 };
            if (request.ResumingFrontier) { request = request with { InitialPlan = null }; progress("继续搜索剩余分支…"); }
            int count = Count(request.Workers, request.AdaptiveWorkers);
            var boundPublishers = new Dictionary<string, LocalSharedHealthBound>();
            var boundPublishGate = new object();
            foreach (var idle in _workers.Skip(count)) idle.Stop("并发上限或可用内存减少", timeline, Array.IndexOf(_workers, idle));
            using var goalReached = new CancellationTokenSource();
            int goalWorker = -1;
            int minimumGoalReached = 0;
            int consumableGoalReached = 0;
            int healthTargetReached = 0;
            int manualVictoryReached = 0;
            LocalSearchResult? finishedWinner = null;
            int finishedWinnerWorker = -1;
            using var minimumLoss = request.StopOnZeroLoss && !request.StopOnFirstWin && !LocalSearchPolicy.HasSpecificGoal(request) &&
                request.ExcludedModels is not { Length: > 0 } ? new LocalMinimumLossBroker(request, count) : null;
            var rootBranches = new int[count];
            var starting = new int[count];
            bool shared = request.ShareSearchWork && (count > 1 || session != null) &&
                request.SearchOrder is LocalSearchOrder.MonteCarlo or LocalSearchOrder.TurnFrontier;
            using var turnWork = shared && request.SearchOrder == LocalSearchOrder.TurnFrontier ? new LocalTurnWork(request, count, session?.Turns) : null;
            using var searchWork = shared && request.SearchOrder == LocalSearchOrder.MonteCarlo && request.MemorySearchWork
                ? new LocalSearchWorkBroker(request, count, retainedQueue: session?.Work) : null;
            request = request with { TurnWorkPipe = turnWork?.PipeName, SearchWorkPipe = searchWork?.PipeName,
                MinimumLossPipe = minimumLoss?.PipeName };
            LocalSearchWork? schedulingWork = null;
            int launched = 0;
            progress("准备计算…");
            if (validateRoot)
            {
                using var probing = timeline.Measure(0, "compatibility", "root_probe", "先核对一个普通实例");
                var probe = await Run(_workers[0], 0, request with { Id = request.Id + "-root",
                    Partition = 0, Partitions = count, ReplayRootOnly = true, VerifyCandidate = null,
                    TurnWorkPipe = null, SearchWorkPipe = null, MinimumLossPipe = null });
                if (probe.Status != "restored")
                {
                    var failed = new CoachException(probe.Failure?.Category ?? "local_failed", probe.Message);
                    failed.Data["local_failures"] = probe.Failure == null ? Array.Empty<LocalSimulationFailure>() : new[] { probe.Failure };
                    failed.Data["local_replay_probe"] = probe;
                    throw failed;
                }
            }
            var results = (await LocalConcurrency.Run(count, LocalConcurrency.AdaptiveAdmission(request.Workers, request.AdaptiveWorkers), shared, Launch, Demand,
                () => goalReached.IsCancellationRequested || victoryReturn?.Requested == true,
                r => LocalSearchRecovery.AbortPass(request, r), cancellation, waitForRoot: true)).ToList();
            int used = results.Count;
            TryStopUsingFinishedWinner();
            bool manualVictory = Volatile.Read(ref manualVictoryReached) != 0;
            // The warmup gate does not wait for engine startup. Import its
            // completed startup spans now, clipped to this calculation only.
            timeline.Import(_lastPreparation, queueStart, timeline.ElapsedMs);
            cancellation.ThrowIfCancellationRequested();
            if (System.Environment.GetEnvironmentVariable("SPIRE_COACH_RECOVERY_AUDIT") == "1" &&
                results.Count == 1 && results[0].Status == "audited")
                throw new CoachException("local_audit_complete", "所属实例已完成只读回复检查；没有可执行方案。");
            if (request.DataOnlyCombat && (!goalReached.IsCancellationRequested || Volatile.Read(ref minimumGoalReached) != 0) &&
                LocalSearchRecovery.NeedsCompatibilityPass(request, results) &&
                !(session?.Baseline?.Best != null && results.Any(r => r.RootBranches > 0 && r.Failure == null)))
            {
                // Prewarming may have started lanes not yet admitted to this
                // invalid root. Release those owned preparations before the
                // single ordinary probe competes for their CPU and memory.
                foreach (var unused in _workers.Skip(used).Take(count - used))
                    unused.Stop("战斗起点未通过核对，省去未使用实例", timeline, Array.IndexOf(_workers, unused));
                await Task.WhenAll(_workers.Skip(used).Take(count - used).Select(w => w.DrainPreparation()));
                var failure = new CoachException("local_data_unavailable", string.Join("\n", results.Select(r => r.Message).Distinct()));
                failure.Data["local_failures"] = results.Where(r => r.Failure != null).Select(r => r.Failure!).ToArray();
                throw failure;
            }
            bool minimumWorkerStop = results.Any(r => !r.StoppedOnManualVictory && !r.StoppedOnHealthTarget && (r.StoppedOnMinimum || r.StoppedEarly && r.Best?.NetHpLoss is > 0 &&
                r.MinimumLoss?.Certificate?.MinimumNetHpLoss is > 0));
            if (!manualVictory && (Volatile.Read(ref minimumGoalReached) != 0 || minimumWorkerStop) &&
                minimumLoss?.Status is not { Confirmed: true, Certificate: not null })
                throw new CoachException("local_data_unavailable", "最低损失的证明未通过全部计算实例的收尾校验，不能按最优结果返回。");
            // Pending native selectors own callbacks. Retire their processes; never reset underneath them.
            // If every lane failed before producing a route, spend only the remaining search budget on
            // one fresh lane excluding the reported actions. This is an explicitly incomplete fallback.
            var blocked = results.Where(r => r.Status is "unsupported" or "partial" && r.BlockedAction is { EndTurn: false })
                .Select(r => r.BlockedAction!).DistinctBy(a => a.ModelId).ToArray();
            var remainingSeconds = request.BudgetSeconds - (int)Math.Ceiling(results.Max(r => r.ElapsedMs) / 1000d);
            if (!manualVictory && victoryReturn?.Requested != true && !results.Any(r => r.Status is "searched" or "done" && r.Best != null) && blocked.Length > 0 && remainingSeconds >= 5)
            {
                progress("部分动作无法完成，正在计算其他路线…");
                var fallback = request with { Partition = 0, Partitions = 1, BudgetSeconds = remainingSeconds, DeferVerification = true,
                    TurnWorkPipe = null, SearchWorkPipe = null, MinimumLossPipe = null,
                    ExcludedModels = (request.ExcludedModels ?? []).Concat(blocked.Select(a => a.ModelId)).Distinct().ToArray() };
                results.Add(await Task.Run(() => Run(_workers[0], 0, fallback), cancellation));
            }
            var valid = results.Where(r => r.Status is "searched" or "done" or "partial" && r.Best != null).ToArray();
            if (session?.Baseline is { Best: { } } previous && results.Any(r => r.RootBranches > 0 && r.Failure == null))
                valid = valid.Append(previous with { Id = request.Id, MinimumLoss = null, HealthBounds = null,
                    StoppedEarly = false, StoppedOnFirstWin = false, StoppedOnCardGoals = false, StoppedOnMinimum = false, StoppedOnManualVictory = false,
                    HealthTarget = null, StoppedOnHealthTarget = false }).ToArray();
            if (manualVictory)
                valid = valid.Where(r => LocalSearchPolicy.WinningRouteFrom(r.Best, request)).ToArray();
            if (Volatile.Read(ref consumableGoalReached) != 0)
                valid = valid.Where(r => LocalSearchPolicy.CanStopOnCardGoals(r.Best, request)).ToArray();
            if (Volatile.Read(ref healthTargetReached) != 0)
                valid = valid.Where(r => LocalSearchPolicy.CanStopAtHealthTarget(r.Best, request, r.HealthTarget)).ToArray();
            if (valid.Length == 0)
            {
                var failed = new CoachException("local_failed", string.Join("\n", results.Select(r => r.Message).Distinct()));
                failed.Data["local_failures"] = results.Where(r => r.Failure != null).Select(r => r.Failure!).ToArray();
                throw failed;
            }
            victoryReturn?.CloseSearch();
            TryStopUsingFinishedWinner();
            manualVictory = Volatile.Read(ref manualVictoryReached) != 0;
            if (manualVictory)
            {
                valid = valid.Where(r => LocalSearchPolicy.WinningRouteFrom(r.Best, request)).ToArray();
                if (valid.Length == 0) throw new CoachException("local_failed", "手动停止后没有通过收尾校验的胜利路线，未采用方案。");
            }
            // Every lane has now completed or acknowledged the goal stop. Only the
            // selected candidate is independently replayed by default. The explicit
            // comparison option uses the selected search's own native execution points.
            var verificationResults = new List<LocalSearchResult>();
            LocalSearchResult? selectedBest = null;
            if (request.SkipFinalVerification)
            {
                var proposed = valid.Aggregate((a, b) => LocalSearchPolicy.BetterForGoal(b.Best!, a.Best, request) ? b : a);
                selectedBest = proposed with { VerificationSkipped = true };
            }
            while (selectedBest == null && valid.Length > 0)
            {
                var proposed = valid.Aggregate((a, b) => LocalSearchPolicy.BetterForGoal(b.Best!, a.Best, request) ? b : a);
                int index = Math.Clamp(results.IndexOf(proposed), 0, used - 1);
                var verify = request with { Id = request.Id + "-verify-" + verificationResults.Count,
                    Partition = index, Partitions = count, DeferVerification = false, VerifyCandidate = proposed.Best,
                    TurnWorkPipe = null, SearchWorkPipe = null, MinimumLossPipe = null };
                progress("正在复核最终路线…");
                var check = await Task.Run(() => Run(_workers[index], index, verify), cancellation);
                verificationResults.Add(check);
                if (check.Status == "done" && check.Best?.Continuation?.Length == check.Best?.Actions.Length && check.Best != null)
                { selectedBest = check with { HealthTarget = proposed.HealthTarget }; break; }
                valid = valid.Where(r => !ReferenceEquals(r, proposed)).ToArray();
            }
            if (selectedBest == null) throw new CoachException("local_verify_failed",
                string.Join("\n", verificationResults.Select(r => r.Message).Distinct()));
            if (manualVictory && (!LocalSearchPolicy.WinningRouteFrom(selectedBest.Best, request) || !LocalSearchPolicy.HasExecutionPoints(selectedBest)))
                throw new CoachException("local_verify_failed", "手动选择的胜利路线未取得可靠的完整执行记录，不能采用。");
            if (!manualVictory && goalReached.IsCancellationRequested && !LocalSearchPolicy.CanStopAfterVictory(selectedBest.Best, request,
                minimumLoss?.Status is { Confirmed: true } proof ? proof.Certificate : null, selectedBest.HealthTarget))
                throw new CoachException("local_verify_failed", "达标候选未通过复核，不能按提前停止的结果返回。");
            var best = selectedBest;
            var allRuns = results.Concat(verificationResults).ToArray();
            LocalWorkStats? workStats = null;
            if (request.ShareSearchWork && request.SearchOrder == LocalSearchOrder.MonteCarlo && (count > 1 || searchWork != null))
            {
                using var scheduling = timeline.Measure(-1, "main", "schedule", "释放已结束的分支提案", depth: 1);
                using var work = searchWork == null ? new LocalSearchWork(Path.GetDirectoryName(_workers[goalWorker >= 0 ? goalWorker : 0].Root)!, request) : null;
                workStats = searchWork?.Stats ?? work!.Stats();
                try { work?.ReleasePlans(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { /* Completed native result remains valid if private diagnostic cleanup is busy. */ }
            }
            var evidence = LocalSearchEvidence.Merge(request, best with { StoppedOnManualVictory = manualVictory }, results,
                turnWork?.Pending ?? workStats?.Pending ?? (count == 1 ? best.TurnSearch?.Pending : null));
            var merged = best with { Evidence = evidence, Evaluated = results.Sum(r => r.Evaluated), Rejected = results.Sum(r => r.Rejected),
                Duplicates = results.Sum(r => r.Duplicates), BudgetPruned = results.Sum(r => r.BudgetPruned),
                Victories = results.Where(r => r.Best != null).Sum(r => r.Victories), Workers = used, WorkerLimit = count, RootBranches = Volatile.Read(ref rootBranches[0]),
                ElapsedMs = (long)timeline.ElapsedMs, Trace = timeline.Snapshot(), SearchElapsedMs = results.Take(used).Max(r => r.ElapsedMs) +
                    (results.Count > used ? results[^1].ElapsedMs : 0),
                WorkerMemoryBytes = (results.Count > used ? results.Skip(1) : results).Sum(r => r.WorkerMemoryBytes),
                IncludePotions = request.IncludePotions,
                VerificationSkipped = request.SkipFinalVerification,
                StoppedEarly = goalReached.IsCancellationRequested,
                StoppedOnFirstWin = !manualVictory && request.StopOnFirstWin && goalReached.IsCancellationRequested,
                StoppedOnCardGoals = !manualVictory && Volatile.Read(ref consumableGoalReached) != 0,
                StoppedOnMinimum = !manualVictory && Volatile.Read(ref minimumGoalReached) != 0,
                StoppedOnManualVictory = manualVictory,
                StoppedOnHealthTarget = !manualVictory && Volatile.Read(ref healthTargetReached) != 0,
                MinimumLoss = manualVictory || Volatile.Read(ref healthTargetReached) != 0 ? null : minimumLoss?.Status,
                Id = request.Id,
                Trials = results.SelectMany(r => r.Trials ?? []).OrderBy(t => t.FinishedMs).ToArray(),
                RecoveredFailures = results.Where(r => r.Status is "failed" or "unsupported" or "partial")
                    .Where(r => r.Failure != null).Select(r => r.Failure!)
                    .Concat(results.SelectMany(r => r.RecoveredFailures ?? [])).ToArray(),
                HealthBounds = new(results.Sum(r => r.HealthBounds?.Pruned ?? 0),
                    results.Sum(r => r.HealthBounds?.KnownRecoveryChecks ?? 0),
                    results.Sum(r => r.HealthBounds?.UnknownRecoveryChecks ?? 0),
                    results.Sum(r => r.HealthBounds?.SharedIncumbentUpdates ?? 0),
                    string.Join("；", results.Select(r => r.HealthBounds?.UnknownReason).Where(r => !string.IsNullOrEmpty(r)).Distinct()),
                    results.Sum(r => r.HealthBounds?.TargetAnalyses ?? 0), results.Sum(r => r.HealthBounds?.TargetAnalysisMs ?? 0),
                    results.Sum(r => r.HealthBounds?.MethodBodyReads ?? 0)),
                Work = workStats,
                TurnSearch = request.SearchOrder != LocalSearchOrder.TurnFrontier ? null : new(
                    results.Sum(r => r.TurnSearch?.Probes ?? 0), results.Sum(r => r.TurnSearch?.BoundPruned ?? 0),
                    turnWork?.Offered ?? results.Sum(r => r.TurnSearch?.Offered ?? 0),
                    turnWork?.DuplicateOffers ?? results.Sum(r => r.TurnSearch?.DuplicateOffers ?? 0),
                    turnWork?.Pending ?? results.Sum(r => r.TurnSearch?.Pending ?? 0), results.Sum(r => r.TurnSearch?.UnknownRecoveryChecks ?? 0),
                    results.Sum(r => r.TurnSearch?.CoveredPrefixes ?? 0),
                    turnWork?.CompletedHistories ?? results.Sum(r => r.TurnSearch?.CompletedHistories ?? 0),
                    turnWork?.RepeatedHistories ?? results.Sum(r => r.TurnSearch?.RepeatedHistories ?? 0),
                    turnWork?.ClaimedByRound ?? results.SelectMany(r => r.TurnSearch?.ClaimedByRound ?? new Dictionary<int, int>())
                        .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value)),
                    results.SelectMany(r => r.TurnSearch?.RolloutStyles ?? new Dictionary<string, int>())
                        .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value)),
                    results.SelectMany(r => r.TurnSearch?.Outcomes ?? []).OrderBy(o => o.CompletedMs).ToArray(),
                    results.Sum(r => r.TurnSearch?.LossProofProbes ?? 0)),
                Timing = new(allRuns.Sum(r => r.Timing?.RestoreMs ?? 0), allRuns.Sum(r => r.Timing?.ActionMs ?? 0),
                    allRuns.Sum(r => r.Timing?.DecisionMs ?? 0), allRuns.Sum(r => r.Timing?.VerificationMs ?? 0),
                    allRuns.Sum(r => r.Timing?.StartupMs ?? 0), allRuns.Sum(r => r.Timing?.Actions ?? 0), allRuns.Sum(r => r.Timing?.Restores ?? 0),
                    allRuns.Sum(r => r.Timing?.Verifications ?? 0)),
                Status = goalReached.IsCancellationRequested || results.All(r => r.Status is "searched" or "done") && verificationResults.All(r => r.Status == "done") ? "done" : "partial",
                Message = goalReached.IsCancellationRequested ? (manualVictory ?
                    "已手动停止后续搜索，采用当前胜利路线；尚未证明最优。" : request.StopOnFirstWin ?
                    "已找到获胜路线，已停止全部后续搜索；未继续优化损失或用药。" : Volatile.Read(ref consumableGoalReached) != 0 ?
                    "当前消耗出牌及补刀目标已完成，战斗获胜且损血符合设置，已停止全部后续搜索。" : Volatile.Read(ref healthTargetReached) != 0 ?
                    LocalSearchPolicy.HealthTargetDescription(best.HealthTarget!) : Volatile.Read(ref minimumGoalReached) != 0 ?
                    LocalSearchPolicy.MinimumProofDescription(best.Best!) + "已停止后续搜索。" :
                    "已找到战后满血获胜路线，已停止全部后续搜索。") +
                    (request.SkipFinalVerification ? "已跳过最终复核，执行时逐步核对模拟记录。" : "路线已通过复核。") :
                    "本地整场计算完成。" + (request.SkipFinalVerification ? "已跳过最终复核，执行时逐步核对模拟记录。" : "") +
                    (results.Any(r => r.Status is not ("searched" or "done")) || verificationResults.Any(r => r.Status != "done") ?
                        request.SkipFinalVerification ? "部分搜索未完成，显示已取得的模拟路线。" : "部分搜索未完成，显示已复核的可用路线。" : "") +
                    (results.Count > used ? "本次补充搜索未纳入：" + string.Join("、", blocked.Select(a => a.CardName)) + "。" : "") +
                    (request.CardGoals?.Enabled == true ?
                        "按可选出牌目标选路；预算内候选，未证明目标最优。" :
                        "按战后实际生命选路，同等生命优先保留药水；预算内候选，未证明全局最优。") };

            session?.Complete(merged); retainSession = true;
            return merged;

            Task<LocalSearchResult> Launch(int index, CancellationToken passFailure)
            {
                Volatile.Write(ref launched, index + 1);
                Volatile.Write(ref starting[index], 1);
                using (timeline.Measure(index, "main", "admit_worker", $"计算 {index + 1}；上限 {count}")) { }
                return Task.Run(async () =>
                {
                    try { return await Run(_workers[index], index, passFailure: passFailure); }
                    finally { Volatile.Write(ref starting[index], 0); }
                }, cancellation);
            }

            void TryStopUsingFinishedWinner()
            {
                if (goalReached.IsCancellationRequested) return;
                if (victoryReturn?.Requested == true)
                {
                    Volatile.Write(ref manualVictoryReached, 1);
                    victoryReturn.CloseSearch();
                    goalReached.Cancel();
                    progress("已手动停止后续搜索，正在确认胜利路线…");
                    return;
                }
                LocalSearchResult? winner; int index;
                lock (boundPublishGate) { winner = finishedWinner; index = finishedWinnerWorker; }
                if (winner == null || goalReached.IsCancellationRequested ||
                    !LocalSearchPolicy.CanStopAfterVictory(winner.Best, request, minimumLoss?.Status.Certificate, winner.HealthTarget) ||
                    Interlocked.CompareExchange(ref goalWorker, index, -1) != -1) return;
                Volatile.Write(ref consumableGoalReached, LocalSearchPolicy.CanStopOnCardGoals(winner.Best, request) ? 1 : 0);
                bool healthReached = LocalSearchPolicy.CanStopAtHealthTarget(winner.Best, request, winner.HealthTarget);
                Volatile.Write(ref healthTargetReached, healthReached ? 1 : 0);
                Volatile.Write(ref minimumGoalReached, !healthReached && LocalSearchPolicy.RequiresMinimumConfirmation(winner.Best, request, minimumLoss?.Status.Certificate) ? 1 : 0);
                victoryReturn?.CloseSearch();
                goalReached.Cancel();
                progress(request.StopOnFirstWin ? "已找到获胜路线，正在停止其余搜索并确认路线…" :
                    Volatile.Read(ref consumableGoalReached) != 0 ? "消耗目标已完成且获胜，正在停止其余搜索并确认路线…" :
                    healthReached ? "生命目标已达成且获胜，正在停止其余搜索并确认路线…" :
                    Volatile.Read(ref minimumGoalReached) != 0 ? $"已达到可证明的最高战后生命 {winner.Best!.Hp}，正在停止其余搜索并确认路线…" :
                    "已找到战后满血获胜路线，正在停止其余搜索并确认路线…");
            }

            LocalWorkerDemand Demand(int admitted)
            {
                int roots = Volatile.Read(ref rootBranches[0]);
                int pending = 0;
                if (turnWork != null) pending = turnWork.Pending;
                else if (searchWork != null) pending = searchWork.Stats.Pending;
                else if (shared && roots > 0 && _workers[0].Root.Length > 0)
                {
                    schedulingWork ??= new LocalSearchWork(Path.GetDirectoryName(_workers[0].Root)!, request);
                    pending = schedulingWork.Stats().Pending;
                }
                // Systematic workers all retain the root-derived fixed partition;
                // changing it mid-search could silently omit a worker's histories.
                int allowed = shared ? Count(request.Workers) : count;
                return new(pending, Enumerable.Range(0, admitted).Count(i => Volatile.Read(ref starting[i]) != 0), roots, allowed);
            }

            async Task<LocalSearchResult> Run(Worker worker, int index, LocalSearchRequest? fallback = null,
                CancellationToken passFailure = default)
            {
                var command = fallback ?? request with { Partition = index, Partitions = count, DeferVerification = true };
                bool verifying = command.VerifyCandidate != null;
                bool rootProbe = command.ReplayRootOnly;
                bool requestDispatched = false;
                LocalSearchResult? lastResult = null;
                LocalSearchResult Stopped() => (lastResult ?? new(command.Id, request.SnapshotId, "searched", "", 0, 0, 0, null)) with
                    { Status = "searched", Best = null, StoppedEarly = true, Message = "已停止其余搜索。" };
                try
                {
                    passFailure.ThrowIfCancellationRequested();
                    if (!verifying && goalReached.IsCancellationRequested) return Stopped();
                    simulationProgress?.Invoke(new(request.Id, request.SnapshotId, index, count, 0, 0, 0, request.MaxNodes, 0,
                        0, request.BudgetSeconds, "准备计算", null, []));
                    var preparation = Stopwatch.StartNew();
                    // Cancel this wait, not the worker's tracked preparation. A peer
                    // can reach its goal while an admitted instance is still booting.
                    using var preparing = CancellationTokenSource.CreateLinkedTokenSource(cancellation,
                        verifying ? CancellationToken.None : goalReached.Token, passFailure);
                    await worker.Ensure(directory, index, installation, preparing.Token, timeline);
                    preparation.Stop();
                    cancellation.ThrowIfCancellationRequested();
                    passFailure.ThrowIfCancellationRequested();
                    if (!verifying && goalReached.IsCancellationRequested) return Stopped();
                    progress("正在计算…");
                    File.Delete(Path.Combine(worker.Root, "stop-search.json"));
                    File.Delete(Path.Combine(worker.Root, "idle.json"));
                    File.Delete(Path.Combine(worker.Root, "result.json"));
                    File.Delete(Path.Combine(worker.Root, "progress.json"));
                    LocalProgressTransport? channel = null;
                    if (command.MemoryProgress && command.AsyncProgressOutput)
                    {
                        try { channel = new LocalProgressTransport(command); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                        { timeline.Add(new(index, verifying ? "verify" : "search", "progress_fallback", ex.Message, timeline.ElapsedMs, 0)); }
                    }
                    using var telemetry = channel;
                    command = command with { ProgressPipe = telemetry?.PipeName };
                    using (timeline.Measure(index, verifying ? "verify" : "search", "ipc", depth: 1))
                        LocalWire.Write(Path.Combine(worker.Root, "request.json"), command);
                    requestDispatched = true;
                    double dispatched = timeline.ElapsedMs;
                    var timer = Stopwatch.StartNew();
                    long seenSequence = 0;
                    Stopwatch? stopping = null;
                    int timeoutSeconds = LocalCalculation.WorkerTimeoutSeconds(command.BudgetSeconds,
                        command.VerifyCandidate?.Actions.Length ?? 0);
                    while (timer.Elapsed.TotalSeconds < timeoutSeconds)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        passFailure.ThrowIfCancellationRequested();
                        if (!verifying) TryStopUsingFinishedWinner();
                        if (!verifying && goalReached.IsCancellationRequested && stopping == null)
                        {
                            using var stopScope = timeline.Measure(index, "search", "stop_search", "停止其余搜索", depth: 1);
                            LocalWire.Write(Path.Combine(worker.Root, "stop-search.json"),
                                new LocalSearchStop(command.Id, command.SnapshotId, command.NativeHash,
                                    CardGoalsCompleted: Volatile.Read(ref consumableGoalReached) != 0,
                                    UseWinningRoute: Volatile.Read(ref manualVictoryReached) != 0));
                            stopping = Stopwatch.StartNew();
                        }
                        var preview = telemetry?.Latest;
                        var previewPath = Path.Combine(worker.Root, "progress.json");
                        if (File.Exists(previewPath))
                        {
                            var filePreview = LocalWire.Read<LocalProgress>(previewPath);
                            if (preview == null || filePreview.Sequence > preview.Sequence) preview = filePreview;
                        }
                        if (preview != null)
                        {
                            if (preview.Id == command.Id && preview.SnapshotId == request.SnapshotId && preview.Worker == index &&
                                preview.Workers == command.Partitions && preview.Sequence > seenSequence)
                            { seenSequence = preview.Sequence;
                                if (!verifying && preview.RootBranches > 0)
                                { Volatile.Write(ref rootBranches[index], preview.RootBranches); Volatile.Write(ref starting[index], 0); }
                                simulationProgress?.Invoke(preview with { Id = request.Id, Workers = count,
                                Sequence = preview.Sequence + (verifying ? 2_000_000 : fallback == null ? 0 : 1_000_000) }); }
                        }
                        var file = Path.Combine(worker.Root, "result.json");
                        if (File.Exists(file))
                        {
                            var result = LocalWire.Read<LocalSearchResult>(file);
                            if (result.Id == command.Id && result.SnapshotId == request.SnapshotId)
                            {
                                lastResult = result;
                                if (!verifying && result.RootBranches > 0)
                                { Volatile.Write(ref rootBranches[index], result.RootBranches); Volatile.Write(ref starting[index], 0); }
                                if (result.Status != "running")
                                {
                                    // Results precede native finally/retirement. Do not
                                    // reuse the process or close its broker until it is idle.
                                    if ((result.Status is "searched" or "done" || rootProbe && result.Status == "restored") &&
                                        !await worker.WaitIdle(command, timeline, index))
                                        throw new IOException("Worker did not finish request cleanup");
                                    cancellation.ThrowIfCancellationRequested();
                                    passFailure.ThrowIfCancellationRequested();
                                    if (result.Trace?.Spans is { Length: > 0 } spans)
                                    {
                                        double received = spans.Min(s => s.StartMs);
                                        if (received >= dispatched) timeline.Add(new(index, verifying ? "verify" : "search",
                                            "dispatch", "", dispatched, received - dispatched, Depth: 1));
                                        double finished = spans.Max(s => s.StartMs + s.DurationMs);
                                        if (timeline.ElapsedMs >= finished) timeline.Add(new(index, verifying ? "verify" : "search",
                                            "result_transfer", "写入结果与轮询等待", finished, timeline.ElapsedMs - finished, Depth: 1));
                                    }
                                    using var receiving = timeline.Measure(index, verifying ? "verify" : "search", "receive", depth: 1);
                                    timeline.Import(result.Trace);
                                    result = result with { Timing = (result.Timing ?? new()) with { StartupMs = preparation.ElapsedMilliseconds } };
                                    if (worker.Process?.HasExited == false)
                                    { worker.Process.Refresh(); result = result with { WorkerMemoryBytes = worker.Process.PrivateMemorySize64 }; }
                                    if (worker.GameErrors())
                                    {
                                        if (!verifying) minimumLoss?.RejectOwner(index);
                                        RememberFailure(result);
                                        worker.Stop("模拟报告运行错误", timeline, index);
                                        return result with { Status = "failed", Best = null, Message = "后台游戏报告运行错误，未采用该进程的结果。" };
                                    }
                                    if (result.Status is not ("done" or "searched") && !(rootProbe && result.Status == "restored"))
                                    {
                                        if (!verifying) minimumLoss?.RejectOwner(index);
                                        RememberFailure(result);
                                        worker.Stop("上次模拟未完成：" + result.Message, timeline, index);
                                    }
                                    if (!verifying && result.Status is "searched" or "done")
                                    {
                                        observeWinner?.Invoke(result);
                                        if (result.MinimumLoss?.InvalidReason.Length > 0) minimumLoss?.RejectOwner(index);
                                        else minimumLoss?.ConfirmOwner(index);
                                        // Publish only after complete native execution and the
                                        // parent's game-error gate. Never certify a running seed.
                                        lock (boundPublishGate)
                                        {
                                            string parent = Path.GetDirectoryName(worker.Root)!;
                                            string key = parent + "\n" + command.Id;
                                            if (!boundPublishers.TryGetValue(key, out var exchange))
                                                boundPublishers.Add(key, exchange = new(parent, command));
                                            exchange.PublishFinished(result);
                                            if (result.Best is { Won: true, Dead: false } candidate &&
                                                LocalSearchPolicy.BetterForGoal(candidate, finishedWinner?.Best, request))
                                            { finishedWinner = result; finishedWinnerWorker = index; }
                                        }
                                    }
                                    if (!verifying) TryStopUsingFinishedWinner();
                                    return result;
                                }
                                if (!verifying) observeWinner?.Invoke(result);
                                if (!goalReached.IsCancellationRequested) progress($"正在计算 · 已启用 {Volatile.Read(ref launched)}/{count} 路 · 当前实例已评估 {result.Evaluated} 条路线");
                            }
                        }
                        // Normally the native action settles and acknowledges within one poll.
                        // A stuck callback must not hold the goal route until the search budget.
                        if (stopping?.Elapsed.TotalSeconds >= 2)
                        { minimumLoss?.RejectOwner(index); worker.Stop("停止搜索后未能确认安全清理", timeline, index); return Stopped(); }
                        if (worker.Process?.HasExited != false)
                            throw new CoachException("local_exit", "本次计算意外中断，请重试。");
                        await Task.Delay(250, cancellation);
                    }
                    throw new CoachException("local_timeout", "计算超时，已停止；可以重试或使用 AI 分析。");
                }
                catch (OperationCanceledException) when (passFailure.IsCancellationRequested && !cancellation.IsCancellationRequested)
                {
                    using var stopping = timeline.Measure(index, "search", "stop_failed_pass", "同组数值计算失败，停止本路", depth: 1);
                    if (requestDispatched) await worker.CancelRequest(command, timeline, index);
                    // Unlike a user/goal stop, fallback changes bootstrap mode.
                    // Retire this owned preparation instead of letting it keep booting.
                    worker.Stop("同组数值计算失败，释放实例以切换执行方式", timeline, index);
                    await worker.DrainPreparation();
                    return Stopped() with { Message = "同组数值计算失败，本路已停止。" };
                }
                catch (OperationCanceledException) when (!verifying && goalReached.IsCancellationRequested && !cancellation.IsCancellationRequested)
                { if (requestDispatched) await worker.CancelRequest(command, timeline, index); return Stopped(); }
                catch (OperationCanceledException)
                { if (requestDispatched) await worker.CancelRequest(command, timeline, index); throw; }
                catch (Exception ex)
                {
                    if (!verifying) minimumLoss?.RejectOwner(index);
                    worker.Stop("模拟中断：" + ex.Message, timeline, index);
                    return new(request.Id, request.SnapshotId, "failed", ex is CoachException ? ex.Message :
                        $"本地进程准备失败（{ex.GetType().Name}）：{ex.Message}", 0, 0, 0, null,
                        Failure: LocalSimulationFailure.Capture(ex, index, verifying ? "verify" : "search"));
                }

                void RememberFailure(LocalSearchResult failed)
                {
                    if (!command.DataOnlyCombat) return;
                    LocalWire.Write(Path.Combine(worker.Root, "last-data-failure.json"), failed);
                    // Keep the failing frozen input, not a later request overwriting request.json.
                    LocalWire.Write(Path.Combine(worker.Root, "last-data-failure-request.json"), command);
                    var log = Path.Combine(worker.Root, "game.log");
                    if (File.Exists(log)) File.Copy(log, Path.Combine(worker.Root, "last-data-failure.log"), true);
                }
            }
        }
        catch (CoachException ex) { ex.Data["local_trace"] = timeline.Snapshot(); throw; }
        finally
        {
            if (session != null)
            {
                // All brokers have closed. Return incomplete claims before
                // computing resumability, including a batch with no unclaimed jobs.
                if (retainSession) session.Work?.Resume();
                lock (_resumeGate)
                    if (retainSession && sessionGeneration == _resumeGeneration)
                    { _pausedSearch = session; session = null; }
                session?.Dispose();
            }
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        DiscardSearch();
        foreach (var worker in _workers) worker.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatus
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatus>();
        public uint Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);

    internal sealed class Worker : IDisposable
    {
        private readonly object _lifecycle = new();
        private CancellationTokenSource? _generation;
        private Task? _preparation;
        private bool _disposed;
        public string Generation { get; private set; } = "";
        public string Root { get; private set; } = "";
        public Process? Process { get; private set; }
        public long MemoryBytes
        {
            get
            {
                var process = Process;
                try
                {
                    if (process?.HasExited != false) return 0;
                    // Process caches this snapshot. Refresh before estimating
                    // the memory already held by reusable workers.
                    process.Refresh();
                    return Math.Max(0, process.PrivateMemorySize64);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
            }
        }
        private FileStream? _lock;
        private string? _configuration;
        private long _logPosition;
        private int _starts;
        private long _changed;
        private string _detail = "尚未准备";
        private string _retirement = "实例已退出";

        internal (bool Ready, bool Preparing, int Starts, long Changed, string Detail) ResourceState()
        {
            lock (_lifecycle)
            {
                bool alive = Process?.HasExited == false && _generation != null;
                return (alive && _preparation?.IsCompletedSuccessfully == true,
                    _generation != null && _preparation is { IsCompleted: false }, _starts, _changed, _detail);
            }
        }

        public async Task Ensure(string directory, int index, LocalInstallation installation, CancellationToken token, LocalTimeline? timeline = null)
        {
            var signature = typeof(LocalWorkerPool).Assembly.ManifestModule.ModuleVersionId + "|" +
                installation.GameDirectory + "|" + Path.GetFullPath(directory) + "|" + string.Join("|", installation.ModDirectories) + "|" + installation.MinimalWorkerBootstrap + "|" + installation.LimitRuntimeThreads;
            var configuration = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..16];
            Task preparation;
            bool reuse;
            lock (_lifecycle)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                token.ThrowIfCancellationRequested();
                reuse = _configuration == configuration && _generation != null &&
                    _preparation is { IsFaulted: false, IsCanceled: false } &&
                    (!_preparation.IsCompletedSuccessfully || Process?.HasExited == false);
                if (!reuse)
                {
                    var reason = _configuration != null && _configuration != configuration ? "配置或运行模式改变" : _retirement;
                    StopLocked(reason);
                    _detail = _starts == 0 ? "首次准备" : "重新准备：" + reason;
                    _changed = LocalTimeline.Timestamp;
                    if (timeline != null) timeline.Add(new(index, "prepare", _starts == 0 ? "cold_start" : "rebuild",
                        _detail, timeline.ElapsedMs, 0));
                    var previous = _preparation;
                    var generation = new CancellationTokenSource();
                    var preparationToken = generation.Token;
                    _generation = generation;
                    Generation = Guid.NewGuid().ToString("N");
                    var identity = Generation;
                    _configuration = configuration;
                    _preparation = Task.Run(async () =>
                    {
                        // A retired preparation may still be unwinding file work.
                        // Drain it before touching this lane's installation again.
                        if (previous != null) try { await previous; } catch (Exception) { }
                        try { await Prepare(directory, index, installation, configuration, identity, preparationToken, timeline); }
                        catch
                        {
                            lock (_lifecycle) if (ReferenceEquals(_generation, generation)) StopLocked("准备失败");
                            throw;
                        }
                    });
                    // A cancelled caller can leave no waiter. Still observe faults;
                    // the preparation itself owns its failure cleanup.
                    _ = _preparation.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                preparation = _preparation!;
            }
            using var waiting = reuse ? timeline?.Measure(index, "prepare", preparation.IsCompletedSuccessfully ? "reuse" : "await_ready") : null;
            await preparation.WaitAsync(token);
            token.ThrowIfCancellationRequested();
        }

        private async Task Prepare(string directory, int index, LocalInstallation installation, string configuration,
            string identity, CancellationToken token, LocalTimeline? timeline)
        {
            token.ThrowIfCancellationRequested();
            using var preparation = timeline?.Measure(index, "prepare", "prepare");
            using var files = timeline?.Measure(index, "prepare", "files", depth: 1);
            _logPosition = 0;
            // NTFS hardlinks require one volume. Keep tiny launch trees beside the installation, never in it.
            var sharedRoot = Path.Combine(Directory.GetParent(installation.GameDirectory)!.FullName, ".spire-ai-coach-workers");
            Root = Path.GetFullPath(Path.Combine(sharedRoot, configuration, "worker-" + index));
            if (Root.StartsWith(Path.GetFullPath(installation.GameDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Worker must not be inside game installation");
            if (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any() && !File.Exists(Path.Combine(Root, ".coach-worker")))
                throw new IOException("Refusing an unowned worker directory");
            Directory.CreateDirectory(Root);
            lock (_lifecycle)
            {
                token.ThrowIfCancellationRequested();
                _lock = new FileStream(Path.Combine(Root, ".lock"), FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, FileShare.None);
            }
            File.WriteAllText(Path.Combine(Root, ".coach-worker"), "SpireAiCoach shared local worker v2");
            var game = Path.Combine(Root, "game");
            Directory.CreateDirectory(game);
            foreach (var source in Directory.EnumerateFiles(installation.GameDirectory))
                if (new[] { ".exe", ".dll", ".pck", ".json" }.Contains(Path.GetExtension(source).ToLowerInvariant()))
                    ShareFile(source, Path.Combine(game, Path.GetFileName(source)), token);
            foreach (var source in Directory.EnumerateDirectories(installation.GameDirectory, "data_sts2_*"))
                ShareTree(source, Path.Combine(game, Path.GetFileName(source)), token);
            for (var i = 0; i < installation.ModDirectories.Length; i++)
                CopyTree(installation.ModDirectories[i], Path.Combine(game, "mods", "loaded-" + i), token);
            if (installation.LimitRuntimeThreads)
                File.WriteAllText(Path.Combine(game, "override.cfg"), "[threading]\nworker_pool/max_threads=2\n");
            var roaming = Path.Combine(Root, "Roaming");
            var local = Path.Combine(Root, "Local");
            var settings = Path.Combine(roaming, "SlayTheSpire2", "default", "1");
            Directory.CreateDirectory(settings); Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(settings, "settings.save"), "{\"volume_master\":0,\"volume_bgm\":0,\"volume_sfx\":0,\"volume_ambience\":0,\"skip_intro_logo\":true,\"mod_settings\":{\"mods_enabled\":true,\"mod_list\":[]}}");
            // Read-only mods need not mark a run as gameplay-modded. The native
            // save manager selects the corresponding profile at runtime.
            foreach (var profile in new[] { Path.Combine(settings, "modded", "profile1"), Path.Combine(settings, "profile1") })
            {
                var saves = Path.Combine(profile, "saves"); Directory.CreateDirectory(saves);
                File.WriteAllText(Path.Combine(saves, "progress.save"), "{\"schema_version\":24,\"enable_ftues\":false,\"ftue_completed\":[\"combat_rules_ftue\"]}");
            }
            foreach (var name in new[] { "ready", "fatal.txt", "result.json", "request.json", "progress.json", "audio.json", "idle.json", "stop-search.json", "startup.json" }) File.Delete(Path.Combine(Root, name));
            var start = new ProcessStartInfo(Path.Combine(game, "SlayTheSpire2.exe"))
            { WorkingDirectory = game, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "--headless", "--audio-driver", "Dummy", "--disable-vsync", "--max-fps", "120", "--force-steam=off", "--log-file", Path.Combine(Root, "game.log") }) start.ArgumentList.Add(arg);
            start.Environment["APPDATA"] = roaming; start.Environment["LOCALAPPDATA"] = local;
            start.Environment["SPIRE_COACH_WORKER"] = Root;
            start.Environment["SPIRE_COACH_WORKER_GENERATION"] = identity;
            start.Environment["SPIRE_COACH_WORKER_INDEX"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (installation.LimitRuntimeThreads)
            {
                // Each lane executes game rules on one native main thread. Avoid
                // eight runtimes each allocating one GC heap/thread per CPU.
                // Keep normal GC and all logical worker tasks; bound parallelism.
                start.Environment["DOTNET_GCHeapCount"] = "2";
                start.Environment["DOTNET_GCNoAffinitize"] = "1";
            }
            LocalWorkerOwner.Attach(start);
            start.Environment["SPIRE_COACH_MINIMAL_BOOTSTRAP"] = installation.MinimalWorkerBootstrap ? "1" : "0";
            start.Environment.Remove("SPIRE_NATIVE_PROBE_ROOT");
            token.ThrowIfCancellationRequested();
            files?.Dispose();
            lock (_lifecycle)
            {
                // Dispose/Stop can race file preparation, but never a new launch.
                token.ThrowIfCancellationRequested();
                start.Environment["SPIRE_COACH_STARTUP_ORIGIN"] = LocalTimeline.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
                using (timeline?.Measure(index, "prepare", "launch", depth: 1)) Process = IsolatedProcess.Start(start);
                _starts++; _changed = LocalTimeline.Timestamp;
            }
            var timer = Stopwatch.StartNew();
            using var engine = timeline?.Measure(index, "prepare", "engine", depth: 1);
            while (!File.Exists(Path.Combine(Root, "ready")))
            {
                token.ThrowIfCancellationRequested();
                if (Process.HasExited || timer.Elapsed.TotalSeconds > 90) throw new IOException("Worker startup failed or timed out");
                await Task.Delay(250, token);
            }
            try
            {
                var report = LocalWire.Read<LocalStartupReport>(Path.Combine(Root, "startup.json"));
                if (report.Generation == identity && report.ModMvid == typeof(LocalWorkerPool).Assembly.ManifestModule.ModuleVersionId)
                    timeline?.Import(report.Trace);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { /* Optional startup diagnostics never invalidate a ready worker. */ }
        }

        public async Task<bool> WaitIdle(LocalSearchRequest command, LocalTimeline? timeline = null, int index = 0)
        {
            using var cleanup = timeline?.Measure(index, "main", "await_idle", depth: 1);
            return await LocalWorkerSession.WaitForIdle(Root, command, Generation, Alive, TimeSpan.FromSeconds(2));
        }

        private bool Alive()
        {
            lock (_lifecycle) return Process?.HasExited == false;
        }

        public async Task CancelRequest(LocalSearchRequest command, LocalTimeline? timeline = null, int index = 0)
        {
            try
            {
                using var stopping = timeline?.Measure(index, "main", "cancel_request", depth: 1);
                LocalWire.Write(Path.Combine(Root, "stop-search.json"),
                    new LocalSearchStop(command.Id, command.SnapshotId, command.NativeHash, Cancel: true));
                if (await WaitIdle(command, timeline, index) && !GameErrors() && !File.Exists(Path.Combine(Root, "fatal.txt"))) return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException) { }
            Stop("取消后未能确认安全清理", timeline, index);
        }

        public bool GameErrors()
        {
            var path = Path.Combine(Root, "game.log");
            using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            if (_logPosition > stream.Length) _logPosition = 0;
            stream.Position = _logPosition;
            using var reader = new StreamReader(stream);
            string? line;
            bool failed = false;
            while ((line = reader.ReadLine()) != null)
                failed |= line.Contains("[ERROR]", StringComparison.Ordinal) ||
                    line.StartsWith("System.", StringComparison.Ordinal) && line.Contains("Exception:", StringComparison.Ordinal) ||
                    line.Contains("ERROR: FATAL:", StringComparison.Ordinal);
            _logPosition = stream.Position;
            return failed;
        }

        private static void CopyFile(string source, string target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(source);
            var prior = new FileInfo(target);
            if (!prior.Exists || prior.Length != info.Length || prior.LastWriteTimeUtc != info.LastWriteTimeUtc)
                File.Copy(source, target, true); // Never hardlink a writable worker to the live installation.
        }
        private static void ShareFile(string source, string target, CancellationToken token) =>
            LocalWorkerFileSharing.Share(source, target, token);
        private static void ShareTree(string source, string target, CancellationToken token)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source)) ShareFile(file, Path.Combine(target, Path.GetFileName(file)), token);
            foreach (var child in Directory.EnumerateDirectories(source))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked game directories are not supported");
                ShareTree(child, Path.Combine(target, Path.GetFileName(child)), token);
            }
        }
        private static void CopyTree(string source, string target, CancellationToken token)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source)) CopyFile(file, Path.Combine(target, Path.GetFileName(file)), token);
            foreach (var child in Directory.EnumerateDirectories(source))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked Mod directories are not supported");
                CopyTree(child, Path.Combine(target, Path.GetFileName(child)), token);
            }
        }
        public void Stop(string reason = "释放计算资源", LocalTimeline? timeline = null, int index = 0)
        {
            lock (_lifecycle)
            {
                if (_generation != null && timeline != null)
                    timeline.Add(new(index, "prepare", "retire", reason, timeline.ElapsedMs, 0));
                StopLocked(reason);
            }
        }

        public void Dispose()
        {
            lock (_lifecycle) { _disposed = true; StopLocked(); }
        }

        internal async Task DrainPreparation()
        {
            Task? pending;
            lock (_lifecycle) pending = _preparation;
            if (pending != null) try { await pending; } catch (Exception) { }
        }

        private void StopLocked(string reason = "释放计算资源")
        {
            if (_generation != null || Process != null)
            { _retirement = reason; _detail = reason; _changed = LocalTimeline.Timestamp; }
            _generation?.Cancel(); _generation?.Dispose(); _generation = null;
            Generation = "";
            try
            {
                if (Process?.HasExited == false) { Process.Kill(entireProcessTree: true); Process.WaitForExit(5000); }
            }
            catch (InvalidOperationException) { }
            Process?.Dispose(); Process = null;
            _lock?.Dispose(); _lock = null;
        }
    }
}
