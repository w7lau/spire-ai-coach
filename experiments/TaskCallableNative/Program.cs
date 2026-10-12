using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

if (args.Length != 5) throw new ArgumentException("root game mod-directories.json frozen-request baseline|fixed");
var root = Path.GetFullPath(args[0]);
if (!root.StartsWith(Path.GetFullPath("work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
    !File.Exists(Path.Combine(root, ".task-callable-owner"))) throw new InvalidOperationException("Not an owned callable probe");
bool baseline = args[4] == "baseline";
if (!baseline && args[4] != "fixed") throw new ArgumentException("Unknown case");
var captured = LocalWire.Read<LocalSearchRequest>(args[3]);
var directories = JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[2]))!;
var coach = Path.Combine(root, "coach");
Directory.CreateDirectory(coach);
File.Copy(typeof(LocalWorkerPool).Assembly.Location, Path.Combine(coach, "SpireAiCoach.dll"), true);
File.Copy(Path.Combine(AppContext.BaseDirectory, "SpireAiCoach.json"), Path.Combine(coach, "SpireAiCoach.json"), true);
var observer = Path.Combine(root, "observer");
Directory.CreateDirectory(observer);
var observerSource = Path.GetFullPath("experiments/LocalIntegration/bin/Release/net9.0");
File.Copy(Path.Combine(observerSource, "SpireLocalIntegration.dll"), Path.Combine(observer, "SpireLocalIntegration.dll"), true);
File.WriteAllText(Path.Combine(observer, "SpireLocalIntegration.json"), JsonSerializer.Serialize(new {
    id = "SpireLocalIntegration", name = "Isolated local integration test", author = "w7lau",
    description = "Synthetic capture, search and read-only integration verification", version = "0.0.1",
    has_dll = true, has_pck = false, affects_gameplay = false,
    dependencies = new[] { new { id = "SpireAiCoach", min_version = "0.4.0" } } }));
var observerModule = Assembly.LoadFrom(Path.Combine(observer, "SpireLocalIntegration.dll")).ManifestModule.ModuleVersionId;
var loaded = captured.LoadedMods.Where(m => !m.StartsWith("SpireAiCoach:", StringComparison.Ordinal))
    .Append($"SpireAiCoach:{typeof(LocalWorkerPool).Assembly.GetName().Version!.ToString(3)}:{typeof(LocalWorkerPool).Assembly.ManifestModule.ModuleVersionId}")
    .Append($"SpireLocalIntegration:0.0.1:{observerModule}").Order(StringComparer.Ordinal).ToArray();
directories = directories.Where(p => !Directory.EnumerateFiles(p, "*.json").Any(f =>
    JsonDocument.Parse(File.ReadAllText(f)).RootElement.TryGetProperty("id", out var id) && id.GetString() == "SpireAiCoach"))
    .Append(coach).Append(observer).ToArray();
System.Environment.SetEnvironmentVariable("SPIRE_LOCAL_INTEGRATION", root);
System.Environment.SetEnvironmentVariable("SPIRE_LOCAL_TASK_CALLABLE_PROBE", "1");
System.Environment.SetEnvironmentVariable("SPIRE_LOCAL_TASK_CALLABLE_BASELINE", baseline ? "1" : null);
var installation = new LocalInstallation(Path.GetFullPath(args[1]), directories);
using var pool = new LocalWorkerPool(Path.Combine(root, "pool"));
var samples = new List<object>();
foreach (var order in baseline ? new[] { captured.SearchOrder } : new[] { LocalSearchOrder.TurnFrontier, LocalSearchOrder.MonteCarlo })
{
    var request = captured with { Id = Guid.NewGuid().ToString("N"), LoadedMods = loaded,
        TimelineOrigin = LocalTimeline.Timestamp, InitialTrace = null, SearchOrder = order,
        Workers = captured.Workers, Partition = 0, Partitions = captured.Workers,
        MaxNodes = captured.MaxNodes, BudgetSeconds = captured.BudgetSeconds,
        InitialPlan = null, VerifyCandidate = null, RecordedReplayProbe = null,
        TurnWorkPipe = null, SearchWorkPipe = null, MinimumLossPipe = null, ProgressPipe = null,
        SkipFinalVerification = baseline ? captured.SkipFinalVerification : false, DeferVerification = true };
    var watch = Stopwatch.StartNew();
    Console.WriteLine(JsonSerializer.Serialize(new { phase = "search", algorithm = order.ToString(), request.Workers,
        request.MaxNodes, request.BudgetSeconds, independentFinalReplay = !request.SkipFinalVerification }));
    try
    {
        var result = await pool.Analyze(request, installation, _ => { }, CancellationToken.None);
        LocalWire.Write(Path.Combine(root, "private-" + order + ".json"), result);
        if (baseline) throw new InvalidOperationException("Baseline did not reproduce Task conversion failure");
        if (result.Status != "done" || result.Best is not { } best || (!best.Won && !best.Dead) ||
            result.RecoveredFailures is { Length: > 0 } || result.Timing?.Verifications != 1 ||
            best.Continuation?.Length != best.Actions.Length)
            throw new InvalidOperationException("Frozen native search or ordinary independent replay failed: " + result.Message);
        var sample = new { algorithm = order.ToString(), result.Evaluated, result.Victories, result.ElapsedMs,
            result.Workers, result.Timing.Verifications, checkpoints = best.Continuation.Length,
            best.StartingHp, best.Hp, best.MaxHp, best.HpLost, best.Won,
            recoveredFailures = result.RecoveredFailures?.Length ?? 0 };
        samples.Add(sample);
        Console.WriteLine(JsonSerializer.Serialize(sample));
    }
    catch (CoachException ex) when (baseline && ex.Category == "local_runtime" && ex.Message.Contains("System.Threading.Tasks.Task", StringComparison.Ordinal))
    {
        samples.Add(new { reproduced = true, category = ex.Category, message = ex.Message, elapsedMs = watch.ElapsedMilliseconds });
        Console.WriteLine("baseline_reproduced");
    }
}
var workers = (Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pool)!;
var observed = new List<string>();
for (int i = 0; i < captured.Workers; i++)
{
    var worker = workers.GetValue(i)!;
    var path = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
    var records = Path.Combine(path, "task-callable-observed.jsonl");
    if (File.Exists(records)) observed.AddRange(File.ReadAllLines(records).Select(s => JsonDocument.Parse(s).RootElement.GetProperty("method").GetString()!));
    if (!baseline && (!File.Exists(Path.Combine(path, "task-callable-component.json")) || File.Exists(Path.Combine(path, "task-callable-component-error.txt"))))
        throw new InvalidOperationException("Native Task/void callable component failed");
}
if (observed.Count == 0) throw new InvalidOperationException("Actual frozen incident never reached the audited VFX boundary");
LocalWire.Write(Path.Combine(root, "summary.json"), new { version = typeof(LocalWorkerPool).Assembly.GetName().Version!.ToString(3),
    baseline, samples, observedVfxMethods = observed.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count()) });
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, baseline, observedVfxMethods = observed.Distinct().ToArray() }));
