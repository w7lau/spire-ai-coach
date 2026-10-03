using System.Text.Json;

namespace SpireAiCoach.Core;

// Local IPC only: no provider calls or credentials are part of this protocol.
public sealed record LocalSearchRequest(string Id, string SnapshotId, byte[] Replay,
    string NativeHash, uint ModelHash, string[] LoadedMods, bool ContinueOptimization,
    int Partition = 0, int Partitions = 2, int MaxNodes = 32, int MaxDepth = 24,
    int BudgetSeconds = 60, string? DebugEncounter = null,
    IReadOnlyDictionary<uint, string>? TargetLabels = null, int MaxRounds = 64,
    int Workers = 0, bool IncludePotions = false, LocalHistoryStamp? History = null, string[]? ExcludedModels = null,
    LocalAction[]? InitialPlan = null, int SimulationSpeed = 8, bool DeferVerification = false,
    LocalCandidate? VerifyCandidate = null, long TimelineOrigin = 0, LocalTrace? InitialTrace = null,
    bool FastCardPresentation = true, bool FastNativeWaits = true, bool ShareSearchWork = true,
    bool FastStateSettling = true, bool FastAssetCollection = false, bool StrategicRollouts = true,
    bool ExperimentalNativeData = false, byte[]? RecordedReplayProbe = null, bool DataOnlyCombat = true,
    bool DataOnlyRun = true, bool NumericalExecution = true, bool StopOnZeroLoss = true, bool TrimWorkerOverhead = true,
    bool FastVerification = true);

// A stop belongs to one frozen request, never to another battle or final verification.
public sealed record LocalSearchStop(string Id, string SnapshotId, string NativeHash)
{
    public bool Matches(LocalSearchRequest request) => request.StopOnZeroLoss && request.VerifyCandidate == null &&
        Id == request.Id && SnapshotId == request.SnapshotId && NativeHash == request.NativeHash;
}

public sealed record LocalAction(int HandIndex, string ModelId, uint? TargetId,
    string CardName, string TargetName, string BeforeHash, int Round = 0,
    bool EndTurn = false, int Preference = 0, int? PotionSlot = null, LocalCardChoice[]? Choices = null,
    uint? CombatCardIndex = null);

// Index is relative to this exact ordered native offer, never to a display-name lookup.
public sealed record LocalCardChoice(string OfferHash, int Index, string ModelId, string Name,
    int[]? Indices = null, string Kind = "offer", int Preference = 0);

public sealed record LocalCandidate(LocalAction[] Actions, int Hp, int HpLost, int EnemyHp,
    int Gold, int MaxHp, bool Won, bool Dead, bool RewardCoverageKnown,
    int Rounds = 0, string StopReason = "", LocalContinuationPoint[]? Continuation = null,
    LocalDecision[]? Decisions = null, int? StartingHp = null)
{
    // Gross HP costs remain useful diagnostics, but healing and victory hooks are part of the goal.
    public int? NetHpLoss => StartingHp.HasValue ? Math.Max(0, StartingHp.Value - Hp) : null;
}

// Legal alternatives observed before a real native action. Search hints only, never instructions.
public sealed record LocalDecision(int BeforeStep, LocalAction[] Legal, LocalChoiceDecision[]? Choices = null);
public sealed record LocalChoiceDecision(int AtChoice, LocalCardChoice[] Legal);

public sealed record LocalSearchResult(string Id, string SnapshotId, string Status,
    string Message, int Evaluated, int Rejected, long ElapsedMs, LocalCandidate? Best,
    int Duplicates = 0, int BudgetPruned = 0, int Victories = 0, int Workers = 1,
    long WorkerMemoryBytes = 0, long SearchElapsedMs = 0, bool IncludePotions = false, LocalSearchTiming? Timing = null,
    LocalAction? BlockedAction = null, int MaxRounds = 64, LocalTrace? Trace = null, LocalWorkStats? Work = null,
    bool StoppedEarly = false);

public static class LocalSearchPolicy
{
    public static bool CanStop(LocalCandidate? candidate, bool stopOnZeroLoss) =>
        stopOnZeroLoss && candidate is { Won: true, Dead: false, NetHpLoss: 0 };

