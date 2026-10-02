using System.Text.Json;

namespace SpireAiCoach.Core;

// Local IPC only: no provider calls or credentials are part of this protocol.
public sealed record LocalSearchRequest(string Id, string SnapshotId, byte[] Replay,
    string NativeHash, uint ModelHash, string[] LoadedMods, bool ContinueOptimization,
    int Partition = 0, int Partitions = 2, int MaxNodes = 32, int MaxDepth = 24,
    int BudgetSeconds = 60, string? DebugEncounter = null,
    IReadOnlyDictionary<uint, string>? TargetLabels = null, int MaxRounds = 10,
    int Workers = 0);

public sealed record LocalAction(int HandIndex, string ModelId, uint? TargetId,
    string CardName, string TargetName, string BeforeHash, int Round = 0,
    bool EndTurn = false, int Preference = 0);

public sealed record LocalCandidate(LocalAction[] Actions, int Hp, int HpLost, int EnemyHp,
    int Gold, int MaxHp, bool Won, bool Dead, bool RewardCoverageKnown,
    int Rounds = 0, string StopReason = "");

public sealed record LocalSearchResult(string Id, string SnapshotId, string Status,
    string Message, int Evaluated, int Rejected, long ElapsedMs, LocalCandidate? Best,
    int Duplicates = 0, int BudgetPruned = 0, int Victories = 0, int Workers = 1,
    long WorkerMemoryBytes = 0, long SearchElapsedMs = 0);

public static class LocalSearchPolicy
{
    public static bool CanStop(LocalCandidate candidate, bool continueOptimization) =>
        !continueOptimization && candidate.Won && !candidate.Dead && candidate.HpLost == 0 && candidate.RewardCoverageKnown;

    // Survival, final HP, permanent HP/gold, then remaining enemy HP. Not a global strategy proof.
    public static bool Better(LocalCandidate candidate, LocalCandidate? prior)
    {
        if (prior == null) return true;
        if (candidate.Won != prior.Won) return candidate.Won;
        if (candidate.Dead != prior.Dead) return !candidate.Dead;
        // Unfinished horizons are not comparable to completed victories. Prefer progress within that fallback class.
        if (!candidate.Won && candidate.EnemyHp != prior.EnemyHp) return candidate.EnemyHp < prior.EnemyHp;
        if (candidate.Hp != prior.Hp) return candidate.Hp > prior.Hp;
        if (candidate.MaxHp != prior.MaxHp) return candidate.MaxHp > prior.MaxHp;
        if (candidate.Gold != prior.Gold) return candidate.Gold > prior.Gold;
        if (candidate.EnemyHp != prior.EnemyHp) return candidate.EnemyHp < prior.EnemyHp;
        return candidate.Actions.Length < prior.Actions.Length;
    }

    public static string Format(LocalSearchResult result)
    {
        if (result.Best is not { } best) return result.Message;
        var lines = new List<string> { best.Won ? "本地整场战斗 · 已找到获胜路线" : "本地整场战斗 · 尚未找到获胜路线", result.Message,
            $"{result.Workers} 路并发，评估 {result.Evaluated} 条路线，其中 {result.Victories} 条获胜；去重 {result.Duplicates}，预算裁剪 {result.BudgetPruned}，不支持 {result.Rejected}。",
            $"预测结算：生命 {best.Hp}，期间失去生命 {best.HpLost}，敌人剩余生命合计 {best.EnemyHp}。" };
        if (!best.Won) lines.Add("以下仅为已模拟的部分路线，不代表能打赢本次战斗。停止原因：" + best.StopReason);
        if (best.Dead) lines.Add("注意：目前找到的路线仍会死亡，不能保证存活。");
        int round = -1;
        for (var i = 0; i < best.Actions.Length; i++)
        {
            var action = best.Actions[i];
            if (action.Round != round) { round = action.Round; lines.Add($"—— 第 {round} 回合 ——"); }
            lines.Add(action.EndTurn ? $"{i + 1}. 结束回合，结算敌方行动。" : $"{i + 1}. 打出「{action.CardName}」（当时手牌第 {action.HandIndex + 1} 张）" +
                (action.TargetId is null ? "。" : $" → {action.TargetName}［目标 {action.TargetId}］。"));
        }
        if (best.Won) lines.Add("原生战斗胜利结算已完成。");
        lines.Add("最多规划 10 轮；不使用药水，暂不支持额外选牌。预算裁剪可能遗漏更优路线；未证明任意 Mod 私有状态可恢复。实际出牌或随机结果有变化时请重新计算。" );
        return string.Join("\n", lines);
    }

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
        $"{a.BeforeHash}:{a.Round}:{a.EndTurn}:{a.HandIndex}:{a.ModelId}:{a.TargetId}"));
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
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path))
        ?? throw new InvalidDataException("Local worker returned empty data");
    public static void Write<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, path, true);
    }
}
