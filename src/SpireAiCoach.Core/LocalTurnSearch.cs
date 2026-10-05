using System.Text.Json;

namespace SpireAiCoach.Core;

public sealed record LocalTurnHint(int Hp, int StartingHp, int EnemyHp, int InitialEnemyHp,
    int Block = 0, int PotionsUsed = 0, long? MaximumFurtherHpGain = null);
public sealed record LocalTurnTask(int Id, LocalAction[] Prefix, int SearchRound,
    bool FullRollout = false, int Lane = 0, bool Focused = false, LocalTurnHint? Hint = null,
    LocalRolloutStyle Style = LocalRolloutStyle.Balanced, LocalAction[]? Continuation = null, bool LossProof = false);
public sealed record LocalTurnOutcome(int Worker, int Attempt, double CompletedMs, bool Won,
    int Hp, int GrossLoss, int Rounds, int Potions, LocalRolloutStyle Style, LocalDamageSources? DamageSources);
public sealed record LocalTurnSearchStats(int Probes = 0, int BoundPruned = 0, int Offered = 0,
    int DuplicateOffers = 0, int Pending = 0, int UnknownRecoveryChecks = 0, int CoveredPrefixes = 0,
    int CompletedHistories = 0, int RepeatedHistories = 0,
    IReadOnlyDictionary<int, int>? ClaimedByRound = null,
    IReadOnlyDictionary<string, int>? RolloutStyles = null,
    LocalTurnOutcome[]? Outcomes = null, int LossProofProbes = 0);

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
    void SeedRoot(LocalAction[] continuation, int searchRound, LocalTurnHint hint);
    bool TryTake(out LocalTurnTask task);
    void FocusNext(LocalTurnTask task, IReadOnlyList<LocalAction> actions, IReadOnlyList<LocalDecision> decisions);
    void ObserveOutcome(LocalCandidate candidate);
    void PromoteWinning(LocalCandidate candidate);
    void PrioritizeLossProof(LocalLossProofFocus[] focus);
    void ReturnInterrupted(LocalTurnTask task, LocalTurnHint hint);
    int DiscardDescendants(IReadOnlyList<LocalAction> prefix);
    int DiscardProvenExpenses(LocalWinningBound? incumbent);
    LocalAction Choose(IReadOnlyList<LocalAction> legal, bool coherent = true);
    void OfferAlternatives(IReadOnlyList<LocalAction> actions, LocalDecision decision, LocalTurnHint before,
        LocalTurnTask? owner = null);
}

public sealed record LocalTurnOffer(LocalAction[] Prefix, int SearchRound, LocalTurnHint Hint);

// Request-local tracking of observed choice edges, keyed by the complete parent
// history. Replaying another history with the same visible offer cannot consume
// its siblings. One parent serialization per choice avoids one per combination.
public sealed class LocalPagedReplayTracker
{
    private readonly Dictionary<string, HashSet<LocalCardChoice>> _observed = new(StringComparer.Ordinal);
    private sealed class ChoiceComparer : IEqualityComparer<LocalCardChoice>
    {
        public bool Equals(LocalCardChoice? a, LocalCardChoice? b) => a != null && b != null && LocalTurnSearch.SameChoice(a, b);
        public int GetHashCode(LocalCardChoice choice)
        {
            var hash = new HashCode();
            hash.Add(choice.OfferHash); hash.Add(choice.Kind); hash.Add(choice.Index); hash.Add(choice.ModelId);
            foreach (int index in choice.Indices ?? []) hash.Add(index);
            return hash.ToHashCode();
        }
    }
    private static readonly ChoiceComparer Comparer = new();

