using System.Reflection;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

if (args.Length is < 4 or > 5 || args.Length == 5 && args[4] != "parallel") throw new ArgumentException("root game mod-directories.json frozen-request");
var root = Path.GetFullPath(args[0]);
if (!root.StartsWith(Path.GetFullPath("work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
    !File.Exists(Path.Combine(root, ".hotpath-owner"))) throw new InvalidOperationException("Not an owned hotpath probe");
var dllSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(LocalWorkerPool).Assembly.Location)));
if (dllSha != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes("dist/SpireAiCoach/SpireAiCoach.dll"))))
    throw new InvalidOperationException("Launcher DLL is not the prepared package");
var captured = LocalWire.Read<LocalSearchRequest>(args[3]);
var directories = JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[2]))!;
var coach = Path.Combine(root, "coach"); Directory.CreateDirectory(coach);
File.Copy(typeof(LocalWorkerPool).Assembly.Location, Path.Combine(coach, "SpireAiCoach.dll"), true);
File.Copy(Path.Combine(AppContext.BaseDirectory, "SpireAiCoach.json"), Path.Combine(coach, "SpireAiCoach.json"), true);
var loaded = captured.LoadedMods.Where(m => !m.StartsWith("SpireAiCoach:", StringComparison.Ordinal))
    .Append($"SpireAiCoach:{typeof(LocalWorkerPool).Assembly.GetName().Version!.ToString(3)}:{typeof(LocalWorkerPool).Assembly.ManifestModule.ModuleVersionId}")
    .Order(StringComparer.Ordinal).ToArray();
directories = directories.Where(p => !Directory.EnumerateFiles(p, "*.json").Any(f =>
    JsonDocument.Parse(File.ReadAllText(f)).RootElement.TryGetProperty("id", out var id) && id.GetString() == "SpireAiCoach"))
    .Append(coach).ToArray();
var installation = new LocalInstallation(Path.GetFullPath(args[1]), directories);
using var pool = new LocalWorkerPool(Path.Combine(root, "pool"));

if (args.Length == 5)
{
    await pool.Prepare(installation, 8, CancellationToken.None);
    var request = captured with { Id = Guid.NewGuid().ToString("N"), LoadedMods = loaded,
        TimelineOrigin = LocalTimeline.Timestamp, InitialTrace = null, SearchOrder = LocalSearchOrder.TurnFrontier,
        Workers = 8, AdaptiveWorkers = false, Partition = 0, Partitions = 8,
        MaxNodes = 16, BudgetSeconds = 60, ContinueOptimization = false,
        InitialPlan = null, VerifyCandidate = null, RecordedReplayProbe = null,
        TurnWorkPipe = null, SearchWorkPipe = null, MinimumLossPipe = null, ProgressPipe = null,
        StopOnFirstWin = false, StopOnZeroLoss = false, CardGoals = null,
        TargetVictoryRounds = null, TargetPotionUses = null, RequireKnownZeroEnemyDamage = false,
        SkipFinalVerification = false, FastVerification = false, DeferVerification = true };
    var result = await pool.Analyze(request, installation, _ => { }, CancellationToken.None);
    LocalWire.Write(Path.Combine(root, "parallel-private.json"), result);
    if (result.Status != "done" || result.Evaluated != 128 || result.Workers != 8 ||
        result.RecoveredFailures is { Length: > 0 } || result.Timing?.Verifications != 1 ||
        result.Best is not { } best || best.Continuation?.Length != best.Actions.Length)
        throw new InvalidOperationException("Eight-owner compact native work failed: " + result.Message);
    var output = new { completed = true, dll_sha256 = dllSha, result.Workers, result.Evaluated, result.Victories,
        result.ElapsedMs, result.Timing, result.TurnSearch, checkpoints = best.Continuation.Length,
        ordinaryFinalReplayPassed = true, recoveredFailures = 0,
        scope = "Eight owned native workers and compact exact-history broker; correctness check, not comparative speed" };
    LocalWire.Write(Path.Combine(root, "parallel-summary.json"), output);
    Console.WriteLine(JsonSerializer.Serialize(new { output.completed, output.Workers, output.Evaluated,
        output.Victories, output.ElapsedMs, output.checkpoints }));
    return;
}

