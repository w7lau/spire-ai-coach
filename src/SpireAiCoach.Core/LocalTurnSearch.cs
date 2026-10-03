using System.Text.Json;

namespace SpireAiCoach.Core;

public sealed record LocalTurnHint(int Hp, int StartingHp, int EnemyHp, int InitialEnemyHp,
    int Block = 0, int PotionsUsed = 0);
public sealed record LocalTurnTask(int Id, LocalAction[] Prefix, int SearchRound);
public sealed record LocalTurnSearchStats(int Probes = 0, int BoundPruned = 0, int Offered = 0,
    int DuplicateOffers = 0, int Pending = 0, int UnknownRecoveryChecks = 0);

// Exact native histories only. A turn probe executes through enemy settlement,
// records the resulting next turn, then returns to the scheduler. Scores order
// work; they never prove HP dominance or discard a low-scoring continuation.
public sealed class LocalTurnSearch
{
    private readonly Dictionary<int, Entry> _pending = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly PriorityQueue<int, (double, int)> _health = new();
    private readonly PriorityQueue<int, (double, int)> _damage = new();
    private readonly Queue<int> _fair = new();
    private readonly Random _random;
    private readonly string? _root;
    private int _next, _taken;
    public int Count => _pending.Count;
    public int Offered => _next;
    public int DuplicateOffers { get; private set; }
    private sealed record Entry(LocalTurnTask Task, LocalTurnHint Hint);

    public LocalTurnSearch(int seed, string? root = null) { _random = new(seed); _root = root; }

    public void Offer(LocalAction[] prefix, int searchRound, LocalTurnHint hint)
    {
        if (searchRound < 1) throw new ArgumentOutOfRangeException(nameof(searchRound));
        var key = searchRound + ":" + HistoryKey(prefix);
        if (!_seen.Add(key)) { DuplicateOffers++; return; }
        var task = new LocalTurnTask(_next++, prefix.ToArray(), searchRound);
        Queue(new(task, hint));
    }

    private void Queue(Entry entry)
    {
        _pending.Add(entry.Task.Id, entry);
        double hp = (double)entry.Hint.Hp / Math.Max(1, entry.Hint.StartingHp);
        double progress = 1 - (double)entry.Hint.EnemyHp / Math.Max(1, entry.Hint.InitialEnemyHp);
        double carry = Math.Min(50, entry.Hint.Block) * .002;
        double cost = entry.Hint.PotionsUsed * .2 + entry.Task.SearchRound * .001;
        // A later turn must first replay its entire prefix. Without a cost term,
        // near-victory states monopolize probes that are almost full rollouts.
        // FIFO work retains long prefixes; this only orders the other lanes.
        double replayCost = Math.Sqrt(1 + entry.Task.Prefix.Length);
        _health.Enqueue(entry.Task.Id, (-(hp * 2 + progress + carry - cost) / replayCost, entry.Task.Id));
        _damage.Enqueue(entry.Task.Id, (-(progress * 2 + hp + carry - cost) / replayCost, entry.Task.Id));
        _fair.Enqueue(entry.Task.Id);
    }

    public bool TryTake(out LocalTurnTask task)
    {
        task = null!;
        if (_pending.Count == 0) return false;
        // Alternate HP and damage priorities, with FIFO work every fourth take.
        // An inaccurate prior cannot permanently hide a previously offered task.
        int lane = _taken++ % 4;
        int id;
        if (lane == 3)
        {
            do { id = _fair.Dequeue(); } while (!_pending.ContainsKey(id));
        }
        else
        {
            var queue = lane == 1 ? _damage : _health;
            do { id = queue.Dequeue(); } while (!_pending.ContainsKey(id));
        }
        task = _pending[id].Task;
        _pending.Remove(id);
        return true;
    }

    public void ReturnInterrupted(LocalTurnTask task, LocalTurnHint hint)
    {
        if (_pending.ContainsKey(task.Id)) throw new InvalidOperationException("Task is already pending");
        Queue(new(task, hint));
    }

    public int DiscardDescendants(IReadOnlyList<LocalAction> prefix)
    {
        var ids = _pending.Where(p => p.Value.Task.Prefix.Length >= prefix.Count &&
            prefix.Select((a, i) => SameAction(a, p.Value.Task.Prefix[i]) &&
                (a.Choices ?? []).Length == (p.Value.Task.Prefix[i].Choices ?? []).Length &&
                (a.Choices ?? []).Select((c, n) => SameChoice(c, p.Value.Task.Prefix[i].Choices![n])).All(x => x)).All(x => x))
            .Select(p => p.Key).ToArray();
        foreach (int id in ids) _pending.Remove(id);
        return ids.Length;
    }

