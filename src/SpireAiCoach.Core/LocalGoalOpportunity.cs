namespace SpireAiCoach.Core;

// Observed root copies, not a forecast of future draws, recovery or Mod effects.
public sealed record LocalGoalStock(int Target, int Completed, int Available)
{
    public int Missing => Math.Max(0, Target - Completed - Available);
}

public sealed record LocalGoalOpportunity(string? PlayModelId, string? FinisherModelId,
    LocalGoalStock? Play, LocalGoalStock? Finisher)
{
    public bool Matches(LocalCardGoals? goals) => goals?.Enabled == true &&
        PlayModelId == goals.PlayModelId && FinisherModelId == goals.FinisherModelId;

    public static int LossPenalty(LocalGoalOpportunity? before, LocalGoalOpportunity? after, LocalCardGoals? goals)
    {
        if (before?.Matches(goals) != true || after?.Matches(goals) != true) return 0;
        int Lost(LocalGoalStock? a, LocalGoalStock? b) => a == null || b == null || a.Target != b.Target ? 0 :
            Math.Max(0, b.Missing - a.Missing);
        return 180 * Lost(before.Finisher, after.Finisher) + 60 * Lost(before.Play, after.Play);
    }
}

// Exact losses remain bound to ordered histories. A separate, conditional hint
// remembers a real source instance consuming opportunities in this frozen root;
// it is not a prediction, legality restriction or transferred native result.
public sealed class LocalGoalLossLearning(LocalCardGoals? goals, int capacity = 8192)
{
    private readonly List<Dictionary<string, int>> _prefixes = [new()];
    private readonly Dictionary<(int Prefix, string Action), int> _penalties = [];
    private readonly Dictionary<string, (int Play, int Finisher)> _risks = [];
    private int _prefix;
    public void Begin() => _prefix = 0;
    private static string Key(LocalAction action) => LocalTurnSearch.HistoryKey([action]);
    public int Penalty(LocalAction action) => goals?.Enabled != true || _prefix < 0 || _penalties.Count == 0
        ? 0 : _penalties.GetValueOrDefault((_prefix, Key(action)));

    private static string? SourceKey(LocalAction action) => action.Choices is { Length: > 0 } ||
        !action.EndTurn && !action.PotionSlot.HasValue && !action.CombatCardIndex.HasValue ? null :
        System.Text.Json.JsonSerializer.Serialize(new { action.ModelId, action.CombatCardIndex, action.PotionSlot,
            action.TargetId, action.EndTurn, action.Round });

    public int Penalty(LocalAction action, LocalGoalOpportunity? current, bool finisherReady = false)
    {
        int exact = Penalty(action);
        if (current?.Matches(goals) != true || _risks.Count == 0 || SourceKey(action) is not { } key ||
            !_risks.TryGetValue(key, out var risk)) return exact;
        int Exposed(LocalGoalStock? stock, int removed) => stock == null ? 0 :
            Math.Max(0, stock.Target - stock.Completed - Math.Max(0, stock.Available - removed)) - stock.Missing;
        // A positive native finisher preview supersedes this past non-finishing
        // hint. It still supplies no actual kill credit or replay exemption.
        return Math.Max(exact, 60 * Exposed(current.Play, risk.Play) +
            (finisherReady ? 0 : 180 * Exposed(current.Finisher, risk.Finisher)));
    }

    public void CompleteStep(LocalAction action, LocalGoalOpportunity? before, LocalGoalOpportunity? after)
    {
        if (goals?.Enabled != true) return;
        if (before?.Matches(goals) == true && after?.Matches(goals) == true && SourceKey(action) is { } source)
        {
            int Removed(LocalGoalStock? a, LocalGoalStock? b) => a == null || b == null || a.Target != b.Target ? 0 :
                Math.Max(0, a.Available - b.Available - Math.Max(0, b.Completed - a.Completed));
            var observed = (Play: Removed(before.Play, after.Play), Finisher: Removed(before.Finisher, after.Finisher));
            if ((observed.Play > 0 || observed.Finisher > 0) && (_risks.ContainsKey(source) || _risks.Count < capacity))
            {
                var old = _risks.GetValueOrDefault(source);
                _risks[source] = (Math.Max(old.Play, observed.Play), Math.Max(old.Finisher, observed.Finisher));
            }
        }
        if (_prefix < 0) return;
        string key = Key(action);
        int loss = LocalGoalOpportunity.LossPenalty(before, after, goals);
        if (loss > 0) _penalties[(_prefix, key)] = Math.Max(loss, _penalties.GetValueOrDefault((_prefix, key)));
        var children = _prefixes[_prefix];
        if (!children.TryGetValue(key, out int next))
        {
            if (_prefixes.Count >= capacity) { _prefix = -1; return; }
            next = _prefixes.Count; children.Add(key, next); _prefixes.Add(new());
        }
        _prefix = next;
    }
}
