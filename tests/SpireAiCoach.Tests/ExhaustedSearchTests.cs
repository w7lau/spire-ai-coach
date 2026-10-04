using SpireAiCoach.Core;

static class ExhaustedSearchTests
{
    private static readonly LocalSearchOrder[] Orders = [LocalSearchOrder.MonteCarlo,
        LocalSearchOrder.LimitedDiscrepancy, LocalSearchOrder.DepthDiscrepancy, LocalSearchOrder.DiscrepancyPortfolio];
    private static LocalAction Move(int index, string before = "root") =>
        new(index, "opaque-model", null, "", "", before, CombatCardIndex: (uint)index + 1);
    private static LocalCandidate Result(LocalAction[] actions, int hp = 80) =>
        new(actions, hp, 80 - hp, 0, 0, 80, true, false, false, StartingHp: 80);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Invalid native input was classified as normal exhaustion");
    }

    public static void Register(Action<string, Action> test)
    {
        test("exhausted proposals do not discard measured outcomes or open siblings", () =>
        {
            foreach (var order in Orders)
            {
                var tree = new LocalSearchTree(1, order: order);
                var a = Move(0); var b = Move(1);
                var choices = new[] { Move(0, "offer"), Move(1, "offer") };
                LocalCandidate? best = null;
                for (int i = 0; i < 2; i++)
                {
                    var trial = tree.Begin();
                    var action = tree.Select(trial, [a, b], a);
                    var selected = tree.Select(trial, choices);
                    var outcome = Result([action, selected], 79 + selected.HandIndex);
                    if (LocalSearchPolicy.Better(outcome, best)) best = outcome;
                    tree.Complete(trial, outcome, 100, true);
                }
                var retained = best;
                Check(!tree.TrySelect(tree.Begin(), [a, b], out var skipped, a) && skipped == null,
                    $"{order}: a completed proposal must finish normally");
                Check(!tree.Exhausted && tree.CompletedTrials == 2 && ReferenceEquals(best, retained),
                    "Skipping a proposal must not reward it, erase the best outcome or close another root");
                var other = tree.Begin();
                Check(tree.TrySelect(other, [a, b], out var pending) && pending == b,
                    "The remaining native sibling must still run");
                tree.Complete(other, Result([pending!], 77), 100, true);
                Check(tree.Exhausted && tree.CompletedTrials == 3 && best?.Hp == 80,
                    "Completion must preserve the earlier winning result");
                Check(!tree.TrySelect(tree.Begin(), [a, b], out _) && tree.CompletedTrials == 3,
                    "An already exhausted root must not become an extra trial or a runtime failure");
            }
        });

        test("exhausted native choice proposals keep other selection continuations open", () =>
        {
            foreach (var order in Orders)
            {
                var tree = new LocalSearchTree(2, order: order);
                var card = Move(0);
                var choices = new[] { Move(0, "offer"), Move(1, "offer") };
                var first = tree.Begin();
                tree.Select(first, [card]); tree.Select(first, choices, choices[0]);
                tree.Complete(first, Result([card, choices[0]]), 100, true);
                var repeated = tree.Begin(); tree.Select(repeated, [card]);
                Check(!tree.TrySelect(repeated, choices, out _, choices[0]), "Closed choice must finish the redundant proposal");
                Check(!tree.Exhausted && tree.CompletedTrials == 1, "Another native choice must not be removed or fabricated");
                var pending = tree.Begin(); tree.Select(pending, [card]);
                Check(tree.TrySelect(pending, choices, out var selected) && selected == choices[1], "Remaining choice was lost");
                tree.Complete(pending, Result([card, selected!]), 100, true);
                Check(tree.Exhausted && tree.CompletedTrials == 2, "Both native outcomes must close the parent card");
            }
        });

        test("exhaustion handling does not convert interruptions or invalid native data to completion", () =>
        {
            foreach (var order in Orders)
            {
                var tree = new LocalSearchTree(3, order: order);
                var a = Move(0);
                var trial = tree.Begin(); tree.Select(trial, [a]);
                tree.Complete(trial, Result([a]), 100, false);
                Check(!tree.Exhausted && tree.TrySelect(tree.Begin(), [a], out _), "An interruption cannot exhaust its continuation");
                Reject(() => tree.TrySelect(tree.Begin(), [], out _));
                Reject(() => tree.TrySelect(tree.Begin(), [a], out _, Move(9)));
                Reject(() => tree.TrySelect(new LocalSearchTree(3, order: order).Begin(), [a], out _));
                Check(!tree.Exhausted && tree.CompletedTrials == 1, "Invalid input must not supply coverage or an outcome");
            }
        });

        test("exhaustion skips preserve independent finite-tree coverage and optimum", () =>
        {
            foreach (var order in Orders)
            {
                var tree = new LocalSearchTree(7, order: order);
                var seen = new HashSet<string>();
                LocalCandidate? best = null;
                while (!tree.Exhausted)
                {
                    var trial = tree.Begin(); var actions = new List<LocalAction>();
                    for (int depth = 0; depth < 3; depth++)
                    {
                        var key = string.Join(',', actions.Select(a => a.HandIndex));
                        var legal = new[] { Move(0, key), Move(1, key) };
                        Check(tree.TrySelect(trial, legal, out var next), "Open native history was skipped");
                        actions.Add(next!);
                    }
                    var history = string.Join(',', actions.Select(a => a.HandIndex));
                    Check(seen.Add(history), "A completed native terminal was evaluated twice");
                    int score = actions.Select(a => a.HandIndex).Aggregate(0, (n, i) => n * 2 + i);
                    var outcome = Result(actions.ToArray(), 73 + score);
                    if (LocalSearchPolicy.Better(outcome, best)) best = outcome;
                    tree.Complete(trial, outcome, 100, true);
                }
                var oracle = Enumerable.Range(0, 8).Select(n => string.Join(',', new[] { n >> 2, n >> 1 & 1, n & 1 }));
                Check(seen.SetEquals(oracle) && best?.Hp == 80 && tree.CompletedTrials == 8,
                    "Normal completion handling changed exhaustive coverage or its best outcome");
            }
        });
    }
}
