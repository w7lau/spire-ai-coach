using System.Text.Json;
using SpireAiCoach.Core;

static class CombatOutcomeTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static LocalAction Move(int i) => new(i, "card:" + i, null, "", "", "root", 1, CombatCardIndex: (uint)i);
    static LocalCandidate Candidate(LocalCombatOutcome outcome, int hp = 50) =>
        new([Move(1)], hp, 50 - hp, 0, 0, 50, outcome.Won(false), false, false,
            StartingHp: 50, CombatOutcome: outcome);

    public static void Register(Action<string, Action> test)
    {
        test("escaped enemies cannot supply victory stops bounds or manual winning return", () =>
        {
            var escaped = new LocalCombatOutcome(true, true, [new(7, "opaque:enemy", 25)]);
            var candidate = Candidate(escaped);
            var request = new LocalSearchRequest("request", "snapshot", [], "root", 1, [], true,
                StopOnFirstWin: true);
            Check(!candidate.Won && !LocalSearchPolicy.FullHealthVictory(candidate) &&
                !LocalSearchPolicy.CanStop(candidate, request) &&
                !LocalSearchPolicy.CanStop(candidate, request with { StopOnFirstWin = false }) &&
                !LocalSearchPolicy.WinningRouteFrom(candidate, request), "Escape was promoted to a winning route");
            var manual = new LocalVictoryReturn(request);
            manual.Observe(new(request.Id, request.SnapshotId, "running", "", 1, 0, 1, candidate));
            Check(!manual.CanRequest && !manual.TryRequest(), "Manual winning-route return accepted an escape");
            var win = Candidate(new(true, true, []), 40);
            Check(LocalSearchPolicy.Better(win, candidate) && !LocalSearchPolicy.Better(candidate, win),
                "Zero-cost escape outranked defeating the enemies");
            Check(!new LocalCombatOutcome(false, true, []).Won(false) && !new LocalCombatOutcome(true, true, []).Won(true),
                "Unsettled victory or death became a win");
        });
        test("escaped terminal evidence and advice distinguish settlement from unfinished combat", () =>
        {
            var candidate = Candidate(new(true, true, [new(7, "opaque:enemy", 25)]));
            var request = new LocalSearchRequest("request", "snapshot", [], "root", 1, [], true, Partitions: 1);
            var audit = new LocalSearchAudit(); audit.Outcome(candidate, true);
            var evidence = audit.Snapshot(request, candidate, false, 1, 1, false, false, 0);
            var result = new LocalSearchResult("request", "snapshot", "done", "", 1, 0, 1, candidate, Evidence: evidence);
            var merged = LocalSearchEvidence.Merge(request, result, [result, result], 1);
            Check(evidence.TerminalWins == 0 && evidence.TerminalEscapes == 1 && evidence.UnconfirmedEnds == 0 &&
                merged.TerminalEscapes == 2 && merged.Conclusion == "unknown", "Escape accounting lost its actual terminal outcome");
            var advice = LocalSearchPolicy.FormatAdvice(result); var details = LocalSearchPolicy.Format(result);
            Check(advice.Contains("敌人逃跑") && advice.Contains("战斗已结束") && !advice.Contains("预计获胜") &&
                details.Contains("不计为获胜路线") && !details.Contains("模拟结果：战斗获胜"), "Escape advice claimed a victory or unfinished combat");
        });
        test("escaped outcome replay matches native identities and HP through local wire serialization", () =>
        {
            var outcome = new LocalCombatOutcome(true, true, [new(7, "same:model", 25), new(8, "same:model", 25)]);
            var roundTrip = JsonSerializer.Deserialize<LocalCandidate>(JsonSerializer.Serialize(Candidate(outcome)))!;
            Check(!roundTrip.Won && outcome.Matches(roundTrip.CombatOutcome), "Local result serialization lost native escape evidence");
            Check(!outcome.Matches(new(true, true, [new(7, "same:model", 25)])) &&
                !outcome.Matches(new(true, true, [new(7, "same:model", 25), new(9, "same:model", 25)])) &&
                !outcome.Matches(new(true, true, [new(7, "same:model", 25), new(8, "same:model", 24)])),
                "Independent replay accepted a different escaped creature or settlement");
        });
        test("loss proof treats escape as a terminal without creating a winning health target", () =>
        {
            var request = new LocalSearchRequest("proof", "snapshot", [], "root", 1, [], true, Partitions: 1);
            var proof = new LocalMinimumLossProof(request); var legal = new[] { Move(1), Move(2) };
            proof.Observe(new(50, [new(legal[0], legal, [])], 50, Escaped: true));
            Check(proof.Status.Target == null, "Escape created a zero-loss winning target");
            proof.Observe(new(50, [new(legal[1], legal, [])], 40, Won: true));
            Check(proof.Status.InvalidReason == "" && proof.Status.Target?.FinalHp == 40 &&
                proof.Status.Certificate?.MaximumFinalHp == 40, "Escape prevented the actual victorious branch from establishing its bound");
        });
    }
}
