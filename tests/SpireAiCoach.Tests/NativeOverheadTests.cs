using System.Collections.Concurrent;
using SpireAiCoach.Core;

public static class NativeOverheadTests
{
    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        void Check(bool condition) { if (!condition) throw new Exception("Native overhead assertion failed"); }
        test("optional visual factories require a leading null guard without effects or cleanup", () =>
        {
            var disabled = typeof(NativeOverheadTests).GetProperty(nameof(PresentationDisabled))!.GetMethod!;
            foreach (var name in new[] { nameof(OptionalVisual), nameof(EffectBeforeGuard), nameof(RequiredVisual), nameof(FactoryWithCleanup) })
            {
                var method = typeof(NativeOverheadTests).GetMethod(name)!;
                Check(LocalPresentationGuard.OptionalNullFactory(method, disabled) == (name == nameof(OptionalVisual)));
            }
            // Metadata inspection must never execute even the disabled getter.
            Check(_effects == 0);
        });
        test("decision fingerprints invalidate on state notifications frames state identity and reuse", () =>
        {
            var cache = new LocalDecisionFingerprint(); var state = new object();
            foreach (var change in Enumerable.Range(0, 5))
            {
                cache.Remember(state, "native", 10, 20);
                Check(!cache.Consume(change == 0 ? new object() : state, change == 1 ? "different" : "native",
                    change == 2 ? 11 : 10, change == 3 ? 21 : 20, change != 4));
                Check(!cache.Consume(state, "native", 10, 20, true));
            }
            cache.Remember(state, "native", 10, 20);
            Check(cache.Consume(state, "native", 10, 20, true));
            Check(!cache.Consume(state, "native", 10, 20, true));
            cache.Remember(state, "native", 10, 20); cache.Clear();
            Check(!cache.Consume(state, "native", 10, 20, true));
        });
        test("decision fingerprints do not cross threads", () =>
        {
            var cache = new LocalDecisionFingerprint(); var state = new object();
            cache.Remember(state, "native", 1, 1);
            bool reused = true;
            var thread = new Thread(() => reused = cache.Consume(state, "native", 1, 1, true));
            thread.Start(); thread.Join(); Check(!reused);
        });
        asyncTest("progress output merges backlog preserves ordering and drains before idle", async () =>
        {
            using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var written = new ConcurrentQueue<int>();
            var writer = new LocalLatestWriter<int>(value =>
            {
                if (value == 1) { started.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
                written.Enqueue(value);
            });
            try
            {
                writer.Publish(1); Check(started.Wait(TimeSpan.FromSeconds(5)));
                for (int i = 2; i <= 1000; i++) writer.Publish(i);
                var flush = writer.FlushAsync(); Check(!flush.IsCompleted);
                release.Set(); await flush.WaitAsync(TimeSpan.FromSeconds(5));
                Check(written.ToArray().SequenceEqual([1, 1000]));
                writer.Publish(1001);
                await writer.DisposeAsync(); Check(written.Last() == 1001);
                try { writer.Publish(1002); throw new Exception("Closed output accepted a new request"); }
                catch (InvalidOperationException) { }
                await writer.FlushAsync();
            }
            finally { release.Set(); await writer.DisposeAsync(); }
        });
        asyncTest("progress write failures reach flush and cleanup", async () =>
        {
            var writer = new LocalLatestWriter<int>(_ => throw new IOException("test output failure"));
            writer.Publish(1);
            try { await writer.FlushAsync(); throw new Exception("Output failure was ignored"); }
            catch (IOException ex) { Check(ex.Message == "test output failure"); }
            try { await writer.DisposeAsync(); throw new Exception("Cleanup ignored output failure"); }
            catch (IOException) { }
        });
        var action = new LocalAction(0, "card", 1, "name", "enemy", "native", 1, CombatCardIndex: 4);
        var request = new LocalSearchRequest("native-overhead", "snapshot", [], "root", 42, ["mod"], true,
            Partitions: 8, Workers: 8);
        test("memory search queue keeps capacity fairness terminal identity and request separation", () =>
        {
            using var memory = new LocalSearchWork("", request, 2, inMemory: true);
            memory.Offer("expand", [[action], [action, action]], initializeRoot: true);
            Check(memory.Stats() is { RootReady: true, Pending: 2 });
            var deep = memory.Take("expand", 0, deeper: true)!;
            var shallow = memory.Take("expand", 1)!;
            Check(deep.Plan.Length == 2 && shallow.Plan.Length == 1);
            memory.Complete(deep); memory.Complete(shallow);
            memory.Offer("expand", [[action], [action with { BeforeHash = "later" }]]);
            Check(memory.Stats() is { Submitted: 3, Pending: 1, DuplicateOffers: 1 });
            var later = memory.Take("expand", 0)!;
            memory.Offer("improve", [later.Plan]);
            var unfinished = new LocalCandidate(later.Plan, 40, 0, 10, 1, 40, false, false, false);
            memory.RecordTerminal(unfinished); Check(memory.Stats().Pending == 1);
            memory.RecordTerminal(unfinished with { Won = true }); Check(memory.Stats().Pending == 0);
            using var fresh = new LocalSearchWork("", request with { Id = "next" }, inMemory: true);
            fresh.Offer("expand", [later.Plan]); Check(fresh.Take("expand", 0) != null);
            memory.Retire(0); Check(memory.Stats().Active == 0);
            var stats = memory.Stats(); memory.ReleasePlans(); Check(memory.Stats() == stats);
        });
        test("search work broker eight owners claim each exact prefix once", () =>
        {
            using var broker = new LocalSearchWorkBroker(request, 8);
            var clients = Enumerable.Range(0, 8).Select(i => new LocalSearchWork("", request with
                { SearchWorkPipe = broker.PipeName, Partition = i })).ToArray();
            try
            {
                Check(!clients[7].Stats().RootReady);
                clients[0].Offer("expand", Enumerable.Range(0, 64).Select(i => new[] { action with { TargetId = (uint)i } }), initializeRoot: true);
                var claimed = new ConcurrentBag<string>();
                Parallel.For(0, 8, i =>
                {
                    while (clients[i].Take("expand", i) is { } task) { claimed.Add(task.Key); clients[i].Complete(task); }
                });
                Check(claimed.Count == 64 && claimed.Distinct().Count() == 64);
                Check(broker.Stats is { Submitted: 64, Claimed: 64, Completed: 64, Pending: 0, Active: 0 });
            }
            finally { foreach (var client in clients) client.Dispose(); }
        });
        test("search work broker rejects changed scopes and foreign task ownership", () =>
        {
            using var broker = new LocalSearchWorkBroker(request, 8);
            using var owner = new LocalSearchWork("", request with { SearchWorkPipe = broker.PipeName });
            using var peer = new LocalSearchWork("", request with { SearchWorkPipe = broker.PipeName, Partition = 1 });
            owner.Offer("expand", [[action]], initializeRoot: true);
            var task = owner.Take("expand", 0)!;
            try { peer.Complete(task); throw new Exception("Foreign completion accepted"); }
            catch (InvalidDataException) { }
            foreach (var changed in new[] { request with { NativeHash = "other" }, request with { LoadedMods = ["other"] }, request with { ModelHash = 43 } })
            {
                using var stranger = new LocalSearchWork("", changed with { SearchWorkPipe = broker.PipeName, Partition = 2 });
                try { stranger.Stats(); throw new Exception("Wrong frozen scope accepted"); }
                catch (InvalidDataException) { }
            }
            owner.Complete(task); Check(broker.Stats.Completed == 1);
            owner.Offer("expand", [[action with { BeforeHash = "unexplored" }]]);
            var interrupted = peer.Take("expand", 1)!; peer.Dispose();
            Check(broker.Stats is { Active: 0, Completed: 1 });
            Check(interrupted.Key != task.Key);
            broker.Dispose(); broker.Dispose();
        });
    }

    private static int _effects;
    public static bool PresentationDisabled => throw new InvalidOperationException("A metadata probe executed a getter");
    public static object? OptionalVisual() { if (PresentationDisabled) return null; return new object(); }
    public static object? EffectBeforeGuard() { Interlocked.Increment(ref _effects); if (PresentationDisabled) return null; return new object(); }
    public static object? RequiredVisual() { if (PresentationDisabled) return new object(); return null; }
    public static object? FactoryWithCleanup()
    {
        try { if (PresentationDisabled) return null; return new object(); }
        finally { Interlocked.Increment(ref _effects); }
    }
}