    public int DiscardProvenExpenses(LocalWinningBound? incumbent)
    {
        if (incumbent == null || incumbent.NetHpLoss != 0 || _root == null || incumbent.Root != _root) return 0;
        // Every action in this prefix is mandatory, even though its native
        // execution has not happened yet. No recovery can reduce net loss below
        // zero or undo the objective's already-prescribed potion expense.
        var ids = _pending.Where(p => LocalHealthBound.CannotImprove(new(_root,
            p.Value.Hint.StartingHp, p.Value.Hint.Hp, p.Value.Task.Prefix.Count(a => a.PotionSlot.HasValue)), incumbent))
            .Select(p => p.Key).ToArray();
        foreach (int id in ids) _pending.Remove(id);
        return ids.Length;
    }

    public LocalAction Choose(IReadOnlyList<LocalAction> legal, bool coherent = true)
    {
        if (legal.Count == 0) throw new InvalidOperationException("No legal action");
        if (!coherent) return legal[_random.Next(legal.Count)];
        return legal.OrderByDescending(a => a.Preference).ThenBy(a => a.EndTurn).First();
    }

    public void OfferAlternatives(IReadOnlyList<LocalAction> actions, LocalDecision decision, LocalTurnHint before)
    {
        if (decision.BeforeStep < 0 || decision.BeforeStep >= actions.Count)
            throw new InvalidOperationException("Decision is outside the executed history");
        var actual = actions[decision.BeforeStep];
        var prefix = actions.Take(decision.BeforeStep).ToArray();
        foreach (var alternate in decision.Legal.OrderByDescending(a => a.Preference))
            if (!SameAction(actual, alternate)) Offer([..prefix, alternate], actual.Round, before);
        foreach (var choice in decision.Choices ?? [])
            foreach (var alternate in choice.Legal.OrderByDescending(c => c.Preference))
            {
                if (actual.Choices == null || choice.AtChoice < 0 || choice.AtChoice >= actual.Choices.Length)
                    throw new InvalidOperationException("Choice is outside the executed action");
                var prior = actual.Choices[choice.AtChoice];
                if (SameChoice(prior, alternate)) continue;
                // A changed earlier selection may produce a different later offer.
                // Fix only the observed prefix through this selection, never its tail.
                var choices = actual.Choices.Take(choice.AtChoice).Append(alternate).ToArray();
                Offer([..prefix, actual with { Choices = choices }], actual.Round, before);
            }
    }

    public static LocalAction ResolveExact(LocalAction planned, IReadOnlyList<LocalAction> legal) =>
        legal.SingleOrDefault(a => SameAction(planned, a)) ??
            throw new InvalidOperationException("Exact native search prefix diverged");

    public static bool SameAction(LocalAction a, LocalAction b) =>
        a.BeforeHash == b.BeforeHash && a.Round == b.Round && a.EndTurn == b.EndTurn &&
        a.PotionSlot == b.PotionSlot && a.TargetId == b.TargetId && a.ModelId == b.ModelId &&
        (a.CombatCardIndex.HasValue || b.CombatCardIndex.HasValue
            ? a.CombatCardIndex == b.CombatCardIndex : a.HandIndex == b.HandIndex);

    public static bool SameChoice(LocalCardChoice a, LocalCardChoice b) =>
        a.OfferHash == b.OfferHash && a.Kind == b.Kind && a.Index == b.Index &&
        a.ModelId == b.ModelId && (a.Indices ?? []).SequenceEqual(b.Indices ?? []);

    // Structured keys avoid third-party model-name delimiter collisions. Display
    // names and tactical preferences never identify an action or a card choice.
    public static string HistoryKey(IEnumerable<LocalAction> actions) => JsonSerializer.Serialize(actions.Select(a => new
    {
        a.BeforeHash, a.Round, a.EndTurn, a.PotionSlot, a.TargetId, a.ModelId, a.CombatCardIndex,
        Hand = a.CombatCardIndex.HasValue ? (int?)null : a.HandIndex,
        Choices = (a.Choices ?? []).Select(c => new { c.OfferHash, c.Kind, c.Index, c.ModelId, Indices = c.Indices ?? [] })
    }));
}
