using System.Text.Json;
using SpireAiCoach.Core;

static class HealthTargetTests
{
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    static LocalSearchRequest Request() => new("health-goal", "snapshot", [], "root", 1, ["sts2:fixture", "mod:dynamic"], false);
    static LocalCandidate Win(int hp = 52, int start = 52) => new(
        [new(0, "fixture", null, "card", "", "root")], hp, 3, 0, 0, 66, true, false, false, StartingHp: start);
    static LocalHealthTarget Target(LocalSearchRequest r, int hp = 52, int start = 52, bool full = false) =>
        new(LocalMinimumLossProof.Scope(r), start, hp, full, Uncertain: true);

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("content health target returns zero or fixed gain without a ceiling certificate", () =>
        {
            foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            foreach (bool skip in new[] { false, true })
            {
                var r = Request() with { SearchOrder = order, SkipFinalVerification = skip };
                Check(LocalSearchPolicy.CanStopAfterVictory(Win(), r, healthTarget: Target(r)), "Zero target did not return");
                var gain = Target(r, 39, 38);
                Check(!LocalSearchPolicy.CanStopAfterVictory(Win(38, 38), r, healthTarget: gain), "Terminal gain was ignored");
                Check(LocalSearchPolicy.CanStopAfterVictory(Win(39, 38) with { MaxHp = 67 }, r, healthTarget: gain), "Fixed gain did not return");
            }
        });
        test("content health target requires settled living victory at the same native root", () =>
        {
            var r = Request(); var goal = Target(r); var win = Win();
            foreach (var bad in new[] { win with { Won = false }, win with { Dead = true }, win with { Hp = 0 },
                win with { Hp = 51 }, win with { Hp = 67 }, win with { MaxHp = 0 }, win with { Actions = [] },
                win with { StartingHp = null }, win with { StartingHp = 51 },
                win with { Actions = [win.Actions[0] with { BeforeHash = "stale" }] } })
                Check(!LocalSearchPolicy.CanStopAtHealthTarget(bad, r, goal), "Invalid victory qualified");
            foreach (var changed in new[] { r with { Id = "old" }, r with { SnapshotId = "old" }, r with { NativeHash = "old" },
                r with { ModelHash = 2 }, r with { LoadedMods = ["sts2:other"] }, r with { IncludePotions = true } })
                Check(!LocalSearchPolicy.CanStopAtHealthTarget(win, changed, goal), "Stale content goal qualified");
        });
        test("detected dynamic recovery uses the actual final full-health cap", () =>
        {
            var r = Request(); var goal = Target(r, full: true);
            Check(!LocalSearchPolicy.CanStopAtHealthTarget(Win(), r, goal), "Detected recovery stopped at starting HP");
            Check(!LocalSearchPolicy.CanStopAtHealthTarget(Win(66) with { MaxHp = 67 }, r, goal), "Cap growth was ignored");
            Check(LocalSearchPolicy.CanStopAtHealthTarget(Win(67) with { MaxHp = 67 }, r, goal), "Full health did not qualify");
        });
        test("content health target preserves explicit card round potion and damage goals", () =>
        {
            var r = Request();
            foreach (var configured in new[] { r with { StopOnZeroLoss = false }, r with { StopOnFirstWin = true },
                r with { VerifyCandidate = Win() }, r with { TargetVictoryRounds = 2 }, r with { TargetPotionUses = 0 },
                r with { RequireKnownZeroEnemyDamage = true }, r with { CardGoals = new(FinisherModelId: "fixture") } })
                Check(!LocalSearchPolicy.CanStopAtHealthTarget(Win(), configured, Target(configured)), "Another goal was bypassed");
        });
        test("content health target is not an optimality or recovery pruning certificate", () =>
        {
            var r = Request(); var goal = Target(r); var win = Win();
            var result = new LocalSearchResult(r.Id, r.SnapshotId, "done", "", 1, 0, 1, win,
                MinimumLoss: new(Certificate: new(goal.Scope, 52, 0, 0, 52), Confirmed: true),
                HealthTarget: goal, StoppedOnHealthTarget: true);
            Check(!LocalSearchPolicy.HasMinimumProof(result), "Goal became proof of best HP");
            Check(!LocalHealthBound.CannotImprove(new("root", 52, 49, 0, null), LocalWinningBound.From("root", win)),
                "Unknown recovery was pruned after the target");
            foreach (var text in new[] { LocalSearchPolicy.Format(result), LocalSearchPolicy.FormatAdvice(result) })
                Check(text.Contains("生命目标") && text.Contains("尚未证明全局最优") && !text.Contains("不满足满血提前返回"),
                    "Achieved goal was described as optimal or incomplete");
        });
        test("legacy result IPC has no inferred content goal", () =>
        {
            var r = Request();
            var result = JsonSerializer.Deserialize<LocalSearchResult>(JsonSerializer.Serialize(
                new LocalSearchResult(r.Id, r.SnapshotId, "done", "", 1, 0, 1, Win())))!;
            Check(result.HealthTarget == null && !result.StoppedOnHealthTarget &&
                !LocalSearchPolicy.CanStopAfterVictory(result.Best, r), "Missing legacy goal silently became zero");
        });
        asyncTest("all search workers reuse one current-content calculation and invalidate another root", async () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "spire-health-target-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var r = Request(); int reads = 0;
                var targets = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                    LocalHealthTargetCache.Get(directory, r, 52, () => { Interlocked.Increment(ref reads); return Target(r); }))));
                Check(reads == 1 && targets.All(t => t == Target(r)), "Content was scanned again by another worker");
                var changed = r with { Id = "next" };
                var fresh = LocalHealthTargetCache.Get(directory, changed, 38, () => { reads++; return Target(changed, 39, 38); });
                Check(reads == 2 && fresh?.TargetHp == 39, "A new root reused the old goal");
            }
            finally { Directory.Delete(directory, true); }
        });
    }
}
