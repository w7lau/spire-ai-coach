using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

internal static class TwoWorkerAcceptance
{
    // Fault injection is restricted to the process handle returned by this owned pool.
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr handle);

    public static async Task<int> Run(string path)
    {
        var root = Path.GetFullPath(path);
        if (!root.StartsWith(Path.GetFullPath("work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(root, ".native-worker-reuse-owner")))
            throw new InvalidOperationException("Two-worker acceptance requires its private owned directory");
        var game = Path.Combine(root, "game");
        var frozen = LocalWire.Read<LocalSearchRequest>(Path.Combine(root, "frozen-request.json"));
        if (frozen.InitialPlan is { Length: > 0 } || frozen.VerifyCandidate != null || frozen.RecordedReplayProbe != null)
            throw new InvalidOperationException("Answer seeds are not accepted");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, "mods", "SpireAiCoach", "SpireAiCoach.json")));
        var version = manifest.RootElement.GetProperty("version").GetString();
        var loaded = frozen.LoadedMods.Select(m => m.StartsWith("SpireAiCoach:", StringComparison.Ordinal)
            ? $"SpireAiCoach:{version}:{typeof(LocalWorkerPool).Assembly.ManifestModule.ModuleVersionId}" : m).Order(StringComparer.Ordinal).ToArray();
        var capture = frozen with { LoadedMods = loaded, InitialTrace = null, TimelineOrigin = 0 };
        var installation = new LocalInstallation(game, Directory.GetDirectories(Path.Combine(game, "mods")), MinimalWorkerBootstrap: false);
        var directory = Path.Combine(root, "native-pool");
        using var pool = new LocalWorkerPool(directory);
        var workers = (Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
        var sync = new object();
        var records = new List<object>();
        var observed = new HashSet<Identity>[] { [], [] };
        var witnesses = new Dictionary<Identity, Process>();
        Identity[]? expected = null;
        string? previousPipe = null;
        string? previousRequestId = null;
        int maxLive = 0;
        object Lane(int i) => workers.GetValue(i)!;
        Process? Child(int i) => (Process?)Lane(i).GetType().GetProperty("Process")!.GetValue(Lane(i));
        string LaneRoot(int i) => (string)Lane(i).GetType().GetProperty("Root")!.GetValue(Lane(i))!;
        string Generation(int i) => (string)Lane(i).GetType().GetProperty("Generation")!.GetValue(Lane(i))!;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void Budget()
        {
            int live = workers.Cast<object>().Count(w => ((Process?)w.GetType().GetProperty("Process")!.GetValue(w))?.HasExited == false);
            maxLive = Math.Max(maxLive, live);
            Check(live <= 2 && workers.Cast<object>().Skip(2).All(w => ((Process?)w.GetType().GetProperty("Process")!.GetValue(w)) == null),
                "Acceptance exceeded its two owned worker limit");
        }
        Identity Observe(int i)
        {
            lock (sync)
            {
                Budget();
                var child = Child(i) ?? throw new InvalidOperationException("Owned lane missing: " + i);
                Check(!child.HasExited, "Owned lane exited: " + i);
                var identity = new Identity(i, child.Id, child.StartTime.ToUniversalTime(), Generation(i));
                if (observed[i].Add(identity))
                    // This PID comes exclusively from a child this pool launched.
                    witnesses.Add(identity, Process.GetProcessById(child.Id));
                return identity;
            }
        }
        object[] Identities() => Enumerable.Range(0, 2).Select(i =>
        {
            var identity = Observe(i); var p = Child(i)!; p.Refresh();
            return (object)new { identity.Lane, pid = identity.Pid, started_utc = identity.Start,
                generation = identity.Generation, private_memory_bytes = p.PrivateMemorySize64 };
        }).ToArray();
        void Same()
        {
            Check(expected != null, "Expected identities not established");
            for (int i = 0; i < 2; i++) Check(Observe(i) == expected![i], "Healthy lane rebuilt: " + i);
        }
        async Task Ensure(int i)
        {
            var method = Lane(i).GetType().GetMethod("Ensure")!;
            await (Task)method.Invoke(Lane(i), [directory, i, installation, CancellationToken.None, new LocalTimeline()])!;
            Observe(i);
        }
        void Record(object record)
        {
            records.Add(record); LocalWire.Write(Path.Combine(root, "native-progress-summary.json"), records);
            Console.WriteLine(JsonSerializer.Serialize(record));
        }
        LocalSearchRequest Command(bool goal) => LocalCalculation.Configure(capture, LocalSearchOrder.TurnFrontier, 2,
            capture.IncludePotions, goal, targetVictoryRounds: goal ? 6 : 0) with
        {
            Id = Guid.NewGuid().ToString("N"), InitialTrace = null, TimelineOrigin = 0,
            Partition = 0, Partitions = 2, AdaptiveWorkers = false, ShareSearchWork = true,
            EfficientTactics = true, LearnBuffDuration = true, GuideWinningRoutes = true, OwnedWinningFocus = true
        };
        object[] Cleanup(LocalSearchRequest parent)
        {
            var commands = Enumerable.Range(0, 2).Select(i => LocalWire.Read<LocalSearchRequest>(Path.Combine(LaneRoot(i), "request.json"))).ToArray();
            var idles = Enumerable.Range(0, 2).Select(i => LocalWire.Read<LocalWorkerIdle>(Path.Combine(LaneRoot(i), "idle.json"))).ToArray();
            Check(LaneRoot(0) != LaneRoot(1), "Lane storage was shared");
            for (int i = 0; i < 2; i++)
            {
                var command = commands[i];
                Check(command.Id.StartsWith(parent.Id, StringComparison.Ordinal) && command.SnapshotId == parent.SnapshotId &&
                    command.NativeHash == parent.NativeHash && command.Partition == i && command.Partitions == 2,
                    "Wrong current request/partition in lane " + i);
                Check(idles[i].Matches(command, Generation(i)), "Missing current cleanup acknowledgment in lane " + i);
                Check(!idles[i].Matches(commands[1 - i], Generation(i)) && !idles[i].Matches(command, Generation(1 - i)),
                    "Other lane request/generation accepted cleanup");
            }
            return Enumerable.Range(0, 2).Select(i => (object)new
            {
                lane = i, request_id = commands[i].Id, partition = commands[i].Partition, partitions = commands[i].Partitions,
                cleanup_matches = true, other_lane_rejected = true, current_generation = idles[i].Generation,
                phase = commands[i].VerifyCandidate == null ? "search" : "independent_verification"
            }).ToArray();
        }
        void FreshBroker(LocalSearchRequest parent, string pipe)
        {
            Check(!string.IsNullOrWhiteSpace(pipe) && pipe != previousPipe && parent.Id != previousRequestId,
                "Search reused an earlier request/broker endpoint");
            previousPipe = pipe; previousRequestId = parent.Id;
        }
        async Task<LocalSearchResult> Goal(string stage, bool coldPeer = false)
        {
            var request = Command(true); var watch = Stopwatch.StartNew();
            var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
            watch.Stop(); Budget();
            LocalWire.Write(Path.Combine(root, stage + "-private-result.json"), result);
            var best = result.Best;
            bool passed = result.Status == "done" && result.StoppedEarly && result.Rejected == 0 && result.WorkerLimit == 2 &&
                result.Workers == 2 && LocalSearchPolicy.MeetsGoal(best, request) && best != null &&
                best.Actions.Length == best.Continuation?.Length && result.Timing?.Verifications == 1;
            bool peerUnready = !File.Exists(Path.Combine(LaneRoot(1), "ready"));
            object? cleanup = null;
            if (coldPeer)
            {
                Check(peerUnready && Child(1)?.HasExited == false, "Goal did not occur while the real peer was still preparing");
                Check(!File.Exists(Path.Combine(LaneRoot(1), "request.json")), "Unready peer received a search request");
            }
            else { Same(); cleanup = Cleanup(request); }
            var searched = LocalWire.Read<LocalSearchRequest>(Path.Combine(LaneRoot(coldPeer ? 0 : 1), "request.json"));
            // The selected lane's final verification disables the broker; use the other lane or trace.
            string? pipe = searched.TurnWorkPipe;
            if (string.IsNullOrWhiteSpace(pipe))
                pipe = Enumerable.Range(0, 2).Where(i => File.Exists(Path.Combine(LaneRoot(i), "request.json")))
                    .Select(i => LocalWire.Read<LocalSearchRequest>(Path.Combine(LaneRoot(i), "request.json")).TurnWorkPipe)
                    .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            // A cold-peer run has only the selected lane's verification file by now.
            if (!coldPeer) FreshBroker(request, pipe ?? "");
            Record(new { stage, wall_ms = watch.ElapsedMilliseconds, result.Status, result.StoppedEarly, result.Workers, result.WorkerLimit,
                result.Evaluated, result.Rejected, result.Timing, best?.Won, best?.StartingHp, best?.Hp, best?.HpLost,
                best?.NetHpLoss, best?.Rounds, damage_sources = best?.DamageSources,
                potion_uses = best?.Actions.Count(a => a.PotionSlot != null), verified_steps = best?.Continuation?.Length,
                unseeded = true, goal_passed = passed, peer_still_preparing = coldPeer && peerUnready,
                cleanup, identities = Identities() });
            Check(passed, "Strict two-worker goal failed: " + result.Message);
            return result;
        }
        async Task Cancel(string stage, bool verification, bool suspendPeer = false, bool staleStop = false)
        {
            var request = Command(verification);
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            long requestedAt = 0;
            var seen = new HashSet<int>();
            long staleSequence = 0;
            bool staleRejected = false;
            Identity? suspended = null;
            string? pipe = null;
            var triggers = new Dictionary<int, long>();
            void Progress(LocalProgress p)
            {
                lock (sync)
                {
                    Budget();
                    if (p.Id != request.Id || p.Events.Length == 0 || requestedAt != 0 ||
                        (verification ? p.Sequence < 2_000_000 : p.Sequence >= 2_000_000)) return;
                    triggers[p.Worker] = p.Sequence; seen.Add(p.Worker);
                    if (!verification && seen.Count != 2) return;
                    if (staleStop && staleSequence == 0)
                    {
                        staleSequence = p.Worker == 1 ? p.Sequence : triggers[1];
                        Check(previousRequestId != null && previousRequestId != request.Id, "No older request to inject");
                        LocalWire.Write(Path.Combine(LaneRoot(1), "stop-search.json"),
                            new LocalSearchStop(previousRequestId!, request.SnapshotId, request.NativeHash, Cancel: true));
                        return;
                    }
                    if (staleStop)
                    {
                        if (p.Worker != 1 || p.Sequence <= staleSequence) return;
                        staleRejected = true;
                    }
                    var command = LocalWire.Read<LocalSearchRequest>(Path.Combine(LaneRoot(verification ? p.Worker : 0), "request.json"));
                    pipe = command.TurnWorkPipe;
                    if (suspendPeer)
                    {
                        Check(expected != null && Observe(1) == expected[1], "Fault injection target is not this owned lane");
                        suspended = Observe(1);
                        int status = NtSuspendProcess(Child(1)!.Handle);
                        Check(status == 0, "Owned process suspension rejected, NTSTATUS=" + status);
                    }
                    requestedAt = Stopwatch.GetTimestamp(); cancel.Cancel();
                }
            }
            try
            {
                await Task.Run(() => pool.Analyze(request, installation, _ => { }, cancel.Token, Progress));
                throw new InvalidOperationException("Cancellation did not interrupt dispatched native work");
            }
            catch (OperationCanceledException) when (requestedAt != 0) { }
            Check(requestedAt != 0, "Timed out before observing the required real native progress");
            double latency = Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds;
            object? cleanup;
            if (suspendPeer)
            {
                Check(suspended != null && Child(1) == null, "Unresponsive lane was retained");
                Check(Observe(0) == expected![0], "Healthy peer was retired with the unresponsive lane");
                await witnesses[suspended!].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                using var unlocked = new FileStream(Path.Combine(LaneRoot(1), ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var command = LocalWire.Read<LocalSearchRequest>(Path.Combine(LaneRoot(0), "request.json"));
                var idle = LocalWire.Read<LocalWorkerIdle>(Path.Combine(LaneRoot(0), "idle.json"));
                Check(command.Id == request.Id && idle.Matches(command, Generation(0)), "Healthy peer did not acknowledge cleanup");
                File.Copy(Path.Combine(LaneRoot(1), "game.log"), Path.Combine(root, "unresponsive-peer-game.log"), true);
                cleanup = new { healthy_peer_acknowledged = true, only_faulted_peer_retired = true,
                    retired_pid = suspended!.Pid, retired_child_exited = true, retired_lock_released = true };
            }
            else { Same(); cleanup = Cleanup(request); }
            if (!verification) FreshBroker(request, pipe ?? "");
            Record(new { stage, cancellation_ms = latency, progress_sequences = triggers, cleanup,
                stale_stop_injected = staleStop, stale_stop_rejected_by_later_native_progress = staleRejected,
                fault_injection = suspendPeer ? "NtSuspendProcess on owned lane-1 handle only" : null,
                healthy_lane_pid = Observe(0).Pid, peer_pid = Child(1)?.Id });
        }

        try
        {
            // Automatic mode still starts one instance; this scenario specifically
            // verifies goal/cancellation while its second peer is being prepared.
            var cold = Stopwatch.StartNew(); await pool.Prepare(installation, 0, CancellationToken.None); cold.Stop();
            Observe(0); Check(Child(1) == null, "Prewarming created more than the first lane");
            Record(new { stage = "prewarm_first_only", prepare_ms = cold.ElapsedMilliseconds, pid = Observe(0).Pid, live_workers = 1 });
            await Goal("goal_while_real_peer_preparing", coldPeer: true);
            var before = Observe(1); var joining = Stopwatch.StartNew(); await Ensure(1); joining.Stop();
            Check(Observe(1) == before && File.Exists(Path.Combine(LaneRoot(1), "ready")), "Preparing peer was rebuilt after goal");
            expected = [Observe(0), Observe(1)];
            Record(new { stage = "join_retained_preparation", wait_ms = joining.ElapsedMilliseconds,
                unchanged_peer_identity = true, identities = Identities() });
            await Goal("two_ready_lanes_goal_and_cleanup");
            await Cancel("two_lane_cancel_and_stale_stop_isolation", false, staleStop: true);
            await Goal("two_lane_cancel_then_research");
            await Cancel("two_lane_verification_cancel", true);
            await Goal("two_lane_verification_cancel_then_research");
            await Cancel("unresponsive_peer_only_retired", false, suspendPeer: true);
            var rebuild = Stopwatch.StartNew(); await Ensure(1); rebuild.Stop();
            Check(Observe(0) == expected[0] && Observe(1) != expected[1] && observed[0].Count == 1 && observed[1].Count == 2,
                "Recovery rebuilt a healthy lane or failed to replace the bad lane");
            expected[1] = Observe(1);
            Record(new { stage = "rebuild_only_faulted_peer", prepare_ms = rebuild.ElapsedMilliseconds,
                launches_by_lane = observed.Select(s => s.Count).ToArray(), identities = Identities() });
            await Goal("two_lane_research_after_fault_recovery");
            pool.Dispose();
            foreach (var owned in witnesses.Values) await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            for (int i = 0; i < 2; i++)
                using (new FileStream(Path.Combine(LaneRoot(i), ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Record(new { stage = "dispose", all_owned_children_exited = witnesses.Values.All(p => p.HasExited),
                both_locks_released = true, max_live_owned_workers = maxLive, launches_by_lane = observed.Select(s => s.Count).ToArray(),
                healthy_lane_rebuilds = observed[0].Count - 1, faulted_lane_rebuilds = observed[1].Count - 1 });
            LocalWire.Write(Path.Combine(root, "native-summary.json"), new { passed = true, max_owned_workers = 2,
                admission = "fixed two-lane comparison; adaptive admissions remain in synthetic coverage",
                unseeded_goal = new { rounds = 6, net_loss = 0, potions = 0, known_enemy_damage = 0, independent_replay = true }, records });
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(root, "native-error.txt"), ex.ToString()); Console.Error.WriteLine(ex);
            LocalWire.Write(Path.Combine(root, "native-summary.json"), new { passed = false, error = ex.Message, records });
            return 1;
        }
        finally
        {
            pool.Dispose();
            foreach (var owned in witnesses.Values)
            {
                try { await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { owned.Dispose(); }
            }
        }
    }

    private sealed record Identity(int Lane, int Pid, DateTime Start, string Generation);
}
