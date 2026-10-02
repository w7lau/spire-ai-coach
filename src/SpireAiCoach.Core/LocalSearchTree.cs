namespace SpireAiCoach.Core;

// Outcome-guided tree search. Nodes represent exact action histories, not merged visible states.
// Nothing here predicts card mechanics: only native, settled rollouts supply rewards.
public sealed class LocalSearchTree(int seed, int capacity = 8192)
{
    private readonly Random _random = new(seed);
    private readonly Node _root = new();
    private int _nodes = 1;
    public int Nodes => _nodes;
    public int CompletedTrials => _root.Visits;
    public bool Exhausted => _root.Closed;

    internal sealed class Node
    {
        public readonly Dictionary<string, Node> Children = new(StringComparer.Ordinal);
        public int Visits;
        public double Total, Best;
        public bool Closed;
        public string[]? LegalKeys;
    }

    public sealed class Trial
    {
        internal Node? Current;
        internal readonly List<Node> Path;
        internal bool Finished;
        internal Trial(Node root) { Current = root; Path = [root]; }
    }

    public Trial Begin() => new(_root);

    public LocalAction Select(Trial trial, IReadOnlyList<LocalAction> legal)
    {
        if (trial.Finished) throw new InvalidOperationException("Trial has already finished");
        if (legal.Count == 0) throw new InvalidOperationException("No legal action");
        var parent = trial.Current;
        LocalAction action;
        Node? child = null;
        if (parent == null) action = Explore(legal);
        else
        {
            parent.LegalKeys = legal.Select(Key).Distinct(StringComparer.Ordinal).ToArray();
            var remaining = legal.Where(a => !parent.Children.TryGetValue(Key(a), out var n) || !n.Closed).ToArray();
            if (remaining.Length == 0) throw new InvalidOperationException("This exact subtree has already been exhausted");
            var unseen = remaining.Where(a => !parent.Children.TryGetValue(Key(a), out var n) || n.Visits == 0).ToArray();
            if (unseen.Length > 0) action = Explore(unseen);
            else
            {
                // A good continuation must not be hidden by its earlier unsuccessful samples.
                // Keep an exploration bonus so a single lucky rollout cannot freeze the ordering.
                double best = double.NegativeInfinity;
                action = remaining[0];
                foreach (var candidate in remaining)
                {
                    var node = parent.Children[Key(candidate)];
                    var value = .65 * node.Total / node.Visits + .35 * node.Best +
                        .45 * Math.Sqrt(Math.Log(parent.Visits + 1d) / node.Visits);
                    if (value > best) { best = value; action = candidate; }
                }
            }
            var key = Key(action);
            if (!parent.Children.TryGetValue(key, out child) && _nodes < capacity)
            {
                child = new Node(); parent.Children.Add(key, child); _nodes++;
            }
        }
        trial.Current = child;
        if (child != null) trial.Path.Add(child);
        return action;
    }

    public void Complete(Trial trial, LocalCandidate result, int initialEnemyHp, bool closeExactPrefix = false)
    {
        if (trial.Finished) throw new InvalidOperationException("Trial has already finished");
        trial.Finished = true;
        var reward = Reward(result, initialEnemyHp);
        if (closeExactPrefix && trial.Current != null) trial.Current.Closed = true;
        // Only actions actually executed receive this outcome. Untried alternatives get no credit.
        for (int i = trial.Path.Count - 1; i >= 0; i--)
        {
            var node = trial.Path[i];
            node.Visits++; node.Total += reward; node.Best = Math.Max(node.Best, reward);
            if (node.LegalKeys is { Length: > 0 } keys && keys.All(k => node.Children.TryGetValue(k, out var n) && n.Closed))
                node.Closed = true;
        }
    }

    private LocalAction Explore(IReadOnlyList<LocalAction> actions)
    {
        // State-dependent tactical priors seed exploration; they never suppress unknown mechanics.
        // One quarter of choices ignore the prior so an incorrect preview cannot freeze ordering.
        if (_random.Next(4) != 0)
        {
            var best = actions.Max(a => a.Preference);
            var leaders = actions.Where(a => a.Preference == best).ToArray();
            if (leaders.Length < actions.Count) return leaders[_random.Next(leaders.Length)];
        }
        // No Attack/Skill/Power preference. End turn is a real choice even with playable cards.
        // Potions get a lower exploration frequency, never deletion; all unseen choices are visited
        // before an already measured child, provided the simulation budget reaches this node again.
        double Weight(LocalAction a) => a.PotionSlot.HasValue ? .2 : a.EndTurn ? .5 : 1;
        var sample = _random.NextDouble() * actions.Sum(Weight);
        foreach (var action in actions) { sample -= Weight(action); if (sample < 0) return action; }
        return actions[^1];
    }

    public static double Reward(LocalCandidate result, int initialEnemyHp)
    {
        if (result.Dead) return 0;
        var health = Math.Clamp((double)result.Hp / Math.Max(1, result.MaxHp), 0, 1);
        var progress = Math.Clamp(1d - (double)result.EnemyHp / Math.Max(1, initialEnemyHp), 0, 1);
        var loss = Math.Clamp((double)result.HpLost / Math.Max(1, result.MaxHp), 0, 1);
        var expense = .01 * Math.Min(1, result.Actions.Count(a => a.PotionSlot.HasValue) / 5d) +
            .005 * Math.Min(1, result.Actions.Length / 200d);
        // Disjoint ranges: even a low-HP victory outranks any unfinished horizon.
        return result.Won ? .7 + .25 * health - .025 * loss - expense :
            .1 + .2 * progress + .1 * health - .025 * loss - expense;
    }

    private static string Key(LocalAction a) =>
        $"{a.BeforeHash}:{a.Round}:{a.EndTurn}:{a.PotionSlot}:{a.HandIndex}:{a.ModelId}:{a.TargetId}";
}
