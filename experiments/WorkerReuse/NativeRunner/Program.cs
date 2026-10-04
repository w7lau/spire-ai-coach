using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

if (args.Length == 2 && args[1] == "--two-workers") return await TwoWorkerAcceptance.Run(args[0]);
if (args.Length != 1) throw new ArgumentException("Pass an owned native acceptance directory and optional --two-workers");
var root = Path.GetFullPath(args[0]);
if (!root.StartsWith(Path.GetFullPath("work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
    !File.Exists(Path.Combine(root, ".native-worker-reuse-owner")))
    throw new InvalidOperationException("Native runner requires its new private workspace");
var game = Path.Combine(root, "game");
var frozen = LocalWire.Read<LocalSearchRequest>(Path.Combine(root, "frozen-request.json"));
if (frozen.InitialPlan is { Length: > 0 } || frozen.VerifyCandidate != null || frozen.RecordedReplayProbe != null)
    throw new InvalidOperationException("Native acceptance rejects answer seeds");
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, "mods", "SpireAiCoach", "SpireAiCoach.json")));
var version = manifest.RootElement.GetProperty("version").GetString();
var loaded = frozen.LoadedMods.Select(m => m.StartsWith("SpireAiCoach:", StringComparison.Ordinal)
    ? $"SpireAiCoach:{version}:{typeof(LocalWorkerPool).Assembly.ManifestModule.ModuleVersionId}" : m).Order(StringComparer.Ordinal).ToArray();
