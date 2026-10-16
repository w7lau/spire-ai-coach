using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

if (args.Length != 3) throw new ArgumentException("owned-root game cases.json");
var root = Path.GetFullPath(args[0]);
if (!root.StartsWith(Path.GetFullPath("work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
    !File.Exists(Path.Combine(root, ".route-study-owner"))) throw new InvalidOperationException("Not an owned study directory");
var config = JsonSerializer.Deserialize<Study>(File.ReadAllText(args[2]))!;
var assembly = typeof(LocalWorkerPool).Assembly;
var dllSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
if (dllSha != config.ProductSha256) throw new InvalidOperationException("Product hash differs from accepted package");
var coach = Path.Combine(root, "coach"); Directory.CreateDirectory(coach);
File.Copy(assembly.Location, Path.Combine(coach, "SpireAiCoach.dll"), true);
File.Copy(Path.Combine(Path.GetDirectoryName(args[2])!, "SpireAiCoach.json"), Path.Combine(coach, "SpireAiCoach.json"), true);
var rows = new List<object>();
LossRoundStudy.Install();
DecisionStudy.Install();
using var pool = new LocalWorkerPool(Path.Combine(root, "pool"));
foreach (var item in config.Cases)
{
    var captured = LocalWire.Read<LocalSearchRequest>(item.Request);
    var allowed = captured.LoadedMods.Select(m => m.Split(':')[0]).ToHashSet(StringComparer.Ordinal);
    var directories = (item.ModDirectories ?? config.ModDirectories).Where(p => Directory.EnumerateFiles(p, "*.json").Any(f => {
        using var doc = JsonDocument.Parse(File.ReadAllText(f));
        return doc.RootElement.TryGetProperty("id", out var id) && id.GetString() != "SpireAiCoach" && allowed.Contains(id.GetString()!);
    })).Append(coach).ToArray();
    var loaded = captured.LoadedMods.Where(m => !m.StartsWith("SpireAiCoach:", StringComparison.Ordinal))
        .Append($"SpireAiCoach:{assembly.GetName().Version!.ToString(3)}:{assembly.ManifestModule.ModuleVersionId}")
        .Order(StringComparer.Ordinal).ToArray();
    var installation = new LocalInstallation(Path.GetFullPath(args[1]), directories);
    Console.WriteLine(JsonSerializer.Serialize(new { phase = "prepare", item.Name }));
    var preparation = Stopwatch.StartNew();
    try { await pool.Prepare(installation, config.Workers, CancellationToken.None); }
    catch (Exception ex) { rows.Add(new { item.Name, phase = "prepare", error = ex.ToString() }); Save(false); continue; }
    preparation.Stop();
    for (int repeat = 0; repeat < config.Repeats; repeat++)
    foreach (var order in (repeat % 2 == 0 ? config.Algorithms ?? ["MonteCarlo", "TurnFrontier"] :
        (config.Algorithms ?? ["MonteCarlo", "TurnFrontier"]).Reverse()).Select(Enum.Parse<LocalSearchOrder>))
    foreach (var mode in repeat % 2 == 0 ? config.Modes ?? ["current"] : (config.Modes ?? ["current"]).Reverse())
    {
        if (mode is not ("current" or "absolute-priority" or "fresh-continuations" or "loss-round-refinement" or "fresh-guided-tail" or "goal-before-proof" or "goal-continuation")) throw new ArgumentException("Unknown study mode");
        LossRoundStudy.Begin(mode == "loss-round-refinement");
        DecisionStudy.Begin(mode, captured.CardGoals);
        pool.DiscardSearch();
        var requiredPrefix = item.InitialPlanPath == null ? null : LocalWire.Read<LocalAction[]>(item.InitialPlanPath);
        if (item.SoftContinuationPath != null && (requiredPrefix == null || order != LocalSearchOrder.MonteCarlo))
            throw new ArgumentException("Soft continuation study requires an explicit prefix and MonteCarlo proposal resolution");
        var initialPlan = item.SoftContinuationPath == null ? requiredPrefix :
            requiredPrefix!.Concat(LocalWire.Read<LocalAction[]>(item.SoftContinuationPath)).ToArray();
        var request = captured with { Id = Guid.NewGuid().ToString("N"), LoadedMods = loaded,
            TimelineOrigin = LocalTimeline.Timestamp, InitialTrace = null, SearchOrder = order,
            Workers = config.Workers, AdaptiveWorkers = false, Partition = 0, Partitions = config.Workers,
            MaxNodes = config.MaxNodes, BudgetSeconds = config.BudgetSeconds, ContinueOptimization = false,
            InitialPlan = initialPlan,
            VerifyCandidate = null, RecordedReplayProbe = null, ResumingFrontier = false,
            TurnWorkPipe = null, SearchWorkPipe = null, MinimumLossPipe = null, ProgressPipe = null,
            EfficientTactics = mode == "absolute-priority" ? false : captured.EfficientTactics,
            GuideWinningRoutes = mode == "fresh-continuations" ? false : captured.GuideWinningRoutes,
            SkipFinalVerification = false, FastVerification = false, DeferVerification = true };
        var stem = $"{item.Name}-{repeat}-{order}-{mode}";
        var clock = Stopwatch.StartNew();
        var curve = new List<object>(); int? lastHp = null; int lastPotions = int.MaxValue;
        var curveGate = new object();
        Console.WriteLine(JsonSerializer.Serialize(new { phase = "search", item.Name, repeat, mode, algorithm = order.ToString() }));
        try
        {
            var result = await pool.Analyze(request, installation, _ => { }, CancellationToken.None, progress => {
                if (progress.Best is not { } best) return;
                int potions = best.Potions;
                lock (curveGate) {
                if (lastHp == null || best.Hp > lastHp || best.Hp == lastHp && potions < lastPotions)
                { lastHp = best.Hp; lastPotions = potions; curve.Add(new { observed_ms = clock.Elapsed.TotalMilliseconds,
                    best.Hp, best.NetHpLoss, potions, best.Route }); }
                }
            });
            LocalWire.Write(Path.Combine(root, stem + "-private.json"), result);
            LocalWire.Write(Path.Combine(root, stem + "-decisions-private.json"), DecisionStudy.Snapshot());
            var b = result.Best;
            bool verified = result.Status == "done" && b != null && !result.VerificationSkipped &&
                result.Timing?.Verifications == 1 && b.Continuation?.Length == b.Actions.Length &&
                result.RecoveredFailures is not { Length: > 0 } && result.Failure == null &&
                (!b.Won || b.CombatOutcome is { NativeVictory: true, Escaped: false } && LocalSearchPolicy.HasExecutionPoints(result)) &&
                (!result.StoppedOnMinimum || LocalSearchPolicy.HasMinimumProof(result));
            var row = new { item.Name, repeat, mode, algorithm = order.ToString(), verified, loss_round_reorders = LossRoundStudy.Reorders,
                soft_tails_cleared = DecisionStudy.Cleared, proof_focus_deferred = DecisionStudy.Deferred,
                goal_continuations_promoted = DecisionStudy.Promoted,
                seeded_counterfactual = item.InitialPlanPath != null,
                continuation_guided = item.SoftContinuationPath != null,
                counterfactual_prefix_matched = requiredPrefix == null ? (bool?)null :
                    b != null && LocalSearchWork.MatchesPrefix(b.Actions, requiredPrefix),
                result.Status, result.Message, result.ElapsedMs, wall_ms = clock.Elapsed.TotalMilliseconds,
                preparation_ms = preparation.Elapsed.TotalMilliseconds, result.Evaluated, result.Victories,
                result.Rejected, result.Duplicates, result.Timing, result.TurnSearch, result.MinimumLoss,
                result.StoppedOnHealthTarget, result.StoppedOnCardGoals, result.StoppedOnMinimum,
                best = b == null ? null : new { b.Won, b.Dead, b.StartingHp, b.Hp, b.MaxHp, b.NetHpLoss,
                    b.Rounds, b.CombatOutcome, b.CardGoalOutcome,
                    potions = b.Actions.Count(a => a.PotionSlot.HasValue), actions = b.Actions.Length,
                    checkpoints = b.Continuation?.Length,
                    cards = b.Actions.Where(a => !a.EndTurn && !a.PotionSlot.HasValue).GroupBy(a => a.ModelId)
                        .ToDictionary(g => g.Key, g => g.Count()) }, curve,
                result.Failure, result.RecoveredFailures };
            rows.Add(row); Save(false);
            Console.WriteLine(JsonSerializer.Serialize(new { item.Name, repeat, mode, algorithm = order.ToString(), verified,
                result.Status, result.ElapsedMs, result.Evaluated, hp = b?.Hp, loss = b?.NetHpLoss, won = b?.Won }));
        }
        catch (Exception ex) { rows.Add(new { item.Name, repeat, mode, algorithm = order.ToString(), error = ex.ToString(),
                category = (ex as CoachException)?.Category, failures = ex.Data["local_failures"] as LocalSimulationFailure[],
                trace = ex.Data["local_trace"] as LocalTrace }); Save(false);
            Console.WriteLine(JsonSerializer.Serialize(new { item.Name, mode, algorithm = order.ToString(), error = ex.Message })); }
    }
}
Save(true);
void Save(bool completed) => LocalWire.Write(Path.Combine(root, "summary.json"), new { completed,
    product_sha256 = dllSha, config.Workers, config.MaxNodes, config.BudgetSeconds, config.Repeats, rows,
    scope = "Frozen real roots; optional prefix-seeded counterfactuals explicitly recorded per case; ordinary independent final replay. Preparation excluded. Bounded study budgets, not default eight-worker performance. Parallel schedule is nondeterministic. Raw output private." });
record Study(Case[] Cases, string[] ModDirectories, int Workers, int MaxNodes, int BudgetSeconds, int Repeats, string ProductSha256,
    string[]? Algorithms = null, string[]? Modes = null);
record Case(string Name, string Request, string[]? ModDirectories = null, string? InitialPlanPath = null,
    string? SoftContinuationPath = null);
