namespace SpireAiCoach.Core;

// Candidate proposals only. Never rewrite a published plan: every proposal must be executed
// and compared in the native game. Card instance IDs survive changes in hand positions.
public sealed class LocalRouteRefiner
{
    private readonly Queue<LocalAction[]> _pending = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private string? _seed;
    public int Count => _pending.Count;

    public void Offer(LocalCandidate candidate, int partition = 0, int partitions = 1)
    {
        if (candidate.Dead) return;
        var seed = LocalFrontier.Key(candidate.Actions);
        if (_seed == seed) return;
        _seed = seed; _pending.Clear();
        var actions = candidate.Actions;
        int proposal = 0;
        foreach (var group in actions.Select((a, i) => (Action: a, Index: i)).GroupBy(x => x.Action.Round))
        {
            var cards = group.Where(x => !x.Action.EndTurn && x.Action.PotionSlot == null && x.Action.CombatCardIndex != null).ToArray();
            // Examine late actions first. This tests delayed setup without identifying any card by name/type.
            for (int later = cards.Length - 1; later > 0; later--)
                for (int earlier = 0; earlier < later; earlier++)
                {
                    int from = cards[later].Index, to = cards[earlier].Index;
                    // A potion or enemy-turn boundary must not be moved as a side effect.
                    if (actions.Skip(to).Take(from - to + 1).Any(a => a.EndTurn || a.PotionSlot != null)) continue;
                    var plan = actions.ToList(); var moved = plan[from]; plan.RemoveAt(from); plan.Insert(to, moved);
                    var key = string.Join("/", plan.Select(a => $"{a.Round}:{a.EndTurn}:{a.PotionSlot}:{a.CombatCardIndex}:{a.ModelId}:{a.TargetId}:" +
                        string.Join(",", (a.Choices ?? []).Select(c => $"{c.OfferHash}:{c.Index}:{c.ModelId}"))));
                    if (!_seen.Add(key)) continue;
                    if (proposal++ % partitions == partition && _pending.Count < 64) _pending.Enqueue(plan.ToArray());
                }
        }
    }

    public bool TryTake(out LocalAction[] actions) => _pending.TryDequeue(out actions!);

    // Never resolve by display name or current hand index after changing order.
    public static LocalAction? Resolve(LocalAction planned, IReadOnlyList<LocalAction> legal) => legal.FirstOrDefault(a =>
        a.Round == planned.Round && a.EndTurn == planned.EndTurn && a.PotionSlot == planned.PotionSlot &&
        a.TargetId == planned.TargetId && a.ModelId == planned.ModelId &&
        (a.EndTurn || a.PotionSlot != null || planned.CombatCardIndex != null && a.CombatCardIndex == planned.CombatCardIndex));
}

public static class LocalSelectionBranches
{
    // Enumerate cardinalities fairly. Large multi-select spaces are bounded, not claimed exhaustive.
    public static int[][] Generate(int count, int minimum, int maximum, int limit = 64)
    {
        minimum = Math.Max(0, minimum); maximum = Math.Min(count, maximum);
        if (count < 0 || minimum > maximum || limit < 1) throw new InvalidOperationException("Invalid native selection limits");
        IEnumerable<int[]> Combinations(int size, int start = 0)
        {
            if (size == 0) { yield return []; yield break; }
            for (int i = start; i <= count - size; i++)
                foreach (var tail in Combinations(size - 1, i + 1)) yield return new[] { i }.Concat(tail).ToArray();
        }
        var enumerators = Enumerable.Range(minimum, maximum - minimum + 1).Select(s => Combinations(s).GetEnumerator()).ToList();
        var output = new List<int[]>();
        try
        {
            while (enumerators.Count > 0 && output.Count < limit)
                for (int i = 0; i < enumerators.Count && output.Count < limit;)
                    if (enumerators[i].MoveNext()) { output.Add(enumerators[i].Current); i++; }
                    else { enumerators[i].Dispose(); enumerators.RemoveAt(i); }
        }
        finally { foreach (var enumerator in enumerators) enumerator.Dispose(); }
        return output.ToArray();
    }
}
