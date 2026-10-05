using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

if (args.Length != 5 && args.Length != 7) throw new ArgumentException("root, game, frozen-mods, workers, compact|native, optional frozen request and result");
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
File.Copy("SpireAiCoach.json", Path.Combine(privateCoach, "SpireAiCoach.json"), true);
mods = mods.Where(p => !Directory.EnumerateFiles(p, "*.json").Any(f =>
    JsonDocument.Parse(File.ReadAllText(f)).RootElement.TryGetProperty("id", out var id) && id.GetString() == "SpireAiCoach"))
    .Append(privateCoach).ToArray();
using var pool = new LocalWorkerPool(Path.Combine(root, "pool"));
var watch = Stopwatch.StartNew();
await pool.Prepare(new LocalInstallation(game, mods, LimitRuntimeThreads: args[4] == "compact"), count, CancellationToken.None);
var workers = (Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pool)!;
var records = Enumerable.Range(0, count).Select(index => {
    var worker = workers.GetValue(index)!;
    var path = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
    return JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "startup.json"))).RootElement.Clone();
}).ToArray();
var report = new { wall_ms = watch.Elapsed.TotalMilliseconds, records };
LocalWire.Write(Path.Combine(root, "summary.json"), report);
Console.WriteLine(JsonSerializer.Serialize(new { report.wall_ms, workers = count }));
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
                JsonSerializer.Serialize(best.Actions) != JsonSerializer.Serialize(expected.Actions) ||
                JsonSerializer.Serialize(best.Continuation) != JsonSerializer.Serialize(expected.Continuation) ||
                (best.Hp, best.HpLost, best.MaxHp, best.Gold, best.EnemyHp, best.Won, best.Dead, best.DamageSources, best.HealthChanges) !=
                (expected.Hp, expected.HpLost, expected.MaxHp, expected.Gold, expected.EnemyHp, expected.Won, expected.Dead, expected.DamageSources, expected.HealthChanges) ||
                (bool)worker.GetType().GetMethod("GameErrors")!.Invoke(worker, null)!)
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
