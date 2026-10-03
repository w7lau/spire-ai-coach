using System.Text.Json;

namespace SpireAiCoach.Core;

public sealed record LocalTurnHint(int Hp, int StartingHp, int EnemyHp, int InitialEnemyHp,
    int Block = 0, int PotionsUsed = 0);
public sealed record LocalTurnTask(int Id, LocalAction[] Prefix, int SearchRound,
    bool FullRollout = false, int Lane = 0, bool Focused = false, LocalTurnHint? Hint = null,
    LocalRolloutStyle Style = LocalRolloutStyle.Balanced, LocalAction[]? Continuation = null);
public sealed record LocalTurnOutcome(int Worker, int Attempt, double CompletedMs, bool Won,
    int Hp, int GrossLoss, int Rounds, int Potions, LocalRolloutStyle Style, LocalDamageSources? DamageSources);
public sealed record LocalTurnSearchStats(int Probes = 0, int BoundPruned = 0, int Offered = 0,
    int DuplicateOffers = 0, int Pending = 0, int UnknownRecoveryChecks = 0, int CoveredPrefixes = 0,
    int CompletedHistories = 0, int RepeatedHistories = 0,
    IReadOnlyDictionary<int, int>? ClaimedByRound = null,
    IReadOnlyDictionary<string, int>? RolloutStyles = null,
    LocalTurnOutcome[]? Outcomes = null);

// Exact native histories only. A turn probe executes through enemy settlement,
// records the resulting next turn, then returns to the scheduler. Scores order
// work; they never prove HP dominance or discard a low-scoring continuation.
public interface ILocalTurnFrontier
{
    int Count { get; }
    int Offered { get; }
    int DuplicateOffers { get; }
    int LastLane { get; }
    IReadOnlyDictionary<int, int> ClaimedByRound { get; }
    void Offer(LocalAction[] prefix, int searchRound, LocalTurnHint hint);
    bool TryTake(out LocalTurnTask task);
    void FocusNext(LocalTurnTask task, IReadOnlyList<LocalAction> actions, IReadOnlyList<LocalDecision> decisions);
    void ObserveOutcome(LocalCandidate candidate);
    void ReturnInterrupted(LocalTurnTask task, LocalTurnHint hint);
    int DiscardDescendants(IReadOnlyList<LocalAction> prefix);
    int DiscardProvenExpenses(LocalWinningBound? incumbent);
    LocalAction Choose(IReadOnlyList<LocalAction> legal, bool coherent = true);
    void OfferAlternatives(IReadOnlyList<LocalAction> actions, LocalDecision decision, LocalTurnHint before,
        LocalTurnTask? owner = null);
}

public sealed record LocalTurnOffer(LocalAction[] Prefix, int SearchRound, LocalTurnHint Hint);

public sealed class LocalTurnSearch : ILocalTurnFrontier
{
    private readonly Dictionary<int, Entry> _pending = new();
    private readonly Dictionary<(int Round, int Prefix), int> _seen = new();
    private readonly List<Dictionary<StepKey, int>> _prefixes = [new()];
    private readonly record struct StepKey(string Before, int Round, bool End, int? Potion, uint? Target,
        string Model, uint? Card, int? Hand, string Choices);
    private readonly PriorityQueue<int, (double, int)> _health = new();
    private readonly PriorityQueue<int, (double, int)> _damage = new();
    private readonly Queue<int> _fair = new();
    private readonly Stack<int[]> _focus = new();
    private readonly Stack<int[]> _descent = new();
    private readonly Queue<(int Id, LocalAction[] Tail)> _winner = new();
    private LocalAction[]? _lastContinuation;
    private LocalCandidate? _winningOutcome;
    private readonly SortedDictionary<int, RoundQueue> _rounds = new();
    private readonly Dictionary<int, int> _roundClaims = new();
    private int _damageRound, _fairRound;
    private readonly Random _random;
    private readonly string? _root;
    private int _next, _taken, _descentTakes;
    public int Count => _pending.Count;
    public int Offered => _next;
    public int DuplicateOffers { get; private set; }
    public int FocusedTakes { get; private set; }
    public int LastLane { get; private set; }
    public bool LastFocused { get; private set; }
    public IReadOnlyDictionary<int, int> ClaimedByRound => new Dictionary<int, int>(_roundClaims);
    private sealed record Entry(LocalTurnTask Task, LocalTurnHint Hint);
    private sealed class RoundQueue
    {
        public readonly PriorityQueue<int, (double, int)> Damage = new();
        public readonly Queue<int> Fair = new();
    }

