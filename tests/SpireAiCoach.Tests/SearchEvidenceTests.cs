using System.Text.Json;
using SpireAiCoach.Core;

static class SearchEvidenceTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static LocalSearchRequest Request() => new("id", "snapshot", [], "root", 1, [], true,
        Partitions: 1, MaxRounds: 20, SearchOrder: LocalSearchOrder.TurnFrontier);
    static LocalCandidate Loss() => new([], 0, 10, 20, 0, 10, false, true, false, StartingHp: 10);
    static LocalAction Move(int i, string hash = "root") => new(i, "opaque:" + i, null, "same name", "", hash, 1);

    public static void Register(Action<string, Action> test)
    {
        test("search evidence budget completion and empty discovered queues do not prove no win", () =>
        {
            var request = Request(); var audit = new LocalSearchAudit();
            for (int i = 0; i < 64; i++) audit.Outcome(Loss(), true);
            var evidence = audit.Snapshot(request, Loss(), false, 0, 64, false, false, 0);
            var result = new LocalSearchResult("id", "snapshot", "done", "", 64, 0, 1, Loss(), Evidence: evidence);
            var merged = LocalSearchEvidence.Merge(request, result, [result], 0);
            Check(merged.Conclusion == "unknown" && merged.AttemptLimitReached && merged.TerminalLosses == 64,
                "Completed budget was misclassified as an exhaustive impossibility proof");
            Check(LocalSearchPolicy.FormatAdvice(result).Contains("是否有解尚未确定"), "Advice hid unknown reachability");
        });

        test("search evidence tiny complete native history tree proves only its explicit scope", () =>
        {
            var request = Request(); var coverage = new LocalRouteCoverage(); var audit = new LocalSearchAudit();
            var legal = new[] { Move(0), Move(1) };
            foreach (var action in legal)
            {
                var trial = coverage.Begin(); coverage.Open(trial, legal); coverage.Follow(trial, action);
                coverage.Complete(trial, true); audit.Outcome(Loss(), true);
            }
            var evidence = audit.Snapshot(request, Loss(), coverage.Exhausted, 0, 2, false, false, 0);
            Check(evidence.Conclusion == "covered-no-win" && evidence.MaxRounds == 20 && !evidence.IncludePotions,
                "Full terminal tree lost its scoped coverage certificate");
            Check(evidence.Description.Contains("不含主动用药"), "Impossibility omitted the potion search scope");
            var partitioned = audit.Snapshot(request with { Partitions = 2 }, Loss(), true, 0, 2, false, false, 0);
            Check(partitioned.Conclusion == "unknown", "A partition was promoted to a complete root proof");
            Check(evidence.WithFailedPass(Loss(), 0).Conclusion == "unknown", "A recovered failed pass was hidden from the proof");
            var failed = new LocalSearchResult("id", "snapshot", "failed", "", 2, 0, 1, null, Evidence: evidence);
            Check(LocalSearchEvidence.Merge(request, failed, [failed], 0).Conclusion == "unknown",
                "Parent engine-error rejection retained the worker's impossibility claim");
        });

        test("search evidence limits exclusions failures and unexplored pages keep a loss unknown", () =>
        {
            foreach (int kind in Enumerable.Range(0, 7))
            {
                var request = Request(); var audit = new LocalSearchAudit();
                if (kind == 0) audit.RoundLimitHits++;
                if (kind == 1) audit.ActionLimitHits++;
                if (kind == 2) audit.TimeLimitHits++;
                if (kind == 3) audit.SimulationErrors++;
                if (kind == 4) request = request with { ExcludedModels = ["opaque-mod"] };
                if (kind == 5) audit.Outcome(Loss() with { Dead = false }, true);
                var evidence = audit.Snapshot(request, Loss(), true, 0, 2, false, false, kind == 6 ? 1 : 0);
                Check(evidence.Conclusion == "unknown" && !evidence.ExactRootCovered,
                    "Incomplete search condition gained a proof: " + kind);
            }
            var coverage = new LocalRouteCoverage(); var trial = coverage.Begin();
            coverage.Open(trial, [Move(0)], completeLegal: false); coverage.Follow(trial, Move(0)); coverage.Complete(trial, true);
            Check(!coverage.Exhausted, "A single selection page closed the unseen selection domain");
        });

        test("search evidence distinguishes first native victory from independent replay and rejects legacy proof", () =>
        {
            var request = Request(); var move = Move(0); var winner = Loss() with { Won = true, Dead = false, Hp = 10,
                Actions = [move], Continuation = [new(0, "root", new(0, "history"), 0)] };
            var audit = new LocalSearchAudit(); audit.Outcome(winner, true);
            var evidence = audit.Snapshot(request, winner, false, 10, 1, false, true, 0);
            var first = new LocalSearchResult("id", "snapshot", "done", "", 1, 0, 1, winner,
                VerificationSkipped: true, Evidence: evidence);
            Check(LocalSearchEvidence.Merge(request, first, [first], 10).Conclusion == "native-win", "Skipped replay gained verification");
            var verified = first with { VerificationSkipped = false, Timing = new(0, 0, 0, 1, Verifications: 1) };
            Check(LocalSearchEvidence.Merge(request, verified, [first], 10).Conclusion == "verified-win", "Verified native victory was lost");
            var missingPoints = verified with { Best = winner with { Continuation = null } };
            Check(LocalSearchEvidence.Merge(request, missingPoints, [first], 10).Conclusion == "native-win", "Missing execution points gained verification");
            var legacy = first with { Best = Loss(), Evidence = null };
            Check(LocalSearchEvidence.Merge(request, legacy, [legacy], 0).Conclusion == "unknown", "Legacy wire data gained a proof");
            var copy = JsonSerializer.Deserialize<LocalSearchResult>(JsonSerializer.Serialize(first))!;
            Check(copy.Evidence == first.Evidence, "Search evidence was lost on the IPC wire");
        });

        test("paged ancestor replay submits unseen native choices without reopening ordinary siblings", () =>
        {
            var space = new LocalSelectionSpace(5, 0, 5);
            LocalCardChoice Choice(int rank, string hash = "offer") => new(hash, rank, "opaque", "same name",
                space.At(rank), "hand", CompleteOffer: false);
            var actual = Move(0) with { Choices = [Choice(111)] }; var end = Move(1, "after");
            var line = new[] { actual, end };
            var owner = new LocalTurnTask(1, line, 1);
            var decision = new LocalDecision(0, [actual, Move(2)], [new(0, [Choice(111), Choice(200), Choice(290)])]);
            var hint = new LocalTurnHint(10, 10, 20, 20);
            Check(!LocalTurnSearch.Alternatives(line, decision, hint, owner).Any(), "Ordinary ancestor ownership changed");
            var offers = LocalTurnSearch.PagedReplayAlternatives(line, decision, hint).ToArray();
            Check(offers.Length == 2 && offers.All(o => o.Prefix.Length == 1 &&
                o.Prefix[0].BeforeHash == actual.BeforeHash && o.Prefix[0].ModelId == actual.ModelId &&
                o.Prefix[0].Choices!.Length == 1) && offers.Any(o => o.Prefix[0].Choices![0].Index == 290),
                "The later-page winning choice remained unreachable under a fixed ancestor");
            var complete = decision with { Choices = [new(0, [Choice(200) with { CompleteOffer = true }])] };
            Check(!LocalTurnSearch.PagedReplayAlternatives(line, complete, hint).Any(), "Complete ordinary offers were needlessly reopened");
            var frontier = new LocalTurnSearch(1);
            foreach (var offer in offers.Concat(offers)) frontier.Offer(offer.Prefix, offer.SearchRound, offer.Hint);
            Check(frontier.Count == 2 && frontier.DuplicateOffers == 2, "Repeated pages duplicated queued exact histories");
        });

        test("paged replay changes one exact selection and releases the later native choice tail", () =>
        {
            var prior = new LocalCardChoice("first", 0, "opaque-a", "same name", [0], "hand", CompleteOffer: false);
            var next = prior with { Index = 1, Indices = [1] };
            var tail = new LocalCardChoice("later-offer", 0, "opaque-b", "same name", [0], "pile");
            var action = Move(0) with { Choices = [prior, tail] };
            var decision = new LocalDecision(0, [action], [new(0, [prior, next]), new(1, [tail])]);
            var offer = LocalTurnSearch.PagedReplayAlternatives([action], decision, new(10, 10, 20, 20)).Single();
            Check(offer.Prefix[0].Choices!.Length == 1 && LocalTurnSearch.SameChoice(offer.Prefix[0].Choices![0], next),
                "A changed early selection retained a now-invalid later offer");
        });

        test("paged replay tracker submits only new exact edges and cannot merge equal visible ancestors", () =>
        {
            var tracker = new LocalPagedReplayTracker(); var hint = new LocalTurnHint(10, 10, 20, 20);
            var original = new LocalCardChoice("offer", 111, "opaque", "same name", [0], "hand", CompleteOffer: false);
            var later = original with { Index = 290, Indices = [4, 3, 2, 1] };
            var action = Move(0) with { Choices = [original] };
            var first = new LocalDecision(0, [action], [new(0, [original])]);
            Check(!tracker.Observe([action], first, hint, replaying: false).Any(), "Ordinary page was submitted twice");
            var page = first with { Choices = [new(0, [original, later])] };
            Check(tracker.Observe([action], page, hint, replaying: true).Single().Prefix[0].Choices![0].Index == 290,
                "New late-page edge disappeared");
            var repeated = page with { Choices = [new(0, [original, later with { Name = "changed label", Preference = 999 }])] };
            Check(!tracker.Observe([action], repeated, hint, replaying: true).Any(), "Repeated exact edge reached the broker again");
            var otherHistory = new[] { Move(9), action };
            Check(tracker.Observe(otherHistory, page with { BeforeStep = 1 }, hint, replaying: true).Count() == 1,
                "Equal visible offers merged different complete ancestor histories");
        });
    }
}
