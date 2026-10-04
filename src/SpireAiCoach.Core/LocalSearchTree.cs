using System.Diagnostics.CodeAnalysis;

namespace SpireAiCoach.Core;

// Outcome-guided tree search. Nodes represent exact action histories, not merged visible states.
// Nothing here predicts card mechanics: only native, settled rollouts supply rewards.
public sealed class LocalSearchTree(int seed, int capacity = 8192, LocalSearchOrder order = LocalSearchOrder.MonteCarlo)
{
    private readonly Random _random = new(seed);
    private readonly Node _root = new();
    // Systematic modes retain every observed prefix. Native trial/time/round limits
    // still bound the request; the Monte Carlo storage cap must not discard this frontier.
    private readonly LocalDiscrepancyTree? _discrepancy = order switch
    {
        LocalSearchOrder.MonteCarlo => null,
        LocalSearchOrder.LimitedDiscrepancy or LocalSearchOrder.DepthDiscrepancy or LocalSearchOrder.DiscrepancyPortfolio => new(order),
        _ => throw new ArgumentOutOfRangeException(nameof(order))
    };
    private int _nodes = 1;
    public int Nodes => _discrepancy?.Nodes ?? _nodes;
    public int CompletedTrials => _discrepancy?.CompletedTrials ?? _root.Visits;
    public bool Exhausted => _discrepancy?.Exhausted ?? _root.Closed;

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
        internal readonly Node Root;
        internal Node? Current;
        internal readonly List<Node> Path;
        internal bool Finished;
        internal LocalDiscrepancyTree.Trial? Discrepancy;
        internal Trial(Node root) { Root = root; Current = root; Path = [root]; }
    }

    public Trial Begin() => new(_root) { Discrepancy = _discrepancy?.Begin() };

    public LocalAction Select(Trial trial, IReadOnlyList<LocalAction> legal, LocalAction? preferred = null, bool greedy = false,
        Func<LocalAction, int>? priority = null)
    {
        if (TrySelect(trial, legal, out var action, preferred, greedy, priority)) return action;
        throw new InvalidOperationException("This exact subtree has already been exhausted");
    }

    // Exhaustion is a scheduling result, not a failed native simulation. A forced
    // proposal can revisit a closed subtree while other exact histories stay open.
    public bool TrySelect(Trial trial, IReadOnlyList<LocalAction> legal, [NotNullWhen(true)] out LocalAction? selected,
        LocalAction? preferred = null, bool greedy = false, Func<LocalAction, int>? priority = null)
    {
        selected = null;
        if (trial.Root != _root) throw new InvalidOperationException("Trial belongs to a different search");
        if (_discrepancy != null) return _discrepancy.TrySelect(trial.Discrepancy ??
            throw new InvalidOperationException("Trial belongs to a different search"), legal, out selected, preferred);
        if (trial.Finished) throw new InvalidOperationException("Trial has already finished");
        if (legal.Count == 0) throw new InvalidOperationException("No legal action");
        if (preferred != null && !legal.Contains(preferred)) throw new InvalidOperationException("Preferred action is not legal");
        var parent = trial.Current;
        LocalAction action;
        Node? child = null;
        if (parent == null) action = preferred ?? Explore(legal, priority);
        else
        {
            parent.LegalKeys = legal.Select(Key).Distinct(StringComparer.Ordinal).ToArray();
            var remaining = legal.Where(a => !parent.Children.TryGetValue(Key(a), out var n) || !n.Closed).ToArray();
            if (remaining.Length == 0 || preferred != null && parent.Children.TryGetValue(Key(preferred), out var prior) && prior.Closed)
            {
                trial.Finished = true;
                // Do not add a rollout reward, visit or fabricated outcome for a
                // proposal that has no untried continuation at this exact prefix.
                return false;
            }
            var unseen = remaining.Where(a => !parent.Children.TryGetValue(Key(a), out var n) || n.Visits == 0).ToArray();
            if (preferred != null) action = preferred;
            else if (unseen.Length > 0) action = greedy ? unseen.OrderByDescending(a => priority?.Invoke(a) ?? a.Preference).ThenBy(a => a.EndTurn).First() : Explore(unseen, priority);
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
        selected = action;
        return true;
    }

    public void Complete(Trial trial, LocalCandidate result, int initialEnemyHp, bool closeExactPrefix = false)
    {
        if (trial.Root != _root) throw new InvalidOperationException("Trial belongs to a different search");
        if (_discrepancy != null)
        {
            _discrepancy.Complete(trial.Discrepancy ?? throw new InvalidOperationException("Trial belongs to a different search"), result, closeExactPrefix);
            return;
        }
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

    private LocalAction Explore(IReadOnlyList<LocalAction> actions, Func<LocalAction, int>? priority = null)
    {
        // State-dependent tactical priors seed exploration; they never suppress unknown mechanics.
        // One quarter of choices ignore the prior so an incorrect preview cannot freeze ordering.
        if (_random.Next(4) != 0)
        {
            var best = actions.Max(a => priority?.Invoke(a) ?? a.Preference);
            var leaders = actions.Where(a => (priority?.Invoke(a) ?? a.Preference) == best).ToArray();
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
        var expense = .04 * Math.Min(1, result.Actions.Count(a => a.PotionSlot.HasValue) / 5d) +
            .001 * Math.Min(1, result.Actions.Length / 200d);
        // Disjoint ranges: even a low-HP victory outranks any unfinished horizon.
        var damageQuality = Math.Exp(-(result.NetHpLoss ?? Math.Max(0, result.MaxHp - result.Hp)) / 25d);
        return result.Won ? .8 + .18 * damageQuality + .001 * health - expense :
            .1 + .4 * LocalSearchPolicy.UnfinishedQuality(result, initialEnemyHp) - expense;
    }

    private static string Key(LocalAction a) =>
        $"{a.BeforeHash}:{a.Round}:{a.EndTurn}:{a.PotionSlot}:{a.HandIndex}:{a.ModelId}:{a.TargetId}";
}
