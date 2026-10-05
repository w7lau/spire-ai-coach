using SpireAiCoach.Core;

internal static class ResumeSearchTests
{
    private static LocalSearchRequest Request(LocalSearchOrder order = LocalSearchOrder.TurnFrontier) =>
        new(Guid.NewGuid().ToString("N"), "snapshot", [1, 2], "native", 1, ["mod:v1"], false,
            SearchOrder: order, History: new(2, "history"));
    private static LocalAction Action(int n) => new(n, "opaque", null, "card", "", "native", 1, CombatCardIndex: (uint)n);
    private static LocalTurnHint Hint() => new(30, 30, 100, 100);
    private static void Check(bool b, string s = "Resume assertion failed") { if (!b) throw new Exception(s); }

    public static void Register(Action<string, Action> test)
    {
        test("resume scope allows new call IDs budgets worker counts and stop options", () => {
            var r = Request(); using var s = new LocalSearchSession(r);
            Check(s.Matches(r with { Id = "next", ContinueOptimization = true, ResumingFrontier = true,
                MaxNodes = 256, BudgetSeconds = 120, Workers = 1, Partitions = 1,
                StopOnZeroLoss = false, SkipFinalVerification = true, TimelineOrigin = 9, TurnWorkPipe = "new" }));
        });
        test("resume scope rejects changed native roots history replay models mods goals or horizons", () => {
            var r = Request(); using var s = new LocalSearchSession(r);
            var changed = new[] { r with { SnapshotId = "other" }, r with { NativeHash = "changed" },
                r with { History = new(3, "history") }, r with { Replay = [1, 3] }, r with { ModelHash = 2 },
                r with { LoadedMods = ["mod:v2"] }, r with { IncludePotions = true }, r with { MaxRounds = 80 },
                r with { MaxDepth = 30 }, r with { CardGoals = new("opaque", null) },
                r with { SearchOrder = LocalSearchOrder.MonteCarlo } };
            Check(changed.All(x => !s.Matches(x)));
        });
        test("turn resume preserves unsearched and interrupted tasks without reopening completed ones", () => {
            var r = Request(); using var s = new LocalSearchSession(r); var a = Action(0); var b = Action(1);
            LocalTurnTask done;
            using (var broker = new LocalTurnWork(r, 2, s.Turns))
            using (var client = new LocalTurnWorkClient(r with { TurnWorkPipe = broker.PipeName })) {
                client.Offer([a], 1, Hint()); client.Offer([b], 1, Hint());
                Check(client.TryTake(out done)); client.Finish(done);
                Check(client.TryTake(out var pending)); client.ReturnInterrupted(pending, Hint());
            }
            Check(s.Pending == 1);
            var next = r with { Id = "second", Workers = 1, ContinueOptimization = true, ResumingFrontier = true };
            using var resumed = new LocalTurnWork(next, 1, s.Turns);
            using var owner = new LocalTurnWorkClient(next with { TurnWorkPipe = resumed.PipeName });
            owner.Offer(done.Prefix, 1, Hint());
            Check(owner.TryTake(out var fresh) && fresh.Id != done.Id, "Completed prefix reopened");
            owner.Finish(fresh); Check(!owner.TryTake(out _), "Unexpected restarted root");
        });
        test("turn resume retains strict scope and rejects an old-call owner", () => {
            var r = Request(); using var s = new LocalSearchSession(r);
            using var broker = new LocalTurnWork(r with { Id = "new" }, 1, s.Turns);
            using var stale = new LocalTurnWorkClient(r with { TurnWorkPipe = broker.PipeName });
            bool rejected = false; try { stale.Offer([Action(0)], 1, Hint()); stale.TryTake(out _); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected);
            rejected = false; try { using var wrong = new LocalTurnWork(r with { NativeHash = "different" }, 1, s.Turns); }
            catch (InvalidDataException) { rejected = true; } Check(rejected);
        });
        test("whole battle resume preserves pending plans completed keys and returns interrupted claims", () => {
            var r = Request(LocalSearchOrder.MonteCarlo); using var s = new LocalSearchSession(r);
            var a = Action(0); var b = Action(1); string completed;
            using (var broker = new LocalSearchWorkBroker(r, 2, retainedQueue: s.Work))
            using (var client = new LocalSearchWork("", r with { SearchWorkPipe = broker.PipeName })) {
                client.Offer("expand", [[a], [b]], true);
                var first = client.Take("expand", 0, false) ?? throw new Exception("Missing first task");
                completed = first.Key; client.Complete(first);
                Check(client.Take("expand", 0, false) != null); client.Retire(0);
            }
            var next = r with { Id = "second", ContinueOptimization = true, ResumingFrontier = true };
            using var resumed = new LocalSearchWorkBroker(next, 1, retainedQueue: s.Work);
            using var owner = new LocalSearchWork("", next with { SearchWorkPipe = resumed.PipeName });
            owner.Offer("expand", [[a], [b]], true);
            var remaining = owner.Take("expand", 0, false);
            Check(remaining != null && remaining.Key != completed, "Completed job was repeated");
            owner.Complete(remaining!); Check(owner.Take("expand", 0, false) == null);
        });
        test("death without a winning route still exposes retained search progress", () => {
            var r = Request(); using var s = new LocalSearchSession(r);
            using (var broker = new LocalTurnWork(r, 1, s.Turns))
            using (var client = new LocalTurnWorkClient(r with { TurnWorkPipe = broker.PipeName })) client.Offer([Action(0)], 1, Hint());
            var death = new LocalCandidate([Action(1)], 0, 30, 10, 0, 30, false, true, false, StartingHp: 30);
            var result = new LocalSearchResult(r.Id, r.SnapshotId, "done", "budget", 64, 0, 10, death);
            s.Complete(result); var progress = s.Progress(false, result.Evaluated);
            Check(progress.CanContinue && progress.Pending == 1 && progress.TotalEvaluated == 64 && s.Baseline == null);
            Check(!LocalSearchPolicy.HasExecutionPoints(result));
            Check(LocalSearchPolicy.Format(result with { SearchProgress = progress }).Contains("可点击「继续搜索」"));
            s.Complete(result with { Id = "second", Evaluated = 32 });
            Check(s.Progress(true, 32) is { Batch: 2, TotalEvaluated: 96, Resumed: true });
        });
        test("retained whole battle queue cannot cross a different replay domain", () => {
            var r = Request(LocalSearchOrder.MonteCarlo); using var s = new LocalSearchSession(r);
            bool rejected = false; try { using var b = new LocalSearchWorkBroker(r with { LoadedMods = ["other"] }, 1, retainedQueue: s.Work); }
            catch (InvalidDataException) { rejected = true; } Check(rejected);
        });
    }
}
