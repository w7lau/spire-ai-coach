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
    private static bool _combatSettled;
    private static bool _combatWon;
    private static IReadOnlyDictionary<uint, string>? _targetLabels;
    private static bool _includePotions;
    private static LocalChoices? _choices;
    private static HashSet<string> _excludedModels = [];
    private static string? _assetsRequest;
    private static LocalTimeline? _timeline;
    private static int _traceWorker, _traceRoute, _traceStep, _restoreDepth;
    private static string _traceStage = "search";
    private static IDisposable? Trace(string phase, string detail = "", int depth = 1) =>
        _timeline?.Measure(_traceWorker, _traceStage, phase, detail, _traceRoute, _traceStep, depth);
    private static Task Frame() => _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame).AsTask();

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
            File.WriteAllText(Path.Combine(_root, "ready"), "ready");
            string? previous = null;
            var idle = Stopwatch.StartNew();
            while (idle.Elapsed.TotalMinutes < 10)
            {
                var path = Path.Combine(_root, "request.json");
                if (File.Exists(path))
                {
                    var request = LocalWire.Read<LocalSearchRequest>(path);
                    if (request.Id != previous)
                    {
                        previous = request.Id;
                        if (!await Search(request)) break;
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
        _timeline = new(request.TimelineOrigin);
        _traceWorker = request.Partition; _traceRoute = _traceStep = _restoreDepth = 0;
        _traceStage = request.VerifyCandidate == null ? "search" : "verify";
        using var session = Trace(request.VerifyCandidate == null ? "session" : "verify", depth: 0);
        _targetLabels = request.TargetLabels;
        _includePotions = false;
        _excludedModels = new(request.ExcludedModels ?? [], StringComparer.Ordinal);
        var timer = Stopwatch.StartNew();
        var budget = new Stopwatch();
        LocalCandidate? best = null;
        int evaluated = 0, rejected = 0, victories = 0;
        var noPotionSearch = new LocalSearchTree(1729 + request.Partition);
        var potionSearch = new LocalSearchTree(2718 + request.Partition);
        var search = noPotionSearch;
        var refiner = new LocalRouteRefiner();
        LocalCandidate? refinementSeed = null;
        int refinements = 0;
        LocalWorkerResources.Retain = true;
        var originalScale = Engine.TimeScale;
        var originalFps = Engine.MaxFps;
        long restoreMs = 0, actionMs = 0, decisionMs = 0, verifyMs = 0;
        int executed = 0, restores = 0;
        async Task RestoreMeasured()
        {
            var started = Stopwatch.GetTimestamp();
            try { await Restore(request); }
            finally { restoreMs += (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds; restores++; }
        }
        long sequence = 0, lastProgress = -1000;
        int route = 0, bestRoute = 0;
        var events = new Queue<LocalSimEvent>();
        LocalAction? pendingAction = null;
        LocalAction? blockedAction = null;
        void Progress(string phase, LocalSimState? state = null, bool force = false, string status = "running")
        {
            if (!force && timer.ElapsedMilliseconds - lastProgress < 100) return;
            using var publishing = Trace("publish", "过程进度");
            lastProgress = timer.ElapsedMilliseconds;
            LocalWire.Write(Path.Combine(_root, "progress.json"), new LocalProgress(request.Id, request.SnapshotId,
                request.Partition, request.Partitions, ++sequence, route, evaluated, request.MaxNodes, victories,
                budget.ElapsedMilliseconds, request.BudgetSeconds, phase, state, events.ToArray(), status));
        }
        void Publish(string status, string message)
        {
            using var publishing = Trace("publish", "候选结果");
            LocalWire.Write(Path.Combine(_root, "result.json"),
            new LocalSearchResult(request.Id, request.SnapshotId, status, message, evaluated, rejected, timer.ElapsedMilliseconds, best,
                Victories: victories, IncludePotions: request.IncludePotions,
                Timing: new(restoreMs, actionMs, decisionMs, verifyMs, Actions: executed, Restores: restores, Verifications: verifyMs > 0 ? 1 : 0),
                BlockedAction: blockedAction, MaxRounds: request.MaxRounds,
                Trace: status == "running" ? null : _timeline!.Snapshot()));
        }
        try
        {
            if (request.Partitions is < 1 or > 16 || request.Partition < 0 || request.Partition >= request.Partitions ||
                request.MaxNodes is < 1 or > 128 || request.MaxDepth is < 1 or > 64 || request.BudgetSeconds is < 1 or > 120 ||
                request.MaxRounds is < 1 or > 128 || request.SimulationSpeed is < 1 or > 16)
                throw new InvalidDataException("Invalid search limits");
            // Owned worker only. Accelerate native animation/timer waits, never model effects/RNG.
            if (request.SimulationSpeed > 1) { Engine.TimeScale = request.SimulationSpeed; Engine.MaxFps = 240; }
            LocalWorkerVisuals.Active = request.SimulationSpeed > 1;
            if (request.ModelHash != ModelIdSerializationCache.Hash || !request.LoadedMods.SequenceEqual(LocalCapture.LoadedMods()))
                throw new InvalidOperationException("后台的游戏模型或 Mod 清单与当前游戏不一致，请重启游戏后重试。");
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
                best = best with { Continuation = verified.Points };
                Progress("计算完成 · 最终候选复核通过", verified.State, true, "done");
                await Cleanup();
                session?.Dispose();
                Publish("done", "最终候选的每步状态和战后结算已独立复核。");
                return true;
            }
            Publish("running", "正在恢复并核对当前战斗…");
            Progress("恢复当前战斗", force: true);
            await RestoreMeasured();
            // Cold asset loading and root restoration are preparation, not tactical exploration.
            // Otherwise a small budget can expire before the first complete combat rollout.
            budget.Start();
            var decisionStarted = Stopwatch.GetTimestamp();
            LocalAction[] first;
            using (Trace("decision")) first = EnumerateActions();
            decisionMs += (long)Stopwatch.GetElapsedTime(decisionStarted).TotalMilliseconds;
            var roots = first.Where((_, i) => i % request.Partitions == request.Partition).ToArray();
            // More workers than first moves explore different continuations of the same first move.
            if (roots.Length == 0) roots = [first[request.Partition % first.Length]];
            var initialEnemyHp = CombatManager.Instance.DebugOnlyGetState()!.Enemies.Sum(e => Math.Max(0, e.CurrentHp));
            while (evaluated < request.MaxNodes && budget.Elapsed.TotalSeconds < request.BudgetSeconds &&
                (!noPotionSearch.Exhausted || request.IncludePotions && !potionSearch.Exhausted || refiner.Count > 0))
            {
                using var refining = Trace("refine");
                // Other workers' candidates seed exploration only. Their results are never adopted without
                // executing the proposed line in this worker, including all native effects and choices.
                foreach (var peer in Directory.EnumerateDirectories(Path.GetDirectoryName(_root)!, "worker-*"))
                {
                    var file = Path.Combine(peer, "result.json");
                    if (peer == _root || !File.Exists(file)) continue;
                    try
                    {
                        var result = LocalWire.Read<LocalSearchResult>(file);
                        if (result.Id == request.Id && result.SnapshotId == request.SnapshotId && result.Best is { } seed &&
                            LocalSearchPolicy.Better(seed, refinementSeed)) refinementSeed = seed;
                    }
                    catch (IOException) { /* Peer can be replacing its private IPC file. */ }
                }
                if (best != null && LocalSearchPolicy.Better(best, refinementSeed)) refinementSeed = best;
                if (refinementSeed != null) refiner.Offer(refinementSeed, request.Partition, request.Partitions);
                LocalAction[]? planned = null;
                if (evaluated == 0 && request.InitialPlan is { Length: > 0 }) planned = request.InitialPlan;
                if ((evaluated % 2 == 1 || search.Exhausted) && refiner.TryTake(out var proposal))
                { planned = proposal; refinements++; }
                route = evaluated + 1;
                _traceRoute = route; _traceStep = 0;
                refining?.Dispose();
                events.Clear();
                Progress("恢复路线起点", force: true);
                if (evaluated > 0) await RestoreMeasured();
                var player = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState()!)!;
                var startRound = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                var startingHp = player.Creature.CurrentHp;
                // Always establish a no-potion baseline. Then interleave reserve-potion searches,
                // unless a preferred candidate explicitly needs one. Both classes get fresh trials.
                _includePotions = request.IncludePotions && (planned?.Any(a => a.PotionSlot.HasValue) == true ||
                    evaluated > 0 && evaluated % 3 == 2);
                search = _includePotions ? potionSearch : noPotionSearch;
                if (search.Exhausted && planned == null)
                {
                    if (!request.IncludePotions || (_includePotions ? noPotionSearch : potionSearch).Exhausted) break;
                    _includePotions = !_includePotions;
                    search = _includePotions ? potionSearch : noPotionSearch;
                }
                var actions = new List<LocalAction>();
                var decisions = new List<LocalDecision>();
                var trial = search.Begin();
                int planIndex = 0;
                int lost = 0;
                void HpChanged(int oldHp, int newHp) => lost += Math.Max(0, oldHp - newHp);
                player.Creature.CurrentHpChanged += HpChanged;
                try
                {
                    Progress("开始试走", Observe(player), true);
                    int plays = 0;
                    string stop = "";
                    while (!IsTerminal(player))
                    {
                        var round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                        if (round - startRound >= request.MaxRounds) { stop = "达到轮数上限"; break; }
                        if (budget.Elapsed.TotalSeconds >= request.BudgetSeconds) { stop = "达到时间预算"; break; }
                        if (plays >= request.MaxDepth)
                        {
                            // Infinite/very long zero-cost cycles are bounded, not assumed equivalent or worthless.
                            stop = "达到单回合操作上限"; break;
                        }
                        decisionStarted = Stopwatch.GetTimestamp();
                        using var deciding = Trace("decision");
                        var legal = EnumerateActions();
                        LocalAction? preferred = null;
                        LocalAction? plannedAction = null;
                        if (planned != null)
                        {
                            while (planIndex < planned.Length && preferred == null)
                            {
                                plannedAction = planned[planIndex++];
                                preferred = LocalRouteRefiner.Resolve(plannedAction, legal);
                            }
                        }
                        else if (evaluated == 0 && actions.Count == 0)
                            preferred = roots.OrderByDescending(a => a.Preference).First();
                        var next = search.Select(trial, legal, preferred);
                        var choiceDecisions = new List<LocalChoiceDecision>();
                        decisions.Add(new(actions.Count, legal));
                        decisionMs += (long)Stopwatch.GetElapsedTime(decisionStarted).TotalMilliseconds;
                        deciding?.Dispose();
                        var before = Observe(player);
                        Progress("执行：" + LocalSearchPolicy.Describe(next), before);
                        var priorLost = lost;
                        var actionStarted = Stopwatch.GetTimestamp();
                        pendingAction = next;
                        _traceStep = actions.Count + 1;
                        try
                        {
                            int choiceIndex = 0;
                            next = await Play(next, options =>
                            {
                                choiceDecisions.Add(new(choiceDecisions.Count, options));
                                // A choice is a child of the actual action prefix, so siblings get independent outcomes.
                                var expected = preferred != null ? plannedAction?.Choices?.ElementAtOrDefault(choiceIndex++) : null;
                                var match = expected == null ? null : options.SingleOrDefault(c => c.OfferHash == expected.OfferHash &&
                                    c.Kind == expected.Kind && c.Index == expected.Index && c.ModelId == expected.ModelId &&
                                    (c.Indices ?? []).SequenceEqual(expected.Indices ?? []));
                                var choices = options.Select(c => new LocalAction(c.Index, "choice:" + c.ModelId,
                                    null, c.Name, "", c.OfferHash, round)).ToArray();
                                var selected = search.Select(trial, choices, match == null ? null : choices.Single(c => c.HandIndex == match.Index));
                                return options.Single(c => c.Index == selected.HandIndex);
                            });
                        }
                        finally { actionMs += (long)Stopwatch.GetElapsedTime(actionStarted).TotalMilliseconds; executed++; }
                        actions.Add(next);
                        decisions[^1] = decisions[^1] with { Choices = choiceDecisions.ToArray() };
                        pendingAction = null;
                        var after = Observe(player);
                        var changes = before != null && after != null ? LocalProgressBook.Changes(before, after) : "动作已结算；状态预览暂不可用。";
                        if (lost > priorLost) changes += $"；期间实际失去生命 {lost - priorLost}";
                        events.Enqueue(new(actions.Count, next.Round, LocalSearchPolicy.Describe(next), changes));
                        while (events.Count > 12) events.Dequeue();
                        Progress("试走路线", after);
                        plays = next.EndTurn ? 0 : plays + 1;
                    }
                    await StableOrTerminal(player);
                    var state = CombatManager.Instance.DebugOnlyGetState();
                    var won = _combatWon && !player.Creature.IsDead;
                    var candidate = new LocalCandidate(actions.ToArray(), player.Creature.CurrentHp, lost,
                        state?.Enemies.Sum(e => Math.Max(0, e.CurrentHp)) ?? 0,
                        player.Gold, player.Creature.MaxHp, won, player.Creature.IsDead,
                        // A general Mod reward classifier does not exist yet. Unknown must not enable early exit.
                        RewardCoverageKnown: false,
                        Rounds: actions.Select(a => a.Round).Distinct().Count(),
                        StopReason: won ? "胜利结算完成" : player.Creature.IsDead ? "玩家死亡" :
                            string.IsNullOrEmpty(stop) ? "战斗结束但未确认胜利" : stop, Decisions: decisions.ToArray(), StartingHp: startingHp);
                    evaluated++;
                    if (won) victories++;
                    Progress(won ? "路线获胜" : candidate.StopReason, Observe(player), true);
                    if (LocalSearchPolicy.Better(candidate, best)) { best = candidate; bestRoute = route; }
                    // A wall-clock interruption says nothing about the strength of this continuation.
                    if (stop != "达到时间预算") search.Complete(trial, candidate, initialEnemyHp, closeExactPrefix: true);
                    Publish("running", $"已找到 {victories} 条整场获胜路线；已记录 {noPotionSearch.Nodes + (request.IncludePotions ? potionSearch.Nodes : 0)} 个操作树节点，按实际结算反馈选路。");
                    if (LocalSearchPolicy.CanStop(candidate, request.ContinueOptimization)) break;
                }
                catch (LocalChoiceException ex)
                {
                    rejected++;
                    blockedAction = pendingAction;
                    session?.Dispose();
                    // A pending selector may own callbacks. Retire this process instead of resetting it under them.
                    Publish(best == null ? "unsupported" : "partial", ex.Message);
                    Progress("遇到额外选择，停止搜索", force: true, status: "unsupported");
                    return false;
                }
                finally { player.Creature.CurrentHpChanged -= HpChanged; }
            }
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
                best = best with { Continuation = verified.Points };
                Progress("计算完成 · 最佳路线复核通过", verified.State, true, "done");
            }
            else if (best == null) Progress("未取得可用路线", force: true, status: "unsupported");
            else Progress("搜索完成 · 等待最终候选复核", force: true, status: "searched");
            await Cleanup();
            session?.Dispose();
            Publish(best == null ? "unsupported" : request.DeferVerification ? "searched" : "done", best == null ? "没有找到可完整结算的路线。" :
                $"已完成当前预算，操作树 {noPotionSearch.Nodes + (request.IncludePotions ? potionSearch.Nodes : 0)} 个节点，比较了 {refinements} 条补牌、删牌、换牌和选牌路线。");
            return true;
        }
        catch (Exception ex)
        {
            // Any replay divergence invalidates this worker's result, including previous candidates.
            best = null;
            Progress("失败：" + ex.Message, force: true, status: "failed");
            session?.Dispose();
            Publish("failed", ex.Message);
            return false;
        }
        finally { LocalWorkerVisuals.Active = false; LocalWorkerResources.Retain = false; Engine.TimeScale = originalScale; Engine.MaxFps = originalFps; _timeline = null; }
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
        _traceStage = "verify"; _traceRoute = _traceStep = 0;
        await Restore(request);
        var player = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState()!)!;
        _includePotions = request.IncludePotions;
        if (candidate.StartingHp is { } expectedHp && player.Creature.CurrentHp != expectedHp)
            throw new InvalidOperationException("候选起点生命不一致，未发布该路线。");
        int lost = 0;
        var points = new List<LocalContinuationPoint>();
        bool canContinue = request.History != null && request.History == LocalCapture.History();
        void HpChanged(int oldHp, int newHp) => lost += Math.Max(0, oldHp - newHp);
        player.Creature.CurrentHpChanged += HpChanged;
        try
        {
            int step = 0;
            foreach (var action in candidate.Actions)
            {
                if (canContinue) points.Add(new(step, action.BeforeHash, LocalCapture.History(), lost, player.Creature.CurrentHp));
                var before = Observe(player);
                _traceStep = step + 1;
                await Play(action);
                progress(action, ++step, before, Observe(player));
            }
            await StableOrTerminal(player);
            var enemyHp = CombatManager.Instance.DebugOnlyGetState()?.Enemies.Sum(e => Math.Max(0, e.CurrentHp)) ?? 0;
            if ((_combatWon && !player.Creature.IsDead) != candidate.Won || player.Creature.IsDead != candidate.Dead ||
                player.Creature.CurrentHp != candidate.Hp || lost != candidate.HpLost || enemyHp != candidate.EnemyHp ||
                player.Gold != candidate.Gold || player.Creature.MaxHp != candidate.MaxHp)
                throw new InvalidOperationException("最佳路线重新执行后的结算不一致，未发布该进程的建议。");
            return (Observe(player), points.ToArray());
        }
        finally { player.Creature.CurrentHpChanged -= HpChanged; }
    }

    private static async Task Cleanup(int depth = 1)
    {
        using var cleanup = Trace("cleanup", depth: depth);
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

    private static async Task Restore(LocalSearchRequest request)
    {
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
            if (_assetsRequest != request.Id)
            {
                using var assets = Trace("assets", depth: 2);
                await WaitAssets();
                await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
                await PreloadManager.LoadActAssets(run.Acts[run.CurrentActIndex]);
                _assetsRequest = request.Id;
            }
            using var scene = Trace("scene", depth: 2);
            manager.Launch();
            NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await manager.GenerateMap();
            if (request.DebugEncounter is { } encounter)
                await manager.EnterRoomDebug(RoomType.Monster, MapPointType.Monster,
                    ModelDb.AllEncounters.Single(e => e.Id.Entry == encounter).ToMutable(), false);
            else await manager.LoadIntoLatestMapCoord(null);
            var player = LocalContext.GetMe(run)!;
            await StableOrTerminal(player);
            scene?.Dispose();
            using var history = Trace("history", depth: 2);
            foreach (var item in replay.events)
            {
                if (item.eventType is CombatReplayEventType.HookAction or CombatReplayEventType.PlayerChoice or CombatReplayEventType.ResumeAction)
                    continue; // The native synchronization path restores completed choices without a UI.
                if (item.eventType != CombatReplayEventType.GameAction || item.action == null)
                    throw new InvalidOperationException("本地重放暂不支持这场战斗中的额外选择。");
                var action = item.action.ToGameAction(player);
                if (action is UsePotionAction use)
                {
                    var potion = player.GetPotionAtSlotIndex((int)use.PotionIndex) ?? throw new InvalidOperationException("历史药水槽不一致。");
                    var target = use.TargetId == null ? null : player.Creature.CombatState!.Creatures.Single(c => c.CombatId == use.TargetId);
                    potion.EnqueueManualUse(target);
                    await Frame(); await WaitActionQueue(); await StableOrTerminal(player);
                }
                else if (action is PlayCardAction)
                {
                    manager.ActionQueueSet.EnqueueWithoutSynchronizing(action);
                    await Frame();
                    await WaitActionQueue();
                    await StableOrTerminal(player);
                }
                else if (action is EndPlayerTurnAction) await EndTurn(player);
                else if (action is not ReadyToBeginEnemyTurnAction)
                    throw new InvalidOperationException("本地重放暂不支持历史操作：" + action.GetType().Name);
            }
            historical.Finish();
            history?.Dispose();
            using var fingerprint = Trace("fingerprint", depth: 2);
            if (IsTerminal(player) || LocalCapture.Fingerprint() != request.NativeHash)
                throw new InvalidOperationException("后台重放与当前战斗状态不一致，未发布本地建议。可切换 AI 模式。");
        }
        finally { _restoreDepth = 0; }
    }

    private static LocalAction[] EnumerateActions()
    {
        var state = CombatManager.Instance.DebugOnlyGetState()!;
        var player = LocalContext.GetMe(state)!;
        var hash = LocalCapture.Fingerprint();
        var result = new List<LocalAction>();
        var hand = player.PlayerCombatState!.Hand.Cards;
        var tactics = LocalTacticalPreview.Capture(player);
        for (var i = 0; i < hand.Count; i++)
        {
            var card = hand[i];
            if (!card.CanPlay() || _excludedModels.Contains(card.Id.ToString())) continue;
            // Do not assume that attacks precede setup, or that zero damage means no value.
            if (card.IsValidTarget(null)) result.Add(new(i, card.Id.ToString(), null, card.Title, "", hash, state.RoundNumber,
                Preference: tactics.Priority(card, null), CombatCardIndex: NetCombatCard.FromModel(card).CombatCardIndex));
            else foreach (var target in state.Creatures.Where(c => c.IsAlive && card.IsValidTarget(c)))
                result.Add(new(i, card.Id.ToString(), target.CombatId, card.Title,
                    target.CombatId is { } id && _targetLabels?.TryGetValue(id, out var label) == true ? label : target.Name,
                    hash, state.RoundNumber, Preference: tactics.Priority(card, target), CombatCardIndex: NetCombatCard.FromModel(card).CombatCardIndex));
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
        return result.ToArray();
    }

    private static async Task<LocalAction> Play(LocalAction action, Func<LocalCardChoice[], LocalCardChoice>? choose = null)
    {
        using var playing = Trace(action.EndTurn ? "end_turn" : action.PotionSlot.HasValue ? "potion" : "card",
            action.EndTurn ? $"第 {action.Round} 回合" : action.CardName);
        var session = new LocalChoices(action.Choices, choose);
        _choices = session;
        try
        {
            await PlayNative(action);
            session.Finish();
            return action with { Choices = session.Completed };
        }
        catch (LocalChoiceException ex)
        { throw new LocalChoiceException(LocalSearchPolicy.Describe(action) + " " + ex.Message); }
        finally { _choices = null; }
    }

    private static async Task PlayNative(LocalAction action)
    {
        if (LocalCapture.Fingerprint() != action.BeforeHash) throw new InvalidOperationException("搜索分支状态复现不一致。");
        var state = CombatManager.Instance.DebugOnlyGetState()!;
        var player = LocalContext.GetMe(state)!;
        if (action.EndTurn) { await EndTurn(player); return; }
        var target = action.TargetId == null ? null : state.Creatures.Single(c => c.CombatId == action.TargetId);
        if (action.PotionSlot is { } slot)
        {
            var potion = player.GetPotionAtSlotIndex(slot);
            if (!_includePotions || potion == null || potion.Id.ToString() != action.ModelId || !PotionUsable(potion) || !potion.IsValidTarget(target))
                throw new InvalidOperationException("药水实例或目标不再合法。");
            potion.EnqueueManualUse(target);
            await Frame(); await WaitActionQueue(); await StableOrTerminal(player);
            return;
        }
        var card = player.PlayerCombatState!.Hand.Cards[action.HandIndex];
        if (card.Id.ToString() != action.ModelId || !card.CanPlay() || !card.IsValidTarget(target))
            throw new InvalidOperationException("出牌实例或目标不再合法。");
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, target));
        await Frame();
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
        while (!pending.IsCompleted)
        {
            _choices?.Tick();
            if (timer.Elapsed.TotalSeconds > 5) throw new LocalChoiceException("等待动作结算超时（" + LocalChoices.PendingDescription() + "）。");
            await Frame();
        }
        await pending;
    }

    private static bool IsTerminal(Player player) => !CombatManager.Instance.IsStarting &&
        (player.Creature.IsDead || CombatManager.Instance.IsOverOrEnding);

    private static async Task EndTurn(Player player)
    {
        var round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new EndPlayerTurnAction(player, player.PlayerCombatState!.TurnNumber));
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
            if (IsTerminal(player))
            {
                // CombatEnded fires after native victory/death hooks, not when the final enemy merely reaches 0 HP.
                if (_combatSettled)
                {
                    await WaitActionQueue();
                    await Frame();
                    return;
                }
            }
            else if (player.PlayerCombatState?.Phase == PlayerTurnPhase.Play && !CombatManager.Instance.PlayerActionsDisabled &&
                player.PlayerCombatState.PlayPile.IsEmpty && RunManager.Instance.ActionQueueSet.BecameEmpty().IsCompletedSuccessfully)
            { await Frame(); return; }
            _choices?.Tick();
            if (timer.Elapsed.TotalSeconds > 8) throw new LocalChoiceException("等待战斗稳定超时（" + LocalChoices.PendingDescription() + "）。");
            await Frame();
        }
    }

    private sealed class LocalChoiceException(string message) : Exception(message);
}

internal static class SignalTask
{
    public static async Task AsTask(this SignalAwaiter signal) => await signal;
}
