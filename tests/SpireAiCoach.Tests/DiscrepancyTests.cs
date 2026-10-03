using System.Diagnostics;
using System.Text.Json;
using SpireAiCoach.Core;

static class DiscrepancyTests
{
    private static readonly LocalSearchOrder[] Orders = [LocalSearchOrder.LimitedDiscrepancy, LocalSearchOrder.DepthDiscrepancy, LocalSearchOrder.DiscrepancyPortfolio];
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static LocalAction Move(int index, string before = "visible", int preference = 0) =>
        new(index, "opaque-model", null, "", "", before, 1, Preference: preference, CombatCardIndex: (uint)index + 1);
    private static LocalCandidate Result(LocalAction[] actions, int hp = 80, bool won = true) =>
        new(actions, hp, Math.Max(0, 80 - hp), 0, 0, 100, won, false, false, StartingHp: 80);

    public static void Register(Action<string, Action> test)
    {
        test("discrepancy finite multiway search matches an independent exhaustive oracle", () =>
        {
            foreach (var order in Orders)
            for (int seed = 0; seed < 30; seed++)
            {
                int Hash(IReadOnlyList<int> path) => path.Aggregate(seed + 11, (h, x) => unchecked(h * 31 + x + 1) & 0xffff);
                bool Leaf(IReadOnlyList<int> path) => path.Count >= 5 || path.Count > 0 && Hash(path) % 5 == 0;
                LocalAction[] Legal(IReadOnlyList<int> path) => Enumerable.Range(0, 2 + Hash(path) % 2)
                    .Select(i => Move(i, preference: (Hash(path) + i * 17) % 9)).ToArray();
                LocalCandidate Outcome(int[] path) => Result(path.Select(i => Move(i)).ToArray(), 40 + Hash(path) % 61, Hash(path) % 3 != 0)
                    with { EnemyHp = Hash(path) % 3 == 0 ? Hash(path) % 20 + 1 : 0, HpLost = Hash(path) % 40,
                        Gold = Hash(path) % 50, Rounds = path.Length };
                var expected = new Dictionary<string, LocalCandidate>();
                void Enumerate(int[] path)
                {
                    if (Leaf(path)) { expected.Add(string.Join(',', path), Outcome(path)); return; }
                    foreach (var action in Legal(path)) Enumerate([..path, action.HandIndex]);
                }
                Enumerate([]);
                var oracle = expected.Values.Aggregate((a, b) => LocalSearchPolicy.Better(b, a) ? b : a);
                var tree = new LocalSearchTree(seed, capacity: 2, order: order);
                var seen = new HashSet<string>();
                LocalCandidate? best = null;
                while (!tree.Exhausted && seen.Count <= expected.Count)
                {
                    var trial = tree.Begin();
                    var path = new List<int>();
                    while (!Leaf(path)) path.Add(tree.Select(trial, Legal(path)).HandIndex);
                    Check(seen.Add(string.Join(',', path)), $"{order}: repeated a completed leaf");
                    var outcome = Outcome(path.ToArray());
                    if (LocalSearchPolicy.Better(outcome, best)) best = outcome;
                    tree.Complete(trial, outcome, 100, closeExactPrefix: true);
                }
                Check(tree.Exhausted && seen.SetEquals(expected.Keys), $"{order}: lost a legal continuation");
                Check(best != null && !LocalSearchPolicy.Better(oracle, best) && !LocalSearchPolicy.Better(best, oracle),
                    $"{order}: failed the exhaustive objective");
                Check(tree.CompletedTrials == expected.Count && tree.Nodes > 2, "Storage cap must not discard the systematic frontier");
            }
        });

        test("discrepancy fixes setup before attack without knowing card names or effects", () =>
        {
            foreach (var order in Orders)
            {
                var tree = new LocalSearchTree(1, order: order);
                LocalCandidate? best = null;
                for (int route = 0; !tree.Exhausted && route < 20; route++)
                {
                    var trial = tree.Begin();
                    var actions = new List<LocalAction>();
                    int energy = 2, enemyHp = 9; bool upgraded = false;
                    var hand = new HashSet<int> { 0, 1 };
                    while (energy > 0 && enemyHp > 0)
                    {
                        var action = tree.Select(trial, hand.Select(i => Move(i, preference: i == 0 ? 10 : 0)).ToArray());
                        actions.Add(action); hand.Remove(action.HandIndex); energy--;
                        if (action.HandIndex == 1) upgraded = true;
                        else enemyHp -= upgraded ? 9 : 6;
                    }
                    var candidate = Result(actions.ToArray(), won: enemyHp <= 0) with { EnemyHp = Math.Max(0, enemyHp) };
                    if (LocalSearchPolicy.Better(candidate, best)) best = candidate;
                    tree.Complete(trial, candidate, 9, closeExactPrefix: true);
                }
                Check(best?.Won == true && best.Actions.Select(a => a.HandIndex).SequenceEqual(new[] { 1, 0 }), "Setup must precede the actual damaging action");
            }
        });

        test("discrepancy preserves defense with a future payoff and healed HP costs", () =>
        {
            foreach (var order in Orders)
            {
                var tree = new LocalSearchTree(1, order: order);
                LocalCandidate? best = null;
                for (int i = 0; i < 2; i++)
                {
                    var trial = tree.Begin();
                    var first = tree.Select(trial, [Move(0, preference: -10), Move(1, preference: 10) with { EndTurn = true }]);
                    int block = first.EndTurn ? 0 : 12;
                    var slam = tree.Select(trial, [Move(2, "next-round") with { Round = 2 }]);
                    // A toy transition, not production card logic: persistent block enables a kill.
                    var candidate = Result([first, slam], block > 0 ? 80 : 66, block > 0)
                        with { HpLost = block > 0 ? 14 : 0, EnemyHp = Math.Max(0, 12 - block) };
                    if (LocalSearchPolicy.Better(candidate, best)) best = candidate;
                    tree.Complete(trial, candidate, 12, closeExactPrefix: true);
                }
                Check(best?.NetHpLoss == 0 && best.HpLost == 14 && !best.Actions[0].EndTurn,
                    "Current-turn low preference or recovered HP cost must not hide the future outcome");
            }
        });

        test("discrepancy enumerates native targets and nested selection siblings independently", () =>
        {
            foreach (var order in Orders)
            {
                var tree = new LocalSearchTree(1, order: order);
                var seen = new HashSet<(uint?, int)>();
                while (!tree.Exhausted && seen.Count < 6)
                {
                    var trial = tree.Begin();
                    var action = tree.Select(trial, [Move(0) with { TargetId = 1 }, Move(0) with { TargetId = 2 }]);
                    var choices = Enumerable.Range(0, 3).Select(i => new LocalAction(i, "choice:opaque", null, "same name", "", "exact-offer")).ToArray();
                    var choice = tree.Select(trial, choices);
                    Check(seen.Add((action.TargetId, choice.HandIndex)), "Repeated exact target/choice");
                    tree.Complete(trial, Result([action]), 100, closeExactPrefix: true);
                }
                Check(tree.Exhausted && seen.Count == 6, "Each legal native choice must survive");
            }
        });

        test("discrepancy retains instance identity when hand positions and display text change", () =>
        {
            foreach (var order in Orders)
            {
                var tree = new LocalSearchTree(1, order: order);
                var a = Move(0) with { CombatCardIndex = 10 };
                var b = a with { CombatCardIndex = 20 };
                var first = tree.Begin(); var selected = tree.Select(first, [a, b]);
                tree.Complete(first, Result([selected]), 100, closeExactPrefix: true);
                var second = tree.Begin();
                var other = tree.Select(second, [b with { HandIndex = 4, CardName = "renamed", Preference = 99 }, a with { HandIndex = 2 }]);
                Check(other.CombatCardIndex != selected.CombatCardIndex, "Distinct native card instances were merged");
                tree.Complete(second, Result([other]), 100, closeExactPrefix: true);
                Check(tree.Exhausted, "Changing a heuristic must not redefine the visited frontier");
            }
        });

        test("discrepancy interrupted rollouts stay open and trials cannot cross trees", () =>
        {
            foreach (var order in Orders)
            {
                var tree = new LocalSearchTree(1, order: order);
                var trial = tree.Begin(); var first = tree.Select(trial, [Move(0), Move(1)]);
                tree.Complete(trial, Result([first]), 100, closeExactPrefix: false);
                Check(!tree.Exhausted, "An interruption is not a complete subtree");
                var resumed = tree.Begin();
                Check(tree.Select(resumed, [Move(0), Move(1)]).CombatCardIndex == first.CombatCardIndex,
                    "An interrupted canonical continuation remains pending");
                bool rejected = false;
                try { new LocalSearchTree(2, order: order).Complete(resumed, Result([]), 100, true); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "A different worker's trial cannot supply outcomes");
            }
        });

        test("discrepancy rejects a changed legal set without claiming exhaustive completion", () =>
        {
            var tree = new LocalSearchTree(1, order: LocalSearchOrder.DepthDiscrepancy);
            var trial = tree.Begin(); var first = tree.Select(trial, [Move(0), Move(1)]);
            tree.Complete(trial, Result([first]), 100, true);
            bool rejected = false;
            try { tree.Select(tree.Begin(), [Move(0), Move(2)]); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && !tree.Exhausted, "Changed native legality must not yield a completion proof");
        });

        test("discrepancy keeps potions optional and ranks postcombat net loss before their cost", () =>
        {
            foreach (var order in Orders)
            {
                var tree = new LocalSearchTree(1, order: order);
                LocalCandidate? best = null;
                for (int i = 0; i < 2; i++)
                {
                    var trial = tree.Begin();
                    var action = tree.Select(trial, [Move(0), Move(1, preference: 10) with { PotionSlot = 0 }]);
                    var candidate = Result([action], action.PotionSlot.HasValue ? 95 : 80) with { HpLost = 30 };
                    if (LocalSearchPolicy.Better(candidate, best)) best = candidate;
                    tree.Complete(trial, candidate, 100, true);
                }
                Check(best?.NetHpLoss == 0 && best.Actions[0].PotionSlot == null, "No potion is required for an equal no-loss victory");
            }
        });

        test("discrepancy request opt-in preserves the existing default and native budgets", () =>
        {
            var request = new LocalSearchRequest("job", "snapshot", [], "hash", 0, [], false);
            var copy = JsonSerializer.Deserialize<LocalSearchRequest>(JsonSerializer.Serialize(request with { SearchOrder = LocalSearchOrder.DepthDiscrepancy }))!;
            Check(request.SearchOrder == LocalSearchOrder.MonteCarlo && copy.SearchOrder == LocalSearchOrder.DepthDiscrepancy,
                "The experiment must not silently replace the installed search");
            Check(copy.BudgetSeconds == 60 && copy.MaxNodes == 64 && copy.MaxRounds == 64 && copy.MaxDepth == 24,
                "Ordering experiments must not fake speed by reducing the simulation budget");
        });

        test("discrepancy parallel ownership covers later forks when first moves are scarce", () =>
        {
            foreach (int workers in new[] { 1, 2, 4, 7 })
            {
                var histories = new HashSet<string>();
                for (int worker = 0; worker < workers; worker++)
                {
                    var tree = new LocalSearchTree(worker, order: LocalSearchOrder.DiscrepancyPortfolio);
                    while (!tree.Exhausted)
                    {
                        var trial = tree.Begin(); var partition = new LocalBranchPartition(worker, workers);
                        var path = new List<int>(); var actions = new List<LocalAction>();
                        foreach (int width in new[] { 2, 1, 3, 4 })
                        {
                            var legal = Enumerable.Range(0, width).Select(i => Move(i, path.Count.ToString(), preference: i * worker)).Reverse().ToArray();
                            var action = tree.Select(trial, partition.Assign(legal));
                            path.Add(action.HandIndex); actions.Add(action);
                        }
                        Check(histories.Add(string.Join(',', path)), "Different workers evaluated the same branching leaf");
                        tree.Complete(trial, Result(actions.ToArray()), 100, true);
                    }
                }
                Check(histories.Count == 24, "Worker groups omitted a later native action or selection fork");
            }
        });

        test("discrepancy parallel ownership ignores changing previews display names and native hand positions", () =>
        {
            var legal = Enumerable.Range(0, 5).Select(i => Move(i)).ToArray();
            var altered = Enumerable.Reverse(legal).Select(a => a with { Preference = 500 - a.HandIndex, CardName = "new name", HandIndex = 10 }).ToArray();
            for (int worker = 0; worker < 4; worker++)
            {
                var first = new LocalBranchPartition(worker, 4).Assign(legal).Select(a => a.CombatCardIndex);
                var second = new LocalBranchPartition(worker, 4).Assign(altered).Select(a => a.CombatCardIndex);
                Check(first.SequenceEqual(second), "Worker ownership changed with a nonidentity search hint");
            }
        });

        test("discrepancy scheduler comparison exposes early and late heuristic mistakes", () =>
        {
            var timer = Stopwatch.StartNew();
            var measurements = new List<object>();
            foreach (string problem in new[] { "three-early-mistakes", "one-late-mistake" })
            foreach (var order in Enum.GetValues<LocalSearchOrder>().Where(o => o != LocalSearchOrder.TurnFrontier))
            {
                var found = new List<int>();
                for (int seed = 0; seed < 30; seed++)
                {
                    var tree = new LocalSearchTree(seed, order: order);
                    for (int route = 1; route <= 64; route++)
                    {
                        var trial = tree.Begin(); var actions = new List<LocalAction>(); var path = new List<int>();
                        for (int step = 0; step < 24; step++)
                        {
                            var action = tree.Select(trial, [Move(0, preference: 10), Move(1)], greedy: (route - 1) % 4 != 3);
                            path.Add(action.HandIndex); actions.Add(action);
                        }
                        bool goal = problem == "three-early-mistakes" ? path.Take(3).All(x => x == 1) && path.Skip(3).All(x => x == 0) :
                            path.Select((x, i) => x == (i == 20 ? 1 : 0)).All(x => x);
                        var candidate = Result(actions.ToArray(), goal ? 80 : 60);
                        tree.Complete(trial, candidate, 100, true);
                        if (goal) { found.Add(route); break; }
                    }
                }
                found.Sort();
                if (order == LocalSearchOrder.DepthDiscrepancy && problem == "three-early-mistakes")
                    Check(found.Count == 30 && found.Max() <= 8, "DDS must reach the combined early corrections");
                if (order == LocalSearchOrder.LimitedDiscrepancy && problem == "one-late-mistake")
                    Check(found.Count == 30 && found.Max() <= 24, "LDS must also reach a late correction");
                if (order == LocalSearchOrder.DiscrepancyPortfolio)
                    Check(found.Count == 30 && found.Max() <= 48, "The portfolio must retain both useful exploration orders");
                measurements.Add(new { problem, order = order.ToString(), seeds = 30, limit = 64, solved = found.Count,
                    medianTrialsAmongSolved = found.Count == 0 ? (int?)null : found[found.Count / 2],
                    maxTrialsAmongSolved = found.Count == 0 ? (int?)null : found.Max() });
            }
            Console.WriteLine("MEASURE " + JsonSerializer.Serialize(new { nativeGame = false, elapsedMs = timer.ElapsedMilliseconds,
                baseline = "existing tree selection with its 3-of-4 coherent rollout schedule; no shared queue or refiner", measurements }));
        });
    }
}