var installation = new LocalInstallation(game, Directory.GetDirectories(Path.Combine(game, "mods")), MinimalWorkerBootstrap: false);
var capture = frozen with { LoadedMods = loaded, InitialTrace = null, TimelineOrigin = 0 };
using var pool = new LocalWorkerPool(Path.Combine(root, "native-pool"));
var workers = (Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
var records = new List<object>();
var observedIdentities = new HashSet<(int Pid, DateTime Start, string Generation)>();
Process? exitWitness = null;
object Worker() => workers.GetValue(0)!;
Process? ProcessOf(object worker) => (Process?)worker.GetType().GetProperty("Process")!.GetValue(worker);
string WorkerRoot() => (string)Worker().GetType().GetProperty("Root")!.GetValue(Worker())!;
string Generation() => (string)Worker().GetType().GetProperty("Generation")!.GetValue(Worker())!;
object Identity()
{
    var p = ProcessOf(Worker()) ?? throw new InvalidOperationException("Owned worker has exited");
    p.Refresh();
    if (workers.Cast<object>().Count(w => ProcessOf(w)?.HasExited == false) != 1)
        throw new InvalidOperationException("Native acceptance exceeded one owned calculation process");
    observedIdentities.Add((p.Id, p.StartTime.ToUniversalTime(), Generation()));
    if (observedIdentities.Count != 1)
        throw new InvalidOperationException("Native acceptance observed a replacement process");
    return new { pid = p.Id, started_utc = p.StartTime.ToUniversalTime(), generation = Generation(), private_memory_bytes = p.PrivateMemorySize64 };
}
void Record(object record)
{
    records.Add(record);
    LocalWire.Write(Path.Combine(root, "native-progress-summary.json"), records);
    Console.WriteLine(JsonSerializer.Serialize(record));
}
LocalSearchRequest Command(bool goal) => LocalCalculation.Configure(capture, LocalSearchOrder.TurnFrontier, 1,
    capture.IncludePotions, goal, targetVictoryRounds: goal ? 6 : 0) with
{
    Id = Guid.NewGuid().ToString("N"), TimelineOrigin = 0, InitialTrace = null,
    Partition = 0, Partitions = 1, AdaptiveWorkers = true, ShareSearchWork = true,
    EfficientTactics = true, LearnBuffDuration = true, GuideWinningRoutes = true, OwnedWinningFocus = true
};
int? expectedPid = null;
void SameProcess()
{
    if (ProcessOf(Worker())?.Id != expectedPid) throw new InvalidOperationException("Healthy native operation rebuilt its worker");
}
async Task Goal(string stage)
{
    var request = Command(true); var watch = Stopwatch.StartNew();
    var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
    watch.Stop();
    LocalWire.Write(Path.Combine(root, stage + "-private-result.json"), result);
    var best = result.Best;
    bool goal = result.Status == "done" && result.StoppedEarly && result.Rejected == 0 && best != null &&
        LocalSearchPolicy.MeetsGoal(best, request) && best.Actions.Length == best.Continuation?.Length && result.Timing?.Verifications == 1;
    Record(new { stage, wall_ms = watch.ElapsedMilliseconds, result.Status, result.Evaluated, result.Rejected, result.StoppedEarly,
        result.Workers, result.WorkerLimit, result.ElapsedMs, result.Timing, best?.Won, best?.StartingHp, best?.Hp, best?.HpLost,
        best?.NetHpLoss, best?.Rounds, damage_sources = best?.DamageSources, potion_uses = best?.Actions.Count(a => a.PotionSlot != null),
        verified_steps = best?.Continuation?.Length, unseeded = true, goal_passed = goal, identity = Identity() });
    if (!goal) throw new InvalidOperationException("Native six-round goal/independent replay failed: " + result.Message);
    SameProcess();
}
async Task Cancel(string stage, bool verification)
{
    var request = Command(verification);
    using var cancellation = new CancellationTokenSource();
    long requestedAt = 0; LocalProgress? trigger = null;
    void Observe(LocalProgress progress)
    {
        if (progress.Id == request.Id && progress.Events.Length > 0 &&
            (verification ? progress.Sequence >= 2_000_000 : progress.Sequence < 2_000_000) && requestedAt == 0)
        {
            trigger = progress; requestedAt = Stopwatch.GetTimestamp(); cancellation.Cancel();
        }
    }
    try
    {
        await Task.Run(() => pool.Analyze(request, installation, _ => { }, cancellation.Token, Observe));
        throw new InvalidOperationException("Native cancellation did not interrupt a dispatched request");
    }
    catch (OperationCanceledException) when (requestedAt != 0) { }
    SameProcess();
    var idle = LocalWire.Read<LocalWorkerIdle>(Path.Combine(WorkerRoot(), "idle.json"));
    bool matched = idle.Generation == Generation() && idle.Id.StartsWith(request.Id, StringComparison.Ordinal) &&
        idle.SnapshotId == request.SnapshotId && idle.NativeHash == request.NativeHash;
    Record(new { stage, cancellation_ms = Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds,
        trigger_sequence = trigger?.Sequence, trigger_events = trigger?.Events.Length,
        cleanup_acknowledged = matched, identity = Identity() });
    if (!matched) throw new InvalidOperationException("Cancelled native request did not acknowledge current cleanup");
}
try
{
    var cold = Stopwatch.StartNew(); await pool.Prepare(installation, 1, CancellationToken.None); cold.Stop();
    expectedPid = ProcessOf(Worker())!.Id;
    // This handle observes only the child just launched by this pool, never enumeration.
    exitWitness = System.Diagnostics.Process.GetProcessById(expectedPid.Value);
    Record(new { stage = "cold_prepare", prepare_ms = cold.ElapsedMilliseconds, identity = Identity() });
    var hot = Stopwatch.StartNew(); await pool.Prepare(installation, 1, CancellationToken.None); hot.Stop(); SameProcess();
    Record(new { stage = "hot_prepare", prepare_ms = hot.ElapsedMilliseconds, identity = Identity() });
    await Goal("first_unseeded_six_round_goal");
    await Goal("goal_then_research");
    await Cancel("cancel_search", false);
    await Goal("cancel_then_research");
    await Cancel("cancel_verification", true);
    await Goal("verification_cancel_then_research");
    pool.Dispose();
    await exitWitness.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    using var unlocked = new FileStream(Path.Combine(WorkerRoot(), ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    Record(new { stage = "dispose", owned_child_exited = exitWitness.HasExited, owned_lock_released = true,
        observed_launches = observedIdentities.Count, observed_rebuilds = observedIdentities.Count - 1 });
    LocalWire.Write(Path.Combine(root, "native-summary.json"), new { passed = true, max_owned_workers = 1,
        unseeded_goal = new { rounds = 6, net_loss = 0, potions = 0, known_enemy_damage = 0, independent_replay = true }, records });
    return 0;
}
catch (Exception ex)
{
    File.WriteAllText(Path.Combine(root, "native-error.txt"), ex.ToString());
    Console.Error.WriteLine(ex);
    LocalWire.Write(Path.Combine(root, "native-summary.json"), new { passed = false, error = ex.Message, records });
    return 1;
}
finally { pool.Dispose(); exitWitness?.Dispose(); }
