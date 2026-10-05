using SpireAiCoach.Core;

internal static class ManualVictoryReturnTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Register(Action<string, Action> test)
    {
        var request = new LocalSearchRequest("manual", "snapshot", [], "root", 1, [], false,
            StopOnZeroLoss: false, StopOnFirstWin: false, CardGoals: new("play", "finish", 5));
        var win = new LocalCandidate([new(0, "potion", null, "potion", "", "root", PotionSlot: 0)],
            8, 42, 0, 0, 50, true, false, false, StartingHp: 50);
        var result = new LocalSearchResult(request.Id, request.SnapshotId, "running", "", 1, 0, 100, win);
        test("manual return requires a current living victory and closes before verification", () =>
        {
            var control = new LocalVictoryReturn(request);
            Check(!control.CanRequest && !control.TryRequest(), "An empty search was manually adopted");
            foreach (var bad in new[] { result with { Id = "old" }, result with { SnapshotId = "old" },
                result with { Status = "failed" }, result with { Best = win with { Won = false } },
                result with { Best = win with { Dead = true } }, result with { Best = win with { Hp = 0 } },
                result with { Best = win with { Actions = [] } },
                result with { Best = win with { Actions = [win.Actions[0] with { BeforeHash = "old" }] } } })
                control.Observe(bad);
            Check(!control.CanRequest && !control.TryRequest(), "Stale, partial or invalid outcomes enabled return");
            control.Observe(result);
            Check(control.CanRequest && control.TryRequest() && control.Requested && !control.TryRequest(),
                "Costly win with unfinished card goals could not be adopted once");
            Check(control.Matches(request) && !control.Matches(request with { Id = "old" }) &&
                !control.MatchesRoot(request.SnapshotId, "old"), "Manual choice escaped its frozen root");
            var closed = new LocalVictoryReturn(request); closed.Observe(result); closed.CloseSearch();
            Check(!closed.CanRequest && !closed.TryRequest(), "Manual stop interrupted verification");
        });
        test("manual stop reaches workers with auto stops off but never stops final verification", () =>
        {
            var stop = new LocalSearchStop(request.Id, request.SnapshotId, request.NativeHash, UseWinningRoute: true);
            Check(stop.Matches(request) && !stop.Cancel && !stop.Matches(request with { VerifyCandidate = win }),
                "Manual adoption was confused with cancellation or automatic goal stops");
            Check(!stop.Matches(request with { Id = "old" }) && !stop.Matches(request with { SnapshotId = "old" }) &&
                !stop.Matches(request with { NativeHash = "old" }), "Manual stop reached another request");
            Check((stop with { Cancel = true }).Matches(request with { VerifyCandidate = win }),
                "Cancellation can no longer interrupt verification");
        });
        test("manual victory evidence preserves wins without claiming a completed goal or optimum", () =>
        {
            var chosen = result with { Status = "done", StoppedEarly = true, StoppedOnManualVictory = true,
                MinimumLoss = new(Certificate: new("root", 50, 42, 1), Confirmed: true) };
            var evidence = LocalSearchEvidence.Merge(request, chosen, [chosen], 9);
            Check(evidence.Conclusion == "native-win" && evidence.ManualStopped && !evidence.GoalStopped &&
                !evidence.ExactRootCovered && evidence.StopReason.Contains("手动停止"), "Manual return made a coverage or goal claim");
            Check(!LocalSearchPolicy.HasMinimumProof(chosen with { CardGoals = null }), "Manual stop advertised an optimum");
            Check(LocalSearchPolicy.FormatAdvice(chosen).Contains("手动停止") &&
                LocalSearchPolicy.Format(chosen).Contains("尚未证明最优"), "Advice lost the manual return reason");
        });
    }
}
