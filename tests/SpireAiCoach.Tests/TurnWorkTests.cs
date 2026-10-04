using SpireAiCoach.Core;

static class TurnWorkTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static LocalSearchRequest Request() => new(Guid.NewGuid().ToString("N"), "snapshot", [], "native", 1, ["opaque-mod"], true,
        SearchOrder: LocalSearchOrder.TurnFrontier);
    static LocalAction Move(int i, string hash = "root", int round = 1) =>
        new(i, "opaque", null, "same display name", "", hash, round, Preference: 100 - i, CombatCardIndex: (uint)i);
    static LocalTurnHint Hint() => new(50, 50, 100, 100);

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("shared turn work closed broker still releases client resources", () =>
        {
            var captured = Request(); var broker = new LocalTurnWork(captured, 2);
            var client = new LocalTurnWorkClient(captured with { TurnWorkPipe = broker.PipeName });
            client.Offer([Move(0)], 1, Hint()); Check(client.TryTake(out var task), "Missing owned task");
            client.Finish(task);
            broker.Dispose();
            client.Dispose();
            client.Dispose();
        });

        test("shared turn work winning improvement requires its exact owned native history", () =>
        {
            var captured = Request(); using var broker = new LocalTurnWork(captured, 2);
            using var owner = new LocalTurnWorkClient(captured with { TurnWorkPipe = broker.PipeName });
            var start = Move(0); owner.Offer([start], 1, Hint());
            Check(owner.TryTake(out var task), "Missing owned task");
            var actual = Move(1, "after"); var other = Move(2, "after");
            var decision = new LocalDecision(1, [actual, other]);
            owner.OfferAlternatives([start, actual], decision, Hint(), task);
            var win = new LocalCandidate([start, actual], 50, 0, 0, 0, 100, true, false, false,
                Decisions: [decision], StartingHp: 50, RolloutStyle: LocalRolloutStyle.Preparation);
            bool rejected = false;
            try { owner.PromoteWinning(win with { Actions = [start with { CombatCardIndex = 99 }, actual] }); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "A different native prefix must not steer this owned task's improvements");
            owner.PromoteWinning(win); owner.Finish(task);
            Check(broker.Pending == 1, "Owned winning feedback duplicated or deleted pending siblings");
            owner.Offer([Move(3)], 1, Hint());
            Check(owner.TryTake(out var broad) && !broad.Focused, "Missing independent round work");
            owner.Finish(broad);
            Check(owner.TryTake(out var guided) && guided.FullRollout && guided.Style == LocalRolloutStyle.Preparation,
                "Broker rotation replaced the winning route's exploration policy");
            owner.Finish(guided);
        });

        asyncTest("shared turn work early consumers distinguish startup from exhausted work", async () =>
        {
            var captured = Request(); using var broker = new LocalTurnWork(captured, 4);
            var command = captured with { TurnWorkPipe = broker.PipeName };
            await Task.WhenAll(Enumerable.Range(1, 3).Select(owner => Task.Run(() =>
            {
                using var early = new LocalTurnWorkClient(command with { Partition = owner });
                Check(!early.TryTake(out _) && !early.RootReady && early.Active == 0,
                    "An empty startup queue must not be reported as exhausted");
            })));
            using var producer = new LocalTurnWorkClient(command);
            producer.Offer([], 1, Hint()); Check(producer.TryTake(out var root), "Root was not published atomically");
            Check(producer.RootReady, "Published root is still reported as preparing"); producer.Finish(root);
            using var done = new LocalTurnWorkClient(command with { Partition = 1 });
            Check(!done.TryTake(out _) && done.RootReady && done.Active == 0, "Completed work must release waiters");
        });
        test("shared turn work publishes only alternatives beyond its owned action and selection prefix", () =>
        {
            var fixedChoice = new LocalCardChoice("native-offer", 0, "opaque", "display");
            var freeChoice = new LocalCardChoice("later-offer", 1, "opaque", "display");
            var action = Move(0) with { Choices = [fixedChoice, freeChoice] };
            var task = new LocalTurnTask(0, [action with { Choices = [fixedChoice] }], 1);
            var offers = LocalTurnSearch.Alternatives([action], new(0, [Move(0), Move(1)],
                [new(0, [fixedChoice, fixedChoice with { Index = 1 }]),
                 new(1, [freeChoice, freeChoice with { Index = 0 }])]), Hint(), task).ToArray();
            Check(offers.Length == 1 && offers[0].Prefix[0].Choices?.Length == 2 &&
                offers[0].Prefix[0].Choices![0].Index == 0 && offers[0].Prefix[0].Choices![1].Index == 0,
                "Claimed actions or earlier choices leaked back to sibling owners");
            var next = Move(2, "child");
            var deeper = LocalTurnSearch.Alternatives([action, next], new(1, [next, Move(3, "child")]), Hint(), task).ToArray();
            Check(deeper.Length == 1 && deeper[0].Prefix.Length == 2, "New action forks were lost");
        });

        asyncTest("shared turn work claims later forks once across concurrent native owners", async () =>
        {
            var captured = Request(); using var broker = new LocalTurnWork(captured, 8);
            var command = captured with { TurnWorkPipe = broker.PipeName };
            var keys = new System.Collections.Concurrent.ConcurrentBag<string>();
            var styles = new System.Collections.Concurrent.ConcurrentBag<LocalRolloutStyle>();
            using (var producer = new LocalTurnWorkClient(command))
            {
                for (int i = 0; i < 96; i++) producer.Offer([Move(0), Move(i + 1, "later")], 2, Hint());
                // Disposing flushes the small final batch without claiming work.
            }
            await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                using var client = new LocalTurnWorkClient(command with { Partition = i });
                while (client.TryTake(out var task))
                {
                    keys.Add(LocalTurnSearch.HistoryKey(task.Prefix));
                    if (task.FullRollout) styles.Add(task.Style);
                    client.Finish(task);
                }
            })));
            Check(keys.Count == 96 && keys.Distinct().Count() == 96 && broker.Pending == 0,
                "Later branches were lost or claimed by two workers");
            Check(styles.Count == 48 && Enum.GetValues<LocalRolloutStyle>().All(s => styles.Count(x => x == s) == 12),
                "Concurrent owners must share an even portfolio of complete native rollouts");
        });

        test("shared turn work distinguishes a root not yet submitted from an exhausted frontier", () =>
        {
            var captured = Request(); using var broker = new LocalTurnWork(captured, 2);
            var command = captured with { TurnWorkPipe = broker.PipeName };
            using var waiting = new LocalTurnWorkClient(command with { Partition = 1 });
            Check(!waiting.TryTake(out _) && !waiting.RootReady && waiting.Active == 0,
                "A faster owner must wait for the initial native root");
            using (var root = new LocalTurnWorkClient(command)) root.Offer([], 1, Hint());
            Check(waiting.TryTake(out var task) && waiting.RootReady, "The later root was lost");
            waiting.Finish(task);
            Check(!waiting.TryTake(out _) && waiting.RootReady && waiting.Active == 0,
                "An actually exhausted native frontier must remain distinguishable");
        });

        test("shared turn work retains its listener with all sixteen owners connected", () =>
        {
            var captured = Request(); using var broker = new LocalTurnWork(captured, 16);
            var command = captured with { TurnWorkPipe = broker.PipeName };
            var clients = new List<LocalTurnWorkClient>();
            try
            {
                for (int owner = 0; owner < 16; owner++)
                {
                    var client = new LocalTurnWorkClient(command with { Partition = owner });
                    clients.Add(client);
                    client.Offer([Move(owner)], 1, Hint());
                    Check(client.TryTake(out _), "Missing work at maximum owner count");
                }
            }
            finally { foreach (var client in clients) client.Dispose(); }
            using var resumed = new LocalTurnWorkClient(command);
            Check(resumed.TryTake(out var task), "Listener stopped at the configured maximum");
            resumed.Finish(task);
        });

        asyncTest("shared turn work rejects other frozen roots and returns interrupted owned histories", async () =>
        {
            var captured = Request(); using var broker = new LocalTurnWork(captured, 2);
            var command = captured with { TurnWorkPipe = broker.PipeName };
            using (var first = new LocalTurnWorkClient(command))
            {
                first.Offer([Move(0)], 1, Hint()); Check(first.TryTake(out _), "Missing root task");
            }
            using (var second = new LocalTurnWorkClient(command with { Partition = 1 }))
            {
                Check(second.TryTake(out var returned) && returned.Prefix[0].CombatCardIndex == 0 && returned.Hint == Hint(),
                    "Disconnected owner changed or closed the interrupted prefix");
                second.Finish(returned);
            }
            bool rejected = false;
            using (var wrong = new LocalTurnWorkClient(command with { NativeHash = "another native root" }))
                try { wrong.TryTake(out _); }
                catch (InvalidOperationException) { rejected = true; }
            Check(rejected && broker.Pending == 0, "Another root consumed this frontier");
            await Task.CompletedTask;
        });

        asyncTest("shared turn work covers a finite native-history oracle without repeating terminals", async () =>
        {
            var captured = Request(); using var broker = new LocalTurnWork(captured, 4);
            var command = captured with { TurnWorkPipe = broker.PipeName };
            using (var root = new LocalTurnWorkClient(command)) root.Offer([], 1, Hint());
            var histories = new System.Collections.Concurrent.ConcurrentBag<string>();
            await Task.WhenAll(Enumerable.Range(0, 4).Select(owner => Task.Run(async () =>
            {
                using var client = new LocalTurnWorkClient(command with { Partition = owner });
                while (true)
                {
                    if (!client.TryTake(out var task))
                    { if (client.Active == 0) break; await Task.Delay(1); continue; }
                    var line = new List<LocalAction>();
                    for (int step = 0; step < 4; step++)
                    {
                        string hash = "native:" + string.Join(",", line.Select(a => a.CombatCardIndex));
                        var legal = Enumerable.Range(0, 3).Select(i => Move(i, hash, step / 2 + 1)).ToArray();
                        var actual = step < task.Prefix.Length ? LocalTurnSearch.ResolveExact(task.Prefix[step], legal) : legal[0];
                        line.Add(actual);
                        client.OfferAlternatives(line, new(step, legal), Hint(), task);
                        if (!task.FullRollout && line.Count >= task.Prefix.Length && line.Count == 2 && task.SearchRound == 1)
                        { client.Offer(line.ToArray(), 2, Hint()); break; }
                    }
                    if (line.Count == 4)
                    {
                        string history = LocalTurnSearch.HistoryKey(line);
                        histories.Add(history); client.Finish(task, history);
                    }
                    else client.Finish(task);
                }
            })));
            Check(histories.Count == 81 && histories.Distinct().Count() == 81 && broker.CompletedHistories == 81 &&
                broker.RepeatedHistories == 0 && broker.Pending == 0, "Finite tree coverage lost or repeated a terminal");
        });
    }
}
