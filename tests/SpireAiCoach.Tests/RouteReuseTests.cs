using System.Text.Json;
using SpireAiCoach.Core;

static class RouteReuseTests
{
    static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    static LocalAction Move(int index, int round, bool end = false) => new(index, "opaque", null,
        "same display", "", "state:" + index, round, EndTurn: end, CombatCardIndex: (uint)index);
    static LocalCandidate Route() => new([Move(0, 1), Move(1, 1, true), Move(2, 7), Move(3, 7, true)],
        14, 18, 0, 0, 75, true, false, false, StartingHp: 32,
        Decisions: [new(0, [], HpBefore: 32, HpAfter: 32), new(1, [], HpBefore: 32, HpAfter: 32),
            new(2, [], HpBefore: 32, HpAfter: 32), new(3, [], HpBefore: 32, HpAfter: 14)]);

    public static void Register(Action<string, Action> test)
    {
        test("route reuse offers one complete seed while retaining native first-step alternatives", () =>
        {
            var search = new LocalTurnSearch(1); var route = Route(); var hint = new LocalTurnHint(32, 32, 100, 100);
            search.SeedRoot(route.Actions, 1, hint);
            Check(search.Count == 1 && search.TryTake(out var task) && task.FullRollout &&
                task.Prefix.Length == 0 && task.Continuation!.SequenceEqual(route.Actions),
                "Existing route became a turn probe or a forced prefix");
            search.OfferAlternatives([route.Actions[0]], new(0, [route.Actions[0], Move(10, 1), Move(11, 1, true)]), hint);
            Check(search.Count == 2 && search.TryTake(out var alternative) &&
                alternative.Prefix.Length == 1 && alternative.Continuation == null,
                "Seed reuse removed alternate cards or intentional EndTurn");
        });
        test("route reuse rejects late invalid and repeated initial publication without altering pending work", () =>
        {
            var search = new LocalTurnSearch(1); var hint = new LocalTurnHint(32, 32, 100, 100);
            bool Throws(Action action) { try { action(); return false; } catch (Exception ex) when
                (ex is InvalidOperationException or InvalidDataException) { return true; } }
            Check(Throws(() => search.SeedRoot([], 1, hint)) &&
                Throws(() => search.SeedRoot(Route().Actions, 2, hint)) && search.Count == 0, "Invalid seed changed the root");
            search.SeedRoot(Route().Actions, 1, hint);
            Check(Throws(() => search.SeedRoot(Route().Actions, 1, hint)) && search.Count == 1,
                "The initial route was published twice");
        });
        test("route reuse broker accepts only its frozen seed producer and claims the seed once", () =>
        {
            var actions = Route().Actions;
            var request = new LocalSearchRequest(Guid.NewGuid().ToString("N"), "root", [], actions[0].BeforeHash,
                1, ["mod:version:module"], true, Workers: 2, InitialPlan: actions, SearchOrder: LocalSearchOrder.TurnFrontier);
            using var broker = new LocalTurnWork(request, 2);
            var connected = request with { TurnWorkPipe = broker.PipeName };
            using var producer = new LocalTurnWorkClient(connected);
            using var peer = new LocalTurnWorkClient(connected with { Partition = 1 });
            var hint = new LocalTurnHint(32, 32, 100, 100);
            bool Throws(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
            Check(Throws(() => peer.SeedRoot(actions, 1, hint)) && broker.Pending == 0,
                "A different producer published the initial route");
            var changed = actions.ToArray(); changed[0] = changed[0] with { ModelId = "other" };
            Check(Throws(() => producer.SeedRoot(changed, 1, hint)) && broker.Pending == 0,
                "A changed route escaped frozen request identity");
            producer.SeedRoot(actions, 1, hint);
            Check(peer.TryTake(out var task) && task.FullRollout && task.Continuation!.SequenceEqual(actions) &&
                !producer.TryTake(out _), "Multiple workers claimed the same initial route");
            peer.Finish(task);
        });
        test("route feedback uses native per-round losses and includes settlement healing", () =>
        {
            var candidate = Route(); var losses = LocalRouteFeedback.RoundLosses(candidate);
            Check(losses[1] == 0 && losses[7] == 18, "Loss was not attributed to the actual settled round");
            Check(LocalRouteFeedback.RoundLosses(candidate with { Hp = 32 })[7] == 0, "Native victory healing was ignored");
            var selfCost = candidate with { Decisions = [new(0, [], HpBefore: 32, HpAfter: 24),
                new(1, [], HpBefore: 24, HpAfter: 32)] };
            Check(LocalRouteFeedback.RoundLosses(selfCost)[1] == 0, "Already healed self costs were penalized");
        });
        test("route feedback accepts matching legacy points and keeps absent or mismatched data neutral", () =>
        {
            var candidate = Route() with { Decisions = null, Continuation = Enumerable.Range(0, 4)
                .Select(i => new LocalContinuationPoint(i, "state:" + i, new(i, "history"), 0, 32)).ToArray() };
            Check(LocalRouteFeedback.RoundLosses(candidate)[7] == 18, "Legacy native points lost their HP evidence");
            Check(LocalRouteFeedback.RoundLosses(candidate with { Continuation = null }).Count == 0, "Missing data fabricated loss");
            Check(LocalRouteFeedback.RoundLosses(candidate with { Continuation = candidate.Continuation!
                .Select(p => p with { NativeHash = "other" }).ToArray() }).Count == 0, "Different states became feedback");
        });
        test("route feedback fields round-trip and request budgets remain unchanged", () =>
        {
            var candidate = JsonSerializer.Deserialize<LocalCandidate>(JsonSerializer.Serialize(Route()))!;
            Check(candidate.Decisions![3].HpAfter == 14 && LocalRouteFeedback.RoundLosses(candidate)[7] == 18, "HP evidence lost in IPC");
            var legacy = JsonSerializer.Deserialize<LocalDecision>("{\"BeforeStep\":0,\"Legal\":[]}")!;
            Check(legacy.HpBefore == null && legacy.HpAfter == null, "Absent HP became measured data");
            var request = new LocalSearchRequest("job", "root", [], "native", 1, [], false);
            Check(request.MaxNodes == 64 && request.BudgetSeconds == 60 && request.MaxRounds == 64,
                "Route reuse shortened the product budget");
        });
    }
}
