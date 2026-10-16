using System.Reflection;
using HarmonyLib;
using SpireAiCoach.Core;

// Parent-only observations and ablation. Owned task prefixes and all legal
// siblings remain exact; clearing a soft tail changes proposals, never replay.
static class DecisionStudy
{
    private static bool _fresh;
    private static bool _goalBeforeProof;
    private static bool _goalContinuation;
    private static bool _retainedGoal;
    private static readonly Dictionary<LocalAction[], LocalGoalOpportunity?> Retained = new(ReferenceEqualityComparer.Instance);
    private static LocalCardGoals? _goals;
    private static LocalCandidate? _best;
    private static int _deferred;
    public static int Deferred => _deferred;
    public static int Promoted { get; private set; }
    private static readonly List<object> Events = [];
    public static int Cleared { get; private set; }
    public static void Begin(string mode, LocalCardGoals? goals)
    { _fresh = mode == "fresh-guided-tail"; _goalBeforeProof = mode == "goal-before-proof";
      _goalContinuation = mode == "goal-continuation";
      _retainedGoal = mode == "retained-goal-seed"; Retained.Clear();
      _goals = goals; _best = null; _deferred = 0; Cleared = 0; Promoted = 0; Events.Clear(); }
    public static object[] Snapshot() => Events.ToArray();
    public static void Install()
    {
        var h = new Harmony("spire.route.study.decisions.parent.only");
        h.Patch(typeof(LocalCardGoalTactics).GetMethod(nameof(LocalCardGoalTactics.BetterExplorationSeed)),
            postfix: new HarmonyMethod(typeof(DecisionStudy).GetMethod(nameof(ExplorationSeed), BindingFlags.NonPublic | BindingFlags.Static)));
        h.Patch(typeof(LocalTurnSearch).GetMethod(nameof(LocalTurnSearch.TryTake), [typeof(LocalTurnTask).MakeByRefType(), typeof(int?)]),
            postfix: new HarmonyMethod(typeof(DecisionStudy).GetMethod(nameof(Taken), BindingFlags.NonPublic | BindingFlags.Static)));
        h.Patch(typeof(LocalTurnSearch).GetMethod(nameof(LocalTurnSearch.FocusNext),
            [typeof(LocalTurnTask), typeof(IReadOnlyList<LocalAction>), typeof(IReadOnlyList<LocalDecision>), typeof(bool)]),
            postfix: new HarmonyMethod(typeof(DecisionStudy).GetMethod(nameof(Focused), BindingFlags.NonPublic | BindingFlags.Static)));
        h.Patch(typeof(LocalTurnSearch).GetMethod(nameof(LocalTurnSearch.ObserveOutcome)),
            postfix: new HarmonyMethod(typeof(DecisionStudy).GetMethod(nameof(Outcome), BindingFlags.NonPublic | BindingFlags.Static)));
        // Present only in the low-damage proposal candidate. Reflection keeps
        // the observational harness usable against the previous exact package.
        if (typeof(LocalTurnSearch).GetMethod("ProposeLowDamageRoute") is { } lowDamage)
            h.Patch(lowDamage, postfix: new HarmonyMethod(typeof(DecisionStudy).GetMethod(nameof(LowDamage), BindingFlags.NonPublic | BindingFlags.Static)));
        h.Patch(typeof(LocalMinimumLossProof).GetProperty(nameof(LocalMinimumLossProof.Status))!.GetMethod,
            postfix: new HarmonyMethod(typeof(DecisionStudy).GetMethod(nameof(ProofStatus), BindingFlags.NonPublic | BindingFlags.Static)));
    }
    private static void Taken(bool __result, ref LocalTurnTask task)
    {
        if (!__result) return;
        if (_fresh && !task.LossProof && task.Continuation is { Length: > 0 } && task.Prefix.Length > 0)
        {
            task = task with { Continuation = null, GoalLossFocus = true };
            Cleared++;
        }
        // A native prefix that already tried the selected card and survived a
        // turn deserves whole-battle feedback. This is a proposal hint, not
        // credit for achieving the goal, and leaves the original task ordering.
        if (_goalContinuation && !task.LossProof && _goals is { Enabled: true } &&
            task.Prefix.Length > 0 && task.SearchRound > task.Prefix[0].Round &&
            task.Prefix.Any(a => a.ModelId == _goals.PlayModelId || a.ModelId == _goals.FinisherModelId))
        {
            task = task with { FullRollout = true, Continuation = null, GoalLossFocus = true,
                Style = LocalRolloutStyle.Preparation };
            Promoted++;
        }
        Events.Add(new { kind = "take", task });
    }
    private static void ExplorationSeed(LocalCandidate candidate, LocalCandidate? incumbent, LocalCardGoals? goals, ref bool __result)
    {
        if (!_retainedGoal || goals?.Enabled != true || !candidate.Won || candidate.Dead || incumbent == null ||
            goals.Compare(candidate, incumbent) != 0) return;
        LocalGoalOpportunity? Stock(LocalCandidate c)
        {
            if (c.Decisions?.LastOrDefault() is { } last && last.BeforeStep == c.Actions.Length-1)
                Retained[c.Actions] = last.GoalsBefore;
            return Retained.GetValueOrDefault(c.Actions);
        }
        var a=Stock(candidate); var b=Stock(incumbent);
        if (a?.Matches(goals)!=true || b?.Matches(goals)!=true) return;
        int Score(LocalGoalOpportunity s) => Math.Min(s.Finisher?.Available??0,
            Math.Max(0,(s.Finisher?.Target??0)-(s.Finisher?.Completed??0)));
        if (Score(a)!=Score(b)) __result=Score(a)>Score(b);
    }
    private static void Focused(LocalTurnTask task, IReadOnlyList<LocalAction> actions, IReadOnlyList<LocalDecision> decisions) =>
        Events.Add(new { kind = "observed", task.Id, actions = actions.ToArray(), decisions = decisions.ToArray() });
    private static void Outcome(LocalCandidate candidate)
    {
        if (LocalSearchPolicy.Better(candidate, _best, _goals)) _best = candidate;
        Events.Add(new { kind = "candidate", candidate });
    }
    private static void LowDamage(LocalCandidate candidate) => Events.Add(new { kind = "low_damage_seed", candidate });
    private static void ProofStatus(ref LocalMinimumLossStatus __result)
    {
        // Scheduling only. Retain all observations, bounds and certificates.
        // Complete the selected incumbent's finite card goals before spending
        // dedicated probes certifying a route that cannot yet satisfy them.
        if (!_goalBeforeProof || _goals is not { Enabled: true } ||
            _best?.CardGoalOutcome is { ConsumableGoals: { } goals } outcome && goals.Complete(_goals, outcome)) return;
        if (__result.Focus is { Length: > 0 }) { Interlocked.Increment(ref _deferred); __result = __result with { Focus = [] }; }
    }
}
