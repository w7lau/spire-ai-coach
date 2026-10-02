using System.Text.Json;

namespace SpireAiCoach.Core;

// Local IPC only: no provider calls or credentials are part of this protocol.
public sealed record LocalSearchRequest(string Id, string SnapshotId, byte[] Replay,
    string NativeHash, uint ModelHash, string[] LoadedMods, bool ContinueOptimization,
    int Partition = 0, int Partitions = 2, int MaxNodes = 16, int MaxDepth = 8,
    int BudgetSeconds = 60, string? DebugEncounter = null,
    IReadOnlyDictionary<uint, string>? TargetLabels = null);

public sealed record LocalAction(int HandIndex, string ModelId, uint? TargetId,
    string CardName, string TargetName, string BeforeHash);

public sealed record LocalCandidate(LocalAction[] Actions, int Hp, int HpLost, int EnemyHp,
    int Gold, int MaxHp, bool Won, bool Dead, bool RewardCoverageKnown);

public sealed record LocalSearchResult(string Id, string SnapshotId, string Status,
    string Message, int Evaluated, int Rejected, long ElapsedMs, LocalCandidate? Best);

public static class LocalSearchPolicy
{
    public static bool CanStop(LocalCandidate candidate, bool continueOptimization) =>
        !continueOptimization && !candidate.Dead && candidate.HpLost == 0 && candidate.RewardCoverageKnown;

    // Survival, final HP, permanent HP/gold, then remaining enemy HP. Not a global strategy proof.
    public static bool Better(LocalCandidate candidate, LocalCandidate? prior)
    {
        if (prior == null) return true;
        if (candidate.Dead != prior.Dead) return !candidate.Dead;
        if (candidate.Hp != prior.Hp) return candidate.Hp > prior.Hp;
        if (candidate.MaxHp != prior.MaxHp) return candidate.MaxHp > prior.MaxHp;
        if (candidate.Gold != prior.Gold) return candidate.Gold > prior.Gold;
        if (candidate.Won != prior.Won) return candidate.Won;
        if (candidate.EnemyHp != prior.EnemyHp) return candidate.EnemyHp < prior.EnemyHp;
        return candidate.Actions.Length < prior.Actions.Length;
    }

    public static string Format(LocalSearchResult result)
    {
        if (result.Best is not { } best) return result.Message;
        var lines = new List<string> { "本地原生模拟 · 预算内候选方案", result.Message,
            $"已结算 {result.Evaluated} 条路线；跳过 {result.Rejected} 条无法完成的路线。",
            $"预测结算：生命 {best.Hp}，期间失去生命 {best.HpLost}，敌人剩余生命合计 {best.EnemyHp}。" };
        if (best.Dead) lines.Add("注意：目前找到的路线仍会死亡，不能保证存活。");
        for (var i = 0; i < best.Actions.Length; i++)
        {
            var action = best.Actions[i];
            lines.Add($"{i + 1}. 打出「{action.CardName}」（当时手牌第 {action.HandIndex + 1} 张）" +
                (action.TargetId is null ? "。" : $" → {action.TargetName}［目标 {action.TargetId}］。"));
        }
        lines.Add(best.Won ? "战斗已结束。" : $"{best.Actions.Length + 1}. 结束回合。");
        lines.Add("仅搜索当前回合，不使用药水；选牌与特殊流程可能被跳过。未覆盖所有路线，也未证明任意 Mod 私有状态可恢复。出牌后请重新计算。" );
        return string.Join("\n", lines);
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
