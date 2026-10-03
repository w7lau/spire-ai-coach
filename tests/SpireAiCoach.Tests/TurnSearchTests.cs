using SpireAiCoach.Core;

static class TurnSearchTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static LocalAction Move(int i, string before = "root", int round = 1, int preference = 0) =>
        new(i, "opaque", null, "same name", "", before, round, Preference: preference, CombatCardIndex: (uint)i);
    private static LocalCandidate Win(int hp = 30, int starting = 50, LocalAction[]? actions = null) =>
        new(actions ?? [], hp, starting - hp, 0, 0, 100, true, false, false, StartingHp: starting);

    public static void Register(Action<string, Action> test)
    {
        test("turn optimization winning feedback promotes pending native siblings without merging or closing others", () =>
        {
            var search = new LocalTurnSearch(1);
            var start = Move(0);
            var choice = new LocalCardChoice("offer", 0, "opaque", "same");
            start = start with { Choices = [choice] };
            var hint = new LocalTurnHint(50, 50, 100, 100);
            search.Offer([start], 1, hint); search.TryTake(out _);
            for (int i = 10; i < 1010; i++) search.Offer([Move(i)], 1, hint);
            var actual = Move(1, "after root", preference: 60);
            var setup = Move(2, "after root", preference: 20) with { TargetId = 3 };
            var decision = new LocalDecision(1, [actual, setup]);
            search.OfferAlternatives([start, actual], decision, hint);
            var candidate = Win(actions: [start, actual]) with { Decisions = [decision] };
            search.PromoteWinning(candidate with { Won = false });
            search.PromoteWinning(candidate);
            search.PromoteWinning(candidate with { Hp = 1, Actions = [Move(10)],
                Decisions = [new(0, [Move(10), Move(11)])] });
            Check(search.TryTake(out _), "Missing fair broad task");
            Check(search.TryTake(out var improved) && improved.FullRollout && improved.Prefix.Length == 2 &&
                improved.Prefix[1].TargetId == 3 && improved.Prefix[1].CombatCardIndex == 2 &&
                LocalTurnSearch.SameChoice(improved.Prefix[0].Choices![0], choice),
                "Winning feedback lost the exact earlier choices or its pending target/instance");
            Check(search.Count == 999, "Promotion must not create, merge or delete unrelated native branches");
        });

        test("turn optimization complete feedback rotates across all scheduling lanes", () =>
        {
            var counts = new int[4];
            for (int block = 0; block < 16; block++)
            {
                int complete = 0;
                for (int lane = 0; lane < 4; lane++)
                    if (LocalTurnSearch.IsFullRollout(block * 4 + lane)) { complete++; counts[lane]++; }
                Check(complete == 2, "Half the attempts must execute complete battle feedback");
            }
            Check(counts.All(n => n == 8), "A fixed scheduler lane must not monopolize complete feedback");
        });

        test("turn optimization focused work deepens combinations despite a large shallow frontier", () =>
        {
            var search = new LocalTurnSearch(1);
            var start = Move(0);
            var hint = new LocalTurnHint(50, 50, 100, 100);
            search.Offer([start], 1, hint);
            Check(search.TryTake(out var parent), "Missing owned prefix");
            for (int i = 10; i < 1010; i++) search.Offer([Move(i)], 1, hint);
            var actual = Move(1, "native child", preference: 80);
            var setup = Move(2, "native child", preference: 30);
            var other = Move(3, "native child", preference: 10);
            var actions = new[] { start, actual };
            var decision = new LocalDecision(1, [actual, other, setup]);
            search.OfferAlternatives(actions, decision, hint);
            search.FocusNext(parent, actions, [decision]);
            Check(search.TryTake(out _), "Missing broad work");
            Check(search.TryTake(out var nested) && nested.Prefix.Length == 2 &&
                nested.Prefix[0].CombatCardIndex == 0 && nested.Prefix[1].CombatCardIndex == 2,
                "The next compound alternative was buried behind shallow tasks");
            Check(search.FocusedTakes == 1 && search.Count == 1000, "Focused work must not delete other legal proposals");
        });

        test("turn optimization focused proposals retain native targets and completed choices", () =>
        {
            var search = new LocalTurnSearch(1);
            var choice = new LocalCardChoice("native offer", 1, "opaque", "same name");
            var root = Move(0) with { Choices = [choice] };
            var hint = new LocalTurnHint(50, 50, 100, 100);
            search.Offer([root], 1, hint); search.TryTake(out var task);
            var actual = Move(1, "child") with { TargetId = 1 };
            var changed = actual with { TargetId = 2 };
            var decision = new LocalDecision(1, [actual, changed]);
            search.OfferAlternatives([root, actual], decision, hint);
            search.FocusNext(task, [root, actual], [decision]);
            Check(search.TryTake(out var proposal) && proposal.Prefix[1].TargetId == 2 &&
                LocalTurnSearch.SameChoice(proposal.Prefix[0].Choices![0], choice),
                "Focus cannot alias targets or discard earlier native choices");
        });

        test("turn optimization interposed broad probes cannot steal an active compound descent", () =>
        {
            var search = new LocalTurnSearch(1);
            var hint = new LocalTurnHint(50, 50, 100, 100);
            var start = Move(0);
            search.Offer([start], 1, hint); search.TryTake(out var parent);
            for (int i = 10; i < 50; i++) search.Offer([Move(i)], 1, hint);
            var actual = Move(1, "after root", preference: 80);
            var alternate = Move(2, "after root", preference: 30);
            var point = new LocalDecision(1, [actual, alternate]);
            search.OfferAlternatives([start, actual], point, hint);
            search.FocusNext(parent, [start, actual], [point]);
            search.TryTake(out _);
            Check(search.TryTake(out var nested) && search.LastFocused, "Missing focused sibling");
            var next = Move(3, "after alternate", preference: 80);
            var combined = Move(4, "after alternate", preference: 30);
            var nextPoint = new LocalDecision(2, [next, combined]);
            search.OfferAlternatives([start, alternate, next], nextPoint, hint);
            search.FocusNext(nested, [start, alternate, next], [nextPoint]);
            for (int i = 0; i < 3; i++)
            {
                Check(search.TryTake(out var broad), "Missing interposed broad task");
                var newPoint = new LocalDecision(1, [actual, alternate]);
                search.OfferAlternatives([broad.Prefix[0], actual], newPoint, hint);
                search.FocusNext(broad, [broad.Prefix[0], actual], [newPoint]);
            }
            Check(search.TryTake(out var deeper) && search.LastFocused && deeper.Prefix.Length == 3 &&
                deeper.Prefix[0].CombatCardIndex == 0 && deeper.Prefix[1].CombatCardIndex == 2 && deeper.Prefix[2].CombatCardIndex == 4,
                "A newer shallow family hijacked the native compound continuation");
        });

        test("turn optimization resource savings compare surviving native hand instances", () =>
        {
            Dictionary<uint, int> before = new() { [1] = 3, [2] = 2, [3] = 5, [4] = 1 };
            Dictionary<uint, int> after = new() { [1] = 0, [2] = 1, [4] = 2, [5] = 0 };
            Check(LocalResourceEffects.HandCostSavings(before, after, 1) == 1,
                "Played/removed/new cards and increased costs are not a surviving-hand reduction");
            Check(LocalResourceEffects.HandCostSavings(new Dictionary<uint, int> { [1] = 2 },
                new Dictionary<uint, int> { [2] = 0 }, 9) == 0,
                "A same-model replacement cannot inherit another native instance's cost");
            Check(LocalResourceEffects.HandCostSavings(new Dictionary<uint, int> { [1] = -1, [2] = 4 },
                new Dictionary<uint, int> { [1] = 0, [2] = -1 }, 9) == 0,
                "Unknown negative costs must not establish savings");
        });

        test("turn optimization health bounds prune only a proven inferior completed outcome", () =>
        {
            var incumbent = LocalWinningBound.From("battle", Win());
            Check(LocalHealthBound.CannotImprove(new("battle", 50, 29, 0, 0), incumbent), "29 with no future recovery cannot beat a completed 30-HP victory");
            Check(!LocalHealthBound.CannotImprove(new("battle", 50, 29, 0, 1), incumbent), "Equal HP may still improve another objective");
            Check(!LocalHealthBound.CannotImprove(new("battle", 50, 29, 0, 5), incumbent), "Future healing can reverse the current HP order");
            Check(!LocalHealthBound.CannotImprove(new("battle", 50, 29, 0), incumbent), "Unknown recovery must not be treated as zero");
            Check(!LocalHealthBound.CannotImprove(new("other battle", 50, 29, 0, 0), incumbent), "Different native roots are incomparable");
            Check(!LocalHealthBound.CannotImprove(new("battle", 60, 29, 0, 0), incumbent), "Different starting HP changes the net-loss objective");
        });

        test("turn optimization a healthier partial turn is not a winning bound", () =>
        {
            // No healing is needed for this counterexample: the lower-HP branch
            // is about to kill; the healthier branch still faces future attacks.
            var healthier = Win() with { Won = false, EnemyHp = 100 };
            var bound = LocalWinningBound.From("battle", healthier);
            Check(bound == null && !LocalHealthBound.CannotImprove(new("battle", 50, 29, 0, 0), bound),
                "Partial player HP must not delete a different future combat state");
            Check(LocalWinningBound.From("battle", Win() with { Dead = true }) == null, "Dead is not a valid incumbent");
        });

        test("turn optimization unsaturated healing does not establish common future recovery", () =>
        {
            // Both outcomes remain below max HP. One path's extra draw/energy
            // supplies more healing, so merely observing no overflow proves nothing.
            var incumbent = LocalWinningBound.From("battle", Win(35));
            Check(!LocalHealthBound.CannotImprove(new("battle", 50, 29, 0, 12), incumbent),
                "29+12 can beat 30+5 even without either route reaching full HP");
            Check(!LocalHealthBound.CannotImprove(new("battle", 50, 80, 0, long.MaxValue), incumbent),
                "An optimistic recovery ceiling must not overflow into a false prune");
        });

        test("turn optimization potion reserve pruning is safe even with unknown healing", () =>
        {
            var incumbent = LocalWinningBound.From("battle", Win(50));
            Check(LocalHealthBound.CannotImprove(new("battle", 50, 1, 1), incumbent),
                "Even unlimited recovery cannot beat a no-loss victory with fewer spent potions");
            Check(!LocalHealthBound.CannotImprove(new("battle", 50, 1, 0), incumbent),
                "Equal potion count may still improve HP, gold or route length");
            Check(!LocalHealthBound.CannotImprove(new("battle", 50, 1, 1), LocalWinningBound.From("battle", Win(49))),
                "A potion may remove the incumbent's nonzero loss");
            bool rejected = false;
            try { LocalHealthBound.CannotImprove(new("battle", 50, 29, 0, -1), incumbent); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "A negative recovery ceiling is invalid");
        });

        test("turn optimization exact prefixes distinguish native instances and reject changed state", () =>
        {
            var action = Move(1);
            var shifted = action with { HandIndex = 8, CardName = "new name", Preference = 100 };
            Check(LocalTurnSearch.ResolveExact(action, [shifted]) == shifted, "Native identity survives display and hand-position changes");
            Check(LocalTurnSearch.HistoryKey([action]) == LocalTurnSearch.HistoryKey([shifted]), "A search hint cannot redefine a prefix");
            Check(LocalTurnSearch.HistoryKey([action]) != LocalTurnSearch.HistoryKey([action with { CombatCardIndex = 2 }]), "Same-model instances must stay distinct");
            bool failed = false;
            try { LocalTurnSearch.ResolveExact(action, [action with { BeforeHash = "different" }]); }
            catch (InvalidOperationException) { failed = true; }
            Check(failed, "Changed native state must stop exact replay");
        });

        test("turn optimization fair frontier retains weak-looking future setup", () =>
        {
            var search = new LocalTurnSearch(1);
            search.Offer([Move(0)], 1, new(10, 50, 100, 100));
            for (int i = 1; i <= 100; i++) search.Offer([Move(i)], 1, new(50, 50, 10, 100));
            bool sawSetup = false;
            for (int i = 0; i < 4; i++) { Check(search.TryTake(out var task), "Missing task"); sawSetup |= task.Prefix[0].CombatCardIndex == 0; }
            Check(sawSetup && search.Offered == 101, "FIFO work must preserve a bad-looking but potentially useful setup");
            int count = search.Count;
            Check(search.TryTake(out var interrupted), "Missing interrupted task");
            search.ReturnInterrupted(interrupted, new(20, 50, 30, 100));
            Check(search.Count == count, "An incomplete turn remains pending");
        });

        test("turn optimization selection forks keep changed later offers unconstrained", () =>
        {
            var original = new LocalCardChoice("offer-a", 0, "model-a", "name", Kind: "select", Indices: [0, 2]);
            var later = new LocalCardChoice("offer-b", 1, "model-b", "name");
            var alternative = original with { Index = 2, Indices = [1, 2] };
            var played = Move(1) with { Choices = [original, later] };
            var search = new LocalTurnSearch(1);
            search.OfferAlternatives([played], new(0, [Move(1)], [new(0, [original, alternative])]), new(50, 50, 100, 100));
            Check(search.TryTake(out var task) && task.Prefix.Length == 1 && task.Prefix[0].Choices is { Length: 1 },
                "Changed selection must not replay its old later offer");
            Check(LocalTurnSearch.SameChoice(task.Prefix[0].Choices![0], alternative), "Combination identity was lost");
            Check(LocalTurnSearch.HistoryKey([played]) != LocalTurnSearch.HistoryKey([played with { Choices = [alternative, later] }]),
                "Distinct native selection combinations must stay distinct");
        });

        test("turn optimization short-prefix probes do not lose long late-game continuations", () =>
        {
            var search = new LocalTurnSearch(1);
            var longPrefix = Enumerable.Range(0, 60).Select(i => Move(i)).ToArray();
            search.Offer(longPrefix, 10, new(50, 50, 1, 100));
            search.Offer([Move(100)], 1, new(50, 50, 100, 100));
            Check(search.TryTake(out var shortTask) && shortTask.Prefix.Length == 1,
                "A near-kill state must not turn every probe into a long replay");
            Check(search.TryTake(out var lateTask) && lateTask.Prefix.Length == 60,
                "Replay cost orders work but never discards a promising late turn");
        });

        test("turn optimization known winning bounds discard prescribed potion prefixes before replay", () =>
        {
            var search = new LocalTurnSearch(1, "battle");
            var hint = new LocalTurnHint(20, 50, 10, 100);
            search.Offer([Move(1) with { PotionSlot = 0 }], 1, hint);
            search.Offer([Move(2)], 1, hint);
            Check(search.DiscardProvenExpenses(LocalWinningBound.From("battle", Win(49))) == 0,
                "Recovery may improve a nonzero-loss incumbent");
            Check(search.DiscardProvenExpenses(LocalWinningBound.From("another battle", Win(50))) == 0,
                "A different frozen root cannot certify a frontier cut");
            Check(search.DiscardProvenExpenses(LocalWinningBound.From("battle", Win(50))) == 1 && search.Count == 1,
                "An exact future potion expense need not be replayed to prove its inferiority");
            Check(search.TryTake(out var task) && task.Prefix[0].PotionSlot == null, "The no-potion sibling was discarded");
        });

        test("turn optimization prune descendants preserves sibling targets and choices", () =>
        {
            var search = new LocalTurnSearch(1);
            var spent = Move(1) with { PotionSlot = 0 };
            var sibling = spent with { TargetId = 1 };
            var hint = new LocalTurnHint(20, 50, 20, 100);
            search.Offer([spent, Move(2)], 2, hint);
            search.Offer([spent, Move(3)], 2, hint);
            search.Offer([sibling, Move(2)], 2, hint);
            Check(search.DiscardDescendants([spent]) == 2 && search.Count == 1, "Only proven exact-history descendants may be removed");
            Check(search.TryTake(out var survivor) && survivor.Prefix[0].TargetId == 1, "An unrelated native target was pruned");
        });

        test("turn optimization retained turn frontier matches an independent finite-tree oracle", () =>
        {
            for (int seed = 0; seed < 16; seed++)
            {
                int Hash(IEnumerable<int> path) => path.Aggregate(seed + 13, (h, x) => unchecked(h * 31 + x + 7) & 0xffff);
                bool Terminal(int[] path) => path.Length >= 5 || path.Length > 1 && Hash(path) % 7 == 0;
                LocalAction[] Legal(int[] path) => Enumerable.Range(0, 2 + Hash(path) % 2)
                    .Select(i => Move(i, string.Join(',', path), path.Length / 2 + 1, (Hash(path) + i * 11) % 10)).ToArray();
                LocalCandidate Outcome(int[] path, LocalAction[] actions) => Win(30 + Hash(path) % 51, 80, actions)
                    with { HpLost = Hash(path) % 30, Rounds = (path.Length + 1) / 2, Gold = Hash(path) % 12 };
                var leaves = new Dictionary<string, LocalCandidate>();
                void Enumerate(int[] path)
                {
                    if (Terminal(path)) { leaves.Add(string.Join(',', path), Outcome(path, [])); return; }
                    foreach (var a in Legal(path)) Enumerate([..path, a.HandIndex]);
                }
                Enumerate([]);
                var search = new LocalTurnSearch(seed);
                search.Offer([], 1, new(80, 80, 100, 100));
                var visited = new HashSet<string>();
                LocalCandidate? best = null;
                int trials = 0;
                while (search.TryTake(out var task))
                {
                    Check(++trials < 10000, "Finite observed frontier did not finish");
                    bool full = trials % 4 == 1;
                    var actions = new List<LocalAction>(); int[] path = [];
                    int plan = 0;
                    while (!Terminal(path))
                    {
                        int round = path.Length / 2 + 1;
                        if (!full && plan >= task.Prefix.Length && round > task.SearchRound) break;
                        var legal = Legal(path);
                        var next = plan < task.Prefix.Length ? LocalTurnSearch.ResolveExact(task.Prefix[plan++], legal) : search.Choose(legal);
                        var hint = new LocalTurnHint(30 + Hash(path) % 51, 80, 100 - path.Length * 15, 100);
                        actions.Add(next); path = [..path, next.HandIndex];
                        if (actions.Count >= task.Prefix.Length) search.OfferAlternatives(actions, new(actions.Count - 1, legal), hint);
                        if (!Terminal(path) && path.Length % 2 == 0 && actions.Count >= task.Prefix.Length)
                            search.Offer(actions.ToArray(), path.Length / 2 + 1, hint);
                    }
                    if (!Terminal(path)) continue;
                    visited.Add(string.Join(',', path));
                    var result = Outcome(path, actions.ToArray());
                    if (LocalSearchPolicy.Better(result, best)) best = result;
                }
                var oracle = leaves.Values.Aggregate((a, b) => LocalSearchPolicy.Better(b, a) ? b : a);
                Check(visited.SetEquals(leaves.Keys), "Turn stopping or priority order lost a legal terminal continuation");
                Check(best != null && best.NetHpLoss == oracle.NetHpLoss && best.Hp == oracle.Hp && best.Gold == oracle.Gold,
                    "Native-outcome ordering missed the oracle's health objective");
            }
        });
    }
}