    public LocalTurnSearch(int seed, string? root = null) { _random = new(seed); _root = root; }

    public void Offer(LocalAction[] prefix, int searchRound, LocalTurnHint hint)
    {
        if (searchRound < 1) throw new ArgumentOutOfRangeException(nameof(searchRound));
        var key = (searchRound, PrefixId(prefix, true));
        if (_seen.ContainsKey(key)) { DuplicateOffers++; return; }
        var task = new LocalTurnTask(_next++, prefix.ToArray(), searchRound, Hint: hint);
        _seen.Add(key, task.Id);
        Queue(new(task with { Hint = hint }, hint));
    }

    private int PrefixId(IReadOnlyList<LocalAction> actions, bool create)
    {
        // Intern exact action identities under their exact parents. This shares
        // immutable history structure, never native states, RNG or Mod objects.
        // Typed fields avoid serializing and storing the whole prefix per fork.
        int parent = 0;
        foreach (var action in actions)
        {
            string choices = action.Choices is not { Length: > 0 } ? "" : JsonSerializer.Serialize(
                action.Choices.Select(c => new { c.OfferHash, c.Kind, c.Index, c.ModelId, Indices = c.Indices ?? [] }));
            var step = new StepKey(action.BeforeHash, action.Round, action.EndTurn, action.PotionSlot,
                action.TargetId, action.ModelId, action.CombatCardIndex,
                action.CombatCardIndex.HasValue ? null : action.HandIndex, choices);
            var children = _prefixes[parent];
            if (!children.TryGetValue(step, out int next))
            {
                if (!create) return -1;
                next = _prefixes.Count; children.Add(step, next); _prefixes.Add(new());
            }
            parent = next;
        }
        return parent;
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
        var damageScore = (-(progress * 2 + hp + carry - cost) / replayCost, entry.Task.Id);
        _damage.Enqueue(entry.Task.Id, damageScore);
        _fair.Enqueue(entry.Task.Id);
        if (!_rounds.TryGetValue(entry.Task.SearchRound, out var round))
            _rounds.Add(entry.Task.SearchRound, round = new());
        round.Damage.Enqueue(entry.Task.Id, damageScore);
        round.Fair.Enqueue(entry.Task.Id);
    }

    public bool TryTake(out LocalTurnTask task)
    {
        task = null!;
        if (_pending.Count == 0) return false;
        // One lane deepens the next decision of a recently executed exact prefix.
        // Otherwise short replays keep pushing compound alternatives behind a
        // large shallow frontier. Other lanes and FIFO preserve broad coverage.
        // An inaccurate prior cannot permanently hide a previously offered task.
        int lane = _taken++ % 4;
        LastLane = lane;
        LastFocused = false;
        _lastContinuation = null;
        int id;
        if (lane == 2 && TryFocus(out id)) { FocusedTakes++; LastFocused = true; }
        else if (lane == 1 && TryWinner(out id)) { }
        else if (lane == 1 && TryRound(ref _damageRound, true, out id)) { }
        else if (lane == 3 && TryRound(ref _fairRound, false, out id)) { }
        else if (lane == 3)
        {
            do { id = _fair.Dequeue(); } while (!_pending.ContainsKey(id));
        }
        else
        {
            var queue = lane == 1 ? _damage : _health;
            do { id = queue.Dequeue(); } while (!_pending.ContainsKey(id));
        }
        task = _pending[id].Task with { Continuation = _lastContinuation };
        _pending.Remove(id);
        _roundClaims[task.SearchRound] = _roundClaims.GetValueOrDefault(task.SearchRound) + 1;
        return true;
    }

