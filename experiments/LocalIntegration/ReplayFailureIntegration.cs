using System.Diagnostics;
using System.Reflection;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace SpireLocalIntegration;

// Exact frozen incident and native processes in the existing owned host only.
internal static class ReplayFailureIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request, LocalInstallation installation,
        string resultPath)
    {
        var lanes = ((Array)typeof(LocalWorkerPool)
            .GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!).Cast<object>().ToArray();
        string WorkerRoot(object worker) => (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        var progress = new List<string>();
        async Task<CoachException> Fail(LocalSearchRequest command, CancellationToken token = default)
        {
            try { await pool.Analyze(command, installation, progress.Add, token);
                throw new InvalidOperationException("A divergent root published a route"); }
            catch (CoachException failure) when (failure.Category == "local_replay_mismatch") { return failure; }
        }
        var watch = Stopwatch.StartNew();
        var first = await Fail(request); watch.Stop();
        var trace = (LocalTrace)first.Data["local_trace"]!;
        var failures = (LocalSimulationFailure[])first.Data["local_failures"]!;
        var probe = first.Data["local_replay_probe"] as LocalSearchResult;
        if (probe is not { Status: "failed", Evaluated: 0, RootBranches: 0, Best: null } ||
            failures.Length < 2 || failures.Any(f => f.Category != "local_replay_mismatch") ||
            failures.Any(f => f.ExpectedNativeHash != request.NativeHash) ||
            trace.Spans.Count(s => s.Phase == "root_probe") != 1)
            throw new InvalidOperationException("Missing two-mode exact native root rejection");
        var fallback = trace.Spans.Single(s => s.Phase == "fallback");
        var starts = trace.Spans.Where(s => s.Phase == "startup" && s.StartMs >= fallback.StartMs).ToArray();
        if (starts.Any(s => s.Worker != 0) || lanes.Skip(1).Any(w =>
            File.Exists(Path.Combine(WorkerRoot(w), "request.json")) &&
            LocalWire.Read<LocalSearchRequest>(Path.Combine(WorkerRoot(w), "request.json")).Id.Contains("-regular", StringComparison.Ordinal)))
            throw new InvalidOperationException("Root rejection started more than one compatibility lane");
        int launches = pool.Resources().Starts;
        if (launches != 2) throw new InvalidOperationException("A failed first root admitted unused numeric peers");
        var repeatedWatch = Stopwatch.StartNew();
        var repeated = await Fail(request with { Id = Guid.NewGuid().ToString("N") }); repeatedWatch.Stop();
        if (pool.Resources().Starts != launches ||
            ((LocalTrace)repeated.Data["local_trace"]!).Spans.All(s => s.Phase != "replay_failure_reused") ||
            repeatedWatch.Elapsed >= TimeSpan.FromSeconds(2))
            throw new InvalidOperationException("Identical frozen failure restarted native processes");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await Fail(request with { Id = Guid.NewGuid().ToString("N") }, cancel.Token);
            throw new InvalidOperationException("Cached failure displaced caller cancellation"); }
        catch (OperationCanceledException) { }
        // An independent valid root must still clean up and reuse the same native
        // process after root-only checks, including a complete candidate replay.
        var seed = LocalWire.Read<LocalSearchResult>(resultPath).Best!;
        var reader = new PacketReader(); reader.Reset(request.Replay);
        var replay = reader.Read<CombatReplay>(); replay.events.Clear();
        var packet = new PacketWriter(); replay.Serialize(packet);
        var valid = request with { Id = Guid.NewGuid().ToString("N"), NativeHash = seed.Actions[0].BeforeHash,
            Replay = packet.Buffer.AsSpan(0, (packet.BitPosition + 7) / 8).ToArray(),
            History = new(0, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([]))),
            Partition = 0, Partitions = 1, Workers = 1, DataOnlyCombat = false, DataOnlyRun = false,
            ReplayRootOnly = true, TurnWorkPipe = null, SearchWorkPipe = null, MinimumLossPipe = null, ProgressPipe = null };
        var lane = lanes[0]; var ordinary = installation with { MinimalWorkerBootstrap = false };
        async Task Ensure() => await (Task)lane.GetType().GetMethod("Ensure")!.Invoke(lane,
            [Path.Combine(root, "validation-pool"), 0, ordinary, CancellationToken.None, null])!;
        Process Process() => (Process)lane.GetType().GetProperty("Process")!.GetValue(lane)!;
        async Task<LocalSearchResult> Send(LocalSearchRequest command)
        {
            string workerRoot = WorkerRoot(lane);
            File.Delete(Path.Combine(workerRoot, "result.json")); File.Delete(Path.Combine(workerRoot, "idle.json"));
            LocalWire.Write(Path.Combine(workerRoot, "request.json"), command);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(90) && !Process().HasExited)
            {
                if (File.Exists(Path.Combine(workerRoot, "result.json")))
                {
                    var result = LocalWire.Read<LocalSearchResult>(Path.Combine(workerRoot, "result.json"));
                    if (result.Id == command.Id && result.Status != "running")
                    {
                        string generation = (string)lane.GetType().GetProperty("Generation")!.GetValue(lane)!;
                        if (!await LocalWorkerSession.WaitForIdle(workerRoot, command, generation,
                            () => !Process().HasExited, TimeSpan.FromSeconds(5)))
                            throw new InvalidOperationException("Valid root did not acknowledge cleanup");
                        if ((bool)lane.GetType().GetMethod("GameErrors")!.Invoke(lane, null)!)
                            throw new InvalidOperationException("Native validation worker reported a runtime error");
                        return result;
                    }
                }
                await Task.Delay(50);
            }
            throw new TimeoutException("Owned native root validation did not finish");
        }
        await Ensure(); int pid = Process().Id;
        for (int i = 0; i < 2; i++)
        {
            var restored = await Send(valid with { Id = Guid.NewGuid().ToString("N") });
            if (restored is not { Status: "restored", Evaluated: 0, Best: null } || restored.Timing?.Actions != 0)
                throw new InvalidOperationException("Root-only validation searched or failed a valid root");
            await Ensure(); if (Process().Id != pid) throw new InvalidOperationException("Valid root-only cleanup restarted its native process");
        }
        var verified = await Send(valid with { Id = Guid.NewGuid().ToString("N"), ReplayRootOnly = false, VerifyCandidate = seed });
        if (verified is not { Status: "done", Best.Won: true } ||
            verified.Best.Continuation?.Length != seed.Actions.Length || Process().Id != pid)
            throw new InvalidOperationException("Root validation broke native candidate verification or PID reuse");
        LocalWire.Write(Path.Combine(root, "integration-replay-failure-private.json"), new { first.Category, failures, trace });
        LocalWire.Write(Path.Combine(root, "integration-replay-failure-summary.json"), new {
            exact_incident_root_rejected = true, configured_workers = request.Workers, numeric_workers = 1,
            ordinary_root_probes = 1, ordinary_searches = 0, evaluated_routes = 0,
            same_frozen_input_reused_failure = true, extra_starts_on_repeat = 0,
            cold_failure_ms = watch.ElapsedMilliseconds, repeated_failure_ms = repeatedWatch.ElapsedMilliseconds,
            total_process_starts = launches, caller_cancel_preserved = true,
            usable_or_executable_plan = false,
            valid_root_only_checks = 2, root_only_native_actions = 0,
            valid_root_pid_reused = true, valid_candidate_native_checkpoints = seed.Actions.Length,
        });
    }
}