    // Same root, completed native victory: net HP loss first, potions are a reserve resource.
    public static bool Better(LocalCandidate candidate, LocalCandidate? prior)
    {
        if (prior == null) return true;
        if (candidate.Won != prior.Won) return candidate.Won;
        if (candidate.Dead != prior.Dead) return !candidate.Dead;
        // Unfinished horizons are not comparable to completed victories. Prefer progress within that fallback class.
        if (!candidate.Won && candidate.EnemyHp != prior.EnemyHp) return candidate.EnemyHp < prior.EnemyHp;
        bool sameRoot = candidate.StartingHp.HasValue && candidate.StartingHp == prior.StartingHp;
        if (sameRoot && candidate.NetHpLoss != prior.NetHpLoss) return candidate.NetHpLoss < prior.NetHpLoss;
        if (!sameRoot && candidate.Hp != prior.Hp) return candidate.Hp > prior.Hp;
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
        if (result.Best is not { } best) return result.Message;
        var lines = new List<string> { best.Won ? "本地整场战斗 · 已找到获胜路线" : "本地整场战斗 · 尚未找到获胜路线", result.Message,
            $"{result.Workers} 路并发，评估 {result.Evaluated} 条路线，其中 {result.Victories} 条获胜，不支持 {result.Rejected}。",
            best.StartingHp is { } initial ?
                $"{(best.Won ? "预测战后生命" : "已模拟到的生命")} {initial} → {best.Hp}/{best.MaxHp}；净生命损失 {best.NetHpLoss}{(best.Won ? "（包含战中、战后回血）" : "（战斗尚未完成）")}。" :
                $"{(best.Won ? "预测战后生命" : "已模拟到的生命")} {best.Hp}/{best.MaxHp}。",
            $"过程累计扣血 {best.HpLost}" + (best.StartingHp is { } start ? $"，已恢复或增加生命 {Math.Max(0, best.Hp - start + best.HpLost)}" : "") +
                $"；敌人剩余生命合计 {best.EnemyHp}。" };
        if (best.Won && best.NetHpLoss == 0 && !best.Actions.Any(a => a.PotionSlot.HasValue))
            lines.Add("已达到战后净损失 0 且不消耗药水的目标；其他收益和最短路线未证明最优。");
        if (!best.Won) lines.Add("以下仅为已模拟的部分路线，不代表能打赢本次战斗。停止原因：" + best.StopReason);
        if (best.Dead) lines.Add("注意：目前找到的路线仍会死亡，不能保证存活。");
        if (result.Work is { } work) lines.Add($"分支分工：领取 {work.Claimed} 项任务，合并 {work.DuplicateOffers} 次重复提交。");
        lines.Add($"计算用时 {result.ElapsedMs / 1000d:F1} 秒。");
        int round = -1;
        for (var i = 0; i < best.Actions.Length; i++)
        {
            var action = best.Actions[i];
            if (action.Round != round) { round = action.Round; lines.Add($"—— 第 {round} 回合 ——"); }
            lines.Add($"{i + 1}. " + Describe(action));
        }
        if (best.Won) lines.Add("模拟结果：战斗获胜。");
        lines.Add($"最多规划 {result.MaxRounds} 轮；{(result.IncludePotions ? "已纳入主动使用药水，优先保留药水" : "未纳入主动使用药水（自动触发仍按游戏结算）")}；选牌组合受搜索预算限制。实际状态偏离时请重新计算。" );
        return string.Join("\n", lines);
    }

    public static string Describe(LocalAction action) => (action.EndTurn ? "结束回合，结算敌方行动。" :
        (action.PotionSlot is { } slot ? $"使用药水「{action.CardName}」（药水槽 {slot + 1}）" :
            $"打出「{action.CardName}」（当时手牌第 {action.HandIndex + 1} 张）") +
        (action.TargetId is null ? "。" : $" → {action.TargetName}［目标 {action.TargetId}］。")) +
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
    public static void Write<T>(string path, T value)
    {
        using var lease = new FileLease(path);
        // Never reuse a just-retired staging path. On Windows it can still be held by
        // an external reader/scanner after replacement. ReplaceFile preserves an
        // already-published document atomically instead of deleting its directory entry.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
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
