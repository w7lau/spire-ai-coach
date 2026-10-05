using System.Text.Json;

namespace SpireAiCoach.Core;

// Coverage of exact executed histories. This stores no native state or predicted
// outcome; only a fully settled terminal history can close a leaf. Turn probes,
// wall-clock limits and heuristic cuts must remain open.
public sealed class LocalRouteCoverage
{
    internal sealed class Node
    {
        public readonly Dictionary<string, Node> Children = new(StringComparer.Ordinal);
        public readonly HashSet<string> Legal = new(StringComparer.Ordinal);
        public bool Closed;
        public bool CompleteLegal;
    }

    public sealed class Trial
    {
        internal readonly List<Node> Path;
        internal Node Current => Path[^1];
        internal Trial(Node root) => Path = [root];
        internal void Follow(string key)
        {
            if (Current.Closed) throw new InvalidOperationException("Exact history is already complete");
            if (!Current.Children.TryGetValue(key, out var child))
                Current.Children.Add(key, child = new());
            if (child.Closed) throw new InvalidOperationException("Exact continuation is already complete");
            Path.Add(child);
        }
    }

    private readonly Node _root = new();
    public bool Exhausted => _root.Closed;
    public int Avoided { get; private set; }
    public int Completed { get; private set; }
    public Trial Begin() => new(_root);

    public LocalAction[] Open(Trial trial, IReadOnlyList<LocalAction> legal, bool completeLegal = true)
    {
        var current = trial.Current;
        // Union protects against a narrower, later policy filter closing untried
        // moves. Call with the complete worker-owned legal offer before ranking.
        foreach (var action in legal) current.Legal.Add(ActionKey(action));
        current.CompleteLegal |= completeLegal;
        var open = legal.Where(a => !current.Children.TryGetValue(ActionKey(a), out var child) || !child.Closed).ToArray();
        Avoided += legal.Count - open.Length;
        return open;
    }

    public void Follow(Trial trial, LocalAction action) => trial.Follow(ActionKey(action));

    public void Complete(Trial trial, bool terminal)
    {
        if (!terminal) return;
        trial.Current.Closed = true;
        Completed++;
        for (int i = trial.Path.Count - 2; i >= 0; i--)
        {
            var node = trial.Path[i];
            if (!node.CompleteLegal || node.Legal.Count == 0 || !node.Legal.All(k => node.Children.TryGetValue(k, out var child) && child.Closed)) break;
            node.Closed = true;
        }
    }

    public bool IsClosedPrefix(IEnumerable<LocalAction> actions)
    {
        var node = _root;
        if (node.Closed) return true;
        foreach (var action in actions)
        {
            if (!node.Children.TryGetValue(ActionKey(action), out node!)) return false;
            if (node.Closed) return true;
            foreach (var choice in action.Choices ?? [])
            {
                if (!node.Children.TryGetValue(ActionKey(ChoiceAction(choice, action.Round)), out node!)) return false;
                if (node.Closed) return true;
            }
        }
        return false;
    }

    // Every choice dimension belongs to identity, including multi-selection and
    // skip/completion kind. Index or model alone would conflate distinct effects.
    public static LocalAction ChoiceAction(LocalCardChoice choice, int round) => new(choice.Index,
        "choice:" + JsonSerializer.Serialize(new { choice.Kind, choice.ModelId, choice.Indices }),
        null, choice.Name, "", choice.OfferHash, round, Preference: choice.Preference);

    private static string ActionKey(LocalAction action) =>
        LocalTurnSearch.HistoryKey([action with { Choices = null }]);
}
