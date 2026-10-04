namespace SpireAiCoach.Core;

// A preference perturbation remains consistent throughout one native rollout.
// Only completed native outcomes choose the next parent; it never predicts an
// effect, removes a legal action, or transfers a state between histories.
public sealed class LocalRolloutPolicy(int seed, LocalCardGoals? cardGoals = null)
{
    private readonly Random _random = new(seed);
    private Dictionary<string, int> _parent = new(StringComparer.Ordinal);
    private Dictionary<string, int> _current = new(StringComparer.Ordinal);
    private LocalCandidate? _best;
    private int _trials;
    private bool _neutral;
    private double _spread;

    public void Begin()
    {
        // Keep the ordinary prior as the first baseline. Interleave whole-policy
        // exploration with mutations of a measured parent, rather than changing
        // a single card and requiring that single change to improve immediately.
        int trial = _trials++;
        _neutral = trial == 0;
        bool fresh = trial % 4 == 3;
        _current = fresh ? new(StringComparer.Ordinal) : new(_parent, StringComparer.Ordinal);
        _spread = trial % 3 == 2 ? 60 : 30;
        if (!_neutral && !fresh)
            foreach (var key in _current.Keys.ToArray()) _current[key] = Mutate(_current[key]);
    }

    private int Mutate(int prior)
    {
        // Box-Muller gives small corrections and occasional changes in several
        // decisions. Bound preference magnitude, not the search space.
        double z = Math.Sqrt(-2 * Math.Log(Math.Max(double.Epsilon, _random.NextDouble()))) *
            Math.Cos(2 * Math.PI * _random.NextDouble());
        return (int)Math.Clamp(Math.Round(prior + z * _spread), -120, 120);
    }

    public int Priority(LocalAction action)
    {
        if (_neutral || action.EndTurn || action.PotionSlot.HasValue || string.IsNullOrEmpty(action.ModelId))
            return action.Preference;
        // This is a reusable preference for the model, never an identity key.
        // Instances, targets, native choice offers and history still remain separate.
        if (!_current.TryGetValue(action.ModelId, out int bias))
            _current.Add(action.ModelId, bias = Mutate(0));
        return action.Preference + bias;
    }

    public void Complete(LocalCandidate candidate)
    {
        if (!LocalSearchPolicy.Better(candidate, _best, cardGoals)) return;
        _best = candidate with { Continuation = null, Decisions = null };
        _parent = new(_current, StringComparer.Ordinal);
    }
}
