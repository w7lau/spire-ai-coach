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
    public int? HpChange => StartingHp.HasValue ? Hp - StartingHp.Value : null;
}
public sealed record LocalProgress(string Id, string SnapshotId, int Worker, int Workers, long Sequence,
    int Route, int Evaluated, int MaxNodes, int Victories, long ElapsedMs, int BudgetSeconds,
    string Phase, LocalSimState? State, LocalSimEvent[] Events, string Status = "running", int TurnProbes = 0, int BoundPruned = 0,
    int RootBranches = 0, LocalProgressBest? Best = null, int Pass = 0);

public sealed class LocalProgressBook(string id, string snapshotId)
{
    public static LocalProgress? BestVictory(IEnumerable<LocalProgress> progress) =>
        progress.Where(p => p.Best != null).OrderByDescending(p => p.Best!.Hp)
            .ThenBy(p => p.Best!.Potions).ThenBy(p => p.Best!.Rounds).FirstOrDefault();
    private readonly SortedDictionary<int, LocalProgress> _latest = new();
    private int _pass;
    public IReadOnlyDictionary<int, LocalProgress> Latest => _latest;
    public bool Accept(LocalProgress progress)
    {
        if (progress.Id != id || progress.SnapshotId != snapshotId || progress.Worker < 0 || progress.Worker >= progress.Workers ||
            progress.Workers is < 1 or > 16 || progress.Pass < _pass ||
            (progress.Pass == _pass && _latest.TryGetValue(progress.Worker, out var old) && old.Sequence >= progress.Sequence)) return false;
        if (progress.Pass > _pass) { _latest.Clear(); _pass = progress.Pass; }
        _latest[progress.Worker] = progress;
        return true;
    }
    public static double BudgetUsed(LocalProgress progress) => Math.Clamp(100d * progress.ElapsedMs / Math.Max(1000, progress.BudgetSeconds * 1000), 0, 100);
    // A losing trial is not the end of its worker: it may restore another branch.
    public static bool Finished(LocalProgress progress) => progress.Status != "running";
    public static bool Preparing(LocalProgress progress) => !Finished(progress) && progress.RootBranches <= 0 && progress.State == null;
    public static string LaneState(LocalProgress progress) => progress.Status switch
    {
        "running" => Preparing(progress) ? "准备中" : "搜索中",
        "failed" => "失败", "cancelled" => "已取消", "unsupported" or "partial" => "已停止", _ => "已结束"
    };
    public static string Caption(IEnumerable<LocalProgress> progress)
    {
        var lanes = progress.ToArray();
        if (lanes.Length == 0) return "正在准备路线…";
        int finished = lanes.Count(Finished), preparing = lanes.Count(Preparing);
        int victories = lanes.Sum(p => p.Victories);
        var active = lanes.FirstOrDefault(p => !Finished(p) && !Preparing(p)) ??
            lanes.FirstOrDefault(p => !Finished(p)) ?? lanes[0];
        return (victories > 0 ? $"已找到 {victories} 条获胜路线 · " : "") +
            $"已启用 {lanes.Length}/{lanes.Max(p => p.Workers)} 路\n" +
            $"搜索 {lanes.Length - finished - preparing} 路 · 准备 {preparing} 路 · 已结束 {finished} 路 · {Activity(active)}";
    }
    public static string Activity(LocalProgress progress) => !string.IsNullOrWhiteSpace(progress.Phase) ? progress.Phase :
        progress.Status switch { "failed" => "本次试走失败", "cancelled" => "已取消计算",
            "done" or "searched" => "搜索已结束，正在汇总路线", "unsupported" => "此路线无法完成", _ => "正在准备路线" };
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
