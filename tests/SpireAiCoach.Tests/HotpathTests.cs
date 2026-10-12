using SpireAiCoach.Core;

static class HotpathTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static LocalAction Move(int id) => new(id, "model", null, "name", "", "hash", CombatCardIndex: (uint)id);
    static LocalTurnHint Hint() => new(50, 50, 100, 100);
    static LocalSearchRequest Request(bool coalesce) => new(Guid.NewGuid().ToString("N"), "snapshot", [], "native", 1, [], false,
        SearchOrder: LocalSearchOrder.TurnFrontier, CoalesceTurnOffers: coalesce);
    public static void Register(Action<string, Action> test)
    {
        test("turn offer coalescing preserves exact frontier order, hints and duplicate accounting", () =>
        {
            string? expected = null;
            foreach (bool fast in new[] { false, true })
            {
                var request = Request(fast); using var broker = new LocalTurnWork(request, 1);
                using var client = new LocalTurnWorkClient(request with { TurnWorkPipe = broker.PipeName });
                for (int repeat = 0; repeat < 4; repeat++)
                    for (int id = 0; id < 40; id++)
                        client.Offer([Move(id) with { CardName = "display " + repeat, Preference = repeat, HandIndex = id + repeat }], 1,
                            Hint() with { Hp = 50 - repeat });
                var tasks = new List<string>();
                while (client.TryTake(out var task))
                {
                    Check(task.Hint!.Hp == 50, "A duplicate updated first-offer hints");
                    tasks.Add(LocalTurnSearch.HistoryKey(task.Prefix)); client.Finish(task);
                }
                var actual = string.Join("\n", tasks);
                if (expected == null) expected = actual;
                else Check(actual == expected, "Coalescing changed frontier order or action identities");
                Check(tasks.Count == 40 && broker.DuplicateOffers == 120, "Lost branches or duplicate telemetry");
                Check(fast ? client.TransmittedOffers < 160 : client.TransmittedOffers == 160, "Duplicate transport not reduced");
                Check(client.LocallySuppressedOffers == 160 - client.TransmittedOffers, "Incorrect local count");
            }
        });
        test("unique proposal streams leave the secondary prefix cache inactive", () =>
        {
            var request = Request(true); using var broker = new LocalTurnWork(request, 1);
            using var client = new LocalTurnWorkClient(request with { TurnWorkPipe = broker.PipeName });
            for (int id = 0; id < 100; id++) client.Offer([Move(id)], 1, Hint());
            while (client.TryTake(out var task)) client.Finish(task);
            Check(client.TransmittedOffers == 100 && client.LocallySuppressedOffers == 0, "Unique work was suppressed");
            var cache = typeof(LocalTurnWorkClient).GetField("_offeredPrefixes", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
            Check((int)cache.GetType().GetProperty("Count")!.GetValue(cache)! == 1, "Unique stream built a redundant prefix tree");
        });
        test("turn offer cache retains native hash, instance, target, round and ordered choices", () =>
        {
            var request = Request(true); using var broker = new LocalTurnWork(request, 1);
            using var client = new LocalTurnWorkClient(request with { TurnWorkPipe = broker.PipeName });
            var move = Move(0); var choice = new LocalCardChoice("offer", 0, "choice", "display", [1, 2]);
            LocalAction[] variants = [move, move with { BeforeHash = "other" }, move with { TargetId = 7 },
                move with { CombatCardIndex = 1 }, move with { Round = 2 }, move with { PotionSlot = 0 },
                move with { Choices = [choice] }, move with { Choices = [choice with { Indices = [2, 1] }] },
                move with { Choices = [choice with { OfferHash = "another" }] },
                move with { CombatCardIndex = null }, move with { CombatCardIndex = null, HandIndex = 1 }];
            for (int repeat = 0; repeat < 8; repeat++)
                foreach (var action in variants) { client.Offer([action], 1, Hint()); client.Offer([action], 1, Hint()); }
            client.Offer([move], 2, Hint());
            int count = 0; while (client.TryTake(out var task)) { count++; client.Finish(task); }
            Check(count == variants.Length + 1, "Different native histories were merged");
            Check(client.LocallySuppressedOffers > 0, "Identity regression did not exercise the active cache");
        });
        test("coalesced offer cannot reopen completed work and returned work remains claimable", () =>
        {
            var request = Request(true); using var broker = new LocalTurnWork(request, 1);
            using var client = new LocalTurnWorkClient(request with { TurnWorkPipe = broker.PipeName });
            var move = Move(0); client.Offer([move], 1, Hint());
            Check(client.TryTake(out var task), "Missing first task");
            client.ReturnInterrupted(task, Hint()); client.Offer([move], 1, Hint());
            Check(client.TryTake(out var resumed) && resumed.Id == task.Id, "Interrupted work was lost");
            client.Finish(resumed); client.Offer([move], 1, Hint());
            Check(!client.TryTake(out _), "Duplicate reopened completed work");
            // A fresh request/cache is checked separately with its own matching scope.
            var next = Request(true); using var freshBroker = new LocalTurnWork(next, 1);
            using var fresh = new LocalTurnWorkClient(next with { TurnWorkPipe = freshBroker.PipeName });
            fresh.Offer([move], 1, Hint()); Check(fresh.TryTake(out var own), "Cache leaked across requests"); fresh.Finish(own);
        });
        test("coalesced duplicate accounting survives a retained frontier and transport mode changes", () =>
        {
            var request = Request(true); var retained = new LocalTurnWorkState(request);
            using (var broker = new LocalTurnWork(request, 1, retained))
            using (var client = new LocalTurnWorkClient(request with { TurnWorkPipe = broker.PipeName }))
            {
                for (int repeat = 0; repeat < 64; repeat++) client.Offer([Move(0)], 1, Hint());
                Check(client.TryTake(out var task), "Missing initial task"); client.ReturnInterrupted(task, Hint());
                Check(broker.DuplicateOffers == 63 && client.LocallySuppressedOffers > 0, "Missing suppressed duplicate count");
            }
            var next = request with { Id = Guid.NewGuid().ToString("N"), CoalesceTurnOffers = false, ThrottleUnchangedResults = false };
            using var resumed = new LocalTurnWork(next, 1, retained);
            using var peer = new LocalTurnWorkClient(next with { TurnWorkPipe = resumed.PipeName });
            peer.Offer([Move(0)], 1, Hint()); Check(peer.TryTake(out var own), "Retained task was lost"); peer.Finish(own);
            Check(resumed.DuplicateOffers == 64, "Suppressed duplicates disappeared between batches");
        });
        test("candidate heartbeat preserves changed candidates, certificates and every terminal result", () =>
        {
            var gate = new LocalResultPublication(); var best = new object();
            Check(gate.ShouldPublish(0, "running", best, null, true, true), "Missing first packet");
            Check(!gate.ShouldPublish(250, "running", best, null, true, true), "Unchanged packet was serialized again");
            var next = new object();
            Check(gate.ShouldPublish(250, "running", next, null, true, true), "Changed best was hidden");
            var proof = new LocalMinimumLossCertificate("scope", 50, 3, 0);
            Check(gate.ShouldPublish(500, "running", next, proof, true, true), "New proof was hidden");
            Check(!gate.ShouldPublish(750, "running", next, proof with { }, true, true), "Equal proof was treated as changed");
            Check(gate.ShouldPublish(1500, "running", next, proof, true, true), "Missing heartbeat");
            foreach (var status in new[] { "searched", "done", "failed", "cancelled" })
                Check(gate.ShouldPublish(1501, status, next, proof, true, true), "Terminal packet delayed");
            var legacy = new LocalResultPublication(); legacy.ShouldPublish(0, "running", best, null, true, false);
            Check(legacy.ShouldPublish(250, "running", best, null, true, false), "Baseline cadence changed");
        });
    }
}
