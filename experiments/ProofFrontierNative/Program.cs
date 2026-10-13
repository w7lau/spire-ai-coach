using System.Reflection;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

if (args.Length != 4) throw new ArgumentException("root game mod-directories.json frozen-request");
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
var allowed = captured.LoadedMods.Select(m => m.Split(':')[0]).ToHashSet(StringComparer.Ordinal);
directories = directories.Where(p => Directory.EnumerateFiles(p, "*.json").Any(f =>
    JsonDocument.Parse(File.ReadAllText(f)).RootElement.TryGetProperty("id", out var id) && allowed.Contains(id.GetString()!))).ToArray();
var installation = new LocalInstallation(Path.GetFullPath(args[1]), directories);
using var pool = new LocalWorkerPool(Path.Combine(root, "pool"));

await pool.Prepare(installation, 8, CancellationToken.None);
var samples = new List<object>();
for (int run = 0; run < 2; run++)
{
    var request = captured with { Id = Guid.NewGuid().ToString("N"), LoadedMods = loaded,
        TimelineOrigin = LocalTimeline.Timestamp, InitialTrace = null,
        Workers = 8, AdaptiveWorkers = false, Partition = 0, Partitions = 8,
        ContinueOptimization = false, InitialPlan = null, VerifyCandidate = null, RecordedReplayProbe = null,
        TurnWorkPipe = null, SearchWorkPipe = null, MinimumLossPipe = null, ProgressPipe = null,
        SkipFinalVerification = false, FastVerification = false, DeferVerification = true };
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var result = await pool.Analyze(request, installation, _ => { }, CancellationToken.None);
    LocalWire.Write(Path.Combine(root, $"private-{run}.json"), result);
    if (result.Status != "done" || result.Best is not { Won: true, Dead: false } best ||
        result.RecoveredFailures is { Length: > 0 } || result.Timing?.Verifications != 1 ||
        !LocalSearchPolicy.HasExecutionPoints(result) ||
        result.StoppedOnMinimum && !LocalSearchPolicy.HasMinimumProof(result))
        throw new InvalidOperationException("Native proof search or independent replay failed: " + result.Message);
    var sample = new { run, result.ElapsedMs, wallMs = clock.Elapsed.TotalMilliseconds,
        result.Evaluated, result.Victories, best.StartingHp, best.Hp, best.NetHpLoss,
        result.StoppedOnMinimum, result.StoppedOnHealthTarget, result.MinimumLoss,
        result.HealthBounds, result.TurnSearch, checkpoints = best.Continuation!.Length,
        ordinaryFinalReplayPassed = true };
    samples.Add(sample);
    Console.WriteLine(JsonSerializer.Serialize(new { run, result.ElapsedMs, result.Evaluated,
        best.NetHpLoss, result.StoppedOnMinimum, result.MinimumLoss?.Certificate }));
    LocalWire.Write(Path.Combine(root, "summary.json"), new { completed = run == 1, dll_sha256 = dllSha, samples });
}
