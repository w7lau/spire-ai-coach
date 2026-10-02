using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
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
    private static Task Frame() => _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame).AsTask();

    public static bool TryStart()
    {
        var directory = System.Environment.GetEnvironmentVariable("SPIRE_COACH_WORKER");
        if (directory == null) return false;
        _root = Path.GetFullPath(directory);
        if (!File.Exists(Path.Combine(_root, ".coach-worker")) ||
            !string.Equals(Path.GetFullPath(OS.GetExecutablePath()), Path.Combine(_root, "game", "SlayTheSpire2.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing a worker outside its isolated installation");
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
        _targetLabels = request.TargetLabels;
        var timer = Stopwatch.StartNew();
        LocalCandidate? best = null;
        int evaluated = 0, rejected = 0, victories = 0;
        var frontier = new LocalFrontier(256);
        void Publish(string status, string message) => LocalWire.Write(Path.Combine(_root, "result.json"),
            new LocalSearchResult(request.Id, request.SnapshotId, status, message, evaluated, rejected, timer.ElapsedMilliseconds, best,
                frontier.Duplicates, frontier.BudgetPruned, victories));
        try
        {
            if (request.Partitions is < 1 or > 16 || request.Partition < 0 || request.Partition >= request.Partitions ||
                request.MaxNodes is < 1 or > 128 || request.MaxDepth is < 1 or > 64 || request.BudgetSeconds is < 1 or > 120 ||
                request.MaxRounds is < 1 or > 10)
                throw new InvalidDataException("Invalid search limits");
            if (request.ModelHash != ModelIdSerializationCache.Hash || !request.LoadedMods.SequenceEqual(LocalCapture.LoadedMods()))
                throw new InvalidOperationException("后台的游戏模型或 Mod 清单与当前游戏不一致，请重启游戏后重试。");
            Publish("running", "正在恢复并核对当前战斗…");
            await Restore(request);
            var first = EnumerateActions().OrderByDescending(a => a.Preference).ToArray();
            for (var i = 0; i < first.Length; i++)
                if (i % request.Partitions == request.Partition) frontier.Add([first[i]], 2e12 - i);
            // More workers than first moves explore different continuations of the same first move.
            if (frontier.Count == 0) frontier.Add([first[request.Partition % first.Length]], 2e12);
            var random = new Random(1729 + request.Partition);
            while (evaluated < request.MaxNodes && timer.Elapsed.TotalSeconds < request.BudgetSeconds && frontier.TryTake(out var prefix))
            {
                if (evaluated > 0) await Restore(request);
                var player = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState()!)!;
                var startRound = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                var actions = new List<LocalAction>();
                var alternatives = new List<(LocalAction[] Actions, int Preference)>();
                int lost = 0;
                void HpChanged(int oldHp, int newHp) => lost += Math.Max(0, oldHp - newHp);
                player.Creature.CurrentHpChanged += HpChanged;
                try
                {
                    int plays = 0;
                    string stop = "";
                    while (!IsTerminal(player))
                    {
                        var round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                        if (round - startRound >= request.MaxRounds) { stop = "达到轮数上限"; break; }
                        if (timer.Elapsed.TotalSeconds >= request.BudgetSeconds) { stop = "达到时间预算"; break; }
                        LocalAction next;
                        if (actions.Count < prefix.Length) next = prefix[actions.Count];
                        else
                        {
                            var choices = EnumerateActions(); // Includes ending the turn, even when a card remains playable.
                            if (plays >= request.MaxDepth)
                            {
                                // Infinite/very long zero-cost cycles are bounded, not assumed equivalent or worthless.
                                stop = "达到单回合操作上限"; break;
                            }
                            var ordered = choices.OrderByDescending(a => a.Preference).ToArray();
                            // Guided rollout plus seeded exploration. All choices still enter the bounded frontier.
                            next = evaluated == 0 || random.Next(4) != 0 ? ordered[0] : ordered[random.Next(ordered.Length)];
                            foreach (var choice in ordered)
                                if (choice != next) alternatives.Add(([.. actions, choice], choice.Preference));
                        }
                        await Play(next);
                        actions.Add(next);
                        frontier.MarkVisited(actions.ToArray());
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
                            string.IsNullOrEmpty(stop) ? "战斗结束但未确认胜利" : stop);
                    evaluated++;
                    if (won) victories++;
                    if (LocalSearchPolicy.Better(candidate, best)) best = candidate;
                    // Prefer exploring deviations from a successful rollout, while initial moves retain coverage priority.
                    double score = (won ? 1e9 : player.Creature.IsDead ? -1e9 : 0) +
                        candidate.Hp * 10000d - candidate.EnemyHp * 100d + candidate.Gold;
                    foreach (var alternative in alternatives)
                        frontier.Add(alternative.Actions, score + alternative.Preference - alternative.Actions.Length);
                    Publish("running", $"已找到 {victories} 条整场获胜路线；每条路线连续模拟到胜负或预算边界。");
                    if (LocalSearchPolicy.CanStop(candidate, request.ContinueOptimization)) break;
                }
                catch (LocalChoiceException)
                {
                    rejected++;
                    // A pending selector may own callbacks. Retire this process instead of resetting it under them.
                    Publish(best == null ? "unsupported" : "partial", "遇到暂不支持的选牌或特殊流程，已停止这个工作进程；保留此前完整结算的路线。");
                    return false;
                }
                finally { player.Creature.CurrentHpChanged -= HpChanged; }
            }
            if (best != null)
            {
                Publish("running", "正在从当前状态重新执行最佳路线，复核每步状态与最终结算…");
                await VerifyBest(request, best);
            }
            Publish(best == null ? "unsupported" : "done", best == null ? "没有找到可完整结算的路线。" :
                "整场战斗搜索已完成当前预算；未证明最优，奖励机制覆盖尚不完整。");
            await Cleanup();
            return true;
        }
        catch (Exception ex)
        {
            // Any replay divergence invalidates this worker's result, including previous candidates.
            best = null;
            Publish("failed", ex.Message);
            return false;
        }
    }

    private static async Task VerifyBest(LocalSearchRequest request, LocalCandidate candidate)
    {
        await Restore(request);
        var player = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState()!)!;
        int lost = 0;
        void HpChanged(int oldHp, int newHp) => lost += Math.Max(0, oldHp - newHp);
        player.Creature.CurrentHpChanged += HpChanged;
        try
        {
            foreach (var action in candidate.Actions) await Play(action);
            await StableOrTerminal(player);
            var enemyHp = CombatManager.Instance.DebugOnlyGetState()?.Enemies.Sum(e => Math.Max(0, e.CurrentHp)) ?? 0;
            if ((_combatWon && !player.Creature.IsDead) != candidate.Won || player.Creature.IsDead != candidate.Dead ||
                player.Creature.CurrentHp != candidate.Hp || lost != candidate.HpLost || enemyHp != candidate.EnemyHp ||
                player.Gold != candidate.Gold || player.Creature.MaxHp != candidate.MaxHp)
                throw new InvalidOperationException("最佳路线重新执行后的结算不一致，未发布该进程的建议。");
        }
        finally { player.Creature.CurrentHpChanged -= HpChanged; }
    }

    private static async Task Cleanup()
    {
        if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control());
        await Frame(); await Frame();
        _combatSettled = false; _combatWon = false;
    }

    private static async Task Restore(LocalSearchRequest request)
    {
        await Cleanup();
        var reader = new PacketReader(); reader.Reset(request.Replay);
        var replay = reader.Read<CombatReplay>();
        var run = RunState.FromSerializable(replay.serializableRun);
        await RunManager.Instance.SetUpSavedSingleplayer(run, replay.serializableRun);
        var manager = RunManager.Instance;
        manager.ActionQueueSet.FastForwardNextActionId(replay.nextActionId);
        manager.ActionQueueSynchronizer.FastForwardHookId(replay.nextHookId);
        manager.PlayerChoiceSynchronizer.FastForwardChoiceIds(replay.choiceIds);
        manager.RewardsSetSynchronizer.FastForwardRewardIds(replay.rewardIds);
        await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
        await PreloadManager.LoadActAssets(run.Acts[run.CurrentActIndex]);
        manager.Launch();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
        await manager.GenerateMap();
        if (request.DebugEncounter is { } encounter)
            await manager.EnterRoomDebug(RoomType.Monster, MapPointType.Monster,
                ModelDb.AllEncounters.Single(e => e.Id.Entry == encounter).ToMutable(), false);
        else await manager.LoadIntoLatestMapCoord(null);
        var player = LocalContext.GetMe(run)!;
        await StableOrTerminal(player);
        foreach (var item in replay.events)
        {
            if (item.eventType == CombatReplayEventType.HookAction) continue; // Singleplayer re-executes native hooks itself.
            if (item.eventType != CombatReplayEventType.GameAction || item.action == null)
                throw new InvalidOperationException("本地重放暂不支持这场战斗中的额外选择。");
            var action = item.action.ToGameAction(player);
            if (action is PlayCardAction)
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
        if (IsTerminal(player) || LocalCapture.Fingerprint() != request.NativeHash)
            throw new InvalidOperationException("后台重放与当前战斗状态不一致，未发布本地建议。可切换 AI 模式。");
    }

    private static LocalAction[] EnumerateActions()
    {
        var state = CombatManager.Instance.DebugOnlyGetState()!;
        var player = LocalContext.GetMe(state)!;
        var hash = LocalCapture.Fingerprint();
        var result = new List<LocalAction>();
        var hand = player.PlayerCombatState!.Hand.Cards;
        for (var i = 0; i < hand.Count; i++)
        {
            var card = hand[i];
            if (!card.CanPlay()) continue;
            // Ordering only: never delete zero-damage/extra-block cards or merge same-name instances.
            int preference = card.Type == CardType.Attack ? 30 : card.Type == CardType.Power ? 20 : 10;
            if (card.IsValidTarget(null)) result.Add(new(i, card.Id.ToString(), null, card.Title, "", hash, state.RoundNumber, Preference: preference));
            else foreach (var target in state.Creatures.Where(c => c.IsAlive && card.IsValidTarget(c)))
                result.Add(new(i, card.Id.ToString(), target.CombatId, card.Title,
                    target.CombatId is { } id && _targetLabels?.TryGetValue(id, out var label) == true ? label : target.Name,
                    hash, state.RoundNumber, Preference: preference));
        }
        result.Add(new(-1, "", null, "", "", hash, state.RoundNumber, EndTurn: true));
        return result.ToArray();
    }

    private static async Task Play(LocalAction action)
    {
        if (LocalCapture.Fingerprint() != action.BeforeHash) throw new InvalidOperationException("搜索分支状态复现不一致。");
        var state = CombatManager.Instance.DebugOnlyGetState()!;
        var player = LocalContext.GetMe(state)!;
        if (action.EndTurn) { await EndTurn(player); return; }
        var card = player.PlayerCombatState!.Hand.Cards[action.HandIndex];
        var target = action.TargetId == null ? null : state.Creatures.Single(c => c.CombatId == action.TargetId);
        if (card.Id.ToString() != action.ModelId || !card.CanPlay() || !card.IsValidTarget(target))
            throw new InvalidOperationException("出牌实例或目标不再合法。");
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, target));
        await Frame();
        await WaitActionQueue();
        await StableOrTerminal(player);
    }

    private static async Task WaitActionQueue()
    {
        try { await RunManager.Instance.ActionQueueSet.BecameEmpty().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { throw new LocalChoiceException(); }
    }

    private static bool IsTerminal(Player player) => !CombatManager.Instance.IsStarting &&
        (player.Creature.IsDead || CombatManager.Instance.IsOverOrEnding);

    private static async Task EndTurn(Player player)
    {
        var round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
        PlayerCmd.EndTurn(player, canBackOut: false);
        var timer = Stopwatch.StartNew();
        while (!IsTerminal(player) && CombatManager.Instance.DebugOnlyGetState()!.RoundNumber <= round)
        {
            if (timer.Elapsed.TotalSeconds > 8) throw new LocalChoiceException();
            await Frame();
        }
        await StableOrTerminal(player);
    }

    private static async Task StableOrTerminal(Player player)
    {
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
            if (timer.Elapsed.TotalSeconds > 8) throw new LocalChoiceException();
            await Frame();
        }
    }

    private sealed class LocalChoiceException : Exception;
}

internal static class SignalTask
{
    public static async Task AsTask(this SignalAwaiter signal) => await signal;
}