    private bool TryRound(ref int cursor, bool ranked, out int id)
    {
        // A cheap round-one replay must not indefinitely outrank every later
        // native decision. Rotate observed rounds in two lanes, retaining the
        // original global health and compound-descent lanes. Scores still
        // order work inside a round; no state or legal branch is discarded.
        int previous = cursor;
        var rounds = _rounds.Keys.Where(r => r > previous).Concat(_rounds.Keys.Where(r => r <= previous)).ToArray();
        foreach (int number in rounds)
        {
            var round = _rounds[number];
            while (ranked ? round.Damage.TryDequeue(out id, out _) : round.Fair.TryDequeue(out id))
                if (_pending.ContainsKey(id)) { cursor = number; return true; }
        }
        id = -1; return false;
    }

    private bool TryWinner(out int id)
    {
        while (_winner.TryDequeue(out var improvement))
            if (_pending.ContainsKey(improvement.Id))
            { id = improvement.Id; _lastContinuation = improvement.Tail; return true; }
        id = -1; return false;
    }

    public void ObserveOutcome(LocalCandidate candidate)
    {
        if (!candidate.Won || candidate.Dead || !LocalSearchPolicy.Better(candidate, _winningOutcome)) return;
        _winningOutcome = candidate;
        _winner.Clear();
        // Complete native outcomes guide exploration around the actual incumbent.
        // Its exact alternatives have already been offered; no legal prefix is
        // invented, reopened, merged, or discarded by this scheduling hint.
        var alternatives = new List<(int Id, int Round, int Step, int Rank, LocalAction[] Tail)>();
        foreach (var decision in candidate.Decisions ?? [])
        {
            if (decision.BeforeStep < 0 || decision.BeforeStep >= candidate.Actions.Length) continue;
            var actual = candidate.Actions[decision.BeforeStep];
            var hint = new LocalTurnHint(candidate.Hp, candidate.StartingHp ?? candidate.Hp, candidate.EnemyHp, 1);
            foreach (var offer in Alternatives(candidate.Actions, decision, hint))
            {
                var key = (offer.SearchRound, PrefixId(offer.Prefix, false));
                if (_seen.TryGetValue(key, out int id) && _pending.ContainsKey(id))
                    alternatives.Add((id, actual.Round, decision.BeforeStep, offer.Prefix[^1].Preference,
                        candidate.Actions.Skip(decision.BeforeStep).ToArray()));
            }
        }
        foreach (var item in alternatives.OrderBy(x => x.Round).ThenBy(x => x.Step).ThenByDescending(x => x.Rank))
            _winner.Enqueue((item.Id, item.Tail));
    }

    private bool TryFocus(out int id)
    {
        // Broad scheduling occurs between focused takes. It must not overwrite
        // the active descent with its own newest shallow sibling. Reserve four
        // successive focused takes, then admit a different observed family.
        if (_descentTakes < 4 && TryStack(_descent, out id)) { _descentTakes++; return true; }
        if (TryStack(_focus, out id)) { _descentTakes = 0; return true; }
        if (TryStack(_descent, out id)) { _descentTakes = 1; return true; }
        id = -1; return false;
    }

    private bool TryStack(Stack<int[]> stack, out int id)
    {
        while (stack.TryPop(out var siblings))
        {
            int next = Array.FindIndex(siblings, _pending.ContainsKey);
            if (next < 0) continue;
            id = siblings[next];
            if (next + 1 < siblings.Length) stack.Push(siblings[(next + 1)..]);
            return true;
        }
        id = -1; return false;
    }

    // The action at the frontier is a native decision already executed in this
    // trial. Its legal siblings are proposals, not transferred outcomes. Change
    // one more decision under that exact prefix rather than repeatedly changing
    // the root. Never promote across an enemy-turn boundary or by display name.
    public void FocusNext(LocalTurnTask task, IReadOnlyList<LocalAction> actions,
        IReadOnlyList<LocalDecision> decisions)
        => FocusNext(task, actions, decisions, LastFocused);

