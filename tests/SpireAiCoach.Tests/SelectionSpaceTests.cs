using System.Numerics;
using SpireAiCoach.Core;

internal static class SelectionSpaceTests
{
    public static void Register(Action<string, Action> test)
    {
        void Check(bool value) { if (!value) throw new Exception("Selection domain regression"); }
        test("native selection domain contains every ordered subset and stable inverse ranks", () =>
        {
            // Independent tiny exhaustive oracle; includes zero, partial and full selection.
            IEnumerable<int[]> Oracle(int[] prefix, int n)
            {
                yield return prefix;
                foreach (int i in Enumerable.Range(0, n).Except(prefix))
                    foreach (var next in Oracle(prefix.Append(i).ToArray(), n)) yield return next;
            }
            for (int n = 0; n <= 5; n++)
            {
                var expected = Oracle([], n).Select(a => string.Join(",", a)).ToHashSet();
                var space = new LocalSelectionSpace(n, 0, n);
                var actual = new HashSet<string>();
                for (int rank = 0; rank < space.Count; rank++)
                {
                    var indices = space.At(rank);
                    Check(space.Rank(indices) == rank); Check(actual.Add(string.Join(",", indices)));
                }
                Check(actual.SetEquals(expected));
            }
        });
        test("large offers page past 64 choices without allocating the factorial domain", () =>
        {
            var space = new LocalSelectionSpace(10, 0, 10);
            Check(space.Count == 9864101);
            var cursor = new LocalSelectionCursor();
            var first = cursor.Page("same-offer", space);
            var second = cursor.Page("same-offer", space);
            Check(first.Length == 128 && second.Length == 128 && !first.Intersect(second).Any());
            Check(first.Select(r => space.At(r).Length).Distinct().Count() == 11);
            var huge = new LocalSelectionSpace(32, 20, 20);
            Check(huge.Count > long.MaxValue);
            foreach (var rank in new[] { BigInteger.Zero, huge.Count / 2, huge.Count - 1 })
                Check(huge.Rank(huge.At(rank)) == rank);
        });
        test("unordered and constrained native domains retain only reachable counts", () =>
        {
            var space = new LocalSelectionSpace(6, 0, 6, false);
            Check(space.Count == 64);
            for (int i = 0; i < 64; i++) Check(space.Rank(space.At(i)) == i);
            var grid = new LocalSelectionSpace(6, 0, 2, sizes: [0, 2]);
            Check(grid.Count == 31);
            Check(Enumerable.Range(0, 31).All(i => grid.At(i).Length is 0 or 2));
        });
        test("search and coverage never close unenumerated native choice pages", () =>
        {
            var a = new LocalAction(0, "choice:a", null, "a", "", "offer");
            var b = new LocalAction(1, "choice:b", null, "b", "", "offer");
            var outcome = new LocalCandidate([], 10, 0, 0, 0, 10, true, false, true);
            foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.LimitedDiscrepancy })
            {
                var tree = new LocalSearchTree(1, order: order);
                var one = tree.Begin(); Check(tree.TrySelect(one, [a], out _, completeLegal: false));
                tree.Complete(one, outcome, 0, closeExactPrefix: true); Check(!tree.Exhausted);
                var two = tree.Begin(); Check(tree.TrySelect(two, [b], out _, completeLegal: false));
                tree.Complete(two, outcome, 0, closeExactPrefix: true); Check(!tree.Exhausted);
                var final = tree.Begin(); Check(!tree.TrySelect(final, [a, b], out _, completeLegal: true));
            }
            var coverage = new LocalRouteCoverage();
            foreach (var action in new[] { a, b })
            {
                var trial = coverage.Begin(); Check(coverage.Open(trial, [action], false).Length == 1);
                coverage.Follow(trial, action); coverage.Complete(trial, true); Check(!coverage.Exhausted);
            }
            Check(coverage.Open(coverage.Begin(), [a, b], false).Length == 0);
        });
    }
}
