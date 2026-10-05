namespace SpireAiCoach.Core;

public enum LocalSearchOrder { MonteCarlo, LimitedDiscrepancy, DepthDiscrepancy, DiscrepancyPortfolio }

// Replay-compatible discrepancy search. The frontier contains exact histories, never
// transferable game states. A native caller must execute every selected action.
// Inspired by LDS and DDS; this variant retains visited prefixes to avoid evaluating
// a completed leaf again, and uses native outcomes only to break frontier-order ties.
internal sealed class LocalDiscrepancyTree(LocalSearchOrder order)
{
    private readonly Node _root = new(0);
    public int Nodes { get; private set; } = 1;
    public int CompletedTrials => _root.Visits;
    public bool Exhausted => _root.Closed;

    // Minimum discrepancy order of a still-open continuation, not a damage lower bound.
    internal readonly record struct Bound(int Count, int LastDepth)
    {
        public static Bound None => new(0, -1);
        public Bound Deviate(int depth) => new(Count + 1, Math.Max(LastDepth, depth));
    }

    // Keep native identities distinct even when names, models or visible hashes coincide.
    // A structured key also avoids delimiter collisions in third-party model IDs.
    private readonly record struct Identity(string Before, int Round, bool EndTurn, int? Potion,
        string Model, uint? Target, uint? Card, int? Hand)
    {
        public static Identity Of(LocalAction a) => new(a.BeforeHash, a.Round, a.EndTurn, a.PotionSlot,
            a.ModelId, a.TargetId, a.CombatCardIndex, a.CombatCardIndex.HasValue ? null : a.HandIndex);
    }

    internal sealed class Node(int depth)
    {
        public readonly int Depth = depth;
        private Dictionary<Identity, Edge>? _edges;
        internal Bound? CountPending = Bound.None, DepthPending = Bound.None;
        private LocalCandidate? _best;
        public int Visits;
        public bool Closed;

        internal static LocalAction Select(Node parent, LocalDiscrepancyTree tree, Trial trial,
            IReadOnlyList<LocalAction> legal, LocalAction? preferred)
        {
            var keyed = legal.Select(a => (Action: a, Key: Identity.Of(a))).ToArray();
            if (keyed.Select(x => x.Key).Distinct().Count() != legal.Count)
                throw new InvalidOperationException("Native legal action identities are not unique");
            if (parent._edges == null)
            {
                // Freeze the heuristic ordering once per exact prefix. Later learning can
                // change preferences, but must not reset coverage or redefine discrepancies.
                parent._edges = keyed.OrderByDescending(x => x.Action.Preference)
                    .Select((x, rank) => (x.Key, Edge: new Edge(rank, rank != 0)))
                    .ToDictionary(x => x.Key, x => x.Edge);
            }
            else if (parent._edges.Count != keyed.Length || keyed.Any(x => !parent._edges.ContainsKey(x.Key)))
                throw new InvalidOperationException("Native legal actions changed at the same exact history");

            Edge? selected = null;
            LocalAction? action = null;
            Bound? selectedBound = null;
            foreach (var item in keyed)
            {
                var edge = parent._edges[item.Key];
                if (edge.Child?.Closed == true) continue;
                if (preferred != null && item.Action != preferred) continue;
                var bound = Pending(parent, edge, trial.Order);
                if (selected == null || Compare(bound, selectedBound!.Value, trial.Order) < 0 ||
                    Compare(bound, selectedBound.Value, trial.Order) == 0 && BetterTie(edge, selected))
                { selected = edge; action = item.Action; selectedBound = bound; }
            }
            if (selected == null)
                throw new InvalidOperationException(preferred == null ? "This exact subtree has already been exhausted" :
                    "Preferred action is not legal or its exact subtree has already been exhausted");
            if (selected.Child == null) { selected.Child = new(parent.Depth + 1); tree.Nodes++; }
            trial.Current = selected.Child;
            trial.Path.Add(selected.Child);
            return action!;
        }