await pool.Prepare(installation, 1, CancellationToken.None);
var samples = new List<object>();
foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
{
    LocalCandidate? expected = null; int index = 0;
    // Both modes warmed, then paired in both orders in the same native process.
    foreach (bool fast in new[] { false, true, false, true, true, false })
    {
        var request = captured with { Id = Guid.NewGuid().ToString("N"), LoadedMods = loaded,
            TimelineOrigin = LocalTimeline.Timestamp, InitialTrace = null, SearchOrder = order,
            Workers = 1, AdaptiveWorkers = false, Partition = 0, Partitions = 1,
            MaxNodes = 32, BudgetSeconds = 120, ContinueOptimization = false,
            InitialPlan = null, VerifyCandidate = null, RecordedReplayProbe = null,
            TurnWorkPipe = null, SearchWorkPipe = null, MinimumLossPipe = null, ProgressPipe = null,
            CoalesceTurnOffers = true, ThrottleUnchangedResults = true, CompactTurnOffers = fast,
            StopOnFirstWin = false, StopOnZeroLoss = false, CardGoals = null,
            TargetVictoryRounds = null, TargetPotionUses = null, RequireKnownZeroEnemyDamage = false,
            SkipFinalVerification = false, FastVerification = false, DeferVerification = true };
        Console.WriteLine(JsonSerializer.Serialize(new { phase = "search", algorithm = order.ToString(), fast, index }));
        var result = await pool.Analyze(request, installation, _ => { }, CancellationToken.None);
        LocalWire.Write(Path.Combine(root, $"private-{order}-{index}.json"), result);
        if (result.Status != "done" || result.Best is not { } best || result.Evaluated != 32 ||
            result.Rejected != 0 || result.RecoveredFailures is { Length: > 0 } || result.Timing?.Verifications != 1 ||
            best.Continuation?.Length != best.Actions.Length)
            throw new InvalidOperationException("Native search or ordinary independent replay failed: " + result.Message);
        expected ??= best;
        if (LocalTurnSearch.HistoryKey(best.Actions) != LocalTurnSearch.HistoryKey(expected.Actions) ||
            !best.Continuation.SequenceEqual(expected.Continuation!) ||
            (best.Hp, best.HpLost, best.MaxHp, best.Gold, best.EnemyHp, best.Won, best.Dead, best.DamageSources) !=
            (expected.Hp, expected.HpLost, expected.MaxHp, expected.Gold, expected.EnemyHp, expected.Won, expected.Dead, expected.DamageSources))
            throw new InvalidOperationException("Identical native workload changed route, checkpoints or settlement");
        var publish = result.Trace!.Methods!.Where(m => m.Method == "LocalWorker.Publish").ToArray();
        var sample = new { algorithm = order.ToString(), fast, warm = index < 2, result.ElapsedMs,
            result.Evaluated, result.Victories, turn = result.TurnSearch == null ? null : new {
                result.TurnSearch.Offered, result.TurnSearch.DuplicateOffers, result.TurnSearch.Probes }, result.Timing,
            publish_ms = publish.Sum(m => m.TotalMs), writes = publish.Sum(m => m.Calls - m.Skipped),
            skipped_writes = publish.Sum(m => m.Skipped), checkpoints = best.Continuation.Length,
            native_history_rng_state_settlement_match = true, ordinary_replay_passed = true };
        samples.Add(sample); Console.WriteLine(JsonSerializer.Serialize(sample)); index++;
        LocalWire.Write(Path.Combine(root, "summary.json"), new { completed = false, samples });
    }
}
// Exercise the actual broker with a repeated stream of native-observed choices.
// This measures transport/identity work only, separately from native game timing.
var native = LocalWire.Read<LocalSearchResult>(Path.Combine(root, "private-TurnFrontier-5.json")).Best!;
var offers = native.Decisions!.SelectMany(d => LocalTurnSearch.Alternatives(native.Actions, d,
    new LocalTurnHint(native.StartingHp ?? native.Hp, native.Hp, native.InitialEnemyHp ?? 1, native.EnemyHp))).ToArray();
var transport = new List<object>(); string[]? expectedTasks = null; int transportIndex = 0;
foreach (bool fast in new[] { false, true, true, false })
{
    var request = captured with { Id = Guid.NewGuid().ToString("N"), CoalesceTurnOffers = true, CompactTurnOffers = fast };
    using var broker = new LocalTurnWork(request, 1);
    using var client = new LocalTurnWorkClient(request with { TurnWorkPipe = broker.PipeName });
    var watch = System.Diagnostics.Stopwatch.StartNew();
    for (int repeat = 0; repeat < 32; repeat++)
        foreach (var decision in native.Decisions!) client.OfferAlternatives(native.Actions, decision,
            new LocalTurnHint(native.StartingHp ?? native.Hp, native.Hp, native.InitialEnemyHp ?? 1, native.EnemyHp));
    var tasks = new List<string>();
    while (client.TryTake(out var task)) { tasks.Add(LocalTurnSearch.HistoryKey(task.Prefix)); client.Finish(task); }
    watch.Stop(); expectedTasks ??= tasks.ToArray();
    if (!tasks.SequenceEqual(expectedTasks)) throw new InvalidOperationException("Proposal coalescing changed native-observed task order");
    var sample = new { fast, index = transportIndex++, input_offers = offers.Length * 32, broker.Offered,
        broker.DuplicateOffers, client.TransmittedOffers, client.LocallySuppressedOffers, elapsed_ms = watch.Elapsed.TotalMilliseconds,
        exact_task_order_matches = true, native_execution_benchmark = false };
    transport.Add(sample); Console.WriteLine(JsonSerializer.Serialize(sample));
}
var batches = native.Decisions!.Select(d => LocalTurnSearch.AlternativeBatch(native.Actions, d,
    new LocalTurnHint(native.StartingHp ?? native.Hp, native.Hp, native.InitialEnemyHp ?? 1, native.EnemyHp))).Where(b => b != null).ToArray();
var payload = new { ordinaryBytes = JsonSerializer.SerializeToUtf8Bytes(offers).Length,
    compactBytes = JsonSerializer.SerializeToUtf8Bytes(batches).Length, offers = offers.Length, batches = batches.Length };
LocalWire.Write(Path.Combine(root, "summary.json"), new { completed = true, dll_sha256 = dllSha, samples, transport, payload,
    scope = "Same DLL and frozen input, one resident native worker, 32 complete attempts per mode; both orders warmed and interleaved. Not an eight-worker or all-combat speed claim." });
