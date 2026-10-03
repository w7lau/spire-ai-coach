using System.Text.Json;
using SpireAiCoach.Core;

static class RouteCoverageTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static LocalAction Move(int i, string before = "root") =>
        new(i, "opaque", null, "display", "", before, 1, CombatCardIndex: (uint)i);

    public static void Register(Action<string, Action> test)
    {
        test("local algorithm buttons share native limits and preserve frozen execution controls", () =>
        {
            var captured = new LocalSearchRequest("id", "snapshot", [1, 2], "native", 17, ["mod"], true,
                Workers: 4, InitialPlan: [Move(1)], StopOnZeroLoss: false, TrimWorkerOverhead: true);
            var old = LocalCalculation.Configure(captured, LocalSearchOrder.MonteCarlo, 4, true, false);
            var turn = LocalCalculation.Configure(captured, LocalSearchOrder.TurnFrontier, 4, true, false);
            foreach (var request in new[] { old, turn })
            {
                Check(request.MaxNodes == 64 && request.BudgetSeconds == 60 && request.MaxRounds == 64, "Button budgets diverged");
                Check(request.Replay == captured.Replay && request.NativeHash == "native" && request.ModelHash == 17 &&
                    request.LoadedMods == captured.LoadedMods && request.NumericalExecution && request.DataOnlyCombat &&
                    request.DataOnlyRun && request.TrimWorkerOverhead && request.IncludePotions && !request.StopOnZeroLoss &&
                    request.Workers == 4 && request.ContinueOptimization, "Changing order altered native capture/execution controls");
            }
            Check(old.InitialPlan == captured.InitialPlan && old.ShareSearchWork && turn.InitialPlan == null && !turn.ShareSearchWork,
                "An old seed/proposal scheduler overrode turn ordering");
            Check(LocalCalculation.Configure(captured, LocalSearchOrder.TurnFrontier, 0, false, true).StopOnZeroLoss,
                "Early return must apply to both buttons");
            var explicitLegacy = JsonSerializer.Deserialize<LocalSearchRequest>(JsonSerializer.Serialize(captured with { MaxNodes = 32 }))!;
            Check(explicitLegacy.MaxNodes == 32, "Explicit historical fixture limits must remain readable");
        });

        test("local algorithm completed prefix is skipped before restoring it", () =>
        {
            var coverage = new LocalRouteCoverage();
            var played = Move(0);
            var other = Move(1);
            var trial = coverage.Begin();
            coverage.Open(trial, [played, other]); coverage.Follow(trial, played); coverage.Complete(trial, true);
            Check(coverage.IsClosedPrefix([played]) && coverage.IsClosedPrefix([played, Move(2, "later")]), "Complete leaf was offered again");
            Check(!coverage.IsClosedPrefix([other]) && !coverage.Exhausted, "An unrelated continuation was discarded");
            Check(coverage.Open(coverage.Begin(), [played, other]).SequenceEqual([other]), "Closed branch was executable again");
        });

        test("local algorithm probes and interrupted trials cannot close a continuation", () =>
        {
            var coverage = new LocalRouteCoverage();
            var action = Move(1);
            for (int i = 0; i < 3; i++)
            {
                var trial = coverage.Begin();
                Check(coverage.Open(trial, [action]).Length == 1, "A partial observation closed the only action");
                coverage.Follow(trial, action); coverage.Complete(trial, false);
                Check(!coverage.IsClosedPrefix([action]) && coverage.Completed == 0, "A budget cutoff proved terminal coverage");
            }
        });

        test("local algorithm choice identity retains combinations skip kind targets and instances", () =>
        {
            var coverage = new LocalRouteCoverage();
            var action = Move(1);
            var choice = new LocalCardChoice("offer", 0, "model", "same", [1, 2], "select");
            var choices = new[] { choice, choice with { Indices = [1, 3] }, choice with { Kind = "skip" }, choice with { OfferHash = "other" } };
            var trial = coverage.Begin(); coverage.Open(trial, [action]); coverage.Follow(trial, action);
            coverage.Open(trial, choices.Select(c => LocalRouteCoverage.ChoiceAction(c, 1)).ToArray());
            coverage.Follow(trial, LocalRouteCoverage.ChoiceAction(choice, 1)); coverage.Complete(trial, true);
            Check(coverage.IsClosedPrefix([action with { Choices = [choice] }]), "Recorded selection was not closed");
            foreach (var other in choices.Skip(1))
                Check(!coverage.IsClosedPrefix([action with { Choices = [other] }]), "Distinct native choice was conflated");
            Check(!coverage.IsClosedPrefix([action]) && !coverage.IsClosedPrefix([action with { TargetId = 2 }]) &&
                !coverage.IsClosedPrefix([action with { CombatCardIndex = 2 }]), "Untried action was conflated");
        });

        test("local algorithm display changes do not repeat a completed native instance", () =>
        {
            var coverage = new LocalRouteCoverage();
            var original = Move(1);
            var trial = coverage.Begin(); coverage.Open(trial, [original]); coverage.Follow(trial, original); coverage.Complete(trial, true);
            Check(coverage.IsClosedPrefix([original with { HandIndex = 8, CardName = "renamed", Preference = 999 }]),
                "Display position changed native route identity");
            var changedState = new LocalRouteCoverage();
            trial = changedState.Begin(); changedState.Open(trial, [original, original with { BeforeHash = "another" }]);
            changedState.Follow(trial, original); changedState.Complete(trial, true);
            Check(!changedState.IsClosedPrefix([original with { BeforeHash = "another" }]), "Another actual state was merged");
        });

        test("local algorithm complete-route coverage matches a finite oracle without repeated terminals", () =>
        {
            // Independent enumeration supplies all 27 terminal action histories.
            var oracle = new HashSet<string>();
            for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++) for (int c = 0; c < 3; c++) oracle.Add($"{a},{b},{c}");
            var coverage = new LocalRouteCoverage();
            var seen = new HashSet<string>();
            var random = new Random(1729);
            while (!coverage.Exhausted)
            {
                var trial = coverage.Begin();
                var path = new List<int>();
                for (int depth = 0; depth < 3; depth++)
                {
                    var legal = Enumerable.Range(0, 3).Select(i => Move(i, string.Join(',', path))).ToArray();
                    var open = coverage.Open(trial, legal);
                    Check(open.Length > 0, "Closed subtree remained selectable");
                    var action = open[random.Next(open.Length)];
                    coverage.Follow(trial, action); path.Add(action.HandIndex);
                }
                Check(seen.Add(string.Join(',', path)), "A completed history was simulated twice");
                coverage.Complete(trial, true);
                Check(seen.Count <= 27, "Finite search failed to close");
            }
            Check(seen.SetEquals(oracle) && coverage.Completed == 27 && coverage.Avoided > 0, "Coverage lost a terminal alternative");
        });

        test("local algorithm workers partition later choice forks without cross-worker terminal duplication", () =>
        {
            var seen = new HashSet<string>();
            for (int worker = 0; worker < 4; worker++)
            {
                var coverage = new LocalRouteCoverage();
                while (!coverage.Exhausted)
                {
                    var trial = coverage.Begin();
                    var partition = new LocalBranchPartition(worker, 4);
                    var path = new List<int>();
                    for (int depth = 0; depth < 4; depth++)
                    {
                        var all = Enumerable.Range(0, 2).Select(i => Move(i, string.Join(',', path))).ToArray();
                        var open = coverage.Open(trial, partition.Assign(all));
                        Check(open.Length > 0, "Owned subtree closure diverged");
                        var action = open[0]; coverage.Follow(trial, action); path.Add(action.HandIndex);
                    }
                    Check(seen.Add(string.Join(',', path)), "Workers evaluated the same complete history");
                    coverage.Complete(trial, true);
                }
            }
            Check(seen.Count == 16, "Worker partition omitted legal histories");
        });
        test("adaptive concurrency smaller root partition preserves every later legal history", () =>
        {
            foreach (int rootBranches in new[] { 1, 2, 3 })
            {
                int workers = LocalConcurrency.Partitions(8, rootBranches, true);
                var seen = new HashSet<string>();
                for (int worker = 0; worker < workers; worker++)
                {
                    var coverage = new LocalRouteCoverage();
                    while (!coverage.Exhausted)
                    {
                        var trial = coverage.Begin();
                        var partition = new LocalBranchPartition(worker, workers);
                        var path = new List<int>();
                        for (int depth = 0; depth < 4; depth++)
                        {
                            var all = Enumerable.Range(0, depth == 0 ? rootBranches : 2)
                                .Select(i => Move(i, string.Join(',', path))).ToArray();
                            var open = coverage.Open(trial, partition.Assign(all));
                            Check(open.Length > 0, "Adaptive partition lost an open continuation");
                            coverage.Follow(trial, open[0]); path.Add(open[0].HandIndex);
                        }
                        Check(seen.Add(string.Join(',', path)), "Adaptive workers repeated a terminal history");
                        coverage.Complete(trial, true);
                    }
                }
                var oracle = new HashSet<string>();
                for (int a = 0; a < rootBranches; a++) for (int b = 0; b < 2; b++)
                    for (int c = 0; c < 2; c++) for (int d = 0; d < 2; d++) oracle.Add($"{a},{b},{c},{d}");
                Check(seen.SetEquals(oracle), "Reducing worker count omitted a later fork");
            }
        });
    }
}