        private static bool BetterTie(Edge a, Edge b)
        {
            // Give unmeasured alternatives a real execution before trusting a sampled one.
            bool unseenA = a.Child == null || a.Child.Visits == 0;
            bool unseenB = b.Child == null || b.Child.Visits == 0;
            if (unseenA != unseenB) return unseenA;
            if (a.Child?._best is { } outcome && LocalSearchPolicy.Better(outcome, b.Child?._best)) return true;
            if (b.Child?._best is { } prior && LocalSearchPolicy.Better(prior, a.Child?._best)) return false;
            int visitsA = a.Child?.Visits ?? 0, visitsB = b.Child?.Visits ?? 0;
            return visitsA != visitsB ? visitsA < visitsB : a.Rank < b.Rank;
        }

        internal void Finish(LocalDiscrepancyTree tree, LocalCandidate outcome)
        {
            Visits++;
            if (LocalSearchPolicy.Better(outcome, _best)) _best = outcome;
            if (Closed) { CountPending = DepthPending = null; return; }
            if (_edges == null) return; // Interrupted/unobserved frontier: still open.
            CountPending = DepthPending = null;
            foreach (var edge in _edges.Values.Where(e => e.Child?.Closed != true))
            {
                var count = Pending(this, edge, LocalSearchOrder.LimitedDiscrepancy);
                var depth = Pending(this, edge, LocalSearchOrder.DepthDiscrepancy);
                if (CountPending == null || Compare(count, CountPending.Value, LocalSearchOrder.LimitedDiscrepancy) < 0) CountPending = count;
                if (DepthPending == null || Compare(depth, DepthPending.Value, LocalSearchOrder.DepthDiscrepancy) < 0) DepthPending = depth;
            }
            Closed = CountPending == null;
        }

        internal sealed class Edge(int rank, bool deviation)
        {
            public readonly int Rank = rank;
            public readonly bool Deviation = deviation;
            public Node? Child;
        }
    }

    internal sealed class Trial(Node root, LocalSearchOrder trialOrder)
    {
        internal readonly Node Root = root;
        internal readonly LocalSearchOrder Order = trialOrder;
        internal Node Current = root;
        internal readonly List<Node> Path = [root];
        internal bool Finished;
    }

    // Both schedules use one history tree and one set of closed leaves. This shares
    // coverage and native outcome statistics, not executable native checkpoints.
    public Trial Begin() => new(_root, order == LocalSearchOrder.DiscrepancyPortfolio ?
        CompletedTrials % 2 == 0 ? LocalSearchOrder.DepthDiscrepancy : LocalSearchOrder.LimitedDiscrepancy : order);

    public LocalAction Select(Trial trial, IReadOnlyList<LocalAction> legal, LocalAction? preferred)
    {
        Check(trial);
        if (legal.Count == 0) throw new InvalidOperationException("No legal action");
        return Node.Select(trial.Current, this, trial, legal, preferred);
    }

    public void Complete(Trial trial, LocalCandidate result, bool closeExactPrefix)
    {
        Check(trial);
        trial.Finished = true;
        if (closeExactPrefix) trial.Current.Closed = true;
        var outcome = result with { Decisions = null, Continuation = null };
        for (int i = trial.Path.Count - 1; i >= 0; i--) trial.Path[i].Finish(this, outcome);
    }

    private void Check(Trial trial)
    {
        if (trial.Root != _root) throw new InvalidOperationException("Trial belongs to a different search");
        if (trial.Finished) throw new InvalidOperationException("Trial has already finished");
    }

    private static Bound Pending(Node parent, Node.Edge edge, LocalSearchOrder trialOrder)
    {
        var bound = (trialOrder == LocalSearchOrder.DepthDiscrepancy ? edge.Child?.DepthPending : edge.Child?.CountPending) ?? Bound.None;
        return edge.Deviation ? bound.Deviate(parent.Depth) : bound;
    }

    private static int Compare(Bound a, Bound b, LocalSearchOrder trialOrder)
    {
        int primary = trialOrder == LocalSearchOrder.DepthDiscrepancy ? a.LastDepth.CompareTo(b.LastDepth) : a.Count.CompareTo(b.Count);
        return primary != 0 ? primary : trialOrder == LocalSearchOrder.DepthDiscrepancy ?
            a.Count.CompareTo(b.Count) : a.LastDepth.CompareTo(b.LastDepth);
    }
}
