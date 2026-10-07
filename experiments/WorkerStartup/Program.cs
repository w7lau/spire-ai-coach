using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

if (args.Length is not (5 or 7 or 8) || args.Length == 8 && args[7] != "first-route")
    throw new ArgumentException("root, game, frozen-mods, workers, compact|native, optional frozen request and result, optional first-route");
if (args[4] is not ("compact" or "native")) throw new ArgumentException("Choose compact or native runtime settings");
var root = Path.GetFullPath(args[0]);
if (!root.StartsWith(Path.GetFullPath("work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
    !File.Exists(Path.Combine(root, ".startup-probe-owner"))) throw new InvalidOperationException("Not an owned startup probe");
var game = Path.GetFullPath(args[1]);
int count = int.Parse(args[3]);
if (count < 1 || count > 8) throw new ArgumentException("Bounded startup probe");
var mods = Directory.GetDirectories(Path.GetFullPath(args[2]));
var privateCoach = Path.Combine(root, "coach");
Directory.CreateDirectory(privateCoach);
File.Copy(typeof(LocalWorkerPool).Assembly.Location, Path.Combine(privateCoach, "SpireAiCoach.dll"), true);
File.Copy(Path.Combine(AppContext.BaseDirectory, "SpireAiCoach.json"), Path.Combine(privateCoach, "SpireAiCoach.json"), true);
mods = mods.Where(p => !Directory.EnumerateFiles(p, "*.json").Any(f =>
    JsonDocument.Parse(File.ReadAllText(f)).RootElement.TryGetProperty("id", out var id) && id.GetString() == "SpireAiCoach"))
    .Append(privateCoach).ToArray();
using var pool = new LocalWorkerPool(Path.Combine(root, "pool"));
var watch = Stopwatch.StartNew();
var workers = (Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pool)!;
var installation = new LocalInstallation(game, mods, LimitRuntimeThreads: args[4] == "compact");
var preparing = pool.Prepare(installation, count, CancellationToken.None);
Task<LocalSearchResult>? firstSearch = null;
double? firstVerifiedRouteMs = null;
if (args.Length == 8)
{
    var captured = LocalWire.Read<LocalSearchRequest>(args[5]);
    var expected = LocalWire.Read<LocalSearchResult>(args[6]).Best ?? throw new InvalidDataException("Missing frozen native result");
    if (!expected.Won || expected.Actions[0].BeforeHash != captured.NativeHash || expected.Continuation?.Length != expected.Actions.Length)
        throw new InvalidDataException("Frozen victory does not match its native start");
    string version = typeof(LocalWorkerPool).Assembly.GetName().Version!.ToString(3);
    var loaded = captured.LoadedMods.Select(m => m.StartsWith("SpireAiCoach:", StringComparison.Ordinal)
        ? $"SpireAiCoach:{version}:{typeof(LocalWorkerPool).Assembly.ManifestModule.ModuleVersionId}" : m).Order(StringComparer.Ordinal).ToArray();
    var request = captured with { Id = Guid.NewGuid().ToString("N"), LoadedMods = loaded,
        TimelineOrigin = LocalTimeline.Timestamp, InitialTrace = null, MaxNodes = 1, MaxDepth = expected.Actions.Length,
        MaxRounds = expected.Actions[^1].Round, Workers = count, Partition = 0, Partitions = count,
        InitialPlan = expected.Actions, VerifyCandidate = null, RecordedReplayProbe = null, ShareSearchWork = false,
        SearchWorkPipe = null, TurnWorkPipe = null, MinimumLossPipe = null, ProgressPipe = null,
        StopOnZeroLoss = false, StopOnFirstWin = true, DeferVerification = true, SkipFinalVerification = false,
        DataOnlyCombat = true, DataOnlyRun = true, NumericalExecution = true, TrimWorkerOverhead = true,
        SearchOrder = LocalSearchOrder.MonteCarlo };
    firstSearch = Task.Run(async () => {
        var result = await pool.Analyze(request, installation, _ => { }, CancellationToken.None);
        firstVerifiedRouteMs = watch.Elapsed.TotalMilliseconds;
        LocalWire.Write(Path.Combine(root, "first-route-private.json"), result);
        var best = result.Best ?? throw new InvalidDataException(result.Message);
        if (result.Status != "done" || !result.StoppedOnFirstWin || result.Timing?.Verifications != 1 ||
            NativeActions(best.Actions) != NativeActions(expected.Actions) ||
            JsonSerializer.Serialize(best.Continuation) != JsonSerializer.Serialize(expected.Continuation) ||
            (best.Hp, best.HpLost, best.MaxHp, best.Gold, best.EnemyHp, best.Won, best.Dead) !=
            (expected.Hp, expected.HpLost, expected.MaxHp, expected.Gold, expected.EnemyHp, expected.Won, expected.Dead) ||
            JsonSerializer.Serialize(best.DamageSources) != JsonSerializer.Serialize(expected.DamageSources) ||
            JsonSerializer.Serialize(best.HealthChanges) != JsonSerializer.Serialize(expected.HealthChanges))
            throw new InvalidOperationException("First verified native victory diverged");
        Console.WriteLine(JsonSerializer.Serialize(new { first_verified_route_ms = firstVerifiedRouteMs,
            steps = best.Actions.Length, result.Workers, result.Timing.Verifications }));
        return result;
    });
}
double? firstReadyMs = null;
var readySamples = new List<object>();
int previousReady = -1;
while (!preparing.IsCompleted)
{
    var resources = pool.Resources();
    if (resources.Ready != previousReady)
    { readySamples.Add(new { elapsed_ms = watch.Elapsed.TotalMilliseconds, resources.Ready, resources.Preparing }); previousReady = resources.Ready; }
    var first = workers.GetValue(0)!;
    var state = ((bool Ready, bool Preparing, int Starts, long Changed, string Detail))first.GetType()
        .GetMethod("ResourceState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(first, null)!;
    if (state.Ready) firstReadyMs ??= watch.Elapsed.TotalMilliseconds;
    await Task.Delay(25);
}
await preparing;
firstReadyMs ??= watch.Elapsed.TotalMilliseconds;
double allReadyMs = watch.Elapsed.TotalMilliseconds;
if (firstSearch != null) await firstSearch;
var records = Enumerable.Range(0, count).Select(index => {
    var worker = workers.GetValue(index)!;
    var path = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
    return JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "startup.json"))).RootElement.Clone();
}).ToArray();
double? hotPrepareMs = null;
bool? reused = null;
if (firstSearch == null)
{
var identities = Enumerable.Range(0, count).Select(index => {
    var worker = workers.GetValue(index)!;
    var process = (Process)worker.GetType().GetProperty("Process")!.GetValue(worker)!;
    return (process.Id, Generation: (string)worker.GetType().GetProperty("Generation")!.GetValue(worker)!);
}).ToArray();
var hot = Stopwatch.StartNew();
await pool.Prepare(installation, count, CancellationToken.None);
hot.Stop();
reused = identities.SequenceEqual(Enumerable.Range(0, count).Select(index => {
    var worker = workers.GetValue(index)!;
    return (((Process)worker.GetType().GetProperty("Process")!.GetValue(worker)!).Id,
        (string)worker.GetType().GetProperty("Generation")!.GetValue(worker)!);
}));
if (reused != true || pool.Resources() is not { Preparing: 0 } || pool.Resources().Ready != count)
    throw new InvalidOperationException("Hot preparation replaced a ready instance or changed its generation");
hotPrepareMs = hot.Elapsed.TotalMilliseconds;
}
var report = new { wall_ms = allReadyMs, first_worker_ready_ms = firstReadyMs, first_verified_route_ms = firstVerifiedRouteMs,
    hot_prepare_ms = hotPrepareMs,
    reused_all_processes_and_generations = reused, readySamples, records };