    public IEnumerable<LocalTurnOffer> Observe(IReadOnlyList<LocalAction> actions, LocalDecision decision,
        LocalTurnHint before, bool replaying)
    {
        if (decision.Choices?.Any(d => d.Legal.Any(c => !c.CompleteOffer)) != true) yield break;
        if (decision.BeforeStep < 0 || decision.BeforeStep >= actions.Count)
            throw new InvalidOperationException("Decision is outside the executed history");
        var actual = actions[decision.BeforeStep];
        foreach (var choice in decision.Choices.Where(d => d.Legal.Any(c => !c.CompleteOffer)))
        {
            if (actual.Choices == null || choice.AtChoice < 0 || choice.AtChoice >= actual.Choices.Length)
                throw new InvalidOperationException("Choice is outside the executed action");
            var parent = LocalTurnSearch.HistoryKey(actions.Take(decision.BeforeStep).Append(
                actual with { Choices = actual.Choices.Take(choice.AtChoice).ToArray() }));
            if (!_observed.TryGetValue(parent, out var seen)) _observed.Add(parent, seen = new(Comparer));
            var fresh = choice.Legal.Where(c => seen.Add(c)).ToArray();
            // Ordinary expansion already submitted every observed sibling.
            if (!replaying || fresh.Length == 0) continue;
            var page = decision with { Legal = [], Choices = [choice with { Legal = fresh }] };
            foreach (var offer in LocalTurnSearch.PagedReplayAlternatives(actions, page, before)) yield return offer;
        }
    }
}

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
    private readonly Queue<int> _lossProof = new();
    private readonly Stack<int[]> _focus = new();
    private readonly Stack<int[]> _descent = new();
    private readonly Queue<(int Id, LocalAction[] Tail)> _winner = new();
    private LocalAction[]? _lastContinuation;
    private LocalRolloutStyle? _lastStyle;
    private LocalCandidate? _winningOutcome;
    private readonly SortedDictionary<int, RoundQueue> _rounds = new();
    private readonly Dictionary<int, int> _roundClaims = new();
    private int _damageRound, _fairRound;
    private readonly Stack<int[]> _winning = new();
    private readonly Dictionary<int, LocalAction[]> _winningTails = new();
    private readonly Random _random;
    private readonly string? _root;
    private readonly LocalCardGoals? _cardGoals;
    private int _next, _taken, _descentTakes, _guidedTakes;
    private LocalCandidate? _guidedIncumbent;
    private int? _winningOwner;
    private int _ownerGuidedTakes;
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

    public LocalTurnSearch(int seed, string? root = null, LocalCardGoals? cardGoals = null)
    { _random = new(seed); _root = root; _cardGoals = cardGoals; }

    public void Offer(LocalAction[] prefix, int searchRound, LocalTurnHint hint)
    {
        if (searchRound < 1) throw new ArgumentOutOfRangeException(nameof(searchRound));
        var key = (searchRound, PrefixId(prefix, true));
        if (_seen.ContainsKey(key)) { DuplicateOffers++; return; }
        var task = new LocalTurnTask(_next++, prefix.ToArray(), searchRound, Hint: hint);
        _seen.Add(key, task.Id);
        Queue(new(task with { Hint = hint }, hint));
    }

    public void SeedRoot(LocalAction[] continuation, int searchRound, LocalTurnHint hint)
    {
        if (continuation.Length == 0 || continuation[0].Round != searchRound)
            throw new InvalidDataException("Existing route does not start at the current round");
        if (_seen.Count != 0) throw new InvalidOperationException("Initial route must precede root exploration");
        Offer([], searchRound, hint);
        int id = _seen[(searchRound, 0)];
        var entry = _pending[id];
        _pending[id] = entry with { Task = entry.Task with { FullRollout = true, Continuation = continuation.ToArray() } };
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

    public bool TryTake(out LocalTurnTask task) => TryTake(out task, null);

    public bool TryTake(out LocalTurnTask task, int? owner)
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
        _lastStyle = null;
        int id;
        bool guided = false;
        bool proof = false;
        bool winningAllowed = _winningOwner == null || owner == _winningOwner;
        // Native learning affinity must not replace health, round and FIFO
        // coverage. Alternate it only inside the dedicated focused lane.
        bool ownerTurn = lane == 2 && _winningOwner.HasValue && owner == _winningOwner && _ownerGuidedTakes++ % 2 == 1;
        if (lane % 2 == 0 && TryLossProof(out id)) { proof = true; }
        else if (ownerTurn && TryStack(_winning, out id)) { guided = true; FocusedTakes++; LastFocused = true; }
        else if (lane == 2 && TryGuidedFocus(out id, out guided, winningAllowed)) { FocusedTakes++; LastFocused = true; }
        else if (lane == 1 && winningAllowed && TryWinner(out id)) { guided = true; }
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
        task = _pending[id].Task with { FullRollout = !proof && (guided || _pending[id].Task.FullRollout), LossProof = proof,
            Style = _lastStyle ?? (guided ? _guidedIncumbent?.RolloutStyle ?? LocalRolloutStyle.Balanced : _pending[id].Task.Style),
            Continuation = _lastContinuation ?? (guided ? _winningTails.GetValueOrDefault(id) : null) ?? _pending[id].Task.Continuation };
        _pending.Remove(id);
        _roundClaims[task.SearchRound] = _roundClaims.GetValueOrDefault(task.SearchRound) + 1;
        return true;
    }

    public void PrioritizeLossProof(LocalLossProofFocus[] focus)
    {
        _lossProof.Clear();
        foreach (var item in focus)
        {
            Offer(item.Prefix, item.SearchRound, item.Hint);
            int prefix = PrefixId(item.Prefix, false);
            if (_seen.TryGetValue((item.SearchRound, prefix), out int id) && _pending.ContainsKey(id))
                _lossProof.Enqueue(id);
        }
    }

    private bool TryLossProof(out int id)
    {
        while (_lossProof.TryDequeue(out id)) if (_pending.ContainsKey(id)) return true;
        id = -1; return false;
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
            { id = improvement.Id; _lastContinuation = improvement.Tail; _lastStyle = _winningOutcome?.RolloutStyle; return true; }
        id = -1; return false;
    }

    public void ObserveOutcome(LocalCandidate candidate)
    {
        if (!LocalCardGoalTactics.BetterExplorationSeed(candidate, _winningOutcome, _cardGoals)) return;
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

    private bool TryGuidedFocus(out int id, out bool guided, bool winningAllowed)
    {
        // Alternate incumbent improvements with compound descent. Every other
        // scheduler lane and FIFO still retain the unvisited legal histories.
        guided = false;
        if (_guidedTakes++ % 2 == 0 && TryFocus(out id)) return true;
        if (winningAllowed && TryStack(_winning, out id)) { guided = true; return true; }
        if (TryFocus(out id)) return true;
        return false;
    }

    public void PromoteWinning(LocalCandidate candidate) => PromoteWinning(candidate, null);

    public void PromoteWinning(LocalCandidate candidate, int? owner)
    {
        if (!candidate.Won || candidate.Dead || candidate.Decisions == null ||
            !LocalCardGoalTactics.BetterExplorationSeed(candidate, _guidedIncumbent, _cardGoals)) return;
        // Cross-worker measurements steer proposals only, never a health cut.
        // With optional goals, an achieved goal can be refined to lower its HP
        // cost. Without goals, a weaker local win cannot replace the shared focus.
        _guidedIncumbent = candidate with { Continuation = null, Decisions = null };
        var options = new List<(int Id, double Priority)>();
        var tails = new Dictionary<int, LocalAction[]>();
        foreach (var decision in candidate.Decisions)
        {
            int step = decision.BeforeStep;
            if (step < 0 || step >= candidate.Actions.Length) continue;
            var actual = candidate.Actions[step];
            foreach (var alternative in decision.Legal.Where(a => !SameAction(a, actual)))
            {
                var key = (actual.Round, PrefixId([.. candidate.Actions.Take(step), alternative], false));
                int id = _seen.GetValueOrDefault(key, -1);
                if (_pending.ContainsKey(id))
                {
                    options.Add((id, (alternative.Preference - actual.Preference) / Math.Sqrt(1 + step)));
                    tails[id] = candidate.Actions.Skip(step).ToArray();
                }
            }
        }
        var ids = options.OrderByDescending(p => p.Priority).ThenBy(p => p.Id).Select(p => p.Id).Distinct().ToArray();
        if (ids.Length == 0) return;
        _winningTails.Clear();
        foreach (var item in tails) _winningTails.Add(item.Key, item.Value);
        _winning.Clear(); _winning.Push(ids); _guidedTakes = 0;
        _winningOwner = owner; _ownerGuidedTakes = 0;
        // No new branch, state merge or health proof is inferred here. These
        // are already-observed pending native alternatives of an executed win.
    }

    public void ReleaseWinningOwner(int owner)
    { if (_winningOwner == owner) _winningOwner = null; }

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
        if (incumbent == null || _root == null || incumbent.Root != _root) return 0;
        // Every action in the prefix is mandatory. Its last native observation
        // carries a certified recovery ceiling, or null when effects are unknown.
        // Potion expense alone can still bound a no-loss incumbent with null.
        var ids = _pending.Where(p => LocalHealthBound.CannotImprove(new(_root,
            p.Value.Hint.StartingHp, p.Value.Hint.Hp, p.Value.Task.Prefix.Count(a => a.PotionSlot.HasValue),
            p.Value.Hint.MaximumFurtherHpGain), incumbent, _cardGoals))
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

    // Replaying a mandatory prefix can reveal a new lazy page at an ancestor.
    // Normal owned-subtree expansion intentionally skips that ancestor; submit
    // only its observed paged choices, using the shared exact-history deduper.
    // Changing a choice drops the later choice/action tail, never invents one.
    public static IEnumerable<LocalTurnOffer> PagedReplayAlternatives(IReadOnlyList<LocalAction> actions,
        LocalDecision decision, LocalTurnHint before)
    {
        if (decision.Choices?.Any(d => d.Legal.Any(c => !c.CompleteOffer)) != true) yield break;
        var paged = decision with { Legal = [], Choices = decision.Choices
            .Where(d => d.Legal.Any(c => !c.CompleteOffer)).ToArray() };
        foreach (var offer in Alternatives(actions, paged, before)) yield return offer;
    }

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
