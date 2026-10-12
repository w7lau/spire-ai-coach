using SpireAiCoach.Core;
using System.Text.Json;

static class CompactTurnTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static LocalAction Move(int id, int round = 1) => new(id, "card", null, "card", "", "state:" + id, round, CombatCardIndex: (uint)id);
    static readonly LocalTurnHint Hint = new(30, 40, 80, 100, MaximumFurtherHpGain: 0);
    public static void Register(Action<string, Action> test)
    {
        test("compact alternatives retain exact task ordering hints choices and duplicate counts across pipe", () =>
        {
            string? expected = null;
            foreach (bool compact in new[] { false, true })
            {
                var request = new LocalSearchRequest(Guid.NewGuid().ToString("N"), "s", [], "r", 1, [], false,
                    CompactTurnOffers: compact);
                using var broker = new LocalTurnWork(request, 1);
                using var client = new LocalTurnWorkClient(request with { TurnWorkPipe = broker.PipeName });
                var parent = Enumerable.Range(0, 40).Select(i => Move(i)).ToArray();
                var choice = new LocalCardChoice("offer", 0, "c", "c", [1, 2]);
                var actual = Move(100) with { Choices = [choice] };
                var history = parent.Append(actual).ToArray();
                var decision = new LocalDecision(40, Enumerable.Range(100, 60).Select(i => Move(i) with { Preference = i }).ToArray(),
                    [new(0, [choice, choice with { Indices = [2, 1] }, choice with { OfferHash = "other" }])]);
                for (int repeat = 0; repeat < 3; repeat++)
                {
                    client.Offer([Move(500 + repeat)], 1, Hint);
                    client.OfferAlternatives(history, decision, Hint with { Hp = 30 - repeat });
                    client.Offer([Move(800 + repeat)], 1, Hint);
                }
                var tasks = new List<string>();
                while (client.TryTake(out var task))
                {
                    tasks.Add(JsonSerializer.Serialize(task)); client.Finish(task);
                }
                Check(tasks.Count == 67 && broker.DuplicateOffers == 122, "Lost exact siblings or duplicate counts");
                string result = string.Join("\n", tasks);
                if (expected == null) expected = result;
                else Check(result == expected, "Compaction changed FIFO IDs, native identity, first hints or rollout scheduling");
            }
        });
        test("compact pending branches survive retirement retained requests and transport mode changes", () =>
        {
            var request = new LocalSearchRequest(Guid.NewGuid().ToString("N"), "s", [], "r", 1, [], false);
            var state = new LocalTurnWorkState(request); string? interrupted = null;
            using (var broker = new LocalTurnWork(request, 1, state))
            using (var client = new LocalTurnWorkClient(request with { TurnWorkPipe = broker.PipeName }))
            {
                client.OfferAlternatives([Move(0), Move(1)], new(1, [Move(1), Move(2), Move(3)]), Hint);
                Check(client.TryTake(out var task), "No compact branch"); interrupted = LocalTurnSearch.HistoryKey(task.Prefix);
            }
            var resumed = request with { Id = Guid.NewGuid().ToString("N"), CompactTurnOffers = false };
            using var next = new LocalTurnWork(resumed, 1, state);
            using var peer = new LocalTurnWorkClient(resumed with { TurnWorkPipe = next.PipeName });
            var histories = new List<string>();
            while (peer.TryTake(out var task)) { histories.Add(LocalTurnSearch.HistoryKey(task.Prefix)); peer.Finish(task); }
            Check(histories.Count == 2 && histories.Contains(interrupted!), "Retirement stranded compact or interrupted work");
        });
        test("compact branches retain descendant rejection and certified expense cuts", () =>
        {
            foreach (bool compact in new[] { false, true })
            {
                var frontier = new LocalTurnSearch(1, "root");
                var history = new[] { Move(0), Move(1) };
                var decision = new LocalDecision(1, [Move(1), Move(2), Move(3) with { PotionSlot = 0 }]);
                if (compact) frontier.OfferAlternatives(history, decision, Hint);
                else foreach (var offer in LocalTurnSearch.Alternatives(history, decision, Hint)) frontier.Offer(offer.Prefix, offer.SearchRound, offer.Hint);
                Check(frontier.DiscardDescendants([Move(0), Move(2)]) == 1 && frontier.Count == 1, "Descendant cut changed");
                Check(frontier.TryTake(out var potion) && potion.Prefix.Length == 2 && potion.Prefix[1].PotionSlot == 0, "Incorrect lazy prefix");
                frontier.ReturnInterrupted(potion, Hint);
                Check(frontier.DiscardDescendants([Move(0)]) == 1 && frontier.Count == 0, "Returned lazy prefix no longer matches");
                var known = new LocalTurnBatch([Move(10)], [Move(11)], 1, Hint);
                var unknown = known with { Alternatives = [Move(12)], Hint = Hint with { MaximumFurtherHpGain = null } };
                if (compact) { frontier.OfferBatch(known); frontier.OfferBatch(unknown); }
                else { frontier.Offer([Move(10), Move(11)], 1, Hint); frontier.Offer([Move(10), Move(12)], 1, unknown.Hint); }
                Check(frontier.DiscardProvenExpenses(new("root", 40, 5, 0, 35)) == 1 && frontier.Count == 1,
                    "Certified cuts must remove the inferior history while retaining unknown recovery");
            }
        });
        test("incumbent refinement first tries an observed potion substitute while retaining broad work", () =>
        {
            var search = new LocalTurnSearch(1);
            for (int i = 100; i < 110; i++) search.Offer([Move(i)], 1, Hint);
            var potion = Move(1) with { PotionSlot = 0 };
            var actions = new[] { Move(0), potion, Move(4) };
            LocalDecision[] decisions = [new(0, [Move(0), Move(2)]), new(1, [potion, Move(3)])];
            foreach (var d in decisions) search.OfferAlternatives(actions, d, Hint);
            search.ObserveOutcome(new(actions, 40, 0, 0, 0, 60, true, false, true, StartingHp: 40, Decisions: decisions));
            Check(search.TryTake(out _), "No broad lane");
            Check(search.TryTake(out var selected) && selected.Prefix.Length == 2 && selected.Prefix[1].CombatCardIndex == 3 &&
                selected.Continuation?.Length == 2, "Winning lane did not try the observed potion-saving deviation");
            Check(search.Count == 10, "Refinement removed unvisited alternatives");
        });
    }
}