LocalWire.Write(Path.Combine(root, "summary.json"), report);
Console.WriteLine(JsonSerializer.Serialize(new { report.wall_ms, report.first_worker_ready_ms, report.hot_prepare_ms, workers = count }));
if (args.Length == 7)
{
    var captured = LocalWire.Read<LocalSearchRequest>(args[5]);
    var expected = LocalWire.Read<LocalSearchResult>(args[6]).Best ?? throw new InvalidDataException("Missing frozen native result");
    if (expected.Actions[0].BeforeHash != captured.NativeHash || expected.Continuation?.Length != expected.Actions.Length)
        throw new InvalidDataException("Frozen route does not match its native start");
    string version = typeof(LocalWorkerPool).Assembly.GetName().Version!.ToString(3);
    var loaded = captured.LoadedMods.Select(m => m.StartsWith("SpireAiCoach:", StringComparison.Ordinal)
        ? $"SpireAiCoach:{version}:{typeof(LocalWorkerPool).Assembly.ManifestModule.ModuleVersionId}" : m).Order(StringComparer.Ordinal).ToArray();
    var samples = await Task.WhenAll(Enumerable.Range(0, count).Select(async index => {
        var worker = workers.GetValue(index)!;
        var path = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        var request = captured with { Id = Guid.NewGuid().ToString("N"), LoadedMods = loaded,
            TimelineOrigin = LocalTimeline.Timestamp, InitialTrace = null, MaxNodes = 1, MaxDepth = expected.Actions.Length,
            MaxRounds = expected.Actions[^1].Round, Workers = 1, Partition = 0, Partitions = 1,
            InitialPlan = expected.Actions, VerifyCandidate = null, RecordedReplayProbe = null, ShareSearchWork = false,
            SearchWorkPipe = null, TurnWorkPipe = null, MinimumLossPipe = null, ProgressPipe = null,
            StopOnZeroLoss = false, StopOnFirstWin = false, DeferVerification = true, SkipFinalVerification = true,
            DataOnlyCombat = true, DataOnlyRun = true, NumericalExecution = true, TrimWorkerOverhead = true,
            SearchOrder = index % 2 == 0 ? LocalSearchOrder.MonteCarlo : LocalSearchOrder.TurnFrontier };
        LocalWire.Write(Path.Combine(path, "request.json"), request);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < 90)
        {
            await Task.Delay(100);
            if (!File.Exists(Path.Combine(path, "result.json"))) continue;
            var result = LocalWire.Read<LocalSearchResult>(Path.Combine(path, "result.json"));
            if (result.Id != request.Id || result.Status == "running") continue;
            if (result.Status is "failed" or "unsupported" or "partial") throw new InvalidDataException(result.Message);
            if (!File.Exists(Path.Combine(path, "idle.json")) || LocalWire.Read<LocalWorkerIdle>(Path.Combine(path, "idle.json")).Id != request.Id) continue;
            var best = result.Best ?? throw new InvalidDataException(result.Message);
            if (result.Status != "searched" || result.Evaluated != 1 || result.Rejected != 0 ||
                NativeActions(best.Actions) != NativeActions(expected.Actions) ||
                JsonSerializer.Serialize(best.Continuation) != JsonSerializer.Serialize(expected.Continuation) ||
                (best.Hp, best.HpLost, best.MaxHp, best.Gold, best.EnemyHp, best.Won, best.Dead, best.DamageSources, best.HealthChanges) !=
                (expected.Hp, expected.HpLost, expected.MaxHp, expected.Gold, expected.EnemyHp, expected.Won, expected.Dead, expected.DamageSources, expected.HealthChanges) ||
                worker.GetType().GetMethod("RuntimeFailure")!.Invoke(worker, [index, "search"]) != null)
                throw new InvalidOperationException("Native route/state/RNG/history/health diverged");
            LocalWire.Write(Path.Combine(root, $"route-{index}-private.json"), result);
            return new { index, order = request.SearchOrder.ToString(), result.ElapsedMs, steps = best.Actions.Length,
                identicalNativeStateRngHistoryAndHealth = true };
        }
        throw new TimeoutException("Frozen native route did not complete");
    }));
    LocalWire.Write(Path.Combine(root, "native-summary.json"), new { passed = true, samples });
    Console.WriteLine(JsonSerializer.Serialize(new { nativeRoutesPassed = samples.Length, steps = expected.Actions.Length }));
}

// Preference is an algorithm ranking hint, including inside an exact offer.
// Every executable action/selection field and every native checkpoint remains exact.
static string NativeActions(LocalAction[] actions) => JsonSerializer.Serialize(actions.Select(action => action with
{
    Preference = 0,
    Choices = action.Choices?.Select(choice => choice with { Preference = 0 }).ToArray()
}));
