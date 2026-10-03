using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Fixed human/native replay, not a search-quality test. Compare both execution
// modes inside one owned warm worker; private combat input/output stays local.
internal static class NumericalIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation, string recordedPath)
    {
        if (!request.NumericalExecution) throw new InvalidOperationException("The default numerical request mode must be enabled");
        await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
        var worker = ((Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pool)!).GetValue(0)!;
        var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        if (!File.Exists(Path.Combine(workerRoot, ".coach-worker"))) throw new InvalidOperationException("Not an owned worker");
        var replay = File.ReadAllBytes(recordedPath);
        LocalCandidate? baseline = null;
        var records = new List<object>();
        foreach (var numerical in new[] { false, true, true, false })
        {
            var command = request with { Id = Guid.NewGuid().ToString("N"), Partition = 0, Partitions = 1,
                RecordedReplayProbe = replay, VerifyCandidate = null, InitialPlan = null,
                DataOnlyCombat = true, DataOnlyRun = true };
            if (!numerical) command = command with { NumericalExecution = false };
            LocalWire.Write(Path.Combine(workerRoot, "request.json"), command);
            var timer = Stopwatch.StartNew();
            LocalSearchResult? result = null;
            while (timer.Elapsed.TotalSeconds < 120)
            {
                await Task.Delay(100);
                var file = Path.Combine(workerRoot, "result.json");
                if (!File.Exists(file)) continue;
                var latest = LocalWire.Read<LocalSearchResult>(file);
                if (latest.Id == command.Id && latest.Status != "running") { result = latest; break; }
            }
            if (result == null) throw new InvalidOperationException("Numerical comparison timed out");
            LocalWire.Write(Path.Combine(root, $"integration-numerical-private-{records.Count}.json"), result);
            var best = result.Best;
            if (result.Status != "recorded" || best == null || !best.Won || best.Dead)
                throw new InvalidOperationException("Recorded numerical comparison failed: " + result.Message);
            if (baseline != null && (JsonSerializer.Serialize(best.Actions) != JsonSerializer.Serialize(baseline.Actions) ||
                best.Hp != baseline.Hp || best.HpLost != baseline.HpLost || best.StartingHp != baseline.StartingHp ||
                best.MaxHp != baseline.MaxHp || best.Gold != baseline.Gold || best.EnemyHp != baseline.EnemyHp || best.Rounds != baseline.Rounds))
                throw new InvalidOperationException("Numerical execution changed native action/state fingerprints or final settlement");
            baseline ??= best;
            using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(workerRoot, "runtime.json")));
            var counters = runtime.RootElement.GetProperty("numerical").Clone();
            if (numerical && (!counters.GetProperty("available").GetBoolean() || counters.GetProperty("DirectActions").GetInt64() == 0))
                throw new InvalidOperationException("Numerical executor was not exercised");
            records.Add(new { numerical, result.Status, result.ElapsedMs, best.Rounds, steps = best.Actions.Length,
                best.StartingHp, best.Hp, best.HpLost, best.NetHpLoss, best.Gold, best.MaxHp,
                native_action_state_fingerprints_match = true, counters,
                card_ms = result.Trace!.Spans.Where(s => s.Phase == "card").Sum(s => s.DurationMs),
                end_turn_ms = result.Trace.Spans.Where(s => s.Phase == "end_turn").Sum(s => s.DurationMs) });
            LocalWire.Write(Path.Combine(root, "integration-numerical-summary.json"), records);
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SEED_RESULT") is not { Length: > 0 } seedPath) return;
        var seed = LocalWire.Read<LocalSearchResult>(seedPath).Best?.Actions
            ?? throw new InvalidOperationException("No fixed search route");
        baseline = null;
        records.Clear();
        LocalCandidate? numericalCandidate = null;
        foreach (var numerical in new[] { false, true, true, false })
        {
            // One fixed route measures execution. It is deliberately not an unseeded
            // optimization search or evidence of better search coverage/quality.
            var command = request with { Id = Guid.NewGuid().ToString("N"), Partition = 0, Partitions = 1,
                RecordedReplayProbe = null, VerifyCandidate = null, InitialPlan = seed,
                DataOnlyCombat = true, DataOnlyRun = true,
                MaxNodes = 1, DeferVerification = true, ShareSearchWork = false };
            if (!numerical) command = command with { NumericalExecution = false };
            var result = await Submit(command);
            LocalWire.Write(Path.Combine(root, $"integration-numerical-search-private-{records.Count}.json"), result);
            var best = result.Best;
            if (result.Status != "searched" || result.Rejected != 0 || best == null || !best.Won || best.Dead)
                throw new InvalidOperationException("Numerical fixed search failed: " + result.Message);
            if (baseline != null && (JsonSerializer.Serialize(best.Actions) != JsonSerializer.Serialize(baseline.Actions) ||
                best.Hp != baseline.Hp || best.HpLost != baseline.HpLost || best.StartingHp != baseline.StartingHp ||
                best.MaxHp != baseline.MaxHp || best.Gold != baseline.Gold || best.EnemyHp != baseline.EnemyHp || best.Rounds != baseline.Rounds))
                throw new InvalidOperationException("Numerical search changed native actions/choices/state or final settlement");
            baseline ??= best;
            if (numerical) numericalCandidate = best;
            using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(workerRoot, "runtime.json")));
            var counters = runtime.RootElement.GetProperty("numerical").Clone();
            if (numerical && (!counters.GetProperty("available").GetBoolean() || counters.GetProperty("DirectActions").GetInt64() == 0))
                throw new InvalidOperationException("Numerical search was not exercised");
            records.Add(new { numerical, result.Status, result.ElapsedMs, result.Timing, best.Rounds, steps = best.Actions.Length,
                best.StartingHp, best.Hp, best.HpLost, best.NetHpLoss, best.Gold, best.MaxHp, counters,
                native_action_choices_state_fingerprints_match = true });
            LocalWire.Write(Path.Combine(root, "integration-numerical-search-summary.json"), records);
        }
        var verified = await Submit(request with { Id = Guid.NewGuid().ToString("N"), Partition = 0, Partitions = 1,
            RecordedReplayProbe = null, VerifyCandidate = numericalCandidate, InitialPlan = null,
            NumericalExecution = false, DeferVerification = false });
        LocalWire.Write(Path.Combine(root, "integration-numerical-verification-private.json"), verified);
        if (verified.Status != "done" || verified.Best?.Continuation?.Length != numericalCandidate!.Actions.Length)
            throw new InvalidOperationException("Ordinary scene verification failed: " + verified.Message);
        LocalWire.Write(Path.Combine(root, "integration-numerical-verification-summary.json"),
            new { verified.Status, verified.ElapsedMs, verified_steps = verified.Best.Continuation.Length,
                verified.Best.Hp, verified.Best.HpLost, verified.Best.NetHpLoss });

        async Task<LocalSearchResult> Submit(LocalSearchRequest command)
        {
            LocalWire.Write(Path.Combine(workerRoot, "request.json"), command);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < 120)
            {
                await Task.Delay(100);
                var file = Path.Combine(workerRoot, "result.json");
                if (!File.Exists(file)) continue;
                var latest = LocalWire.Read<LocalSearchResult>(file);
                if (latest.Id == command.Id && latest.Status != "running") return latest;
            }
            throw new InvalidOperationException("Numerical comparison timed out");
        }
    }
}
