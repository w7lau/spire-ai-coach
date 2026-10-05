using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Every game command in this class executes only in an owned, separate headless game process.
public static class LocalWorker
{
    private static SceneTree _tree = null!;
    private static string _root = "";
    private static LocalSearchRequest? _activeRequest;
    private static void CheckCancellation()
    {
        if (_activeRequest != null) LocalWorkerSession.ThrowIfCancelled(_root, _activeRequest);
    }
    private static bool _combatSettled;
    private static bool _combatWon;
    private static IReadOnlyDictionary<uint, string>? _targetLabels;
    private static bool _includePotions;
    private static bool _fastNativeWaits;
    private static bool _fastStateSettling;
    private static LocalNativeLearning? _nativeLearning;
    private static LocalRolloutStyle _rolloutStyle;
    private static bool _efficientTactics;
    private static LocalChoices? _choices;
    private static LocalSelectionCursor? _selectionCursor;
    private static Func<bool> _nativeModeBeforeRequest = () => false;
    internal static LocalChoices CurrentChoices => _choices ?? throw new LocalChoiceException("未处于原生选牌操作中。");
    private static HashSet<string> _excludedModels = [];
    private static string? _assetsRequest;
    private static LocalTimeline? _timeline;
    private static int _traceWorker, _traceRoute, _traceStep, _restoreDepth;
    private static string _traceStage = "search";
    private static IDisposable? Trace(string phase, string detail = "", int depth = 1) =>
        _timeline?.Measure(_traceWorker, _traceStage, phase, detail, _traceRoute, _traceStep, depth);
    internal static IDisposable? TracePreloadCollection() => Trace("asset_gc", depth: 3);
    internal static IDisposable? TraceLogicFrame() => Trace("logic_frame", "原生外部依赖", depth: _restoreDepth > 0 ? 3 : 2);
    internal static LocalTimeline.MethodScope MeasureMethod(string name) =>
        _timeline?.MeasureMethod(_traceWorker, _traceStage, name) ?? default;
    internal static void SkipMethod(string name) => _timeline?.SkipMethod(_traceWorker, _traceStage, name);
    private static readonly LocalDecisionFingerprint DecisionFingerprint = new();
    internal static bool ReuseFingerprintBuffer => _activeRequest?.ReuseFingerprintBuffer != false && LocalWorkerOverhead.Active;
    private static readonly FieldInfo? PendingNotification = typeof(CombatStateTracker)
        .GetField("_combatStateChangedDeferredTask", BindingFlags.NonPublic | BindingFlags.Instance);
    private static long _frameVersion;
    private static Task Frame()
    {
        _frameVersion++;
        return _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame).AsTask();
    }
    private static bool DecisionSettled() => PendingNotification != null && LocalCapture.Stable() &&
        (PendingNotification.GetValue(CombatManager.Instance.StateTracker) is not Task pending || pending.IsCompletedSuccessfully);

    public static bool TryStart()
    {
        var directory = System.Environment.GetEnvironmentVariable("SPIRE_COACH_WORKER");
        if (directory == null) return false;
        _root = Path.GetFullPath(directory);
        if (!File.Exists(Path.Combine(_root, ".coach-worker")) ||
            !string.Equals(Path.GetFullPath(OS.GetExecutablePath()), Path.Combine(_root, "game", "SlayTheSpire2.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing a worker outside its isolated installation");
        var harmony = new Harmony("SpireAiCoach.owned-worker");
        LocalReplayChoices.Install(harmony);
        LocalWorkerResources.Install(harmony);
        LocalWorkerVisuals.Install(harmony);
        LocalWorkerBootstrap.Install(harmony);
        LocalWorkerDataMode.Install(harmony);
        LocalModelDisplay.Install();
        LocalWorkerLogic.Install();
        LocalHpAccounting.Install();
        LocalWorkerOverhead.Install();
        LocalWorkerVerification.Install();
        LocalDiscardObservation.Install();
        Callable.From(Run).CallDeferred();
        return true;
    }

    private static async void Run()
    {
        _tree = (SceneTree)Engine.GetMainLoop();
        try
        {
            while (NGame.Instance == null) await Frame();
            await NGame.Instance.GameStartupComplete;
            await WaitAssets();
            Silence();
            long nextMute = 0;
            _tree.ProcessFrame += () => { if (System.Environment.TickCount64 >= nextMute) { nextMute = System.Environment.TickCount64 + 1000; Silence(); } };
            LocalWire.Write(Path.Combine(_root, "audio.json"), new { silent_settings = SaveManager.Instance.SettingsSave.VolumeMaster == 0,
                godot_master_muted = AudioServer.IsBusMute(0), audio_driver = AudioServer.GetDriverName() });
            CombatManager.Instance.CombatEnded += _ => _combatSettled = true;
            CombatManager.Instance.CombatWon += _ => _combatWon = true;
            SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
            // Keep the native runtime warm without rendering hundreds of empty
            // frames between requests. Search restores the native/fast cap first.
            int readyFps = Engine.MaxFps;
            void IdleFrames() { Engine.MaxFps = 10; }
            IdleFrames();
            File.WriteAllText(Path.Combine(_root, "ready"), "ready");
            string? previous = null;
            var idle = Stopwatch.StartNew();
            // Keep the owner's warm instances available during long battles. Previously a
            // ten-minute idle exit silently turned a later calculation into a cold start.
            using var owner = LocalWorkerOwner.Open();
            while (owner != null ? owner.IsAlive : idle.Elapsed.TotalMinutes < 10)
            {
                var path = Path.Combine(_root, "request.json");
                if (File.Exists(path))
                {
                    var request = LocalWire.Read<LocalSearchRequest>(path);
                    if (request.Id != previous)
                    {
                        previous = request.Id;
                        Engine.MaxFps = readyFps;
                        try { if (!await Search(request)) break; }
                        finally { IdleFrames(); }
                        LocalWire.Write(Path.Combine(_root, "idle-runtime.json"), new { max_fps = Engine.MaxFps,
                            observed_fps = Engine.GetFramesPerSecond(), native_search_fps = readyFps, warm = true });
                        LocalWire.Write(Path.Combine(_root, "idle.json"), new LocalWorkerIdle(request.Id,
                            request.SnapshotId, request.NativeHash, request.Partition,
                            System.Environment.GetEnvironmentVariable("SPIRE_COACH_WORKER_GENERATION") ?? "standalone"));
                        idle.Restart();
                    }
                }
                await Task.Delay(100);
            }
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(_root, "fatal.txt"), ex.ToString()); }
        finally { _tree.Quit(); }
    }

    private static async Task<bool> Search(LocalSearchRequest request)
    {
        _activeRequest = request;
        _selectionCursor = new();
        DecisionFingerprint.Clear();
        LocalWorkerLogic.ResetCounters();
        LocalWorkerVerification.Reset();
        _timeline = new(request.TimelineOrigin);
        _traceWorker = request.Partition; _traceRoute = _traceStep = _restoreDepth = 0;
        _traceStage = request.VerifyCandidate == null ? "search" : "verify";
        using var session = Trace(request.VerifyCandidate == null ? "session" : "verify", depth: 0);
        _targetLabels = request.TargetLabels;
        _includePotions = false;
        bool systematic = request.SearchOrder != LocalSearchOrder.MonteCarlo;
        bool turnMode = request.SearchOrder == LocalSearchOrder.TurnFrontier;
        _rolloutStyle = LocalRolloutStyle.Balanced;
        var learningByStyle = new Dictionary<LocalRolloutStyle, LocalNativeLearning>();
        LocalNativeLearning? Learning(LocalRolloutStyle style)
        {
            if (!request.StrategicRollouts) return null;
            if (!learningByStyle.TryGetValue(style, out var learner))
            {
                // Preparation explores complete combinations with absolute gains.
                // Other policies measure individual efficiency and buff retention.
                // Mixing their observations changes the units of the learned hints.
                learner = new(trackCosts: true, trackDurations: request.LearnBuffDuration &&
                    (!turnMode || style != LocalRolloutStyle.Preparation));
                learningByStyle.Add(style, learner);
            }
            return learner;
        }
        _nativeLearning = Learning(_rolloutStyle);
        _efficientTactics = request.EfficientTactics;
        _excludedModels = new(request.ExcludedModels ?? [], StringComparer.Ordinal);
        var timer = Stopwatch.StartNew();
        var budget = new Stopwatch();
        LocalCandidate? best = null;
        int evaluated = 0, rejected = 0, victories = 0, attempts = 0, probes = 0, boundPruned = 0, unknownRecoveryChecks = 0;
        int knownRecoveryChecks = 0, sharedIncumbentUpdates = 0;
        int lossProofProbes = 0;
        string unknownRecoveryReason = "";
        var recovery = new LocalRecoveryEstimator(request);
        bool trackMinimum = request.StopOnZeroLoss && !request.StopOnFirstWin && request.VerifyCandidate == null &&
            !LocalSearchPolicy.HasSpecificGoal(request) && request.ExcludedModels is not { Length: > 0 };
        var minimumProof = trackMinimum && request.MinimumLossPipe == null ? new LocalMinimumLossProof(request) : null;
        LocalMinimumLossClient? minimumClient = null;
        LocalMinimumLossStatus? minimumStatus = null;
        void ObserveMinimum(LocalLossProofTrial trial)
        {
            if (!trackMinimum || minimumStatus?.InvalidReason.Length > 0) return;
            using var measuring = MeasureMethod("LocalMinimumLoss.ObserveTrial");
            try
            {
                if (minimumClient != null) minimumStatus = minimumClient.Observe(trial);
                else { minimumProof!.Observe(trial); minimumStatus = minimumProof.Status; }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException)
            { minimumStatus = new(InvalidReason: "Loss proof transport unavailable: " + ex.GetType().Name); }
        }
        var sharedBounds = new LocalSharedHealthBound(Path.GetDirectoryName(_root)!, request);
        LocalWinningBound? lastSharedBound = null;
        LocalWinningBound? WinningBound()
        {
            var own = request.CardGoals?.Enabled != true && LocalSearchPolicy.HasSpecificGoal(request) && !LocalSearchPolicy.MeetsGoal(best, request)
                ? null : LocalWinningBound.From(sharedBounds.Root, best);
            var peer = sharedBounds.Read();
            var selected = LocalHealthBound.Better(own, peer);
            if (selected != null && Equals(selected, peer) && !Equals(selected, own) && !Equals(peer, lastSharedBound))
            { sharedIncumbentUpdates++; lastSharedBound = peer; }
            return selected;
        }
        LocalHealthBoundStats HealthStats() => new(boundPruned, knownRecoveryChecks, unknownRecoveryChecks,
            sharedIncumbentUpdates, unknownRecoveryReason);
        LocalHealthEnvelope Envelope(Player p, int hp, IReadOnlyList<LocalAction> line)
        {
            var allowance = recovery.Estimate(p);
            if (allowance.MaximumFurtherHpGain.HasValue) knownRecoveryChecks++;
            else { unknownRecoveryChecks++; unknownRecoveryReason = allowance.Reason; }
            return new(sharedBounds.Root, hp, p.Creature.CurrentHp, line.Count(a => a.PotionSlot.HasValue),
                allowance.MaximumFurtherHpGain);
        }
        bool stoppedEarly = false;
        bool StopRequested()
        {
            CheckCancellation();
            if ((!request.StopOnZeroLoss && !request.StopOnFirstWin) || request.VerifyCandidate != null) return false;
            var path = Path.Combine(_root, "stop-search.json");
            if (!File.Exists(path)) return false;
            return LocalWire.Read<LocalSearchStop>(path).Matches(request);
        }
        var treeOrder = turnMode ? LocalSearchOrder.MonteCarlo : request.SearchOrder;
        var noPotionSearch = new LocalSearchTree(1729 + request.Partition, order: treeOrder, cardGoals: request.CardGoals);
        var potionSearch = new LocalSearchTree(2718 + request.Partition, order: treeOrder, cardGoals: request.CardGoals);
        string healthRoot = request.SnapshotId + ":" + request.NativeHash;
        ILocalTurnFrontier? turns = turnMode ? new LocalTurnSearch(1729 + request.Partition, healthRoot, request.CardGoals) : null;
        LocalTurnWorkClient? sharedTurns = null;
        bool sharedExhausted = false;
        var coverage = turnMode ? new LocalRouteCoverage() : null;
        int coveredTasks = 0;
        var terminalHistories = new HashSet<string>(StringComparer.Ordinal);
        int repeatedHistories = 0;
        var rolloutStyles = new Dictionary<string, int>();
        var turnOutcomes = new List<LocalTurnOutcome>();
        var audit = new LocalSearchAudit();
        var pagedReplays = new LocalPagedReplayTracker();
        bool searchFinished = false, searchTimeReached = false, independentlyVerified = false;
        int fullTrials = 0;
        LocalTurnSearchStats? TurnStats() => turns == null ? null :
            new(probes, boundPruned, turns.Offered, turns.DuplicateOffers, turns.Count, unknownRecoveryChecks,
                coveredTasks + (coverage?.Avoided ?? 0), terminalHistories.Count + repeatedHistories, repeatedHistories,
                turns.ClaimedByRound, rolloutStyles, turnOutcomes.ToArray(), lossProofProbes);
        var search = noPotionSearch;
        var refiner = new LocalRouteRefiner();
        var goalRefiner = request.CardGoals?.Enabled == true ? new LocalRouteRefiner() : null;
        LocalCandidate? goalSeed = null;
        var policy = turnMode || request.CorrelatedRollouts && !systematic ? new LocalRolloutPolicy(314159 + request.Partition, request.CardGoals) : null;
        LocalCandidate? refinementSeed = null;
        // Turn work uses an exact shared frontier, separate from the old soft
        // improvement proposals. Every claimed history is replayed in its owner.
        LocalSearchWork? work = !systematic && request.ShareSearchWork && (request.Partitions > 1 || request.SearchWorkPipe != null) && request.VerifyCandidate == null
            ? new(Path.GetDirectoryName(_root)!, request) : null;
        int refinements = 0;
        LocalWorkerResources.Retain = true;
        var originalScale = Engine.TimeScale;
        var originalFps = Engine.MaxFps;
        var originalNativeMode = NonInteractiveMode.AutoSlayerCheck;
        _nativeModeBeforeRequest = originalNativeMode;
        long restoreMs = 0, actionMs = 0, decisionMs = 0, verifyMs = 0;
        int executed = 0, restores = 0;
        async Task RestoreMeasured()
        {
            DecisionFingerprint.Clear();
            CheckCancellation();
            LocalWorkerOverhead.LeanSearchChecksums = false;
            _nativeLearning?.ResetDecision();
            var started = Stopwatch.GetTimestamp();
            try { await LocalWorkerLogic.Run(() => Restore(request), () => _choices?.Tick(), Frame, 60); CheckCancellation(); }
            finally { restoreMs += (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds; restores++; }
        }
        long sequence = 0, lastProgress = -1000, lastResult = -1000;
        LocalCandidate? lastPublishedBest = null;
        int route = 0, bestRoute = 0, rootBranches = 0;
        var events = new Queue<LocalSimEvent>();
        var trials = new List<LocalSearchTrial>();
        LocalAction? pendingAction = null;
        LocalAction? blockedAction = null;
        LocalSimulationFailure? failure = null;
        var outputTimeline = _timeline!;
        using var progressSender = request.MemoryProgress && request.AsyncProgressOutput && request.ProgressPipe != null
            ? new LocalProgressSender(request) : null;
        void WriteProgress((LocalProgress Progress, string Stage) update)
        {
            using var writing = outputTimeline.MeasureMethod(request.Partition, update.Stage, "LocalWorker.ProgressWrite");
            if (progressSender?.TrySend(update.Progress,
                operation => outputTimeline.MeasureMethod(request.Partition, update.Stage, "LocalProgress." + operation)) == true)
                return;
            LocalWire.Write(Path.Combine(_root, "progress.json"), update.Progress,
                operation => outputTimeline.MeasureMethod(request.Partition, update.Stage, "LocalProgress." + operation));
        }
        var progressWriter = request.AsyncProgressOutput ? new LocalLatestWriter<(LocalProgress, string)>(WriteProgress) : null;
        void Progress(string phase, LocalSimState? state = null, bool force = false, string status = "running")
        {
            if ((!force || LocalWorkerOverhead.Active && status == "running") && timer.ElapsedMilliseconds - lastProgress < 100)
            { SkipMethod("LocalWorker.Progress"); return; }
            using var publishing = Trace("publish", "过程进度");
            using var measuring = MeasureMethod("LocalWorker.Progress");
            lastProgress = timer.ElapsedMilliseconds;
            var update = new LocalProgress(request.Id, request.SnapshotId,
                request.Partition, request.Partitions, ++sequence, route, evaluated, request.MaxNodes, victories,
                budget.ElapsedMilliseconds, request.BudgetSeconds, phase, state, events.ToArray(), status, probes, boundPruned, rootBranches,
                best is { Won: true, Dead: false } winner ? new(bestRoute, winner.Hp, winner.MaxHp,
                    winner.StartingHp, winner.Rounds, winner.Actions.Count(a => a.PotionSlot.HasValue)) : null);
            if (progressWriter != null) progressWriter.Publish((update, _traceStage));
            else WriteProgress((update, _traceStage));
        }
        void Publish(string status, string message)
        {
            if (status != "running") progressWriter?.FlushAsync().GetAwaiter().GetResult();
            // The parent polls every 250 ms. Rewriting the unchanged, potentially large
            // candidate after every fast route does not provide any newer recommendation.
            if (status == "running" && (LocalWorkerOverhead.Active || ReferenceEquals(best, lastPublishedBest)) && timer.ElapsedMilliseconds - lastResult < 250)
            { SkipMethod("LocalWorker.Publish"); return; }
            using var publishing = Trace("publish", "候选结果");
            using var measuring = MeasureMethod("LocalWorker.Publish");
            lastResult = timer.ElapsedMilliseconds; lastPublishedBest = best;
            if (status != "running") LocalWire.Write(Path.Combine(_root, "runtime.json"), new
                { status, model_display = LocalModelDisplay.Status(), mode = LocalWorkerDataMode.MinimalRun ? "native-model" : LocalWorkerDataMode.Active ? "no-combat-scene" : "regular-scene",
                    max_fps = Engine.MaxFps, observed_fps = Engine.GetFramesPerSecond(), numerical = LocalWorkerLogic.Counters(),
                    overhead = LocalWorkerOverhead.Status(), verification = LocalWorkerVerification.Status(), preload_enabled = PreloadManager.Enabled,
                    progress_transport = new { pipe = progressSender?.Connected == true, fallback = progressSender?.Fallback } });
            LocalWire.Write(Path.Combine(_root, "result.json"),
            new LocalSearchResult(request.Id, request.SnapshotId, status, message, evaluated, rejected, timer.ElapsedMilliseconds, best,
                Victories: victories, IncludePotions: request.IncludePotions,
                Timing: new(restoreMs, actionMs, decisionMs, verifyMs, Actions: executed, Restores: restores, Verifications: verifyMs > 0 ? 1 : 0),
                BlockedAction: blockedAction, MaxRounds: request.MaxRounds,
                Trace: status == "running" ? null : _timeline!.Snapshot(), StoppedEarly: stoppedEarly, TurnSearch: TurnStats(), RootBranches: rootBranches,
                HealthBounds: HealthStats(), Failure: failure,
                Trials: status == "running" ? null : trials.ToArray(), CardGoals: request.CardGoals, MinimumLoss: minimumStatus,
                StoppedOnFirstWin: stoppedEarly && LocalSearchPolicy.CanStopAtFirstWin(best, request),
                StoppedOnCardGoals: stoppedEarly && LocalSearchPolicy.CanStopOnCardGoals(best, request),
                Evidence: status == "running" ? null : audit.Snapshot(request, best, coverage?.Exhausted == true,
                    turns?.Count, evaluated, searchFinished ? searchTimeReached : budget.Elapsed.TotalSeconds >= request.BudgetSeconds,
                    stoppedEarly, boundPruned, independentlyVerified)));
        }
        try
        {
            request.CardGoals?.Validate();
            if (request.Partitions is < 1 or > 16 || request.Partition < 0 || request.Partition >= request.Partitions ||
                !LocalCalculation.ValidLimits(request.MaxNodes, request.MaxRounds, request.BudgetSeconds) ||
                request.MaxDepth is < 1 or > 64 || request.SimulationSpeed is < 1 or > 16)
                throw new InvalidDataException("Invalid search limits");
            CheckCancellation();
            if (trackMinimum && request.MinimumLossPipe != null)
            {
                try { minimumClient = new(request); }
                catch (Exception ex) when (ex is IOException or TimeoutException)
                { trackMinimum = false; minimumStatus = new(InvalidReason: "Loss proof connection unavailable: " + ex.GetType().Name); }
            }
            // Owned worker only. Accelerate native animation/timer waits, never model effects/RNG.
            if (request.SimulationSpeed > 1) { Engine.TimeScale = request.SimulationSpeed; Engine.MaxFps = 240; }
            LocalWorkerVisuals.Active = request.SimulationSpeed > 1;
            LocalWorkerVisuals.FastCardPresentation = request.SimulationSpeed > 1 && request.FastCardPresentation;
            _fastNativeWaits = request.SimulationSpeed > 1 && request.FastNativeWaits;
            _fastStateSettling = request.SimulationSpeed > 1 && request.FastStateSettling;
            LocalWorkerResources.FastCollection = request.SimulationSpeed > 1 && request.FastAssetCollection;
            LocalWorkerDataMode.Active = request.DataOnlyCombat && LocalWorkerDataMode.Available;
            LocalWorkerDataMode.MinimalRun = LocalWorkerDataMode.Active && request.DataOnlyRun;
            LocalWorkerLogic.Enabled = request.NumericalExecution;
            LocalWorkerOverhead.Enabled = request.TrimWorkerOverhead && (request.NumericalExecution || request.FastVerification);
            if (request.ExperimentalNativeData) NonInteractiveMode.AutoSlayerCheck = () => true;
            var actualMods = LocalCapture.LoadedMods();
            if (request.ModelHash != ModelIdSerializationCache.Hash || !request.LoadedMods.SequenceEqual(actualMods))
            {
                LocalWire.Write(Path.Combine(_root, "model-mismatch.json"), new
                    { expected_model_hash = request.ModelHash, actual_model_hash = ModelIdSerializationCache.Hash,
                        expected_mods = request.LoadedMods, actual_mods = actualMods });
                throw new InvalidOperationException("后台的游戏模型或 Mod 清单与当前游戏不一致，请重启游戏后重试。");
            }
            if (request.RecordedReplayProbe is { } recorded)
            {
                int startHp = 0;
                var recordedActions = new List<LocalAction>();
                Player? recordedPlayer = null;
                LocalDamageAccounting? recordedDamage = null;
                LocalHpAccounting? recordedHealth = null;
                try
                {
                    var bound = LocalRecordedProbe.Bind(recorded, request.Replay);
                    await LocalWorkerLogic.Run(() => Restore(request with { Replay = bound.Replay }, true, player =>
                    { recordedPlayer = player; startHp = player.Creature.CurrentHp;
                        recordedDamage = new(player); recordedHealth = new(player); },
                    (action, player) =>
                    {
                        if (action is ReadyToBeginEnemyTurnAction) return;
                        var next = EnumerateActions().Single(a => action switch
                        {
                            PlayCardAction card => a.CombatCardIndex == card.NetCombatCard.CombatCardIndex && a.TargetId == card.TargetId,
                            UsePotionAction potion => a.PotionSlot == potion.PotionIndex,
                            EndPlayerTurnAction => a.EndTurn,
                            _ => false
                        });
                        recordedActions.Add(next);
                        _traceStep = recordedActions.Count;
                    }, bound.PrefixEvents), () => _choices?.Tick(), Frame, 60);
                    var player = recordedPlayer ?? throw new InvalidOperationException("Recorded root was not validated");
                    var health = recordedHealth!.Snapshot();
                    best = new(recordedActions.ToArray(), player.Creature.CurrentHp, health.HpLost,
                        CombatManager.Instance.DebugOnlyGetState()!.Enemies.Sum(e => Math.Max(0, e.CurrentHp)), player.Gold,
                        player.Creature.MaxHp, _combatWon && !player.Creature.IsDead, player.Creature.IsDead, false,
                        Rounds: recordedActions.Select(a => a.Round).Distinct().Count(), StartingHp: startHp,
                        DamageSources: recordedDamage!.Snapshot(health.HpLost), HealthChanges: health);
                    // Experimental terminal status is deliberately excluded from the product pool's
                    // acceptable results; this cannot become a displayed/executable empty plan.
                    await Cleanup();
                    session?.Dispose();
                    Publish("recorded", "已核对原始战斗起点并执行本机已记录的全部操作与选牌。");
                    return true;
                }
                finally
                {
                    recordedDamage?.Dispose();
                    recordedHealth?.Dispose();
                }
            }
            if (request.VerifyCandidate is { } proposed)
            {
                best = proposed;
                Progress("复核最终候选", force: true);
                Publish("running", "正在独立复核最终候选…");
                var verifyStarted = Stopwatch.GetTimestamp();
                var verified = await VerifyBest(request, proposed, (action, step, before, after) =>
                {
                    events.Enqueue(new(step, action.Round, LocalSearchPolicy.Describe(action), before != null && after != null ?
                        LocalProgressBook.Changes(before, after) : "动作已复核。"));
                    while (events.Count > 12) events.Dequeue();
                    Progress("复核最终候选", after);
                });
                verifyMs += (long)Stopwatch.GetElapsedTime(verifyStarted).TotalMilliseconds;
                best = best with { Continuation = verified.Points, ContinuationFromSearch = false };
                independentlyVerified = true;
                Progress("计算完成 · 最终候选复核通过", verified.State, true, "done");
                await Cleanup();
                session?.Dispose();
                Publish("done", "最终候选的每步状态和战后结算已独立复核。");
                return true;
            }
            Publish("running", "正在恢复并核对当前战斗…");
            if (turnMode && request.TurnWorkPipe != null)
                turns = sharedTurns = new LocalTurnWorkClient(request);
            // Keep the legal set stable for exact-history worker ownership. A
            // baseline continuation still chooses no potion; reserve moves are
            // offered as alternatives, never forced at the beginning of a trial.
            if (turnMode) _includePotions = request.IncludePotions;
            Progress("恢复当前战斗", force: true);
            await RestoreMeasured();
            // Owned diagnostic process only. Read one restored root; never
            // search, play cards or return an executable empty candidate.
            if (System.Environment.GetEnvironmentVariable("SPIRE_COACH_RECOVERY_AUDIT") == "1")
            {
                var auditPlayer = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState()!)!;
                var beforeAudit = LocalCapture.Fingerprint();
                var auditClock = Stopwatch.StartNew();
                var allowance = recovery.Estimate(auditPlayer);
                auditClock.Stop();
                var afterAudit = LocalCapture.Fingerprint();
                if (beforeAudit != afterAudit) throw new InvalidOperationException("Recovery audit mutated its native root");
                LocalHealthChanges[]? healthAudit = null;
                if (System.Environment.GetEnvironmentVariable("SPIRE_COACH_HP_AUDIT") == "1")
                {
                    healthAudit = LocalHpAccounting.Audit(auditPlayer);
                    await RestoreMeasured();
                    if (beforeAudit != LocalCapture.Fingerprint())
                        throw new InvalidOperationException("Health audit did not restore its frozen root");
                }
                LocalWire.Write(Path.Combine(_root, "recovery-audit.json"), new { request.Id, allowance,
                    ElapsedMs = auditClock.Elapsed.TotalMilliseconds, RootUnchanged = true,
                    HealthWritesHooked = LocalHpAccounting.WritesHooked, HealthAudit = healthAudit,
                    version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3),
                    module = typeof(LocalWorker).Assembly.ManifestModule.ModuleVersionId });
                await Cleanup(); session?.Dispose(); Publish("audited", "回复代码检查完成；没有搜索或出牌。");
                return true;
            }
            // Cold asset loading and root restoration are preparation, not tactical exploration.
            // Otherwise a small budget can expire before the first complete combat rollout.
            budget.Start();
            var decisionStarted = Stopwatch.GetTimestamp();
            LocalAction[] first;
            using (Trace("decision")) first = EnumerateActions();
            decisionMs += (long)Stopwatch.GetElapsedTime(decisionStarted).TotalMilliseconds;
            rootBranches = first.Length;
            int partitions = systematic && sharedTurns == null ? LocalConcurrency.Partitions(request.Partitions, rootBranches, request.AdaptiveWorkers) : request.Partitions;
            if (sharedTurns == null && request.Partition >= partitions) throw new InvalidDataException("Worker is outside the admitted native root partition");
            var roots = first.Where((_, i) => i % partitions == request.Partition).ToArray();
            // More workers than first moves explore different continuations of the same first move.
            if (roots.Length == 0) roots = [first[request.Partition % first.Length]];
            if (work != null && request.Partition == 0 && !request.ResumingFrontier)
            {
                using var scheduling = Trace("schedule");
                work.Offer("expand", first.OrderByDescending(a => a.Preference).Select(a => new[] { a }), initializeRoot: true);
                if (request.InitialPlan is { Length: > 0 }) work.Offer("improve", [request.InitialPlan]);
            }
            Progress("已准备可探索分支", force: true);
            var initialEnemyHp = CombatManager.Instance.DebugOnlyGetState()!.Enemies.Sum(e => Math.Max(0, e.CurrentHp));
            LocalTurnHint TurnHint(Player p, int hp, IReadOnlyList<LocalAction> line) =>
                new(p.Creature.CurrentHp, hp, CombatManager.Instance.DebugOnlyGetState()?.Enemies.Sum(e => Math.Max(0, e.CurrentHp)) ?? 0,
                    initialEnemyHp, p.Creature.Block, line.Count(a => a.PotionSlot.HasValue), recovery.Estimate(p).MaximumFurtherHpGain);
            if (turns != null)
            {
                var rootPlayer = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState()!)!;
                if (!request.ResumingFrontier && (sharedTurns == null || request.Partition == 0))
                {
                    int round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                    var hint = TurnHint(rootPlayer, rootPlayer.Creature.CurrentHp, []);
                    if (request.InitialPlan is { Length: > 0 } seed)
                    {
                        if (seed[0].BeforeHash != request.NativeHash)
                            throw new InvalidDataException("Existing route does not match the frozen native root");
                        turns.SeedRoot(seed, round, hint);
                    }
                    else turns.Offer([], round, hint);
                }
            }
            while (evaluated < request.MaxNodes && budget.Elapsed.TotalSeconds < request.BudgetSeconds &&
                (turns != null ? sharedTurns != null || turns.Count > 0 && !coverage!.Exhausted : work != null || !noPotionSearch.Exhausted || request.IncludePotions && !potionSearch.Exhausted || refiner.Count > 0))
            {
                if (StopRequested()) { stoppedEarly = true; break; }
                using var refining = Trace("refine");
                // Other workers' candidates seed exploration only. Their results are never adopted without
                // executing the proposed line in this worker, including all native effects and choices.
                if (!systematic || turnMode) foreach (var peer in Directory.EnumerateDirectories(Path.GetDirectoryName(_root)!, "worker-*"))
                {
                    var file = Path.Combine(peer, "search-seed.json");
                    if (peer == _root || !File.Exists(file)) continue;
                    try
                    {
                        var result = LocalWire.Read<LocalSearchSeed>(file);
                        if (result.Id == request.Id && result.SnapshotId == request.SnapshotId &&
                            result.NativeHash == request.NativeHash && result.Candidate is { } seed &&
                            LocalSearchPolicy.Better(seed, refinementSeed, request.CardGoals)) refinementSeed = seed;
                    }
                    catch (IOException) { /* Peer can be replacing its private IPC file. */ }
                }
                if (best != null && LocalSearchPolicy.Better(best, refinementSeed, request.CardGoals)) refinementSeed = best;
                // Each producer submits improvements of its own measured best.
                // Retain distinct local seeds, rather than regenerating one peer's
                // plan on every worker or deleting all weaker-looking seeds.
                var proposalSeed = work == null ? refinementSeed : best;
                if (!systematic && proposalSeed != null) refiner.Offer(proposalSeed, work == null ? request.Partition : 0,
                    work == null ? request.Partitions : 1);
                if (!systematic && goalSeed != null) goalRefiner!.Offer(goalSeed,
                    work == null ? request.Partition : 0, work == null ? request.Partitions : 1);
                LocalAction[]? planned = null;
                LocalTurnTask? turnTask = null;
                bool fullRollout = !turnMode || LocalTurnSearch.IsFullRollout(attempts);
                LocalWorkTask? sharedTask = null;
                if (turns != null)
                {
                    using var turnScheduling = MeasureMethod("LocalTurnFrontier.Take");
                    // The parent can also certify a finished peer's native victory.
                    // Running peer seeds remain exploration proposals only.
                    boundPruned += turns.DiscardProvenExpenses(WinningBound());
                    if (!turns.TryTake(out turnTask))
                    {
                        if (sharedTurns != null && (!sharedTurns.RootReady || sharedTurns.Active > 0))
                        {
                            refining?.Dispose(); Progress("等待可探索分支"); await Task.Delay(50); continue;
                        }
                        sharedExhausted = sharedTurns != null; break;
                    }
                    fullRollout = !turnTask.LossProof && (sharedTurns != null ? turnTask.FullRollout : fullRollout || turnTask.FullRollout);
                    if (turnTask.Prefix.Length == 1 && !turnTask.LossProof) fullRollout = true;
                    planned = turnTask.Prefix;
                    // Skip already completed subtrees before restoring their prefix.
                    if (coverage!.IsClosedPrefix(planned)) { coveredTasks++; sharedTurns?.Finish(turnTask); continue; }
                }
                else if (work != null)
                {
                    using var scheduling = Trace("schedule");
                    var proposals = new List<LocalAction[]>();
                    while (refiner.Count > 0 || goalRefiner?.Count > 0)
                    {
                        if (goalRefiner != null && goalRefiner.TryTake(out var goalProposal)) proposals.Add(goalProposal);
                        if (refiner.TryTake(out var proposal)) proposals.Add(proposal);
                    }
                    work.Offer("improve", proposals);
                    bool improving = evaluated % 2 == 1 || evaluated == 0 && request.InitialPlan is { Length: > 0 };
                    sharedTask = work.Take(improving ? "improve" : "expand", request.Partition,
                        evaluated % 8 == 4, focused: evaluated % 8 == 2 || evaluated % 8 == 6)
                        ?? work.Take(improving ? "expand" : "improve", request.Partition, evaluated % 8 == 4,
                            focused: evaluated % 8 == 2 || evaluated % 8 == 6);
                    planned = sharedTask?.Plan;
                    if (sharedTask?.Kind == "improve") refinements++;
                    if (sharedTask == null)
                    {
                        // Do not turn an empty shared queue into independent root
                        // rollouts. Peers can publish new branches after their jobs.
                        var workState = work.Stats();
                        if (workState.RootReady && workState.Active == 0 && workState.Pending == 0)
                        { sharedExhausted = true; break; }
                        refining?.Dispose();
                        Progress("等待可探索分支");
                        await Task.Delay(50);
                        continue;
                    }
                }
                else
                {
                    if (evaluated == 0 && request.InitialPlan is { Length: > 0 }) planned = request.InitialPlan;
                    if (!systematic && (evaluated % 2 == 1 || search.Exhausted))
                    {
                        if (evaluated % 4 == 1 && goalRefiner != null && goalRefiner.TryTake(out var goalProposal))
                        { planned = goalProposal; refinements++; }
                        else if (refiner.TryTake(out var proposal)) { planned = proposal; refinements++; }
                    }
                }
                route = ++attempts;
                _rolloutStyle = turnMode && fullRollout ? sharedTurns != null || turnTask!.FullRollout ? turnTask!.Style :
                    turnTask!.Prefix.Length <= 1 ? LocalRolloutStyle.Preparation :
                    (LocalRolloutStyle)(fullTrials % 4) : LocalRolloutStyle.Balanced;
                _nativeLearning = Learning(_rolloutStyle);
                _nativeLearning?.ResetDecision();
                if (fullRollout)
                {
                    fullTrials++;
                    string style = _rolloutStyle.ToString();
                    rolloutStyles[style] = rolloutStyles.GetValueOrDefault(style) + 1;
                }
                _traceRoute = route; _traceStep = 0;
                using var turnTiming = turns == null ? null : Trace(fullRollout ? "rollout" : "turn-probe",
                    $"round={turnTask!.SearchRound};lane={turns!.LastLane};prefix={planned!.Length};style={_rolloutStyle}");
                refining?.Dispose();
                events.Clear();
                Progress("恢复路线起点", force: true);
                if (attempts > 1) await RestoreMeasured();
                if (StopRequested()) { stoppedEarly = true; break; }
                var player = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState()!)!;
                LocalWorkerOverhead.LeanSearchChecksums = request.LeanSearchChecksums;
                using var checksumListener = request.ProbeChecksumListener
                    ? LocalWorkerOverhead.ObserveChecksums(RunManager.Instance.ChecksumTracker) : null;
                var startRound = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                var startingHp = player.Creature.CurrentHp;
                // Always establish a no-potion baseline. Then interleave reserve-potion searches,
                // unless a preferred candidate explicitly needs one. Both classes get fresh trials.
                _includePotions = turnMode ? request.IncludePotions : request.IncludePotions && (planned?.Any(a => a.PotionSlot.HasValue) == true ||
                    evaluated > 0 && evaluated % 3 == 2);
                search = _includePotions ? potionSearch : noPotionSearch;
                if (!turnMode && search.Exhausted && planned == null)
                {
                    if (!request.IncludePotions || (_includePotions ? noPotionSearch : potionSearch).Exhausted) break;
                    _includePotions = !_includePotions;
                    search = _includePotions ? potionSearch : noPotionSearch;
                }
                var actions = new List<LocalAction>();
                var proofSteps = trackMinimum ? new List<LocalLossProofStep>() : null;
                var continuationPoints = request.SkipFinalVerification && request.History != null ? new List<LocalContinuationPoint>() : null;
                var decisions = new List<LocalDecision>();
                var partition = systematic && sharedTurns == null ? new LocalBranchPartition(request.Partition, partitions) : null;
                var trial = search.Begin();
                var activePolicy = turns == null || fullRollout && _rolloutStyle == LocalRolloutStyle.Correlated ? policy : null;
                activePolicy?.Begin();
                LocalAction ChooseTurn(IReadOnlyList<LocalAction> options, bool coherent) =>
                    turns!.Choose(activePolicy == null ? options :
                        options.Select(a => a with { Preference = activePolicy.Priority(a) }).ToArray(), coherent);
                var coveredTrial = coverage?.Begin();
                var winningBound = WinningBound();
                // Interleave measured winning-tail proposals with fresh native
                // continuations. Borrowing a slower winner every time prevents
                // changed setup from replacing its old defensive ordering.
                var continuation = turnMode && fullRollout && (attempts / 4) % 4 == 0 && refinementSeed?.Won == true
                    ? refinementSeed.Actions : null;
                int continuationIndex = 0;
                int planIndex = 0;
                int tailIndex = 0;
                string? terminalDigest = null;
                using var damageSources = new LocalDamageAccounting(player);
                using var cardGoalCounts = new LocalCardGoalAccounting(player, request.CardGoals);
                using var healthAccounting = new LocalHpAccounting(player);
                try
                {
                    Progress("开始试走", Observe(player), true);
                    int plays = 0;
                    string stop = "";
                    bool turnProbed = false, cut = false, covered = false;
                    while (!IsTerminal(player))
                    {
                        if (StopRequested()) { stoppedEarly = true; break; }
                        var round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                        if (round - startRound >= request.MaxRounds) { audit.RoundLimitHits++; stop = "达到轮数上限"; break; }
                        if (budget.Elapsed.TotalSeconds >= request.BudgetSeconds) { audit.TimeLimitHits++; stop = "达到时间预算"; break; }
                        if (turnTask != null && !fullRollout && planIndex >= planned!.Length && round > turnTask.SearchRound)
                        { turnProbed = true; stop = "本回合及敌方结算完成"; break; }
                        if (plays >= request.MaxDepth)
                        {
                            // Infinite/very long zero-cost cycles are bounded, not assumed equivalent or worthless.
                            audit.ActionLimitHits++;
                            stop = "达到单回合操作上限"; break;
                        }
                        decisionStarted = Stopwatch.GetTimestamp();
                        using var deciding = Trace("decision");
                        var legal = EnumerateActions(out var finisherHints);
                        var proofLegal = proofSteps == null ? null : legal;
                        bool proofComplete = _includePotions == request.IncludePotions;
                        if (partition != null) legal = partition.Assign(legal);
                        if (coverage != null)
                        {
                            legal = coverage.Open(coveredTrial!, legal);
                            if (legal.Length == 0) { covered = true; stop = "该操作前缀已全部评估"; break; }
                        }
                        // An incumbent may arrive from another worker during exact
                        // replay. Keep prescribed actions intact; prune their state
                        // after execution rather than making replay appear illegal.
                        if (winningBound?.NetHpLoss == 0 && planIndex >= (planned?.Length ?? 0))
                        {
                            int spent = actions.Count(a => a.PotionSlot.HasValue);
                            var admissible = legal.Where(a => !LocalHealthBound.CannotImprove(new(healthRoot, startingHp,
                                player.Creature.CurrentHp, spent + (a.PotionSlot.HasValue ? 1 : 0)), winningBound, request.CardGoals)).ToArray();
                            boundPruned += legal.Length - admissible.Length;
                            legal = admissible;
                            if (legal.Length == 0) { cut = true; stop = "该操作前缀的所有后续均已无法改进"; break; }
                        }
                        LocalAction? preferred = null;
                        LocalAction? plannedAction = null;
                        bool exactAction = false;
                        if (turnMode && planIndex < planned!.Length)
                        {
                            plannedAction = planned[planIndex++];
                            preferred = LocalTurnSearch.ResolveExact(plannedAction, legal);
                            exactAction = true;
                        }
                        else if (turnMode && fullRollout && turnTask?.Continuation is { } tail)
                        {
                            // Preserve a measured combination after changing its
                            // prefix. The old states are not replay assertions:
                            // only a currently legal native action may be proposed.
                            while (tailIndex < tail.Length && preferred == null)
                            {
                                var expected = tail[tailIndex];
                                if (expected.Round > round) break;
                                tailIndex++;
                                if (expected.Round < round) continue;
                                preferred = LocalRouteRefiner.Resolve(expected, legal);
                                if (preferred != null) plannedAction = expected;
                            }
                        }
                        else if (!turnMode && planned != null)
                        {
                            while (planIndex < planned.Length && preferred == null)
                            {
                                    plannedAction = planned[planIndex];
                                    // A changed action can leave the current round with more plays.
                                    // Do not consume the seed's later rounds before they are reached.
                                    if (plannedAction.Round > round) { plannedAction = null; break; }
                                    planIndex++;
                                preferred = LocalRouteRefiner.Resolve(plannedAction, legal);
                            }
                        }
                        if (turnMode && preferred == null && continuation != null)
                        {
                            // A native winning line supplies continuation proposals, not
                            // transferred outcomes. Changed early plays/choices are still
                            // executed and the resulting full battle decides their merit.
                            while (continuationIndex < continuation.Length && preferred == null)
                            {
                                plannedAction = continuation[continuationIndex];
                                if (plannedAction.Round > round) { plannedAction = null; break; }
                                continuationIndex++;
                                preferred = LocalRouteRefiner.Resolve(plannedAction, legal);
                            }
                            // Preserve this proposed line, including deliberate holding.
                            // Other legal plays are already published as exact siblings;
                            // a heuristic must not rewrite its end turn before measuring it.
                        }
                        else if (!systematic && preferred == null && evaluated == 0 && actions.Count == 0)
                            preferred = roots.OrderByDescending(a => a.Preference).First();
                        // Reused winning tails are proposals, not replay assertions.
                        // After measuring the baseline, allow the current goal to
                        // improve a soft tail instead of blindly spending its finite
                        // finisher again. Exact expansion prefixes remain untouched.
                        bool adaptGoalTail = evaluated > 0 && !exactAction &&
                            (turnMode || work == null || sharedTask?.Kind == "improve");
                        var adapted = LocalCardGoalTactics.AdaptSoftContinuation(preferred,
                            finisherHints.Where(h => legal.Contains(h.Action)).ToArray(), adaptGoalTail);
                        if (adapted != preferred) { preferred = adapted; plannedAction = null; }
                        // Explore explicit branch proposals, then continue most trials coherently.
                        // Randomizing every card in a long rollout almost never preserves a combo.
                        // Monte Carlo retains stochastic trials. The turn frontier
                        // retains exact legal siblings and explores those explicitly.
                        bool coherent = request.StrategicRollouts && (turnMode || evaluated % 4 != 3);
                        var continuations = turnMode && attempts % 3 != 0 ? legal.Where(a => a.PotionSlot == null).ToArray() : legal;
                        if (continuations.Length == 0) continuations = legal;
                        LocalAction next;
                        if (turns != null) next = preferred ?? ChooseTurn(continuations, coherent);
                        else if (search.TrySelect(trial, legal, out var selectedAction, preferred,
                            greedy: coherent, priority: activePolicy == null ? null : activePolicy.Priority)) next = selectedAction;
                        else { covered = true; stop = "该操作前缀已全部评估"; break; }
                        coverage?.Follow(coveredTrial!, next);
                        var choiceDecisions = new List<LocalChoiceDecision>();
                        var proofChoices = proofSteps == null ? null : new List<LocalChoiceDecision>();
                        decisions.Add(new(actions.Count, legal));
                        decisionMs += (long)Stopwatch.GetElapsedTime(decisionStarted).TotalMilliseconds;
                        deciding?.Dispose();
                        var before = Observe(player);
                        int previousHp = player.Creature.CurrentHp;
                        var beforeHint = turns == null ? null : TurnHint(player, startingHp, actions);
                        var learned = !next.EndTurn && next.PotionSlot == null
                            ? _nativeLearning?.Before(player.PlayerCombatState!.Hand.Cards[next.HandIndex], player) : null;
                        Progress("执行：" + LocalSearchPolicy.Describe(next), before);
                        var priorLost = healthAccounting.HpLost;
                        var actionStarted = Stopwatch.GetTimestamp();
                        pendingAction = next;
                        _traceStep = actions.Count + 1;
                        if (continuationPoints != null)
                        {
                            try
                            {
                                // Reuse the fingerprint from native legal-action enumeration;
                                // recording adds no frame wait or independent state replay.
                                var history = LocalCapture.History();
                                if (actions.Count == 0 && history != request.History) continuationPoints = null;
                                else continuationPoints.Add(new(actions.Count, next.BeforeHash, history, healthAccounting.HpLost, player.Creature.CurrentHp));
                            }
                            catch (InvalidOperationException) { continuationPoints = null; }
                        }
                        try
                        {
                            int choiceIndex = 0;
                            next = await Play(next, options =>
                            {
                                // The native action may ask for several choices. If its
                                // subtree was already covered, finish each pending choice
                                // legally, then discard this redundant trial after settling.
                                if (covered) return options[0];
                                proofChoices?.Add(new(proofChoices.Count, options));
                                // A choice is a child of the actual action prefix, so siblings get independent outcomes.
                                var expected = exactAction && preferred != null ? plannedAction?.Choices?.ElementAtOrDefault(choiceIndex++) : null;
                                var match = !exactAction ?
                                    preferred != null && plannedAction?.Choices is { } intended
                                        ? LocalRouteRefiner.ProposeNextChoice(intended, options, ref choiceIndex) : null :
                                    expected == null ? null : options.SingleOrDefault(c => c.OfferHash == expected.OfferHash &&
                                    c.Kind == expected.Kind && c.Index == expected.Index && c.ModelId == expected.ModelId &&
                                    (c.Indices ?? []).SequenceEqual(expected.Indices ?? []));
                                var choices = options.Select(c => LocalRouteCoverage.ChoiceAction(c, round)).ToArray();
                                if (partition != null) choices = partition.Assign(choices);
                                bool completeOffer = options.All(c => c.CompleteOffer);
                                if (!completeOffer) audit.PagedChoiceObservations++;
                                if (coverage != null) choices = coverage.Open(coveredTrial!, choices, completeOffer);
                                if (choices.Length == 0) { covered = true; stop = "本组选牌已评估，继续探索其他组合"; return options[0]; }
                                bool SameOption(LocalCardChoice c, LocalAction a) =>
                                    LocalRouteCoverage.ChoiceAction(c, round) with { Preference = 0 } == a with { Preference = 0 };
                                var ownedOptions = options.Where(c => choices.Any(a => SameOption(c, a))).ToArray();
                                choiceDecisions.Add(new(choiceDecisions.Count, ownedOptions));
                                var fixedChoice = match == null ? null : choices.SingleOrDefault(c => SameOption(match, c));
                                if (exactAction && expected != null && fixedChoice == null)
                                    throw new InvalidOperationException("Exact native selection prefix diverged");
                                LocalAction selected;
                                if (turns != null) selected = fixedChoice ?? ChooseTurn(choices, coherent);
                                else if (search.TrySelect(trial, choices, out var selectedChoice, fixedChoice,
                                    greedy: coherent, priority: activePolicy == null ? null : activePolicy.Priority,
                                    completeLegal: completeOffer)) selected = selectedChoice;
                                else { covered = true; stop = completeOffer ? "该选牌前缀已全部评估" : "本组选牌已评估，继续探索其他组合"; return ownedOptions[0]; }
                                coverage?.Follow(coveredTrial!, selected);
                                return options.Single(c => SameOption(c, selected));
                            }, preferred != null ? plannedAction?.Choices : null);
                        }
                        finally { actionMs += (long)Stopwatch.GetElapsedTime(actionStarted).TotalMilliseconds; executed++; }
                        actions.Add(next);
                        cardGoalCounts.CompleteStep();
                        if (covered) break;
                        if (exactAction && plannedAction != null)
                        {
                            int expectedChoices = plannedAction.Choices?.Length ?? 0, actualChoices = next.Choices?.Length ?? 0;
                            if (actualChoices < expectedChoices || planIndex < planned!.Length && actualChoices != expectedChoices)
                                throw new InvalidOperationException("Exact native selection history changed");
                        }
                        _nativeLearning?.After(learned, player);
                        if (next.EndTurn) _nativeLearning?.SettleBuffs(player);
                        decisions[^1] = decisions[^1] with { Choices = choiceDecisions.ToArray(),
                            HpBefore = previousHp, HpAfter = player.Creature.CurrentHp };
                        if (turns != null && actions.Count >= planned!.Length)
                        {
                            using var offering = MeasureMethod("LocalTurnFrontier.OfferAlternatives");
                            turns.OfferAlternatives(actions, decisions[^1], beforeHint!, turnTask);
                        }
                        // New lazy pages observed while replaying fixed ancestors must
                        // still reach the common frontier. Exact-history ownership and
                        // deduplication are unchanged; ordinary ancestor siblings stay suppressed.
                        if (turns != null)
                        {
                            using var paging = MeasureMethod("LocalTurnFrontier.ReplayPages");
                            foreach (var offer in pagedReplays.Observe(actions, decisions[^1], beforeHint!, exactAction))
                            {
                                turns.Offer(offer.Prefix, offer.SearchRound, offer.Hint);
                                audit.PagedReplayBranches++;
                            }
                        }
                        else if (work != null && preferred != null &&
                            decisions[^1].Choices?.Any(d => d.Legal.Any(c => !c.CompleteOffer)) == true)
                        {
                            using var paging = MeasureMethod("LocalSearchWork.ReplayPages");
                            // The old broker consumes prefixes only. Reuse the
                            // existing pre-action observation, without querying rules.
                            var hint = new LocalTurnHint(previousHp, startingHp,
                                before?.Enemies.Sum(e => Math.Max(0, e.Hp)) ?? initialEnemyHp, initialEnemyHp);
                            var pages = pagedReplays.Observe(actions, decisions[^1], hint, replaying: true).ToArray();
                            if (pages.Length > 0)
                            {
                                work.Offer("expand", pages.Select(p => p.Prefix));
                                audit.PagedReplayBranches += pages.Length;
                            }
                        }
                        pendingAction = null;
                        var after = Observe(player);
                        var changes = before != null && after != null ? LocalProgressBook.Changes(before, after) : "动作已结算；状态预览暂不可用。";
                        if (healthAccounting.HpLost > priorLost) changes += $"；期间实际失去生命 {healthAccounting.HpLost - priorLost}";
                        events.Enqueue(new(actions.Count, next.Round, LocalSearchPolicy.Describe(next), changes));
                        while (events.Count > 12) events.Dequeue();
                        Progress("试走路线", after);
                        plays = next.EndTurn ? 0 : plays + 1;
                        // Only changed health, potion expense or a settled turn can
                        // raise this floor. Reuse the existing pruning checkpoint.
                        LocalHealthEnvelope? stepEnvelope = proofSteps != null && !IsTerminal(player) &&
                            (next.EndTurn || next.PotionSlot.HasValue || player.Creature.CurrentHp != previousHp ||
                             CombatManager.Instance.DebugOnlyGetState()!.RoundNumber > round)
                            ? Envelope(player, startingHp, actions) : null;
                        var settledState = CombatManager.Instance.DebugOnlyGetState();
                        var afterHint = turns != null && stepEnvelope != null ? new LocalTurnHint(player.Creature.CurrentHp,
                            startingHp, settledState?.Enemies.Sum(e => Math.Max(0, e.CurrentHp)) ?? 0, initialEnemyHp,
                            player.Creature.Block, stepEnvelope.PotionsUsed, stepEnvelope.MaximumFurtherHpGain) : null;
                        proofSteps?.Add(new(next, proofLegal!, proofChoices!.ToArray(), stepEnvelope, proofComplete,
                            beforeHint, afterHint, settledState?.RoundNumber));
                        if (turnTask?.LossProof == true && stepEnvelope != null && minimumStatus is { InvalidReason.Length: 0, Target: { } target } &&
                            (LocalHealthBound.MinimumNetHpLoss(stepEnvelope) > target.NetHpLoss ||
                             LocalHealthBound.MinimumNetHpLoss(stepEnvelope) == target.NetHpLoss &&
                             stepEnvelope.PotionsUsed >= target.PotionsUsed))
                        {
                            turnProbed = true; stop = "本分支已达到现有获胜路线的损失下界"; break;
                        }
                        if (!IsTerminal(player) &&
                            (next.PotionSlot.HasValue || player.Creature.CurrentHp < previousHp ||
                             CombatManager.Instance.DebugOnlyGetState()!.RoundNumber > round))
                        {
                            winningBound = WinningBound();
                            if (winningBound != null && LocalHealthBound.CannotImprove(stepEnvelope ?? Envelope(player, startingHp, actions), winningBound, request.CardGoals))
                            {
                                cut = true; boundPruned += 1 + (turns?.DiscardDescendants(actions) ?? 0);
                                stop = "分支已无法优于现有获胜路线"; break;
                            }
                            int nextRound = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                            // A full trial already continues this exact turn boundary.
                            // Only a short probe needs to publish that open continuation.
                            if (turns != null && !fullRollout && nextRound > round && actions.Count >= planned!.Length)
                                turns.Offer(actions.ToArray(), nextRound, TurnHint(player, startingHp, actions));
                        }
                    }
                    // A peer reached the goal. Discard this unfinished trial; only a
                    // previously completed candidate may survive to final verification.
                    if (stoppedEarly)
                    {
                        if (turnTask != null) turns!.ReturnInterrupted(turnTask, TurnHint(player, startingHp, actions));
                        break;
                    }
                    if (covered)
                    {
                        coveredTasks++;
                        await StableOrTerminal(player);
                        using (Trace("covered-prefix", stop)) { }
                        if (sharedTask != null) work!.Complete(sharedTask);
                        Progress("该分支已完成，继续其他分支", Observe(player));
                        continue;
                    }
                    await StableOrTerminal(player);
                    // An ended combat without native victory/death confirmation does
                    // not certify a failed leaf in a reachability proof.
                    coverage?.Complete(coveredTrial!, _combatWon || player.Creature.IsDead);
                    if (coverage != null && IsTerminal(player))
                    {
                        terminalDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(LocalTurnSearch.HistoryKey(actions))));
                        if (!terminalHistories.Add(terminalDigest)) repeatedHistories++;
                        using (Trace("completed-route", terminalDigest)) { }
                    }
                    var state = CombatManager.Instance.DebugOnlyGetState();
                    var won = _combatWon && !player.Creature.IsDead;
                    var endTurnRisk = !IsTerminal(player) ? LocalTacticalPreview.Capture(player, []).EndTurnHpLossHint : (double?)null;
                    var health = healthAccounting.Snapshot();
                    var candidate = new LocalCandidate(actions.ToArray(), player.Creature.CurrentHp, health.HpLost,
                        state?.Enemies.Sum(e => Math.Max(0, e.CurrentHp)) ?? 0,
                        player.Gold, player.Creature.MaxHp, won, player.Creature.IsDead,
                        // Extra rewards remain unknown; the explicit zero-loss switch does not depend on them.
                        RewardCoverageKnown: false,
                        Rounds: actions.Select(a => a.Round).Distinct().Count(),
                        StopReason: won ? "胜利结算完成" : player.Creature.IsDead ? "玩家死亡" :
                            string.IsNullOrEmpty(stop) ? "战斗结束但未确认胜利" : stop, Decisions: decisions.ToArray(), StartingHp: startingHp,
                        Continuation: continuationPoints?.ToArray(), ContinuationFromSearch: continuationPoints != null,
                        DamageSources: damageSources.Snapshot(health.HpLost), RolloutStyle: _rolloutStyle,
                        InitialEnemyHp: initialEnemyHp, EndTurnHpLossHint: endTurnRisk, CardGoalOutcome: cardGoalCounts.Snapshot(),
                        HealthChanges: health);
                    audit.Outcome(candidate, IsTerminal(player));
                    if (turnTask != null && stop == "达到时间预算")
                        turns!.ReturnInterrupted(turnTask, TurnHint(player, startingHp, actions));
                    if (turnMode && IsTerminal(player) && planIndex < planned!.Length)
                        throw new InvalidOperationException("Exact native search prefix terminated early");
                    if (proofSteps != null)
                    {
                        ObserveMinimum(new(startingHp, proofSteps.ToArray(), candidate.Hp, candidate.Won, candidate.Dead));
                        if (turns != null && minimumStatus != null)
                        {
                            using var prioritizing = MeasureMethod("LocalMinimumLoss.PrioritizePrefixes");
                            turns.PrioritizeLossProof(minimumStatus.Focus ?? []);
                        }
                    }
                    bool completeAttempt = LocalSearchRecovery.CompleteTrial(turnProbed, cut, covered,
                        fullRollout, IsTerminal(player), stop == "达到时间预算");
                    if (turnMode && completeAttempt)
                        turnOutcomes.Add(new(request.Partition, route, _timeline!.ElapsedMs, won, candidate.Hp, health.HpLost,
                            candidate.Rounds, actions.Count(a => a.PotionSlot.HasValue), _rolloutStyle, candidate.DamageSources));
                    if (!turnProbed && !cut && trials.Count < 129)
                        trials.Add(new(request.Partition, route, _timeline!.ElapsedMs, candidate.Won, candidate.Hp,
                            candidate.NetHpLoss, candidate.Rounds, actions.Count(a => a.PotionSlot.HasValue), completeAttempt,
                            sharedTask?.Kind == "expand" ? LocalSearchWork.MatchesPrefix(actions, sharedTask.Plan) : null,
                            candidate.CardGoalOutcome?.Plays, candidate.CardGoalOutcome?.Kills));
                    using (Trace("trial-result", $"won={won};hp={candidate.Hp};loss={candidate.NetHpLoss};gross={candidate.HpLost};potions={actions.Count(a => a.PotionSlot.HasValue)};" +
                        $"rounds={candidate.Rounds};plays={candidate.CardGoalOutcome?.Plays};finishers={candidate.CardGoalOutcome?.Kills};" +
                        $"complete={completeAttempt};probe={turnProbed};cut={cut};limited={stop == "达到时间预算"}")) { }
                    if (turnTask != null && stop != "达到时间预算" && !cut)
                        turns!.FocusNext(turnTask, actions, decisions);
                    if (turnProbed) { probes++; if (turnTask?.LossProof == true) lossProofProbes++; }
                    else if (completeAttempt) evaluated++;
                    if (won) victories++;
                    Progress(won ? "路线获胜" : candidate.StopReason, Observe(player), true);
                    bool improvesBest = completeAttempt && LocalSearchPolicy.BetterForGoal(candidate, best, request);
                    bool improvesGoalSeed = completeAttempt && request.CardGoals is { Enabled: true } selectedGoals &&
                        selectedGoals.Counts(candidate) != (0, 0) &&
                        LocalCardGoalTactics.BetterExplorationSeed(candidate, goalSeed, selectedGoals);
                    if (improvesGoalSeed) goalSeed = candidate;
                    if ((improvesBest || improvesGoalSeed) && turns != null && request.GuideWinningRoutes)
                    {
                        // Actual achieved goals also guide lowering their HP cost.
                        // One message preserves owned-prefix checks and affinity.
                        turns.ObserveOutcome(candidate);
                        if (sharedTurns == null) turns.PromoteWinning(candidate);
                    }
                    if (improvesBest)
                    {
                        best = candidate; bestRoute = route;
                        LocalWire.Write(Path.Combine(_root, "search-seed.json"),
                            new LocalSearchSeed(request.Id, request.SnapshotId, request.NativeHash,
                                candidate with { Continuation = null }));
                    }
                    if (completeAttempt && stop != "达到时间预算") activePolicy?.Complete(candidate);
                    if (LocalSearchPolicy.CanStopAfterVictory(best, request, minimumStatus?.Certificate))
                    { stoppedEarly = true; break; }
                    if (work != null)
                    {
                        using var scheduling = Trace("schedule");
                        work.RecordTerminal(candidate);
                        // Deepen exactly the next observed decision under the claimed
                        // prefix. Do not replace several unrelated setup decisions with
                        // independent root noise. Other lanes retain broad coverage.
                        if (sharedTask?.Kind == "expand" && LocalSearchWork.MatchesPrefix(actions, sharedTask.Plan))
                            work.Offer("expand", ExpansionPrefixes(candidate).Where(p => p.Length == sharedTask.Plan.Length + 1),
                                parent: sharedTask.Key);
                        work.Offer("expand", ExpansionPrefixes(candidate).Take(96));
                        if (sharedTask != null && stop != "达到时间预算") work.Complete(sharedTask);
                    }
                    // A wall-clock interruption says nothing about the strength of this continuation.
                    // A proven bound closes only this exact native prefix; it is
                    // not counted as a completed rollout or a winning candidate.
                    if (!turnMode && stop != "达到时间预算") search.Complete(trial, candidate, initialEnemyHp, closeExactPrefix: true);
                    Publish("running", turns != null ? $"已找到 {victories} 条整场获胜路线；另探查 {probes} 个回合组合，剪枝 {boundPruned} 次，等待 {turns.Count} 个操作前缀。" :
                        $"已找到 {victories} 条整场获胜路线；已记录 {noPotionSearch.Nodes + (request.IncludePotions ? potionSearch.Nodes : 0)} 个操作树节点，按实际结算反馈选路。");
                }
                catch (LocalWorkerCancelledException)
                {
                    if (turnTask != null) turns!.ReturnInterrupted(turnTask, TurnHint(player, startingHp, actions));
                    throw;
                }
                catch (LocalChoiceException ex)
                {
                    audit.SimulationErrors++;
                    rejected++;
                    blockedAction = pendingAction;
                    session?.Dispose();
                    // A pending selector may own callbacks. Retire this process instead of resetting it under them.
                    Publish(best == null ? "unsupported" : "partial", ex.Message);
                    Progress("遇到额外选择，停止搜索", force: true, status: "unsupported");
                    return false;
                }
                finally
                {
                    if (turnTask != null)
                    {
                        using var finishing = MeasureMethod("LocalTurnFrontier.Finish");
                        sharedTurns?.Finish(turnTask, terminalDigest);
                    }
                }
            }
            searchFinished = true;
            searchTimeReached = budget.Elapsed.TotalSeconds >= request.BudgetSeconds;
            LocalWorkerOverhead.LeanSearchChecksums = false;
            if (best != null && !request.DeferVerification)
            {
                Publish("running", "正在从当前状态重新执行最佳路线，复核每步状态与最终结算…");
                events.Clear();
                route = bestRoute;
                Progress("复核最佳路线", force: true);
                var verifyStarted = Stopwatch.GetTimestamp();
                var verified = await VerifyBest(request, best, (action, step, before, after) =>
                {
                    events.Enqueue(new(step, action.Round, LocalSearchPolicy.Describe(action), before != null && after != null ?
                        LocalProgressBook.Changes(before, after) : "动作已复核。"));
                    while (events.Count > 12) events.Dequeue();
                    Progress("复核最佳路线", after);
                });
                verifyMs += (long)Stopwatch.GetElapsedTime(verifyStarted).TotalMilliseconds;
                best = best with { Continuation = verified.Points, ContinuationFromSearch = false };
                independentlyVerified = true;
                Progress("计算完成 · 最佳路线复核通过", verified.State, true, "done");
            }
            else if (stoppedEarly) Progress("已停止搜索，等待返回路线", force: true, status: "searched");
            else if (best == null) Progress(sharedExhausted ? "已完成分工" : "未取得可用路线", force: true,
                status: sharedExhausted ? "searched" : "unsupported");
            else Progress("搜索完成 · 等待最终候选复核", force: true, status: "searched");
            await Cleanup();
            session?.Dispose();
            Publish(best == null ? stoppedEarly || sharedExhausted ? "searched" : "unsupported" : request.DeferVerification ? "searched" : "done",
                stoppedEarly ? request.StopOnFirstWin ? best?.Won == true ?
                    "已找到获胜路线，停止后续搜索。" : "已停止其余搜索。" :
                    request.CardGoals?.Enabled == true ? LocalSearchPolicy.CanStopOnCardGoals(best, request) ?
                    "当前消耗目标已完成，损血符合设置，停止后续搜索。" : "已停止其余搜索。" :
                    best?.NetHpLoss is > 0 ? $"已达到最低净损失 {best.NetHpLoss}，停止后续搜索。" :
                    "已达到无伤通关停止条件，停止后续搜索。" : best == null ? "没有找到可完整结算的路线。" :
                turns != null ? $"已完成当前预算；评估 {evaluated} 条整场路线，另探查 {probes} 个回合组合，剪枝 {boundPruned} 次；尚未证明全局最优。" :
                $"已完成当前预算，操作树 {noPotionSearch.Nodes + (request.IncludePotions ? potionSearch.Nodes : 0)} 个节点，比较了 {refinements} 条补牌、删牌、换牌和选牌路线。");
            return true;
        }
        catch (LocalWorkerCancelledException)
        {
            best = null;
            await Cleanup();
            Progress("已取消计算", force: true, status: "cancelled");
            session?.Dispose();
            Publish("cancelled", "已安全停止本次计算。");
            return true;
        }
        catch (Exception ex)
        {
            audit.SimulationErrors++;
            // Any replay divergence invalidates this worker's result, including previous candidates.
            best = null;
            failure = LocalSimulationFailure.Capture(ex, request.Partition, _traceStage);
            // Keep the complete cause before a compatibility pass restarts this instance.
            // These private per-request files are not overwritten by Ensure/request polling.
            try
            {
                LocalWire.Write(Path.Combine(_root, "failure-" + Guid.NewGuid().ToString("N") + ".json"), new
                {
                    captured_at = DateTimeOffset.UtcNow, request.Id, request.SnapshotId, worker = request.Partition,
                    stage = _traceStage, route = _traceRoute, step = _traceStep,
                    request.DataOnlyCombat, request.DataOnlyRun, request.NumericalExecution,
                    exception = ex.ToString(), failure, trace = _timeline?.Snapshot()
                });
            }
            catch (Exception recording) when (recording is IOException or UnauthorizedAccessException)
            { GD.Print("[SpireAiCoach] Failed to retain worker exception: " + recording.Message); }
            Progress("失败：" + ex.Message, force: true, status: "failed");
            session?.Dispose();
            Publish("failed", ex.Message);
            return false;
        }
        finally
        {
            try { if (progressWriter != null) await progressWriter.DisposeAsync(); }
            finally
            {
                try { minimumClient?.Dispose(); sharedTurns?.Dispose(); try { work?.Retire(request.Partition); } finally { work?.Dispose(); } }
                finally { DecisionFingerprint.Clear(); NonInteractiveMode.AutoSlayerCheck = originalNativeMode; LocalWorkerOverhead.Enabled = false; LocalWorkerOverhead.LeanSearchChecksums = false; LocalWorkerLogic.Enabled = false; LocalWorkerDataMode.Reset(); LocalWorkerVisuals.Reset(); _fastNativeWaits = _fastStateSettling = false; LocalWorkerResources.Retain = LocalWorkerResources.FastCollection = false; Engine.TimeScale = originalScale; Engine.MaxFps = originalFps; _timeline = null; _activeRequest = null; _nativeLearning = null; _targetLabels = null; _choices = null; _selectionCursor = null; _excludedModels.Clear(); _includePotions = false; }
            }
        }
    }

    private static IEnumerable<LocalAction[]> ExpansionPrefixes(LocalCandidate candidate)
    {
        foreach (var point in candidate.Decisions ?? [])
        {
            if (point.BeforeStep < 0 || point.BeforeStep >= candidate.Actions.Length) continue;
            var actual = candidate.Actions[point.BeforeStep];
            var prefix = candidate.Actions.Take(point.BeforeStep).ToArray();
            foreach (var alternate in point.Legal)
                if (LocalSearchWork.Key("expand", [alternate]) != LocalSearchWork.Key("expand", [actual]))
                    yield return prefix.Append(alternate).ToArray();
            foreach (var choice in point.Choices ?? [])
                foreach (var alternate in choice.Legal)
                {
                    var original = actual.Choices;
                    if (original == null || choice.AtChoice >= original.Length ||
                        alternate.OfferHash != original[choice.AtChoice].OfferHash) continue;
                    var choices = original.ToArray(); choices[choice.AtChoice] = alternate;
                    yield return prefix.Append(actual with { Choices = choices }).ToArray();
                }
        }
    }

    private static void Silence()
    {
        var settings = SaveManager.Instance.SettingsSave;
        settings.VolumeMaster = settings.VolumeBgm = settings.VolumeSfx = settings.VolumeAmbience = 0;
        NGame.Instance!.AudioManager.SetMasterVol(0);
        NGame.Instance.AudioManager.SetBgmVol(0);
        NGame.Instance.AudioManager.SetSfxVol(0);
        NGame.Instance.AudioManager.SetAmbienceVol(0);
        NGame.Instance.DebugAudio.SetMasterAudioVolume(0);
        NGame.Instance.DebugAudio.SetSfxAudioVolume(0);
        for (int i = 0; i < AudioServer.BusCount; i++) AudioServer.SetBusMute(i, true);
    }

    private static LocalSimState? Observe(Player player)
    {
        using var observing = Trace("observe");
        // Telemetry is descriptive only. A third-party display string must not change search execution.
        try
        {
            static string Clip(string text) => text.Length <= 300 ? text : text[..300] + "…";
            string Powers(MegaCrit.Sts2.Core.Entities.Creatures.Creature c) => Clip(string.Join("、", c.Powers.Select(p => p.Title.GetFormattedText() + " " + p.Amount)));
            var state = CombatManager.Instance.DebugOnlyGetState();
            var pcs = player.PlayerCombatState;
            return new(state?.RoundNumber ?? 0, player.Creature.CurrentHp, player.Creature.MaxHp, player.Creature.Block,
                pcs?.Energy ?? 0, Powers(player.Creature), pcs?.Hand.Cards.Take(64).Select(c => Clip(c.Title)).ToArray() ?? [],
                player.PotionSlots.Count(p => p != null), state?.Enemies.Take(32).Select(e => new LocalSimEnemy(e.CombatId, Clip(e.Name),
                    e.CurrentHp, e.MaxHp, e.Block, Powers(e), Clip(e.Monster?.NextMove?.Id ?? "无"))).ToArray() ?? []);
        }
        catch { return null; }
    }

    private static async Task<(LocalSimState? State, LocalContinuationPoint[] Points)> VerifyBest(LocalSearchRequest request, LocalCandidate candidate,
        Action<LocalAction, int, LocalSimState?, LocalSimState?> progress)
    {
        try { return await VerifyBestCore(request, candidate, progress); }
        catch (Exception ex) when (request.FastVerification && LocalWorkerVerification.UsedFast && ex is not OperationCanceledException)
        {
            // Retry only this final candidate in the unchanged regular scene path.
            // Do not redo search or publish an unverified/partially replayed plan.
            LocalWorkerVerification.Fallback = ex.GetType().Name + ": " + ex.Message;
            File.WriteAllText(Path.Combine(_root, "verification-fallback.txt"), ex.ToString());
            // Lazy collection did not certify the normal eager preload. Restore its
            // resource path as well as the real executor for this same candidate.
            _assetsRequest = "";
            using var fallback = Trace("verify_fallback", LocalWorkerVerification.Fallback);
            return await VerifyBestCore(request with { FastVerification = false }, candidate, progress);
        }
    }

    private static async Task<(LocalSimState? State, LocalContinuationPoint[] Points)> VerifyBestCore(LocalSearchRequest request, LocalCandidate candidate,
        Action<LocalAction, int, LocalSimState?, LocalSimState?> progress)
    {
        _traceStage = "verify"; _traceRoute = _traceStep = 0;
        // Keep real scenes, executor scheduling, choices and native state notifications.
        // The optional scope trims only font fitting, fades and eager asset loading.
        LocalWorkerDataMode.Active = false;
        LocalWorkerDataMode.MinimalRun = false;
        NonInteractiveMode.AutoSlayerCheck = _nativeModeBeforeRequest;
        using var presentation = LocalWorkerVerification.Begin(request.FastVerification);
        await Restore(request);
        CheckCancellation();
        var player = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState()!)!;
        _includePotions = request.IncludePotions;
        if (candidate.StartingHp is { } expectedHp && player.Creature.CurrentHp != expectedHp)
            throw new InvalidOperationException("候选起点生命不一致，未发布该路线。");
        var points = new List<LocalContinuationPoint>();
        bool canContinue = request.History != null && request.History == LocalCapture.History();
        using var damageSources = new LocalDamageAccounting(player);
        using var cardGoalCounts = new LocalCardGoalAccounting(player, request.CardGoals);
        using var healthAccounting = new LocalHpAccounting(player);
        {
            int step = 0;
            foreach (var action in candidate.Actions)
            {
                CheckCancellation();
                if (canContinue) points.Add(new(step, action.BeforeHash, LocalCapture.History(), healthAccounting.HpLost, player.Creature.CurrentHp));
                var before = Observe(player);
                _traceStep = step + 1;
                await Play(action);
                cardGoalCounts.CompleteStep();
                progress(action, ++step, before, Observe(player));
            }
            await StableOrTerminal(player);
            CheckCancellation();
            var enemyHp = CombatManager.Instance.DebugOnlyGetState()?.Enemies.Sum(e => Math.Max(0, e.CurrentHp)) ?? 0;
            var health = healthAccounting.Snapshot();
            if ((_combatWon && !player.Creature.IsDead) != candidate.Won || player.Creature.IsDead != candidate.Dead ||
                player.Creature.CurrentHp != candidate.Hp || health.HpLost != candidate.HpLost || enemyHp != candidate.EnemyHp ||
                player.Gold != candidate.Gold || player.Creature.MaxHp != candidate.MaxHp ||
                candidate.DamageSources != null && damageSources.Snapshot(health.HpLost) != candidate.DamageSources ||
                candidate.HealthChanges != null && candidate.HealthChanges != health ||
                !cardGoalCounts.Matches(candidate.CardGoalOutcome))
                throw new InvalidOperationException("最佳路线重新执行后的结算不一致，未发布该进程的建议。");
            return (Observe(player), points.ToArray());
        }
    }

    private static async Task Cleanup(int depth = 1)
    {
        using var cleanup = Trace("cleanup", depth: depth);
        LocalWorkerDataMode.FreeSelections();
        if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control());
        await Frame(); await Frame();
        _combatSettled = false; _combatWon = false;
    }

    private static async Task WaitAssets()
    {
        // GameStartupComplete precedes the background Common session finishing. Drain it before
        // run preloading can unload/replace its resources and trigger a Godot GC-handle race.
        var timer = Stopwatch.StartNew();
        await Frame(); await Frame();
        while (NAssetLoader.Instance.IsProcessing())
        {
            if (timer.Elapsed.TotalSeconds > 60) throw new IOException("游戏资源准备超时。");
            await Frame();
        }
    }

    private static async Task Restore(LocalSearchRequest request, bool completedReplayProbe = false, Action<Player>? atRoot = null,
        Action<GameAction, Player>? atAction = null, int rootAfterEvents = 0)
    {
        CheckCancellation();
        using var restoring = Trace("restore", "战斗起点");
        _restoreDepth = 1;
        try
        {
            await Cleanup(2);
            using var decoding = Trace("decode", depth: 2);
            var reader = new PacketReader(); reader.Reset(request.Replay);
            var replay = reader.Read<CombatReplay>();
            var run = RunState.FromSerializable(replay.serializableRun);
            decoding?.Dispose();
            using var setup = Trace("setup", depth: 2);
            await RunManager.Instance.SetUpSavedSingleplayer(run, replay.serializableRun);
            var manager = RunManager.Instance;
            // This owned simulation has no persistent run to advance. Native death/victory otherwise
            // deletes its repeatedly restored save/backup, producing storage errors unrelated to combat.
            typeof(RunManager).GetProperty(nameof(RunManager.ShouldSave))!.SetValue(manager, false);
            manager.ActionQueueSet.FastForwardNextActionId(replay.nextActionId);
            manager.ActionQueueSynchronizer.FastForwardHookId(replay.nextHookId);
            manager.PlayerChoiceSynchronizer.FastForwardChoiceIds(replay.choiceIds);
            manager.RewardsSetSynchronizer.FastForwardRewardIds(replay.rewardIds);
            using var historical = new LocalReplayChoices(replay.events);
            setup?.Dispose();
            if (!LocalWorkerDataMode.MinimalRun && _assetsRequest != request.Id)
            {
                using var assets = Trace("assets", depth: 2);
                using (Trace("assets_wait", depth: 3)) await WaitAssets();
                using (Trace("assets_run", depth: 3)) await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
                using (Trace("assets_act", depth: 3)) await PreloadManager.LoadActAssets(run.Acts[run.CurrentActIndex]);
                _assetsRequest = request.Id;
            }
            using var scene = Trace("scene", LocalWorkerDataMode.Active ? "原生战斗初始化" : "场景与战斗初始化", depth: 2);
            manager.Launch();
            if (!LocalWorkerDataMode.MinimalRun)
            {
                NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
                // Native map/overlay/timer initialization is deferred until the
                // scene is attached. An instant short victory can otherwise
                // replace that scene before its callbacks obtain their receivers.
                // Model-only search creates no NRun and pays no frame wait here.
                await Frame(); await Frame();
            }
            await manager.GenerateMap();
            var originalFastMode = SaveManager.Instance.PrefsSave.FastMode;
            // Some native events start repeating, effect-free scene animations.
            // Instant waits make those loops synchronous before recorded choices
            // can cancel them. Scene-free restoration has no animation receiver;
            // regular restoration retains native pacing only during event entry.
            if (request.EventEntry != null && !LocalWorkerDataMode.Active)
                SaveManager.Instance.PrefsSave.FastMode = FastModeType.Normal;
            try
            {
                if (request.DebugEncounter is { } encounter)
                    await manager.EnterRoomDebug(RoomType.Monster, MapPointType.Monster,
                        ModelDb.AllEncounters.Single(e => e.Id.Entry == encounter).ToMutable(), false);
                else await manager.LoadIntoLatestMapCoord(null);
                using (Trace("event_entry", "进入事件战斗", depth: 3)) await LocalEventReplay.Restore(request.EventEntry, run);
            }
            finally { SaveManager.Instance.PrefsSave.FastMode = originalFastMode; }
            var player = LocalContext.GetMe(run)!;
            await StableOrTerminal(player);
            scene?.Dispose();
            using var history = Trace("history", depth: 2);
            void CheckRecordedRoot()
            {
                // The full recorded route must begin at the exact frozen source, before any
                // historical action. A different/anonymized root is rejected, never guessed.
                if (IsTerminal(player) || LocalCapture.Fingerprint() != request.NativeHash)
                    throw new InvalidOperationException("已记录路线的起点与冻结战斗不一致。");
                atRoot!(player);
            }
            if (completedReplayProbe && rootAfterEvents == 0) CheckRecordedRoot();
            for (int eventIndex = 0; eventIndex < replay.events.Count; eventIndex++)
            {
                var item = replay.events[eventIndex];
                if (item.eventType is CombatReplayEventType.HookAction or CombatReplayEventType.PlayerChoice or CombatReplayEventType.ResumeAction)
                {
                    if (completedReplayProbe && eventIndex + 1 == rootAfterEvents) CheckRecordedRoot();
                    continue; // The native synchronization path restores completed choices without a UI.
                }
                if (item.eventType != CombatReplayEventType.GameAction || item.action == null)
                    throw new InvalidOperationException("本地重放暂不支持这场战斗中的额外选择。");
                var action = item.action.ToGameAction(player);
                if (eventIndex >= rootAfterEvents) atAction?.Invoke(action, player);
                using var recordedStep = completedReplayProbe && eventIndex >= rootAfterEvents && action is not ReadyToBeginEnemyTurnAction
                    ? Trace(action is EndPlayerTurnAction ? "end_turn" : action is UsePotionAction ? "potion" : "card",
                        action.GetType().Name, depth: 3) : null;
                if (action is UsePotionAction use)
                {
                    var potion = player.GetPotionAtSlotIndex((int)use.PotionIndex) ?? throw new InvalidOperationException("历史药水槽不一致。");
                    var target = use.TargetId == null ? null : player.Creature.CombatState!.Creatures.Single(c => c.CombatId == use.TargetId);
                    potion.EnqueueManualUse(target);
                    if (!LocalWorkerLogic.Active) await Frame();
                    await WaitActionQueue(); await StableOrTerminal(player);
                }
                else if (action is PlayCardAction)
                {
                    manager.ActionQueueSet.EnqueueWithoutSynchronizing(action);
                    if (!_fastNativeWaits) await Frame();
                    await WaitActionQueue();
                    await StableOrTerminal(player);
                }
                else if (action is EndPlayerTurnAction) await EndTurn(player);
                else if (action is not ReadyToBeginEnemyTurnAction)
                    throw new InvalidOperationException("本地重放暂不支持历史操作：" + action.GetType().Name);
                if (completedReplayProbe && eventIndex + 1 == rootAfterEvents) CheckRecordedRoot();
            }
            historical.Finish();
            history?.Dispose();
            using var fingerprint = Trace("fingerprint", depth: 2);
            if (!completedReplayProbe && (IsTerminal(player) || LocalCapture.Fingerprint() != request.NativeHash))
                throw new InvalidOperationException("后台重放与当前战斗状态不一致，未发布本地建议。可切换 AI 模式。");
        }
        finally { _restoreDepth = 0; }
    }

    private static LocalAction[] EnumerateActions() => EnumerateActions(out _);

    private static LocalAction[] EnumerateActions(out LocalFinisherHint[] finisherHints)
    {
        var state = CombatManager.Instance.DebugOnlyGetState()!;
        var player = LocalContext.GetMe(state)!;
        DecisionFingerprint.Clear();
        const string hash = "";
        var result = new List<LocalAction>();
        var finiteHints = new List<(int Index, int Priority)>();
        var hand = player.PlayerCombatState!.Hand.Cards;
        // CanPlay walks the hook chain. Query once per card and reuse within this settled
        // decision, instead of recomputing the full hand for every card/target preview.
        var playable = hand.Where(c => !_excludedModels.Contains(c.Id.ToString()) && c.CanPlay()).ToArray();
        var tactics = LocalTacticalPreview.Capture(player, playable, _efficientTactics);
        var hints = new Dictionary<uint, int>();
        for (var i = 0; i < hand.Count; i++)
        {
            var card = hand[i];
            if (!playable.Contains(card)) continue;
            // Do not assume that attacks precede setup, or that zero damage means no value.
            if (card.IsValidTarget(null)) result.Add(new(i, card.Id.ToString(), null, card.Title, "", hash, state.RoundNumber,
                Preference: tactics.Priority(card, null, _rolloutStyle),
                CombatCardIndex: NetCombatCard.FromModel(card).CombatCardIndex));
            else foreach (var target in state.Creatures.Where(c => c.IsAlive && card.IsValidTarget(c)))
                result.Add(new(i, card.Id.ToString(), target.CombatId, card.Title,
                    target.CombatId is { } id && _targetLabels?.TryGetValue(id, out var label) == true ? label : target.Name,
                    hash, state.RoundNumber, Preference: tactics.Priority(card, target, _rolloutStyle),
                    CombatCardIndex: NetCombatCard.FromModel(card).CombatCardIndex));
        }
        foreach (var action in result)
            if (action.CombatCardIndex is { } id) hints[id] = Math.Max(hints.GetValueOrDefault(id, int.MinValue), action.Preference);
        _nativeLearning?.ObserveHints(player, hints);
        for (int i = 0; i < result.Count; i++)
        {
            var card = hand[result[i].HandIndex];
            int priority = _nativeLearning?.Priority(card, result[i].Preference) ?? result[i].Preference;
            if (_nativeLearning != null) priority += _nativeLearning.Selections.SourcePriority(card, player, tactics,
                _rolloutStyle, _nativeLearning.Priority);
            result[i] = result[i] with { Preference = priority };
        }
        if (_rolloutStyle == LocalRolloutStyle.Preparation) _nativeLearning?.OrderDependencies(player, result);
        if (_activeRequest?.CardGoals is { Enabled: true } goals)
            for (int i = 0; i < result.Count; i++)
            {
                var action = result[i];
                var target = state.Creatures.FirstOrDefault(c => c.CombatId == action.TargetId);
                result[i] = action with { Preference = action.Preference +
                    tactics.CardGoalPriority(hand[action.HandIndex], target, goals) };
                var card = hand[action.HandIndex];
                if (card.Id.ToString() == goals.FinisherModelId && card.Keywords.Contains(CardKeyword.Exhaust))
                    finiteHints.Add((i, tactics.FinisherPriority(card, target)));
            }
        if (_includePotions)
        {
            for (int i = 0; i < player.PotionSlots.Count; i++)
            {
                var potion = player.PotionSlots[i];
                if (potion == null || !PotionUsable(potion) || _excludedModels.Contains(potion.Id.ToString())) continue;
                if (potion.IsValidTarget(null)) result.Add(new(-1, potion.Id.ToString(), null, potion.Title.GetFormattedText(), "", hash,
                    state.RoundNumber, PotionSlot: i));
                else foreach (var target in state.Creatures.Where(c => c.IsAlive && potion.IsValidTarget(c)))
                    result.Add(new(-1, potion.Id.ToString(), target.CombatId, potion.Title.GetFormattedText(),
                        target == player.Creature ? "自己" : target.CombatId is { } id && _targetLabels?.TryGetValue(id, out var label) == true ? label : target.Name,
                        hash, state.RoundNumber, PotionSlot: i));
            }
        }
        result.Add(new(-1, "", null, "", "", hash, state.RoundNumber, EndTurn: true, Preference: tactics.EndTurnPriority));
        // Native legality/preview hooks have finished before capturing the decision.
        var fingerprint = LocalCapture.Fingerprint();
        if (_activeRequest?.ReuseDecisionFingerprint == true && _traceStage == "search" &&
            LocalWorkerLogic.Enabled && LocalWorkerLogic.Available && LocalWorkerDataMode.MinimalRun && DecisionSettled())
            DecisionFingerprint.Remember(state, fingerprint, LocalWorkerLogic.Revision, _frameVersion);
        var actions = result.Select(a => a with { BeforeHash = fingerprint }).ToArray();
        finisherHints = finiteHints.Select(h => new LocalFinisherHint(actions[h.Index], h.Priority)).ToArray();
        return actions;
    }

    private static async Task<LocalAction> Play(LocalAction action, Func<LocalCardChoice[], LocalCardChoice>? choose = null,
        LocalCardChoice[]? selectionIntent = null)
    {
        CheckCancellation();
        using var playing = Trace(action.EndTurn ? "end_turn" : action.PotionSlot.HasValue ? "potion" : "card",
            action.EndTurn ? $"第 {action.Round} 回合" : action.CardName);
        // Search enumerates the next page of large native offers. Supply the
        // planned choices as well, so exact replay can directly materialize its
        // legal selection even when that rank is outside the current page.
        var player = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState()!)!;
        var source = !action.EndTurn && !action.PotionSlot.HasValue && action.HandIndex >= 0
            ? player.PlayerCombatState!.Hand.Cards[action.HandIndex] : null;
        // Search-only ordering. Guarded live execution/replay has no chooser
        // and must resolve exactly the recorded native offer/rank instead.
        var learner = choose != null ? _nativeLearning : null;
        using var observing = LocalDiscardObservation.Use(learner?.Selections.Discards, player);
        var session = new LocalChoices(selectionIntent ?? action.Choices, choose, _selectionCursor)
        {
            SelectionPriority = source == null || learner == null ? null : (kind, cards, prefs, ordinal, gold) =>
                learner.Selections.Rank(source, player, kind, cards, prefs, ordinal, _rolloutStyle, _efficientTactics, learner.Priority, gold)
        };
        _choices = session;
        try
        {
            await LocalWorkerLogic.Run(() => PlayNative(action), () => _choices?.Tick(), Frame);
            session.Finish();
            CheckCancellation();
            if (source != null) learner?.Selections.Observe(source, session.Selections);
            return action with { Choices = session.Completed };
        }
        catch (LocalChoiceException ex)
        { throw new LocalChoiceException(LocalSearchPolicy.Describe(action) + " " + ex.Message); }
        finally { _choices = null; }
    }

    private static async Task PlayNative(LocalAction action)
    {
        var state = CombatManager.Instance.DebugOnlyGetState()!;
        bool reused = _activeRequest?.ReuseDecisionFingerprint == true && _traceStage == "search" && LocalWorkerLogic.Active &&
            DecisionFingerprint.Consume(state, action.BeforeHash, LocalWorkerLogic.Revision, _frameVersion, DecisionSettled());
        if (reused) SkipMethod("LocalCapture.ReusedDecisionFingerprint");
        else
        {
            DecisionFingerprint.Clear();
            if (LocalCapture.Fingerprint() != action.BeforeHash) throw new InvalidOperationException("搜索分支状态复现不一致。");
        }
        var player = LocalContext.GetMe(state)!;
        if (action.EndTurn) { await EndTurn(player); return; }
        var target = action.TargetId == null ? null : state.Creatures.Single(c => c.CombatId == action.TargetId);
        if (action.PotionSlot is { } slot)
        {
            var potion = player.GetPotionAtSlotIndex(slot);
            if (!_includePotions || potion == null || potion.Id.ToString() != action.ModelId || !PotionUsable(potion) || !potion.IsValidTarget(target))
                throw new InvalidOperationException("药水实例或目标不再合法。");
            potion.EnqueueManualUse(target);
            if (!LocalWorkerLogic.Active) await Frame();
            await WaitActionQueue(); await StableOrTerminal(player);
            return;
        }
        var card = player.PlayerCombatState!.Hand.Cards[action.HandIndex];
        if (card.Id.ToString() != action.ModelId || !card.CanPlay() || !card.IsValidTarget(target))
            throw new InvalidOperationException("出牌实例或目标不再合法。");
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, target));
        // Enqueue creates the native queue-completion task synchronously. No extra frame is
        // needed to observe it; choices still get serviced while that task is incomplete.
        if (!_fastNativeWaits) await Frame();
        await WaitActionQueue();
        await StableOrTerminal(player);
    }

    private static bool PotionUsable(PotionModel potion) => potion.Usage is PotionUsage.CombatOnly or PotionUsage.AnyTime &&
        potion.PassesCustomUsabilityCheck && !potion.IsQueued && !potion.HasBeenRemovedFromState;

    private static async Task WaitActionQueue()
    {
        using var waiting = Trace("action_queue", depth: _restoreDepth > 0 ? 3 : 2);
        var timer = Stopwatch.StartNew();
        var pending = RunManager.Instance.ActionQueueSet.BecameEmpty();
        if (LocalWorkerLogic.Active) { await pending; return; }
        while (!pending.IsCompleted)
        {
            _choices?.Tick();
            if (timer.Elapsed.TotalSeconds > 5) throw new LocalChoiceException("等待动作结算超时（" + LocalChoices.PendingDescription() + "）。");
            if (_fastNativeWaits) await Task.WhenAny(pending, Frame());
            else await Frame();
        }
        await pending;
    }

    private static async Task WaitExecutor()
    {
        using var waiting = Trace("executor", depth: _restoreDepth > 0 ? 3 : 2);
        // An empty queue can be reported before CheckWinCondition and executor cleanup.
        // Await the real completion instead of using a whole frame as a proxy for it.
        var pending = RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (LocalWorkerLogic.Active) { await pending; return; }
        var timer = Stopwatch.StartNew();
        while (!pending.IsCompleted)
        {
            _choices?.Tick();
            if (timer.Elapsed.TotalSeconds > 8) throw new LocalChoiceException("等待动作执行完成超时（" + LocalChoices.PendingDescription() + "）。");
            await Task.WhenAny(pending, Frame());
        }
        await pending;
    }

    private static bool IsTerminal(Player player) => !CombatManager.Instance.IsStarting &&
        (player.Creature.IsDead || CombatManager.Instance.IsOverOrEnding);

    private static async Task EndTurn(Player player)
    {
        var round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new EndPlayerTurnAction(player, player.PlayerCombatState!.TurnNumber));
        if (LocalWorkerLogic.Active)
        {
            await LocalWorkerLogic.Until(() => IsTerminal(player) || CombatManager.Instance.DebugOnlyGetState()!.RoundNumber > round);
            await StableOrTerminal(player);
            return;
        }
        var timer = Stopwatch.StartNew();
        while (!IsTerminal(player) && CombatManager.Instance.DebugOnlyGetState()!.RoundNumber <= round)
        {
            _choices?.Tick();
            if (timer.Elapsed.TotalSeconds > 8) throw new LocalChoiceException("等待下一回合超时（" + LocalChoices.PendingDescription() + "）。");
            await Frame();
        }
        await StableOrTerminal(player);
    }

    private static async Task StableOrTerminal(Player player)
    {
        using var settling = Trace("settle", depth: _restoreDepth > 0 ? 3 : 2);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            if (_fastStateSettling) await WaitExecutor();
            if (LocalWorkerDataMode.MinimalRun && typeof(CombatStateTracker)
                .GetField("_combatStateChangedDeferredTask", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(CombatManager.Instance.StateTracker) is Task notification) await notification;
            if (IsTerminal(player))
            {
                // CombatEnded fires after native victory/death hooks, not when the final enemy merely reaches 0 HP.
                if (_combatSettled)
                {
                    await WaitActionQueue();
                    if (!_fastStateSettling) await Frame();
                    return;
                }
            }
            else if (player.PlayerCombatState?.Phase == PlayerTurnPhase.Play && !CombatManager.Instance.PlayerActionsDisabled &&
                player.PlayerCombatState.PlayPile.IsEmpty && RunManager.Instance.ActionQueueSet.BecameEmpty().IsCompletedSuccessfully)
            { if (!_fastStateSettling) await Frame(); return; }
            _choices?.Tick();
            if (timer.Elapsed.TotalSeconds > 8) throw new LocalChoiceException("等待战斗稳定超时（" + LocalChoices.PendingDescription() + "）。");
            if (LocalWorkerLogic.Active)
                await LocalWorkerLogic.Until(() => IsTerminal(player) ? _combatSettled :
                    player.PlayerCombatState?.Phase == PlayerTurnPhase.Play && !CombatManager.Instance.PlayerActionsDisabled &&
                    player.PlayerCombatState.PlayPile.IsEmpty && RunManager.Instance.ActionQueueSet.BecameEmpty().IsCompletedSuccessfully);
            else await Frame();
        }
    }

    private sealed class LocalChoiceException(string message) : Exception(message);
}

internal static class SignalTask
{
    public static async Task AsTask(this SignalAwaiter signal) => await signal;
}
