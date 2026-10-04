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
        public Fixture(bool failStartup = false)
        {
            var game = Path.Combine(Root, "synthetic-installation"); Directory.CreateDirectory(game);
            foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
                if (Path.GetExtension(source) is ".dll" or ".json") File.Copy(source, Path.Combine(game, Path.GetFileName(source)));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "SpireAiCoach.Tests.exe"), Path.Combine(game, "SlayTheSpire2.exe"));
            LocalWire.Write(Path.Combine(game, "synthetic-worker.json"), new { test_apphost = true, fail_startup = failStartup });
            Installation = new(game, []); Pool = new(Path.Combine(Root, "pool"));
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
            var result = await f.Pool.Analyze(Request("goal") with { StopOnZeroLoss = true }, f.Installation, _ => { }, CancellationToken.None);
            Check(result.StoppedEarly && result.Timing?.Verifications == 1, "Goal route was not verified");
            Check(lanes[1].Process != null, "Cold peer was terminated by the goal");
            int peer = lanes[1].Process!.Id;
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
        asyncTest("worker reuse isolated unsafe stop exit and configuration changes retire only owned processes", async () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var f = new Fixture(); var worker = Workers(f.Pool)[0]; var directory = Path.Combine(f.Root, "pool");
            await worker.Ensure(directory, 0, f.Installation, CancellationToken.None);
            var r = Request("ignore-stop"); LocalWire.Write(Path.Combine(worker.Root, "request.json"), r);
            await Until(() => Received(worker, r, false), "Stuck request missing");
            using var owned = Process.GetProcessById(worker.Process!.Id); var watch = Stopwatch.StartNew();
            await worker.CancelRequest(r); watch.Stop();
            Check(worker.Process == null && owned.HasExited && watch.Elapsed < TimeSpan.FromSeconds(5), "Unsafe callback was retained or cancellation unbounded");
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

    private static LocalCandidate Candidate(LocalSearchRequest request) => new(
        [new(0, "synthetic", null, "fake", "", request.NativeHash)], 50, 0, 0, 0, 50, true, false, false,
        StartingHp: 50, Continuation: [new(0, request.NativeHash, new(0, "synthetic"), 0, 50)]);

    public static async Task<int> Child()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("SPIRE_COACH_WORKER")!);
        if (!File.Exists(Path.Combine(root, ".coach-worker")) || !File.Exists(Path.Combine(root, "game", "synthetic-worker.json"))) return 91;
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "game", "synthetic-worker.json")));
        if (configuration.RootElement.GetProperty("fail_startup").GetBoolean()) return 86;
        File.AppendAllText(Path.Combine(root, "launches.txt"), Environment.ProcessId + Environment.NewLine);
        File.WriteAllText(Path.Combine(root, "game.log"), "synthetic test worker\n");
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
                    using var client = request.TurnWorkPipe == null ? null : new LocalTurnWorkClient(request);
                    LocalTurnTask? task = null;
                    if (client != null) { client.Offer([], 1, new(50, 50, 100, 100)); if (client.TryTake(out var claimed)) task = claimed; }
                    int delay = request.DebugEncounter is "slow" or "ignore-stop" or "cancel-errors" || request.DebugEncounter == "slow-verify" && request.VerifyCandidate != null ||
                        request.DebugEncounter == "peer-slow" && request.Partition == 1 && request.VerifyCandidate == null ? 10000 : 60;
                    var timer = Stopwatch.StartNew(); bool cancelled = false, goal = false;
                    while (timer.ElapsedMilliseconds < delay)
                    {
                        if (request.DebugEncounter != "ignore-stop")
                        {
                            cancelled = LocalWorkerSession.Cancelled(root, request);
                            var stop = Path.Combine(root, "stop-search.json");
                            goal = File.Exists(stop) && LocalWire.Read<LocalSearchStop>(stop).Matches(request);
                            if (cancelled || goal) break;
                        }
                        await Task.Delay(15);
                    }
                    if (task != null) { if (cancelled || goal) client!.ReturnInterrupted(task, new(50, 50, 100, 100)); else client!.Finish(task); }
                    var result = new LocalSearchResult(request.Id, request.SnapshotId,
                        cancelled ? "cancelled" : request.VerifyCandidate == null ? "searched" : "done", "synthetic", 1, 0, timer.ElapsedMilliseconds,
                        cancelled || goal ? null : Candidate(request), RootBranches: 2, Timing: new(Verifications: request.VerifyCandidate == null ? 0 : 1));
                    LocalWire.Write(Path.Combine(root, "result.json"), result);
                    if (cancelled && request.DebugEncounter == "cancel-errors") File.AppendAllText(Path.Combine(root, "game.log"), "[ERROR] synthetic native error\n");
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
