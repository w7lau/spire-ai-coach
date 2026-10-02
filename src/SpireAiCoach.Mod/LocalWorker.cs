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
        int evaluated = 0, rejected = 0;
        void Publish(string status, string message) => LocalWire.Write(Path.Combine(_root, "result.json"),
            new LocalSearchResult(request.Id, request.SnapshotId, status, message, evaluated, rejected, timer.ElapsedMilliseconds, best));
        try
        {
            if (request.Partitions is < 1 or > 2 || request.Partition < 0 || request.Partition >= request.Partitions ||
                request.MaxNodes is < 1 or > 128 || request.MaxDepth is < 1 or > 12 || request.BudgetSeconds is < 1 or > 120)
                throw new InvalidDataException("Invalid search limits");
            if (request.ModelHash != ModelIdSerializationCache.Hash || !request.LoadedMods.SequenceEqual(LocalCapture.LoadedMods()))
                throw new InvalidOperationException("后台的游戏模型或 Mod 清单与当前游戏不一致，请重启游戏后重试。");
            Publish("running", "正在恢复并核对当前战斗…");
            await Restore(request);
            var first = EnumerateActions();
            var queue = new PriorityQueue<LocalAction[], (int Depth, int Order)>();
            int order = 0;
            if (request.Partition == 0) queue.Enqueue([], (0, order++));
            for (var i = 0; i < first.Length; i++)
                if (i % request.Partitions == request.Partition) queue.Enqueue([first[i]], (1, order++));
            while (queue.TryDequeue(out var actions, out _) && evaluated < request.MaxNodes && timer.Elapsed.TotalSeconds < request.BudgetSeconds)
            {
                await Restore(request);
                var player = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState()!)!;
                int lost = 0;
                void HpChanged(int oldHp, int newHp) => lost += Math.Max(0, oldHp - newHp);
                player.Creature.CurrentHpChanged += HpChanged;
                try
                {
                    foreach (var action in actions) await Play(action);
                    var children = IsTerminal(player) ? [] : EnumerateActions();
                    if (!IsTerminal(player)) await EndTurn(player);
                    await StableOrTerminal(player);
                    var state = CombatManager.Instance.DebugOnlyGetState();
                    var won = _combatWon && !player.Creature.IsDead;
                    var candidate = new LocalCandidate(actions, player.Creature.CurrentHp, lost,
                        state?.Enemies.Sum(e => Math.Max(0, e.CurrentHp)) ?? 0,
                        player.Gold, player.Creature.MaxHp, won, player.Creature.IsDead,
                        // A general Mod reward classifier does not exist yet. Unknown must not enable early exit.
                        RewardCoverageKnown: false);
                    evaluated++;
                    if (LocalSearchPolicy.Better(candidate, best)) best = candidate;
                    Publish("running", "正在比较出牌顺序；未识别的成长收益不会触发无伤提前停止。");
                    if (LocalSearchPolicy.CanStop(candidate, request.ContinueOptimization)) break;
                    if (actions.Length < request.MaxDepth)
                        foreach (var child in children)
                        queue.Enqueue([.. actions, child], (-(actions.Length + 1), order++));
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
            Publish(best == null ? "unsupported" : "done", best == null ? "没有找到可完整结算的路线。" :
                "当前预算内完成的候选结果；未证明最优，奖励机制覆盖尚不完整。");
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
            if (card.IsValidTarget(null)) result.Add(new(i, card.Id.ToString(), null, card.Title, "", hash));
            else foreach (var target in state.Creatures.Where(c => c.IsAlive && card.IsValidTarget(c)))
                result.Add(new(i, card.Id.ToString(), target.CombatId, card.Title,
                    target.CombatId is { } id && _targetLabels?.TryGetValue(id, out var label) == true ? label : target.Name, hash));
        }
        return result.ToArray();
    }

    private static async Task Play(LocalAction action)
    {
        if (LocalCapture.Fingerprint() != action.BeforeHash) throw new InvalidOperationException("搜索分支状态复现不一致。");
        var state = CombatManager.Instance.DebugOnlyGetState()!;
        var player = LocalContext.GetMe(state)!;
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
