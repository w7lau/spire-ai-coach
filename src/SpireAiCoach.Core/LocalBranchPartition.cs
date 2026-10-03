namespace SpireAiCoach.Core;

// One instance per native trial. When a prefix has fewer moves than workers, split
// the remaining worker group at later action/selection forks. This assigns histories,
// not states, and never transfers a game object or an outcome between processes.
public sealed class LocalBranchPartition
{
    private int _rank, _size;
    public LocalBranchPartition(int worker, int workers)
    {
        if (workers < 1 || worker < 0 || worker >= workers) throw new ArgumentOutOfRangeException(nameof(worker));
        _rank = worker; _size = workers;
    }

    public LocalAction[] Assign(IReadOnlyList<LocalAction> legal)
    {
        if (legal.Count == 0) throw new InvalidOperationException("No legal action");
        if (_size == 1) return legal.ToArray();
        // Shard order must not depend on display text, changing heuristic preferences
        // or hand positions when a native card instance exists.
        var ordered = legal.OrderBy(a => a.BeforeHash, StringComparer.Ordinal).ThenBy(a => a.Round)
            .ThenBy(a => a.EndTurn).ThenBy(a => a.PotionSlot).ThenBy(a => a.ModelId, StringComparer.Ordinal)
            .ThenBy(a => a.CombatCardIndex).ThenBy(a => a.CombatCardIndex.HasValue ? 0 : a.HandIndex)
            .ThenBy(a => a.TargetId).ToArray();
        if (ordered.Length >= _size)
        {
            var assigned = ordered.Where((_, i) => i % _size == _rank).ToArray();
            _rank = 0; _size = 1;
            return assigned;
        }
        int child = _rank % ordered.Length;
        _size = (_size - 1 - child) / ordered.Length + 1;
        _rank /= ordered.Length;
        return [ordered[child]];
    }
}
