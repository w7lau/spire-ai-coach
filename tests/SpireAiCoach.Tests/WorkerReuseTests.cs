using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

// The executable copied into these unique installations is THIS test apphost,
// never a game executable. No existing game, process, save or broker is inspected.
internal static class WorkerReuseTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static LocalSearchRequest Request(string scenario = "quick", string snapshot = "battle-a") =>
        new(Guid.NewGuid().ToString("N"), snapshot, [], "native-" + snapshot, 1, [], false,
            Workers: 2, Partitions: 2, AdaptiveWorkers: false, ShareSearchWork: false,
            StopOnZeroLoss: false, DebugEncounter: scenario, DataOnlyCombat: false, SearchOrder: LocalSearchOrder.TurnFrontier);
    private static LocalWorkerPool.Worker[] Workers(LocalWorkerPool pool) =>
        (LocalWorkerPool.Worker[])typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pool)!;
    private static async Task Until(Func<bool> ready, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        Check(ready(), message);
    }
    private static async Task Cancelled(Task task)
    {
        try { await task; throw new Exception("Cancelled request returned a result"); }
        catch (OperationCanceledException) { }
    }

    private static bool Received(LocalWorkerPool.Worker worker, LocalSearchRequest request, bool verify)
    {
        try { return File.ReadAllText(Path.Combine(worker.Root, verify ? "verify-received" : "search-received")).StartsWith(request.Id, StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string Root = Path.GetFullPath(Path.Combine("work", "worker-reuse-" + Guid.NewGuid().ToString("N")));
        public readonly LocalInstallation Installation;
        public readonly LocalWorkerPool Pool;
        public Fixture(bool failStartup = false, bool holdStartup = false)
        {
            var game = Path.Combine(Root, "synthetic-installation"); Directory.CreateDirectory(game);
            foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
                if (Path.GetExtension(source) is ".dll" or ".json") File.Copy(source, Path.Combine(game, Path.GetFileName(source)));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "SpireAiCoach.Tests.exe"), Path.Combine(game, "SlayTheSpire2.exe"));
            LocalWire.Write(Path.Combine(game, "synthetic-worker.json"), new { test_apphost = true, fail_startup = failStartup,
                startup_release = holdStartup ? Path.Combine(Root, "startup-release") : null });
            Installation = new(game, []); Pool = new(Path.Combine(Root, "pool"));
        }
        public void ReleaseStartup(int index)
        {
            var release = Path.Combine(Root, "startup-release"); Directory.CreateDirectory(release);
            File.WriteAllText(Path.Combine(release, index.ToString()), "allow synthetic readiness");
        }
        public async ValueTask DisposeAsync()
        {
            // Only handles to children launched by this fixture, never enumeration.
            var owned = Workers(Pool).Where(w => w.Process?.HasExited == false)
                .Select(w => Process.GetProcessById(w.Process!.Id)).ToArray();
            try
            {
                Pool.Dispose();
                await Task.WhenAll(Workers(Pool).Select(w => w.DrainPreparation()));
                foreach (var child in owned) { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Check(child.HasExited, "Owned child leaked"); }
                Check(Workers(Pool).All(w => w.Process == null), "Disposed pool retains a process");
            }
            finally
            {
                foreach (var child in owned) child.Dispose();
                var resolved = Path.GetFullPath(Root);
                Check(resolved.StartsWith(Path.GetFullPath("work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Unsafe test cleanup path");
                Directory.Delete(resolved, true);
            }
        }
    }

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        asyncTest("cold prewarming prioritizes the root while peer engines load in parallel and preserves normal hot reuse", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture(holdStartup: true);
            var lanes = Workers(f.Pool);
            var warming = f.Pool.Prepare(f.Installation, 8, CancellationToken.None);
            await Until(() => lanes.Take(8).All(w => w.Process != null), "Engine loading remained serialized behind readiness");
            Check(f.Pool.Resources() is { Starts: 8, Preparing: 8 } && lanes[0].Process!.PriorityClass == ProcessPriorityClass.Normal &&
                lanes.Skip(1).Take(7).All(w => w.Process!.PriorityClass == ProcessPriorityClass.BelowNormal) &&
                lanes.Skip(1).Take(7).All(w => w.Process!.StartTime >= lanes[0].Process!.StartTime),
                "First launch order or owned cold peer priority was lost: " + JsonSerializer.Serialize(f.Pool.Resources()) + " " +
                string.Join(";", lanes.Take(8).Select((w, index) => $"{index}:{w.Process!.PriorityClass}:{w.Process.StartTime:O}")));
            var searching = f.Pool.Analyze(Request("goal") with { Workers = 8, StopOnFirstWin = true, IncludePotions = true },
                f.Installation, _ => { }, CancellationToken.None);
            f.ReleaseStartup(0);
            await Until(() => f.Pool.Resources().Ready == 1, "The root did not become independently ready");
            Check(f.Pool.Resources() is { Ready: 1, Starts: 8 } && !warming.IsCompleted,
                "Engine loading blocked independent root readiness");
            int rootPid = lanes[0].Process!.Id;
            var result = await searching;
            Check(result.Status == "done" && result.Best is { Won: true } && result.Timing?.Verifications == 1 &&
                lanes[0].Process!.Id == rootPid && !warming.IsCompleted && f.Pool.Resources().Starts == 8 &&
                result.Trace!.Spans.Any(s => s.Worker == 0 && s.Stage == "prepare" && s.Phase == "engine"),
                "Root search waited for all cold peers, skipped verification or duplicated a launch");
            for (int index = 1; index < 8; index++) f.ReleaseStartup(index);
            await warming.WaitAsync(TimeSpan.FromSeconds(30));
            var identities = lanes.Take(8).Select(w => (w.Process!.Id, w.Generation)).ToArray();
            await f.Pool.Prepare(f.Installation, 8, CancellationToken.None);
            Check(f.Pool.Resources() is { Ready: 8, Preparing: 0, Starts: 8 } &&
                identities.SequenceEqual(lanes.Take(8).Select(w => (w.Process!.Id, w.Generation))) &&
                lanes.Take(8).All(w => File.ReadAllLines(Path.Combine(w.Root, "launches.txt")).Length == 1 &&
                    w.Process!.PriorityClass == ProcessPriorityClass.BelowNormal),
                "Manual count, generation reuse or exactly-once launch was lost");
        });
        asyncTest("retiring a queued cold lane never launches it and a later preparation reuses its healthy peers", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture(holdStartup: true);
            var lanes = Workers(f.Pool);
            var slots = (SemaphoreSlim)typeof(LocalWorkerPool.Worker).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(field => field.FieldType == typeof(SemaphoreSlim)).GetValue(lanes[0])!;
            await slots.WaitAsync(); await slots.WaitAsync();
            var warming = f.Pool.Prepare(f.Installation, 8, CancellationToken.None);
            var queued = lanes[7];
            try
            {
                Check(f.Pool.Resources() is { Preparing: 8, Starts: 0 }, "A cold launch bypassed occupied launch slots");
                queued.Stop("synthetic queued retirement");
                await queued.DrainPreparation();
                Check(queued.Root == "" && queued.Process == null && queued.ResourceState().Starts == 0,
                    "Cancelled queue acquired storage or launched a child");
            }
            finally { slots.Release(2); }
            for (int index = 0; index < 8; index++) f.ReleaseStartup(index);
            await Cancelled(warming);
            var healthy = lanes.Take(8).Where(w => !ReferenceEquals(w, queued)).Select(w => (w, w.Process!.Id, w.Generation)).ToArray();
            await f.Pool.Prepare(f.Installation, 8, CancellationToken.None);
            Check(f.Pool.Resources() is { Ready: 8, Starts: 8 } &&
                healthy.All(saved => saved.w.Process!.Id == saved.Id && saved.w.Generation == saved.Generation),
                "Queue cancellation leaked a slot or rebuilt healthy peers");
        });
        asyncTest("logged native errors stop only their owner promptly and retain the exact failure across both algorithms", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            foreach (var algorithm in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            {
                await f.Pool.Prepare(f.Installation, 2, CancellationToken.None);
                var lanes = Workers(f.Pool); int healthy = lanes[0].Process!.Id;
                using var bad = Process.GetProcessById(lanes[1].Process!.Id);
                var request = Request("runtime-peer") with { DataOnlyCombat = true, SearchOrder = algorithm, BudgetSeconds = 10 };
                var watch = Stopwatch.StartNew();
                var result = await f.Pool.Analyze(request, f.Installation, _ => { }, CancellationToken.None);
                var failure = result.RecoveredFailures!.Single();
                Check(result.Status == "partial" && result.Best is { Won: true } && failure.Category == "local_runtime" &&
                    failure.Worker == 1 && failure.Message.Contains("synthetic choice context") && failure.Stack.Contains("Native.AfterShuffle"),
                    "The original logged error or healthy native candidate was lost");
                Check(bad.HasExited && lanes[1].Process == null && lanes[0].Process!.Id == healthy &&
                    watch.Elapsed < TimeSpan.FromSeconds(4) && result.Trace!.Spans.Count(s => s.Phase == "retire") == 1 &&
                    !result.Trace.Spans.Any(s => s.Phase == "fallback"),
                    $"Runtime retirement: algorithm={algorithm}; elapsed_ms={watch.ElapsedMilliseconds}; bad_exited={bad.HasExited}; bad_slot_pid={lanes[1].Process?.Id}; " +
                    $"healthy_pid={lanes[0].Process?.Id}; expected_healthy_pid={healthy}; events=" +
                    System.Text.Json.JsonSerializer.Serialize(result.Trace!.Spans.Where(s => s.Phase is "retire" or "rebuild" or "fallback")));
                var recorded = LocalWire.Read<LocalSearchResult>(Path.Combine(lanes[1].Root, "last-data-failure.json"));
                Check(recorded.Failure == failure && recorded.Best == null, "Persisted error evidence differs from the returned failure");
                var next = await f.Pool.Analyze(Request() with { SearchOrder = algorithm }, f.Installation, _ => { }, CancellationToken.None);
                Check(next.Status == "done" && lanes[0].Process!.Id == healthy &&
                    next.Trace!.Spans.Count(s => s.Phase == "rebuild") == 1, "A later search rebuilt healthy resources");
            }
        });
        asyncTest("a lone logged native error preserves structured rejection without starting compatibility searches", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            var request = Request("runtime-all") with { DataOnlyCombat = true, Workers = 1, BudgetSeconds = 10 };
            try { await f.Pool.Analyze(request, f.Installation, _ => { }, CancellationToken.None); throw new Exception("Logged error accepted"); }
            catch (CoachException ex) when (ex.Category == "local_runtime")
            {
                var failure = ((LocalSimulationFailure[])ex.Data["local_failures"]!).Single();
                Check(failure.Worker == 0 && failure.Stack.Contains("Native.AfterShuffle"), "Native log rejection lost its cause");
                Check(!((LocalTrace)ex.Data["local_trace"]!).Spans.Any(s => s.Phase == "fallback"), "Native log error restarted a full search");
            }
            Check(f.Pool.Resources().Starts == 1, "Runtime rejection launched a compatibility process");
        });
        asyncTest("both algorithms retain native owners through a six second busy result publication", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            await f.Pool.Prepare(f.Installation, 2, CancellationToken.None);
            var before = Workers(f.Pool).Take(2).Select(w => (w.Process!.Id, w.Generation)).ToArray();
            foreach (var algorithm in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            {
                var request = Request("busy-result") with { SearchOrder = algorithm, DataOnlyCombat = true, BudgetSeconds = 10 };
                var result = await f.Pool.Analyze(request, f.Installation, _ => { }, CancellationToken.None);
                Check(result.Status == "done" && result.Best is { Won: true } && result.RecoveredFailures is not { Length: > 0 },
                    "Publication pressure became a native failure");
                Check(before.SequenceEqual(Workers(f.Pool).Take(2).Select(w => (w.Process!.Id, w.Generation))) &&
                    !result.Trace!.Spans.Any(s => s.Phase is "retire" or "fallback" or "rebuild"),
                    "Algorithm switch or busy polling replaced a healthy owned process");
            }
        });
        test("worker reuse stop and idle acknowledgements reject stale requests and generations", () =>
        {
            var r = Request(); var stop = new LocalSearchStop(r.Id, r.SnapshotId, r.NativeHash, Cancel: true);
            Check(stop.Matches(r) && stop.Matches(r with { VerifyCandidate = Candidate(r) }), "Caller cancellation lost verification/goals-off support");
            Check(!stop.Matches(r with { Id = "old" }) && !stop.Matches(r with { NativeHash = "other" }) &&
                !stop.Matches(r with { SnapshotId = "other" }), "Cancellation crossed frozen requests");
            var goal = stop with { Cancel = false };
            Check(!goal.Matches(r) && goal.Matches(r with { StopOnZeroLoss = true }) &&
                !goal.Matches(r with { StopOnZeroLoss = true, VerifyCandidate = Candidate(r) }), "Goal stop interrupted verification");
            var idle = new LocalWorkerIdle(r.Id, r.SnapshotId, r.NativeHash, 0, "process-a");
            Check(idle.Matches(r, "process-a") && !idle.Matches(r, "process-b") && !idle.Matches(r, "") &&
                !idle.Matches(r with { Partition = 1 }, "process-a") && !idle.Matches(r with { Id = "old" }, "process-a"), "Stale idle returned a busy lane to the pool");
        });
        asyncTest("worker reuse file handshake rejects stale cleanup and cancellation before accepting the current request", async () =>
        {
            var root = Path.GetFullPath(Path.Combine("work", "worker-handshake-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            try
            {
                var r = Request(); var idle = new LocalWorkerIdle(r.Id, r.SnapshotId, r.NativeHash, r.Partition, "old-process");
                LocalWire.Write(Path.Combine(root, "idle.json"), idle);
                Check(!await LocalWorkerSession.WaitForIdle(root, r, "new-process", () => true, TimeSpan.FromMilliseconds(80)), "Old generation was admitted");
                LocalWire.Write(Path.Combine(root, "stop-search.json"), new LocalSearchStop("old", r.SnapshotId, r.NativeHash, Cancel: true));
                LocalWorkerSession.ThrowIfCancelled(root, r);
                LocalWire.Write(Path.Combine(root, "stop-search.json"), new LocalSearchStop(r.Id, r.SnapshotId, r.NativeHash, Cancel: true));
                try { LocalWorkerSession.ThrowIfCancelled(root, r); throw new Exception("Current cancellation was ignored"); }
                catch (LocalWorkerCancelledException) { }
                LocalWire.Write(Path.Combine(root, "idle.json"), idle with { Generation = "new-process" });
                Check(await LocalWorkerSession.WaitForIdle(root, r, "new-process", () => true, TimeSpan.FromSeconds(1)), "Current cleanup was rejected");
                Check(!await LocalWorkerSession.WaitForIdle(root, r, "new-process", () => false, TimeSpan.FromSeconds(1)), "Exited process admitted old idle");
            }
            finally { Directory.Delete(root, true); }
        });
        asyncTest("worker reuse interrupted turn work returns to its request before connecting a fresh broker", async () =>
        {
            var a = Request(); using var brokerA = new LocalTurnWork(a, 2);
            using (var client = new LocalTurnWorkClient(a with { TurnWorkPipe = brokerA.PipeName }))
            {
                client.Offer([], 1, new(50, 50, 100, 100)); Check(client.TryTake(out var task), "Missing task");
                client.ReturnInterrupted(task, new(50, 50, 100, 100));
            }
            Check(brokerA.Pending == 1 && brokerA.CompletedHistories == 0, "Cancelled history was completed or lost");
            using (var resumed = new LocalTurnWorkClient(a with { TurnWorkPipe = brokerA.PipeName, Partition = 1 }))
            {
                Check(resumed.TryTake(out var returned), "Interrupted owned history was not available");
                resumed.Finish(returned); Check(resumed.Active == 0, "Owner claim was not released");
            }
            var b = Request(snapshot: "save-b"); using var brokerB = new LocalTurnWork(b, 2);
            using var fresh = new LocalTurnWorkClient(b with { TurnWorkPipe = brokerB.PipeName });
            Check(!fresh.TryTake(out _) && !fresh.RootReady, "Old frontier crossed requests");
            await Task.CompletedTask;
        });
        asyncTest("worker reuse isolated processes retain PID across searches cancellation menu reentry and changed captures", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture(); var lanes = Workers(f.Pool); var timeline = new LocalTimeline();
            var cold = Stopwatch.StartNew(); await f.Pool.Prepare(f.Installation, 2, CancellationToken.None); cold.Stop();
            Check(lanes.Count(w => w.Process != null) == 2, "Manual prewarming did not prepare both chosen lanes");
            var pid = lanes[0].Process!.Id; var started = lanes[0].Process!.StartTime.ToUniversalTime();
            Check(f.Pool.Resources() is { Ready: 2, Preparing: 0, Starts: 2 }, "Ready resource display lost the actual worker state");
            var hot = Stopwatch.StartNew(); await f.Pool.Prepare(f.Installation, 2, CancellationToken.None); hot.Stop();
            var runs = new List<object>();
            foreach (var scenario in new[] { "repeat", "menu-reentry", "save-b" })
            {
                var request = Request(snapshot: scenario) with { Workers = 1 };
                var result = await f.Pool.Analyze(request, f.Installation, _ => { }, CancellationToken.None);
                Check(result.Status == "done" && result.Timing?.Verifications == 1 && lanes[0].Process!.Id == pid,
                    "Repeated/synthetic reentry capture restarted or skipped verification");
                runs.Add(new { scenario, result.Id, result.SnapshotId, pid = lanes[0].Process!.Id });
            }
            foreach (var verify in new[] { false, true })
            {
                using var cancellation = new CancellationTokenSource();
                var request = Request(verify ? "slow-verify" : "slow") with { Workers = 1 };
                var analyze = f.Pool.Analyze(request, f.Installation, _ => { }, cancellation.Token);
                await Until(() => Received(lanes[0], request, verify), "Fake request did not dispatch");
                var watch = Stopwatch.StartNew(); cancellation.Cancel(); await Cancelled(analyze); watch.Stop();
                Check(lanes[0].Process?.Id == pid && !lanes[0].Process!.HasExited, "Healthy cancellation rebuilt the process");
                var idle = LocalWire.Read<LocalWorkerIdle>(Path.Combine(lanes[0].Root, "idle.json"));
                Check(idle.Id.StartsWith(request.Id, StringComparison.Ordinal) && idle.Generation == lanes[0].Generation, "Cleanup acknowledgement was missing");
                runs.Add(new { scenario = verify ? "cancel-verification" : "cancel-search", pid, cancellation_ms = watch.ElapsedMilliseconds });
                var result = await f.Pool.Analyze(Request() with { Workers = 1 }, f.Installation, _ => { }, CancellationToken.None);
                Check(result.Status == "done" && lanes[0].Process!.Id == pid, "Cancelled-then-restarted search did not reuse");
                Check(f.Pool.Resources().Starts == 2, "Healthy cancellation counted as a new launch");
            }
            var evidence = new { kind = "synthetic protocol/process acceptance; no native game", cold_prepare_ms = cold.ElapsedMilliseconds,
                hot_prepare_ms = hot.ElapsedMilliseconds, pid, started_utc = started, generation = lanes[0].Generation,
                launches = File.ReadAllLines(Path.Combine(lanes[0].Root, "launches.txt")).Length, runs };
            Check(File.ReadAllLines(Path.Combine(lanes[0].Root, "launches.txt")).Length == 1, "Healthy loop reconstructed its worker");
            Directory.CreateDirectory("work"); LocalWire.Write(Path.GetFullPath("work/worker-reuse-process-evidence.json"), evidence);
            Console.WriteLine("  worker reuse evidence: " + JsonSerializer.Serialize(evidence));
        });
        asyncTest("worker reuse isolated preparation survives goal and caller cancellation and joins readiness", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture(); var lanes = Workers(f.Pool);
            await f.Pool.Prepare(f.Installation, 0, CancellationToken.None);
            // Root admission can now finish a goal without launching a peer.
            // An independently owned prewarm must still survive that goal.
            var peerWarmup = lanes[1].Ensure(Path.Combine(f.Root, "pool"), 1, f.Installation, CancellationToken.None);
            await Until(() => lanes[1].Process != null, "Cold peer prewarm was not launched");
            var result = await f.Pool.Analyze(Request("goal") with { StopOnZeroLoss = true }, f.Installation, _ => { }, CancellationToken.None);
            Check(result.StoppedEarly && result.Timing?.Verifications == 1, "Goal route was not verified");
            Check(lanes[1].Process != null, "Cold peer was terminated by the goal");
            int peer = lanes[1].Process!.Id;
            await peerWarmup;
            await lanes[1].Ensure(Path.Combine(f.Root, "pool"), 1, f.Installation, CancellationToken.None);
            Check(lanes[1].Process!.Id == peer && File.Exists(Path.Combine(lanes[1].Root, "ready")), "Alive-but-unready lane was reconstructed or reused prematurely");
            result = await f.Pool.Analyze(Request(), f.Installation, _ => { }, CancellationToken.None);
            Check(result.Status == "done" && lanes[1].Process!.Id == peer, "Goal-then-restart lost the peer");
            var goal = Request("peer-slow") with { StopOnZeroLoss = true, ShareSearchWork = true };
            result = await f.Pool.Analyze(goal, f.Installation, _ => { }, CancellationToken.None);
            Check(result.StoppedEarly && result.Trace!.Spans.Any(s => s.Phase == "stop_search") && lanes[1].Process!.Id == peer,
                "Goal stop failed to retain an active peer after broker retirement");
            result = await f.Pool.Analyze(Request() with { Workers = 1 }, f.Installation, _ => { }, CancellationToken.None);
            Check(result.WorkerLimit == 1 && lanes[1].Process == null, "Retained idle lane exceeded a reduced cap");
            // A separate lane begins cold and both callers join one tracked preparation.
            using var waiter = new CancellationTokenSource();
            var cancelled = lanes[2].Ensure(Path.Combine(f.Root, "pool"), 2, f.Installation, waiter.Token);
            await Until(() => lanes[2].Process != null, "Cold lane not launched"); int pid = lanes[2].Process!.Id;
            waiter.Cancel(); await Cancelled(cancelled);
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => lanes[2].Ensure(Path.Combine(f.Root, "pool"), 2, f.Installation, CancellationToken.None)));
            Check(lanes[2].Process!.Id == pid && File.ReadAllLines(Path.Combine(lanes[2].Root, "launches.txt")).Length == 1, "Preparation waiters duplicated launch");
            Console.WriteLine($"  goal/preparation reuse: peer_pid={peer}, joined_pid={pid}, launches=1 per lane");
        });
        asyncTest("both algorithms keep non-full zero-loss wins searching and return an achieved full-health route", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            foreach (var algorithm in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            foreach (var skip in new[] { false, true })
            {
                var request = Request("non-full-health") with { SearchOrder = algorithm, StopOnZeroLoss = true,
                    IncludePotions = true, SkipFinalVerification = skip };
                var partial = await f.Pool.Analyze(request, f.Installation, _ => { }, CancellationToken.None);
                Check(!partial.StoppedEarly && partial.Best is { Hp: 100, MaxHp: 250, NetHpLoss: 0 } && partial.Evaluated == 2,
                    "A non-full zero-loss winner prematurely stopped its peer");
                var full = await f.Pool.Analyze(request with { Id = Guid.NewGuid().ToString("N"), DebugEncounter = "full-health-race" },
                    f.Installation, _ => { }, CancellationToken.None);
                Check(full.StoppedEarly && full.Best is { Hp: 250, MaxHp: 250, NetHpLoss: 0 } &&
                    full.Best.Actions[0].PotionSlot == 0 && full.Timing?.Verifications == (skip ? 0 : 1),
                    "A non-full no-potion peer displaced the complete full-health return goal");
                Console.WriteLine($"  full-health protocol: algorithm={algorithm}; skip_verify={skip}; partial=100/250; partial_stopped=false; final=250/250; final_stopped=true; native_game=false");
            }
        });
        asyncTest("worker reuse isolated unsafe stop exit and configuration changes retire only owned processes", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture(); var worker = Workers(f.Pool)[0]; var directory = Path.Combine(f.Root, "pool");
            await worker.Ensure(directory, 0, f.Installation, CancellationToken.None);
            var r = Request("ignore-stop"); LocalWire.Write(Path.Combine(worker.Root, "request.json"), r);
            await Until(() => Received(worker, r, false), "Stuck request missing");
            using var owned = Process.GetProcessById(worker.Process!.Id); var watch = Stopwatch.StartNew();
            await worker.CancelRequest(r); watch.Stop();
            Check(worker.Process == null && owned.HasExited && watch.Elapsed < TimeSpan.FromSeconds(5),
                $"Unsafe callback was retained or cancellation unbounded: retained={worker.Process != null}, exited={owned.HasExited}, elapsed_ms={watch.ElapsedMilliseconds}");
            await worker.Ensure(directory, 0, f.Installation, CancellationToken.None); int rebuilt = worker.Process!.Id;
            LocalWire.Write(Path.Combine(worker.Root, "request.json"), Request("exit"));
            await worker.Process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await worker.Ensure(directory, 0, f.Installation, CancellationToken.None);
            Check(worker.Process!.Id != rebuilt, "Unexpected exit was reused");
            var oldGeneration = worker.Generation;
            await worker.Ensure(directory, 0, f.Installation with { MinimalWorkerBootstrap = false }, CancellationToken.None);
            Check(worker.Generation != oldGeneration, "Changed native configuration reused an old process");
            using var active = Process.GetProcessById(worker.Process!.Id); worker.Dispose(); await worker.DrainPreparation();
            Check(active.HasExited, "Disposal leaked an owned child");
            try { await worker.Ensure(directory, 0, f.Installation, CancellationToken.None); throw new Exception("Disposed lane relaunched"); }
            catch (ObjectDisposedException) { }
            Console.WriteLine($"  unsafe stop: elapsed_ms={watch.ElapsedMilliseconds}, retired=true; exit/config/disposal recovery verified");
        });
        asyncTest("worker reuse isolated startup failure and disposal during preparation leave no launch or lock behind", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using (var failed = new Fixture(failStartup: true))
            {
                var lane = Workers(failed.Pool)[0];
                try { await failed.Pool.Prepare(failed.Installation, 2, CancellationToken.None); throw new Exception("Startup failure succeeded"); }
                catch (IOException) { }
                Check(lane.Process == null, "Failed startup retained a process");
                using var unlocked = new FileStream(Path.Combine(lane.Root, ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            await using (var pending = new Fixture())
            {
                var lane = Workers(pending.Pool)[0];
                var prepare = pending.Pool.Prepare(pending.Installation, 2, CancellationToken.None);
                await Until(() => lane.Process != null, "Pending process missing");
                using var owned = Process.GetProcessById(lane.Process!.Id);
                pending.Pool.Dispose(); await Cancelled(prepare); await lane.DrainPreparation();
                Check(owned.HasExited && lane.Process == null, "Preparation launched or leaked after disposal");
                using var unlocked = new FileStream(Path.Combine(lane.Root, ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        });
        asyncTest("worker reuse isolated terminal output without matching cleanup fails closed and rebuilds", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture(); var lane = Workers(f.Pool)[0];
            try { await f.Pool.Analyze(Request("bad-idle") with { Workers = 1 }, f.Installation, _ => { }, CancellationToken.None); throw new Exception("Unclean terminal accepted"); }
            catch (CoachException ex) when (ex.Category == "local_failed") { }
            Check(lane.Process == null, "Unclean worker retained");
            Check(f.Pool.Resources() is { Ready: 0, Preparing: 0, Starts: 1 }, "Failed cleanup remained ready in the resource display");
            var result = await f.Pool.Analyze(Request() with { Workers = 1 }, f.Installation, _ => { }, CancellationToken.None);
            Check(result.Status == "done", "Unclean worker did not recover");
            Check(f.Pool.Resources().Starts == 2 && result.Trace!.Spans.Any(s => s.Phase == "rebuild" && s.Detail.Contains("cleanup")),
                "Rebuilding a failed instance lost its reason or launch count");
        });
        asyncTest("both algorithms stop at a shared positive minimum and verify only the final winner", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            // The stop latency must exclude process/assembly startup; native
            // cold-start latency has its own owned integration measurement.
            await f.Pool.Prepare(f.Installation, 2, CancellationToken.None);
            foreach (var algorithm in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            foreach (var scenario in new[] { "minimum-goal", "minimum-late-proof" })
            {
                var r = Request(scenario) with { SearchOrder = algorithm, StopOnZeroLoss = true };
                var watch = Stopwatch.StartNew();
                var result = await f.Pool.Analyze(r, f.Installation, _ => { }, CancellationToken.None);
                Check(result.Status == "done" && result.StoppedEarly && result.Best?.NetHpLoss == 12 &&
                    result.MinimumLoss is { Confirmed: true, Certificate.MinimumNetHpLoss: 12 }, "Positive minimum was not confirmed");
                Check(result.Timing?.Verifications == 1 && result.Trace!.Spans.Any(s => s.Phase == "stop_search") &&
                    watch.Elapsed < TimeSpan.FromSeconds(8), "Peer waited for its full budget or repeated winner verification");
                Check(LocalSearchPolicy.FormatAdvice(result).Contains("已证明最低净损失为 12"), "Advice hid the confirmed optimum");
                Console.WriteLine($"  minimum-loss protocol evidence: algorithm={algorithm}; scenario={scenario}; loss=12; peers=2; elapsed_ms={watch.ElapsedMilliseconds}; confirmed={result.MinimumLoss!.Confirmed}; verifications={result.Timing!.Verifications}; peer_budget_ms=10000; native_game=false");
            }
        });
        asyncTest("both algorithms confirm maximum healed HP before stopping peers", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            foreach (var algorithm in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            foreach (var scenario in new[] { "minimum-zero", "minimum-zero-late-proof", "minimum-healed", "minimum-healed-late-proof" })
            {
                int hp = scenario.Contains("healed") ? 70 : 50;
                var r = Request(scenario) with { SearchOrder = algorithm, StopOnZeroLoss = true };
                var watch = Stopwatch.StartNew();
                var result = await f.Pool.Analyze(r, f.Installation, _ => { }, CancellationToken.None);
                Check(result.Status == "done" && result.StoppedEarly && result.StoppedOnMinimum &&
                    result.Best is { Won: true, MaxHp: 100, NetHpLoss: 0 } && result.Best.Hp == hp &&
                    result.MinimumLoss is { Confirmed: true, Certificate: { } proof } && proof.MaximumFinalHp == hp,
                    "Non-full zero/gained HP was returned without a confirmed final-HP proof");
                Check(result.Timing?.Verifications == 1 && watch.Elapsed < TimeSpan.FromSeconds(8) &&
                    LocalSearchPolicy.FormatAdvice(result).Contains($"已证明最高战后生命为 {hp}"),
                    "The healed optimum waited for its peer budget or lost final verification");
            }
        });
        asyncTest("both algorithms return a first costly victory and stop peers under either loss-stop setting", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            foreach (var algorithm in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            foreach (bool lossStop in new[] { false, true })
            {
                var r = Request("first-win") with { SearchOrder = algorithm, ShareSearchWork = true,
                    StopOnFirstWin = true, StopOnZeroLoss = lossStop, SkipFinalVerification = lossStop, IncludePotions = true,
                    TargetVictoryRounds = 6, TargetPotionUses = 0, RequireKnownZeroEnemyDamage = true,
                    CardGoals = new("play", "finish", 5) };
                var watch = Stopwatch.StartNew();
                var result = await f.Pool.Analyze(r, f.Installation, _ => { }, CancellationToken.None);
                Check(result.Status == "done" && result.StoppedEarly && result.StoppedOnFirstWin &&
                    result.Best is { Won: true, Dead: false, Hp: 8, Rounds: 10 } && result.Best.Actions.Count(a => a.PotionSlot.HasValue) == 1,
                    "Costly victory waited for minimum-loss or the optional zero-loss target");
                Check(result.MinimumLoss == null && result.Timing?.Verifications == (lossStop ? 0 : 1) &&
                    (!Received(Workers(f.Pool)[1], r, false) || result.Trace!.Spans.Any(s => s.Phase == "stop_search")) &&
                    watch.Elapsed < TimeSpan.FromSeconds(8),
                    "Proof tracking, repeated verification or full peer budget delayed the result");
                Check(LocalSearchPolicy.HasExecutionPoints(result), "First-win result lost its execution checkpoints");
                Console.WriteLine($"  first-win protocol evidence: algorithm={algorithm}; loss_stop={lossStop}; skip_verify={lossStop}; elapsed_ms={watch.ElapsedMilliseconds}; loss=42; potions=1; rounds=10; verifications={result.Timing!.Verifications}; peer_budget_ms=10000; native_game=false");
            }
        });
        asyncTest("both algorithms manually adopt a running victory with unfinished goals and either verification setting", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            foreach (var algorithm in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            foreach (bool autoStop in new[] { false, true })
            foreach (bool skip in new[] { false, true })
            {
                var r = Request("manual-win") with { SearchOrder = algorithm, StopOnZeroLoss = autoStop,
                    SkipFinalVerification = skip, IncludePotions = true, DataOnlyCombat = true,
                    CardGoals = new("unplayed", "unfinished", 5) };
                var control = new LocalVictoryReturn(r);
                var watch = Stopwatch.StartNew();
                var task = f.Pool.Analyze(r, f.Installation, _ => { }, CancellationToken.None, victoryReturn: control);
                await Until(() => control.CanRequest, "Running victory did not enable manual return");
                Check(!task.IsCompleted && control.TryRequest(), "Manual choice did not stop the active calculation");
                var result = await task;
                Check(result.Status == "done" && result.StoppedEarly && result.StoppedOnManualVictory &&
                    !result.StoppedOnFirstWin && !result.StoppedOnCardGoals && !result.StoppedOnMinimum &&
                    result.Best is { Won: true, Dead: false, Hp: 8 } && LocalSearchPolicy.HasExecutionPoints(result),
                    "Manual stop discarded the victory or replaced it with an automatic goal claim");
                Check(result.Timing?.Verifications == (skip ? 0 : 1) && result.MinimumLoss == null &&
                    result.Trace!.Spans.Any(s => s.Phase == "stop_search") && watch.Elapsed < TimeSpan.FromSeconds(8) &&
                    !control.CanRequest && result.Evidence is { ManualStopped: true, GoalStopped: false },
                    "Manual stop ignored verification settings or waited for the full peer budget");
            }
        });
        asyncTest("manual victory rejects poisoned owners failed victory verification and missing checkpoints without restarting search", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            foreach (var scenario in new[] { "manual-win-errors", "manual-win-verify-mismatch", "manual-win-bad-points" })
            {
                var r = Request(scenario) with { DataOnlyCombat = true, SkipFinalVerification = scenario.EndsWith("bad-points") };
                var control = new LocalVictoryReturn(r);
                var task = f.Pool.Analyze(r, f.Installation, _ => { }, CancellationToken.None, victoryReturn: control);
                await Until(() => control.CanRequest, "Synthetic provisional victory missing");
                Check(control.TryRequest(), "Manual request rejected");
                try { await task; throw new Exception("Invalid provisional victory became usable"); }
                catch (CoachException ex) when (ex.Category is "local_failed" or "local_verify_failed" or "local_runtime") { }
                Check(!control.CanRequest && !Directory.EnumerateFiles(f.Root, "request.json", SearchOption.AllDirectories)
                    .Select(LocalWire.Read<LocalSearchRequest>).Any(x => x.Id.Contains("-regular", StringComparison.Ordinal)),
                    "Manual stop triggered a fresh compatibility search");
            }
        });
        asyncTest("cancel still abandons a found victory and interrupts manual final verification", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            foreach (bool duringVerification in new[] { false, true })
            {
                var r = Request("manual-win-verify-slow") with { Workers = 1 };
                var control = new LocalVictoryReturn(r);
                using var cancellation = new CancellationTokenSource();
                var task = f.Pool.Analyze(r, f.Installation, _ => { }, cancellation.Token, victoryReturn: control);
                await Until(() => control.CanRequest, "Running victory missing before cancellation");
                if (duringVerification)
                {
                    Check(control.TryRequest(), "Manual return rejected");
                    await Until(() => Received(Workers(f.Pool)[0], r, true), "Manual final verification missing");
                    Check(!control.CanRequest, "Return button remained enabled during verification");
                }
                cancellation.Cancel(); await Cancelled(task);
                Check(!control.CanRequest && !f.Pool.CanResume(r), "Cancel retained the provisional plan or frontier");
            }
        });
        asyncTest("both algorithms stop all peers at fulfilled consumable goals inside the permitted loss range", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            foreach (var algorithm in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            foreach (var skip in new[] { false, true })
            foreach (bool contentHealth in new[] { false, true })
            {
                var r = Request(contentHealth ? "card-goal-health" : "card-goal-stop") with { SearchOrder = algorithm, StopOnZeroLoss = true,
                    DataOnlyCombat = true, SkipFinalVerification = skip, CardGoals = new(null, "mod:finish", contentHealth ? null : 5) };
                var watch = Stopwatch.StartNew();
                var result = await f.Pool.Analyze(r, f.Installation, _ => { }, CancellationToken.None);
                Check(result.Status == "done" && result.StoppedEarly && result.StoppedOnCardGoals && !result.StoppedOnFirstWin &&
                    result.Best is { Won: true, CardGoalOutcome.Kills: 1 } && result.Best.NetHpLoss == (contentHealth ? 0 : 4),
                    "Consumable goal waited for full health or minimum proof");
                Check(!contentHealth || result.HealthTarget?.TargetHp == 50 && result.Best!.Hp < result.Best.MaxHp,
                    "Parent selection or verification lost the combined health target");
                Check(result.MinimumLoss == null && !LocalSearchPolicy.HasMinimumProof(result) &&
                    result.Timing?.Verifications == (skip ? 0 : 1) && LocalSearchPolicy.HasExecutionPoints(result) &&
                    (!Received(Workers(f.Pool)[1], r, false) || result.Trace!.Spans.Any(s => s.Phase == "stop_search")) &&
                    watch.Elapsed < TimeSpan.FromSeconds(8),
                    "Peers ran their full budget, goal scope was lost or final route verification repeated");
                Console.WriteLine($"  consumable-goal protocol evidence: algorithm={algorithm}; skip_verify={skip}; content_health={contentHealth}; elapsed_ms={watch.ElapsedMilliseconds}; kills=1; peers=2; verifications={result.Timing!.Verifications}; peer_budget_ms=10000; native_game=false");
            }
        });
        asyncTest("consumable goal stop rejects final verification that loses the real finishing blow", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            try
            {
                await f.Pool.Analyze(Request("card-goal-mismatch") with { Workers = 1, StopOnZeroLoss = true,
                    DataOnlyCombat = true, CardGoals = new(null, "mod:finish", 5) }, f.Installation, _ => { }, CancellationToken.None);
                throw new Exception("Verification removed the kill but the completed goal returned");
            }
            catch (CoachException ex) when (ex.Category == "local_verify_failed") { }
        });
        asyncTest("consumable goal stop rejects final verification below the current content health target", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            try
            {
                await f.Pool.Analyze(Request("card-goal-health-mismatch") with { Workers = 1, StopOnZeroLoss = true,
                    DataOnlyCombat = true, CardGoals = new(null, "mod:finish") }, f.Installation, _ => { }, CancellationToken.None);
                throw new Exception("A verified route below the combined health target became usable");
            }
            catch (CoachException ex) when (ex.Category == "local_verify_failed") { }
        });
        asyncTest("first-win result still rejects a failed final victory verification", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            try
            {
                await f.Pool.Analyze(Request("first-win-verify-mismatch") with { Workers = 1, StopOnFirstWin = true, IncludePotions = true },
                    f.Installation, _ => { }, CancellationToken.None);
                throw new Exception("A first-win route returned after verification lost the victory");
            }
            catch (CoachException ex) when (ex.Category == "local_verify_failed") { }
        });
        asyncTest("first-win result still rejects a winning worker runtime error", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            try
            {
                await f.Pool.Analyze(Request("first-win-errors") with { Workers = 1, StopOnFirstWin = true, IncludePotions = true },
                    f.Installation, _ => { }, CancellationToken.None);
                throw new Exception("An erroring worker supplied the first-win route");
            }
            catch (CoachException ex) when (ex.Category == "local_failed") { }
        });
        asyncTest("positive minimum does not survive a contributing worker runtime error", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture();
            try
            {
                await f.Pool.Analyze(Request("minimum-errors") with { StopOnZeroLoss = true }, f.Installation, _ => { }, CancellationToken.None);
                throw new Exception("An invalid contributor certified global minimum");
            }
            catch (CoachException ex) when (ex.Category == "local_data_unavailable") { }
        });
        asyncTest("worker reuse isolated cancelled worker with a native error is retired despite cleanup acknowledgement", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture(); var lane = Workers(f.Pool)[0];
            using var cancellation = new CancellationTokenSource(); var r = Request("cancel-errors") with { Workers = 1 };
            var analyze = f.Pool.Analyze(r, f.Installation, _ => { }, cancellation.Token);
            await Until(() => Received(lane, r, false), "Error request missing");
            cancellation.Cancel(); await Cancelled(analyze);
            Check(lane.Process == null, "Erroring worker was kept merely because cleanup acknowledged");
            var result = await f.Pool.Analyze(Request() with { Workers = 1 }, f.Installation, _ => { }, CancellationToken.None);
            Check(result.Status == "done", "Erroring worker did not rebuild");
        });
    }

    private static LocalCandidate Candidate(LocalSearchRequest request)
    {
        var candidate = request.VerifyCandidate ?? new(
            [new(0, "synthetic", null, "fake", "", request.NativeHash)], 50, 0, 0, 0, 50, true, false, false, StartingHp: 50);
        if (request.VerifyCandidate == null && request.DebugEncounter is "minimum-goal" or "minimum-errors" or "minimum-late-proof")
            candidate = candidate with { Hp = 38, HpLost = 12, Actions = [MinimumLossTests.FirstTurn(request, request.Partition).Steps[0].Action] };
        if (request.VerifyCandidate == null && request.DebugEncounter is { } scenario &&
            (scenario.StartsWith("minimum-zero", StringComparison.Ordinal) || scenario.StartsWith("minimum-healed", StringComparison.Ordinal)))
            candidate = candidate with { Hp = scenario.Contains("healed") ? 70 : 50, MaxHp = 100,
                Actions = [MinimumLossTests.FirstTurn(request, request.Partition).Steps[0].Action] };
        if (request.VerifyCandidate == null && (request.DebugEncounter?.StartsWith("first-win", StringComparison.Ordinal) == true ||
            request.DebugEncounter?.StartsWith("manual-win", StringComparison.Ordinal) == true))
            candidate = candidate with { Hp = 8, HpLost = 42, Rounds = 10, DamageSources = new(42, 0, 0, 0, true),
                Actions = [candidate.Actions[0] with { PotionSlot = 0, Round = 10 }] };
        if (request.VerifyCandidate != null && request.DebugEncounter is "first-win-verify-mismatch" or "manual-win-verify-mismatch")
            candidate = candidate with { Won = false, EnemyHp = 10 };
        if (request.VerifyCandidate == null && request.DebugEncounter?.StartsWith("card-goal", StringComparison.Ordinal) == true)
            candidate = candidate with { Hp = 46, HpLost = 4, CardGoalOutcome = new(null, "mod:finish", 0, 1,
                [new(0, 1)], new(null, new(1, 1, 1))) };
        if (request.DebugEncounter?.StartsWith("card-goal-health", StringComparison.Ordinal) == true)
            candidate = candidate with { Hp = request.VerifyCandidate != null && request.DebugEncounter.EndsWith("mismatch") ? 49 : 50,
                MaxHp = 80, HpLost = 0 };
        if (request.VerifyCandidate != null && request.DebugEncounter == "card-goal-mismatch")
            candidate = candidate with { CardGoalOutcome = candidate.CardGoalOutcome! with { Kills = 0,
                ConsumableGoals = new(null, new(1, 0, 1)) } };
        if (request.VerifyCandidate == null && request.DebugEncounter is "non-full-health" or "full-health-race")
            candidate = candidate with { StartingHp = 100, MaxHp = 250,
                Hp = request.DebugEncounter == "full-health-race" && request.Partition == 0 ? 250 : 100,
                Actions = [candidate.Actions[0] with {
                    PotionSlot = request.DebugEncounter == "full-health-race" && request.Partition == 0 ? 0 : null }] };
        return candidate with { Continuation = request.DebugEncounter == "manual-win-bad-points" ? [] : [new(0, request.NativeHash, new(0, "synthetic"), 0, 50)],
            ContinuationFromSearch = request.SkipFinalVerification && request.VerifyCandidate == null };
    }

    public static async Task<int> Child()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("SPIRE_COACH_WORKER")!);
        if (!File.Exists(Path.Combine(root, ".coach-worker")) || !File.Exists(Path.Combine(root, "game", "synthetic-worker.json"))) return 91;
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "game", "synthetic-worker.json")));
        if (configuration.RootElement.GetProperty("fail_startup").GetBoolean()) return 86;
        File.AppendAllText(Path.Combine(root, "launches.txt"), Environment.ProcessId + Environment.NewLine);
        File.WriteAllText(Path.Combine(root, "game.log"), "synthetic test worker\n");
        if (configuration.RootElement.TryGetProperty("startup_release", out var release) && release.ValueKind == JsonValueKind.String)
        {
            var permit = Path.Combine(release.GetString()!, Environment.GetEnvironmentVariable("SPIRE_COACH_WORKER_INDEX")!);
            while (!File.Exists(permit)) await Task.Delay(10);
        }
        await Task.Delay(650); File.WriteAllText(Path.Combine(root, "ready"), "synthetic ready");
        var generation = Environment.GetEnvironmentVariable("SPIRE_COACH_WORKER_GENERATION")!;
        using var owner = LocalWorkerOwner.Open(); string? previous = null;
        while (owner?.IsAlive == true)
        {
            var path = Path.Combine(root, "request.json");
            if (File.Exists(path))
            {
                var request = LocalWire.Read<LocalSearchRequest>(path);
                if (request.Id != previous)
                {
                    previous = request.Id;
                    foreach (var marker in new[] { "search-received", "verify-received" }) File.Delete(Path.Combine(root, marker));
                    File.WriteAllText(Path.Combine(root, request.VerifyCandidate == null ? "search-received" : "verify-received"), request.Id);
                    if (request.DebugEncounter == "exit") return 71;
                    // The synthetic root is ready before its simulated search.
                    // Native admission listens to this same scoped progress/result.
                    LocalWire.Write(Path.Combine(root, "result.json"), new LocalSearchResult(request.Id, request.SnapshotId,
                        "running", "synthetic root ready", 0, 0, 0, null, RootBranches: 2), operation =>
                        {
                            if (operation == "Write" && request.DebugEncounter == "busy-result" && request.Partition == 0 && request.VerifyCandidate == null)
                                Thread.Sleep(6000); // Synthetic producer stalls after acquiring the real publication lease.
                            return null;
                        });
                    using var client = request.TurnWorkPipe == null ? null : new LocalTurnWorkClient(request);
                    using var loss = request.MinimumLossPipe == null ? null : new LocalMinimumLossClient(request);
                    bool minimum = request.DebugEncounter?.StartsWith("minimum-", StringComparison.Ordinal) == true && request.VerifyCandidate == null;
                    if (minimum)
                    {
                        // The first winner finishes before this peer establishes
                        // the final missing bound; it need not be found again.
                        bool late = request.DebugEncounter!.EndsWith("late-proof", StringComparison.Ordinal) && request.Partition == 1;
                        if (late) await Task.Delay(1300);
                        var trial = MinimumLossTests.FirstTurn(request, request.Partition, finish: !late);
                        if (request.DebugEncounter.Contains("zero") || request.DebugEncounter.Contains("healed"))
                        {
                            int hp = request.DebugEncounter.Contains("healed") ? 70 : 50;
                            trial = trial with { Hp = hp, Steps = [trial.Steps[0] with { After = trial.Steps[0].After! with { Hp = hp } }] };
                        }
                        loss!.Observe(trial);
                    }
                    LocalTurnTask? task = null;
                    if (client != null) { client.Offer([], 1, new(50, 50, 100, 100)); if (client.TryTake(out var claimed)) task = claimed; }
                    int delay = request.DebugEncounter is "slow" or "ignore-stop" or "cancel-errors" || request.DebugEncounter == "slow-verify" && request.VerifyCandidate != null ||
                        request.DebugEncounter == "peer-slow" && request.Partition == 1 && request.VerifyCandidate == null ? 10000 : 60;
                    if (minimum) delay = request.Partition == 0 ? 600 : 10000;
                    if (request.DebugEncounter == "full-health-race" && request.VerifyCandidate == null)
                        delay = request.Partition == 0 ? 250 : 60;
                    if (request.DebugEncounter is "goal" or "peer-slow" && request.Partition == 0 && request.VerifyCandidate == null)
                        delay = 600;
                    if (request.DebugEncounter?.StartsWith("first-win", StringComparison.Ordinal) == true && request.VerifyCandidate == null)
                        delay = request.Partition == 0 ? 600 : 10000;
                    if (request.DebugEncounter?.StartsWith("card-goal", StringComparison.Ordinal) == true && request.VerifyCandidate == null)
                        delay = request.Partition == 0 ? 600 : 10000;
                    bool manual = request.DebugEncounter?.StartsWith("manual-win", StringComparison.Ordinal) == true;
                    if (manual && request.VerifyCandidate == null || request.DebugEncounter == "manual-win-verify-slow") delay = 10000;
                    bool runtimeError = request.VerifyCandidate == null &&
                        (request.DebugEncounter == "runtime-all" || request.DebugEncounter == "runtime-peer" && request.Partition == 1);
                    if (runtimeError) delay = 10000;
                    if (request.DebugEncounter == "runtime-peer" && request.Partition == 0 && request.VerifyCandidate == null) delay = 1400;
                    var timer = Stopwatch.StartNew(); bool cancelled = false, goal = false, manualStop = false, publishedWin = false;
                    bool errorLogged = false;
                    while (timer.ElapsedMilliseconds < delay)
                    {
                        if (runtimeError && !errorLogged && timer.ElapsedMilliseconds >= 600)
                        {
                            File.AppendAllText(Path.Combine(root, "game.log"), "[ERROR] synthetic choice context mismatch\n   at Native.AfterShuffle()\n");
                            errorLogged = true;
                        }
                        if (manual && request.VerifyCandidate == null && request.Partition == 0 && !publishedWin && timer.ElapsedMilliseconds >= 600)
                        {
                            publishedWin = true;
                            LocalWire.Write(Path.Combine(root, "result.json"), new LocalSearchResult(request.Id, request.SnapshotId,
                                "running", "synthetic victory", 1, 0, timer.ElapsedMilliseconds, Candidate(request), RootBranches: 2));
                        }
                        if (request.DebugEncounter != "ignore-stop")
                        {
                            cancelled = LocalWorkerSession.Cancelled(root, request);
                            var stop = Path.Combine(root, "stop-search.json");
                            goal = File.Exists(stop) && LocalWire.Read<LocalSearchStop>(stop).Matches(request);
                            manualStop = goal && LocalWire.Read<LocalSearchStop>(stop).UseWinningRoute;
                            if (cancelled || goal) break;
                        }
                        await Task.Delay(15);
                    }
                    if (task != null) { if (cancelled || goal) client!.ReturnInterrupted(task, new(50, 50, 100, 100)); else client!.Finish(task); }
                    var result = new LocalSearchResult(request.Id, request.SnapshotId,
                        cancelled ? "cancelled" : request.VerifyCandidate == null ? "searched" : "done", "synthetic", 1, 0, timer.ElapsedMilliseconds,
                        cancelled || goal && !(manualStop && publishedWin) ? null : Candidate(request), RootBranches: 2,
                        Timing: new(Verifications: request.VerifyCandidate == null ? 0 : 1),
                        MinimumLoss: minimum ? loss!.Observe(null) : null, StoppedEarly: goal, StoppedOnManualVictory: manualStop,
                        HealthTarget: request.DebugEncounter?.StartsWith("card-goal-health", StringComparison.Ordinal) == true ?
                            new(LocalMinimumLossProof.Scope(request), 50, 50, false) : null);
                    LocalWire.Write(Path.Combine(root, "result.json"), result);
                    if (cancelled && request.DebugEncounter == "cancel-errors") File.AppendAllText(Path.Combine(root, "game.log"), "[ERROR] synthetic native error\n");
                    if (minimum && request.Partition == 1 && request.DebugEncounter == "minimum-errors")
                        File.AppendAllText(Path.Combine(root, "game.log"), "[ERROR] synthetic invalid proof contributor\n");
                    if (request.DebugEncounter == "first-win-errors" && request.VerifyCandidate == null)
                        File.AppendAllText(Path.Combine(root, "game.log"), "[ERROR] synthetic invalid first-win worker\n");
                    if (request.DebugEncounter == "manual-win-errors" && request.VerifyCandidate == null && publishedWin)
                        File.AppendAllText(Path.Combine(root, "game.log"), "[ERROR] synthetic invalid manual winner\n");
                    // Deliberately publish before teardown, like the native worker.
                    await Task.Delay(80); client?.Dispose();
                    LocalWire.Write(Path.Combine(root, "idle.json"), new LocalWorkerIdle(request.Id, request.SnapshotId, request.NativeHash, request.Partition,
                        request.DebugEncounter == "bad-idle" ? "wrong-generation" : generation));
                }
            }
            await Task.Delay(20);
        }
        return 0;
    }
}
