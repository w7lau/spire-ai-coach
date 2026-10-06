using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpireAiCoach.Core;

// A trace is submitted only after the native trial settles. Legal offers are
// captured BEFORE partitioning, ranking, expense pruning or coverage filtering.
// Limits and turn probes leave their last state open; they are never terminals.
public sealed record LocalLossProofStep(LocalAction Action, LocalAction[] Legal,
    LocalChoiceDecision[] Choices, LocalHealthEnvelope? After = null, bool CompleteLegal = true,
    LocalTurnHint? BeforeHint = null, LocalTurnHint? AfterHint = null, int? AfterRound = null);
public sealed record LocalLossProofTrial(int StartingHp, LocalLossProofStep[] Steps,
    int Hp, bool Won = false, bool Dead = false);
public sealed record LocalMinimumLossCertificate(string Scope, int StartingHp,
    int MinimumNetHpLoss, int MinimumPotionsUsed, int? MaximumFinalHp = null, bool ContentScoped = false);
public sealed record LocalLossProofTarget(int NetHpLoss, int PotionsUsed, int? FinalHp = null);
public sealed record LocalLossProofFocus(LocalAction[] Prefix, int SearchRound, LocalTurnHint Hint);
public sealed record LocalMinimumLossStatus(int Trials = 0, int Nodes = 1,
    LocalMinimumLossCertificate? Certificate = null, string InvalidReason = "", bool Confirmed = false,
    LocalLossProofTarget? Target = null, LocalLossProofFocus[]? Focus = null);

