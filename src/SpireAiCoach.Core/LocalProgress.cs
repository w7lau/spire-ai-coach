namespace SpireAiCoach.Core;

// Bounded local telemetry, kept separate from completed advice. Each worker owns one monotonic sequence.
public sealed record LocalSimEnemy(uint? Id, string Name, int Hp, int MaxHp, int Block, string Powers, string Intent);
public sealed record LocalSimState(int Round, int Hp, int MaxHp, int Block, decimal Energy, string Powers,
    string[] Hand, int Potions, LocalSimEnemy[] Enemies);
public sealed record LocalSimEvent(int Step, int Round, string Action, string Changes);
// A small measured victory summary; no action graph, native objects or extra
// simulation is sent for presentation. This is a candidate, not verification.
public sealed record LocalProgressBest(int Route, int Hp, int MaxHp, int? StartingHp, int Rounds, int Potions)
{
    public int? NetHpLoss => StartingHp.HasValue ? Math.Max(0, StartingHp.Value - Hp) : null;
}
public sealed record LocalProgress(string Id, string SnapshotId, int Worker, int Workers, long Sequence,
    int Route, int Evaluated, int MaxNodes, int Victories, long ElapsedMs, int BudgetSeconds,
    string Phase, LocalSimState? State, LocalSimEvent[] Events, string Status = "running", int TurnProbes = 0, int BoundPruned = 0,
    int RootBranches = 0, LocalProgressBest? Best = null);

public sealed class LocalProgressBook(string id, string snapshotId)
{
    private readonly SortedDictionary<int, LocalProgress> _latest = new();
    public IReadOnlyDictionary<int, LocalProgress> Latest => _latest;
    public bool Accept(LocalProgress progress)
    {
        if (progress.Id != id || progress.SnapshotId != snapshotId || progress.Worker < 0 || progress.Worker >= progress.Workers ||
            progress.Workers is < 1 or > 16 || (_latest.TryGetValue(progress.Worker, out var old) && old.Sequence >= progress.Sequence)) return false;
        _latest[progress.Worker] = progress;
        return true;
    }
    public static double BudgetUsed(LocalProgress progress) => Math.Clamp(100d * progress.ElapsedMs / Math.Max(1000, progress.BudgetSeconds * 1000), 0, 100);
    public static string Overview(LocalProgress progress) => $"搜索分组 {progress.Worker + 1} · {progress.Phase} · 路线 {progress.Route} · " +
        $"评估 {progress.Evaluated}/{progress.MaxNodes} · 获胜 {progress.Victories}" +
        (progress.TurnProbes > 0 || progress.BoundPruned > 0 ? $" · 回合探查 {progress.TurnProbes} · 剪枝 {progress.BoundPruned}" : "");
    public static string StateText(LocalSimState state) =>
        $"第 {state.Round} 回合 | 生命 {state.Hp}/{state.MaxHp} | 格挡 {state.Block} | 能量 {state.Energy} | 药水 {state.Potions}\n" +
        $"自身状态：{state.Powers}\n手牌：{string.Join("、", state.Hand)}\n" +
        string.Join("\n", state.Enemies.Select(e => $"{e.Name}［{e.Id}］ 生命 {e.Hp}/{e.MaxHp} 格挡 {e.Block} | {e.Powers} | 行动 {e.Intent}"));
    public static string Changes(LocalSimState before, LocalSimState after)
    {
        var changes = new List<string>();
        if (before.Round != after.Round) changes.Add($"回合 {before.Round}→{after.Round}");
        if (before.Hp != after.Hp) changes.Add($"自身生命 {before.Hp}→{after.Hp}");
        if (before.Block != after.Block) changes.Add($"格挡 {before.Block}→{after.Block}");
        if (before.Energy != after.Energy) changes.Add($"能量 {before.Energy}→{after.Energy}");
        if (before.Powers != after.Powers) changes.Add("自身状态：" + after.Powers);
        if (before.Potions != after.Potions) changes.Add($"药水 {before.Potions}→{after.Potions}");
        foreach (var enemy in after.Enemies)
        {
            var old = before.Enemies.FirstOrDefault(e => e.Id == enemy.Id);
            if (old == null) { changes.Add($"出现 {enemy.Name}（{enemy.Hp} 生命）"); continue; }
            if (old.Hp != enemy.Hp) changes.Add($"{enemy.Name}［{enemy.Id}］生命 {old.Hp}→{enemy.Hp}");
            if (old.Block != enemy.Block) changes.Add($"{enemy.Name} 格挡 {old.Block}→{enemy.Block}");
            if (old.Powers != enemy.Powers) changes.Add($"{enemy.Name} 状态：{enemy.Powers}");
        }
        foreach (var enemy in before.Enemies.Where(e => after.Enemies.All(a => a.Id != e.Id))) changes.Add(enemy.Name + "离场");
        if (!before.Hand.SequenceEqual(after.Hand)) changes.Add("手牌：" + string.Join("、", after.Hand));
        return changes.Count == 0 ? "动作已结算，可见数值未变化。" : string.Join("；", changes);
    }
}
