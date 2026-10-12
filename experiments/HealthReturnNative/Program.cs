using System.Reflection;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

if (args.Length != 5) throw new ArgumentException("root game mod-directories.json frozen-request seed-result");
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
await pool.Prepare(installation, 1, CancellationToken.None);

var seed = LocalWire.Read<LocalSearchResult>(args[4]).Best!;
if (seed.HpChange != 0 || seed.Hp >= seed.MaxHp || !seed.Won) throw new InvalidDataException("Need a frozen non-full zero-loss native win");
var samples = new List<object>();
foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
foreach (bool skip in new[] { false, true })
{
    var request = captured with { Id = Guid.NewGuid().ToString("N"), LoadedMods = loaded,
        TimelineOrigin = LocalTimeline.Timestamp, InitialTrace = null, SearchOrder = order,
        Workers = 1, AdaptiveWorkers = false, Partition = 0, Partitions = 1,
        MaxNodes = 4, BudgetSeconds = 20, ContinueOptimization = false,
        InitialPlan = seed.Actions, VerifyCandidate = null, RecordedReplayProbe = null,
        TurnWorkPipe = null, SearchWorkPipe = null, MinimumLossPipe = null, ProgressPipe = null,
        StopOnFirstWin = false, StopOnZeroLoss = true, CardGoals = null,
        TargetVictoryRounds = null, TargetPotionUses = null, RequireKnownZeroEnemyDamage = false,
        SkipFinalVerification = skip, FastVerification = false, DeferVerification = true };
    var result = await pool.Analyze(request, installation, _ => { }, CancellationToken.None);
    LocalWire.Write(Path.Combine(root, $"private-{order}-{skip}.json"), result);
    if (result.Status != "done" || result.Evaluated != 1 || !result.StoppedOnHealthTarget || result.StoppedOnMinimum ||
        result.Best is not { Won: true, Dead: false } best || best.Hp != seed.Hp || best.MaxHp != seed.MaxHp ||
        result.HealthTarget is not { FullHealth: false } target || target.TargetHp != seed.Hp ||
        !LocalSearchPolicy.CanStopAtHealthTarget(best, request, target) || !LocalSearchPolicy.HasExecutionPoints(result) ||
        !best.Continuation!.SequenceEqual(seed.Continuation!) ||
        LocalTurnSearch.HistoryKey(best.Actions) != LocalTurnSearch.HistoryKey(seed.Actions) ||
        result.RecoveredFailures is { Length: > 0 } || result.Timing?.Verifications != (skip ? 0 : 1))
        throw new InvalidOperationException("Seeded native zero-loss goal failed: " + JsonSerializer.Serialize(new {
            result.Status, result.Message, result.Evaluated, result.StoppedOnHealthTarget, result.HealthTarget, result.Best?.Hp }));
    var sample = new { algorithm = order.ToString(), skip, result.Evaluated, result.ElapsedMs, result.StoppedOnHealthTarget,
        best.StartingHp, best.Hp, best.MaxHp, target.TargetHp, target.FullHealth, checkpoints = best.Continuation!.Length,
        verifications = result.Timing.Verifications, exactOriginalNativeCheckpoints = true, seeded = true };
    samples.Add(sample); Console.WriteLine(JsonSerializer.Serialize(sample));
}
LocalWire.Write(Path.Combine(root, "summary.json"), new { completed = true, dll_sha256 = dllSha, samples,
    scope = "Same-DLL seeded native non-full zero-loss return, two algorithms and both final replay modes; distinct frozen fixture from the October 13 Bodyguard incident" });