// Each exact history has an optimistic (signed HP loss, spent potions) floor. Taking
// the MIN over a COMPLETE native offer and the MAX with its own certified floor
// proves an ancestor bound without finishing every descendant battle.
public sealed class LocalMinimumLossProof(LocalSearchRequest request, int capacity = 131072)
{
    private readonly record struct Floor(int Loss, int Potions) : IComparable<Floor>
    {
        public static Floor Unknown => new(int.MinValue, 0);
        public int CompareTo(Floor other) => Loss != other.Loss ? Loss.CompareTo(other.Loss) : Potions.CompareTo(other.Potions);
        public static Floor Max(Floor a, Floor b) => a.CompareTo(b) >= 0 ? a : b;
        public static Floor Min(Floor a, Floor b) => a.CompareTo(b) <= 0 ? a : b;
    }
    private sealed class Node(Node? parent = null)
    {
        public readonly Node? Parent = parent;
        public readonly Dictionary<string, Node> Children = new(StringComparer.Ordinal);
        public readonly HashSet<string> Legal = new(StringComparer.Ordinal);
        public readonly Dictionary<string, LocalAction> Actions = new(StringComparer.Ordinal);
        public readonly Dictionary<string, LocalCardChoice> Choices = new(StringComparer.Ordinal);
        public LocalAction? Action;
        public LocalCardChoice? Choice;
        public LocalTurnHint? Hint;
        public int Round;
        public Floor Own = Floor.Unknown, Minimum = Floor.Unknown;
        public bool Complete, Terminal;
    }
    private readonly Node _root = new();
    private readonly string _scope = Scope(request);
    private int? _startingHp;
    private int _nodes = 1, _trials;
    private string _invalid = "";
    private Floor? _goal;
    private bool _knownRecovery;
    private bool _contentScoped;
    private LocalLossProofFocus[]? _focus;
    public static string Scope(LocalSearchRequest r) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { r.Id, r.SnapshotId, r.NativeHash, r.ModelHash, r.LoadedMods,
            r.IncludePotions, r.ExcludedModels, r.CardGoals, r.DataOnlyCombat, r.DataOnlyRun, r.NumericalExecution,
            HealthObjective = "final-hp-v3-player-content" }))));
    public LocalMinimumLossStatus Status => new(_trials, _nodes,
        _invalid.Length == 0 && _startingHp is { } hp && _root.Minimum.Loss <= hp
            ? new(_scope, hp, Math.Max(0, _root.Minimum.Loss), _root.Minimum.Potions,
                _root.Minimum.Loss == int.MinValue ? null : hp - _root.Minimum.Loss, _contentScoped) : null, _invalid,
        Target: _goal is { } goal ? new(Math.Max(0, goal.Loss), goal.Potions,
            _startingHp!.Value - goal.Loss) : null,
        Focus: _focus ??= FindFocus());
    public void Invalidate(string reason) { if (_invalid.Length == 0) _invalid = reason; _focus = []; }

    public static bool CannotImproveTarget(LocalHealthEnvelope state, LocalLossProofTarget? target) =>
        target?.FinalHp is { } hp && LocalHealthBound.MaximumFinalHp(state) is { } ceiling &&
        (ceiling < hp || ceiling == hp && state.PotionsUsed >= target.PotionsUsed);

    public void Observe(LocalLossProofTrial trial)
    {
        if (_invalid.Length > 0 || trial.Steps.Length == 0) return;
        if (trial.StartingHp <= 0 || trial.Hp < 0 || trial.Won && trial.Dead ||
            trial.Steps[0].Action.BeforeHash != request.NativeHash ||
            _startingHp.HasValue && _startingHp != trial.StartingHp || request.ExcludedModels is { Length: > 0 })
        { Invalidate("Proof trial does not match the complete frozen root"); return; }
        _startingHp = trial.StartingHp;
        _focus = null;
        var node = _root;
        int potions = 0;
        foreach (var step in trial.Steps)
        {
            if (node.Terminal || step.Legal.Length == 0 ||
                step.Legal.Any(a => a.BeforeHash != step.Action.BeforeHash || a.Round != step.Action.Round) ||
                !step.Legal.Any(a => ActionKey(a) == ActionKey(step.Action)) ||
                !request.IncludePotions && step.Legal.Any(a => a.PotionSlot.HasValue))
            { Invalidate("Proof action is outside its full native offer"); return; }
            Offer(node, step.Legal.Select(ActionKey), step.CompleteLegal);
            foreach (var action in step.Legal) node.Actions.TryAdd(ActionKey(action), action with { Choices = null });
            node.Hint = step.BeforeHint ?? node.Hint; node.Round = step.Action.Round;
            node = Follow(node, ActionKey(step.Action)); node.Action = step.Action with { Choices = null };
            if (step.Action.PotionSlot.HasValue) potions++;
            var selected = step.Action.Choices ?? [];
            if (selected.Length != step.Choices.Length)
            { Invalidate("Proof selection history is incomplete"); return; }
            for (int i = 0; i < selected.Length; i++)
            {
                var decision = step.Choices[i];
                if (decision.AtChoice != i || decision.Legal.Length == 0 ||
                    decision.Legal.Any(c => c.OfferHash != selected[i].OfferHash || c.Kind != selected[i].Kind) ||
                    !decision.Legal.Any(c => ChoiceKey(c) == ChoiceKey(selected[i])))
                { Invalidate("Proof selection does not match its exact native offer"); return; }
                Offer(node, decision.Legal.Select(ChoiceKey), decision.Legal.All(c => c.CompleteOffer));
                foreach (var choice in decision.Legal) node.Choices.TryAdd(ChoiceKey(choice), choice);
                node.Hint ??= step.BeforeHint; node.Round = step.Action.Round;
                node = Follow(node, ChoiceKey(selected[i])); node.Choice = selected[i];
            }
            if (step.After is { } envelope)
            {
                if (envelope.Root != request.SnapshotId + ":" + request.NativeHash ||
                    envelope.StartingHp != trial.StartingHp || envelope.Hp < 0 || envelope.PotionsUsed != potions ||
                    envelope.MaximumFurtherHpGain < 0)
                { Invalidate("Proof recovery bound does not match its settled history"); return; }
                var own = new Floor(LocalHealthBound.MinimumHpLoss(envelope) ?? int.MinValue, potions);
                node.Own = Floor.Max(node.Own, own);
                _knownRecovery |= envelope.MaximumFurtherHpGain.HasValue;
                _contentScoped |= envelope.ContentScoped;
            }
            node.Hint = step.AfterHint ?? node.Hint;
            node.Round = step.AfterRound ?? node.Round;
            Recalculate(node);
            if (_invalid.Length > 0) return;
        }
        if (trial.Won || trial.Dead)
        {
            var terminal = trial.Dead ? new Floor(int.MaxValue, 0) :
                new Floor(trial.StartingHp - trial.Hp, potions);
            for (var parent = node; parent != null; parent = parent.Parent)
                if (parent.Own.CompareTo(terminal) > 0)
                { Invalidate("A native victory contradicts a proposed loss floor"); return; }
            if (node.Children.Count > 0 || node.Terminal && node.Own != terminal)
            { Invalidate("Native terminal history changed"); return; }
            node.Terminal = true; node.Own = terminal; Recalculate(node);
            if (trial.Won) _goal = _goal is { } goal ? Floor.Min(goal, terminal) : terminal;
        }
        _trials++;
    }

    // Seek the weakest OPEN prefixes, rather than repeatedly finishing battles
    // whose outcome already reaches an incumbent. This is scheduling only: no
    // optimistic score becomes a certificate and no legal sibling is removed.
    private LocalLossProofFocus[] FindFocus()
    {
        if (_invalid.Length > 0 || !_knownRecovery || _goal is not { } target ||
            _root.Minimum.CompareTo(target) >= 0) return [];
        var result = new List<LocalLossProofFocus>();
        var queue = new Queue<Node>(); queue.Enqueue(_root);
        int inspected = 0;
        while (queue.TryDequeue(out var node) && result.Count < 16 && inspected++ < 512)
        {
            if (node.Terminal || node.Minimum.CompareTo(target) >= 0) continue;
            var prefix = Prefix(node);
            foreach (var key in node.Legal)
            {
                if (node.Children.TryGetValue(key, out var child)) { queue.Enqueue(child); continue; }
                if (node.Hint == null || node.Round < 1) continue;
                LocalAction[]? proposed = node.Actions.TryGetValue(key, out var action) ? [..prefix, action] :
                    node.Choices.TryGetValue(key, out var choice) && prefix.Length > 0 ?
                    [..prefix.Take(prefix.Length - 1), prefix[^1] with { Choices = [..prefix[^1].Choices ?? [], choice] }] : null;
                if (proposed != null) result.Add(new(proposed, node.Round, node.Hint));
                if (result.Count >= 16) break;
            }
            if (node.Legal.Count == 0 && node.Hint != null && node.Round > 0 && prefix.Length > 0)
                result.Add(new(prefix, node.Round, node.Hint));
        }
        return result.ToArray();
    }

    private static LocalAction[] Prefix(Node node)
    {
        var path = new Stack<Node>();
        for (var at = node; at.Parent != null; at = at.Parent) path.Push(at);
        var actions = new List<LocalAction>();
        while (path.TryPop(out var edge))
        {
            if (edge.Action != null) actions.Add(edge.Action);
            else if (edge.Choice != null && actions.Count > 0)
                actions[^1] = actions[^1] with { Choices = [..actions[^1].Choices ?? [], edge.Choice] };
        }
        return actions.ToArray();
    }

    private void Offer(Node node, IEnumerable<string> offered, bool complete)
    {
        var keys = offered.ToHashSet(StringComparer.Ordinal);
        if (node.Terminal || node.Complete && keys.Any(k => !node.Legal.Contains(k)) ||
            complete && node.Legal.Any(k => !keys.Contains(k)))
        { Invalidate("Native legal offer changed at the same exact history"); return; }
        node.Legal.UnionWith(keys); node.Complete |= complete; Recalculate(node);
    }
    private Node Follow(Node node, string key)
    {
        if (!node.Children.TryGetValue(key, out var child))
        {
            // Capacity is never evidence: keep the proof unresolved rather than
            // silently dropping unobserved siblings or shrinking the search.
            if (_nodes >= capacity) Invalidate("Loss proof storage capacity reached");
            child = new(node); node.Children.Add(key, child); _nodes++;
        }
        return child;
    }
    private static void Recalculate(Node node)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            var floor = current.Own;
            if (!current.Terminal && current.Complete && current.Legal.Count > 0)
            {
                var children = new Floor(int.MaxValue, int.MaxValue);
                foreach (var key in current.Legal)
                    children = Floor.Min(children, current.Children.TryGetValue(key, out var child) ? child.Minimum : Floor.Unknown);
                floor = Floor.Max(floor, children);
            }
            if (floor == current.Minimum) break;
            current.Minimum = floor;
        }
    }
    // Native card instance, target, before-state and every ordered choice remain
    // identity. Preferences, display names and UI hand shifts are not identity.
    private static string ActionKey(LocalAction a) => LocalTurnSearch.HistoryKey([a with { Choices = null }]);
    private static string ChoiceKey(LocalCardChoice c) => JsonSerializer.Serialize(new
        { c.OfferHash, c.Kind, c.Index, c.ModelId, Indices = c.Indices ?? [] });
}