    public void FocusNext(LocalTurnTask task, IReadOnlyList<LocalAction> actions,
        IReadOnlyList<LocalDecision> decisions, bool focused)
    {
        int step = task.Prefix.Length;
        if (step >= actions.Count || actions[step].Round != task.SearchRound) return;
        var decision = decisions.SingleOrDefault(d => d.BeforeStep == step);
        if (decision == null) return;
        var prefix = actions.Take(step).ToArray();
        var ids = decision.Legal.OrderByDescending(a => a.Preference)
            .Where(a => !SameAction(a, actions[step]))
            .Select(a => _seen.GetValueOrDefault((task.SearchRound, PrefixId([..prefix, a], false)), -1))
            .Where(id => _pending.ContainsKey(id)).ToArray();
        if (ids.Length > 0) (focused ? _descent : _focus).Push(ids);
    }

    // Half the attempts now reach full combat settlement. Rotate complementary
    // pairs so health, damage, focused and FIFO work all get complete feedback.
    // Every full trial also publishes its observed alternative action prefixes.
    public static bool IsFullRollout(int completedAttempts) =>
        completedAttempts % 4 == completedAttempts / 4 % 4 ||
        completedAttempts % 4 == (completedAttempts / 4 + 2) % 4;

    public void ReturnInterrupted(LocalTurnTask task, LocalTurnHint hint)
    {
        if (_pending.ContainsKey(task.Id)) throw new InvalidOperationException("Task is already pending");
        Queue(new(task with { Hint = hint }, hint));
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

    public void OfferAlternatives(IReadOnlyList<LocalAction> actions, LocalDecision decision, LocalTurnHint before,
        LocalTurnTask? owner = null)
    {
        foreach (var offer in Alternatives(actions, decision, before, owner))
            Offer(offer.Prefix, offer.SearchRound, offer.Hint);
    }

    public static IEnumerable<LocalTurnOffer> Alternatives(IReadOnlyList<LocalAction> actions,
        LocalDecision decision, LocalTurnHint before, LocalTurnTask? owner = null)
    {
        if (decision.BeforeStep < 0 || decision.BeforeStep >= actions.Count)
            throw new InvalidOperationException("Decision is outside the executed history");
        var actual = actions[decision.BeforeStep];
        var prefix = actions.Take(decision.BeforeStep).ToArray();
        // A claimed prefix already fixes its actions and earlier choices. Do not
        // publish its ancestor's siblings from another worker's owned subtree.
        if (owner != null && decision.BeforeStep < owner.Prefix.Length - 1) yield break;
        bool fixedAction = owner != null && decision.BeforeStep < owner.Prefix.Length;
        if (!fixedAction)
            foreach (var alternate in decision.Legal.OrderByDescending(a => a.Preference))
                if (!SameAction(actual, alternate)) yield return new([..prefix, alternate], actual.Round, before);
        int fixedChoices = fixedAction ? owner!.Prefix[decision.BeforeStep].Choices?.Length ?? 0 : 0;
        foreach (var choice in decision.Choices ?? [])
        {
            if (choice.AtChoice < fixedChoices) continue;
            foreach (var alternate in choice.Legal.OrderByDescending(c => c.Preference))
            {
                if (actual.Choices == null || choice.AtChoice < 0 || choice.AtChoice >= actual.Choices.Length)
                    throw new InvalidOperationException("Choice is outside the executed action");
                var prior = actual.Choices[choice.AtChoice];
                if (SameChoice(prior, alternate)) continue;
                // A changed earlier selection may produce a different later offer.
                // Fix only the observed prefix through this selection, never its tail.
                var choices = actual.Choices.Take(choice.AtChoice).Append(alternate).ToArray();
                yield return new([..prefix, actual with { Choices = choices }], actual.Round, before);
            }
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
