using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Execute the identical frozen route with and without the presentation/IO
// shortcuts in one warm owned worker. This is not a search-quality benchmark.
internal static class OverheadIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation, string seedPath)
    {
        var seed = LocalWire.Read<LocalSearchResult>(seedPath).Best?.Actions
            ?? throw new InvalidOperationException("A fixed native route is required");
        if (seed.Length == 0 || seed[0].BeforeHash != request.NativeHash)
            throw new InvalidOperationException("The fixed route and frozen combat request have different roots");
        await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
        var worker = ((Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pool)!).GetValue(0)!;
        var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        if (!File.Exists(Path.Combine(workerRoot, ".coach-worker"))) throw new InvalidOperationException("Not an owned worker");
        LocalCandidate? baseline = null;
        LocalCandidate? candidate = null;
        var records = new List<object>();
        var command = request with { Partition = 0, Partitions = 1, Workers = 1, MaxNodes = 1,
            RecordedReplayProbe = null, VerifyCandidate = null, InitialPlan = seed,
            DataOnlyCombat = true, DataOnlyRun = true, NumericalExecution = true,
            DeferVerification = true, ShareSearchWork = false, StopOnZeroLoss = false };
        // Warm both paths, then reverse their order to reduce startup/order bias.
        foreach (var (trim, warm) in new[] { (false, true), (true, true), (false, false), (true, false), (true, false), (false, false) })
        {
            var result = await Submit(command with { TrimWorkerOverhead = trim });
            var best = result.Best;
            if (result.Status != "searched" || result.Rejected != 0 || best == null || !best.Won || best.Dead)
                throw new InvalidOperationException("Fixed overhead comparison failed: " + result.Message);
            if (baseline != null && (JsonSerializer.Serialize(best.Actions) != JsonSerializer.Serialize(baseline.Actions) ||
                best.Hp != baseline.Hp || best.HpLost != baseline.HpLost || best.StartingHp != baseline.StartingHp ||
                best.MaxHp != baseline.MaxHp || best.Gold != baseline.Gold || best.EnemyHp != baseline.EnemyHp || best.Rounds != baseline.Rounds))
                throw new InvalidOperationException("Presentation/IO shortcuts changed native actions, choices, fingerprints or final settlement");
            baseline ??= best;
            if (trim) candidate = best;
            using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(workerRoot, "runtime.json")));
            var overhead = runtime.RootElement.GetProperty("overhead").Clone();
            if (overhead.GetProperty("failures").GetArrayLength() != 0)
                throw new InvalidOperationException("An overhead boundary failed to install: " + overhead);
            var methods = result.Trace?.Methods ?? throw new InvalidOperationException("Missing method profiling");
            var replay = methods.Single(m => m.Method == "CombatReplayWriter.WriteReplay");
            if (replay.Calls == 0 || (trim ? replay.Skipped != replay.Calls : replay.Skipped != 0))
                throw new InvalidOperationException("Replay output boundary was not exercised on its requested mode");
            if (!methods.Any(m => m.Method == "ChecksumTracker.GenerateChecksum" && m.Calls > 0 && m.Skipped == 0) ||
                !methods.Any(m => m.Method == "PlayerCombatState.RecalculateCardValues" && m.Calls > 0 && m.Skipped == 0))
                throw new InvalidOperationException("Native checksums or card recalculation were not preserved");
            if (!warm)
            {
                LocalWire.Write(Path.Combine(root, $"integration-overhead-private-{records.Count}.json"), result);
                records.Add(new { trim, result.Status, result.ElapsedMs, steps = best.Actions.Length, best.Rounds,
                    best.StartingHp, best.Hp, best.HpLost, best.NetHpLoss, best.MaxHp, best.Gold,
                    native_actions_choices_state_and_settlement_match = true, methods, overhead });
                LocalWire.Write(Path.Combine(root, "integration-overhead-summary.json"), records);
            }
        }
        var verified = await Submit(command with { VerifyCandidate = candidate, InitialPlan = null,
            NumericalExecution = false, DeferVerification = false, TrimWorkerOverhead = true, FastVerification = false });
        LocalWire.Write(Path.Combine(root, "integration-overhead-verification-private.json"), verified);
        if (verified.Status != "done" || verified.Best?.Continuation?.Length != candidate!.Actions.Length ||
            verified.Trace!.Methods!.Any(m => m.Stage == "verify" && m.Skipped > 0 &&
                (m.Method.StartsWith("SfxCmd.") || m.Method.StartsWith("VfxCmd.") || m.Method == "CombatReplayWriter.WriteReplay")))
            throw new InvalidOperationException("Ordinary verification failed or presentation shortcuts remained active: " + verified.Message);
        LocalWire.Write(Path.Combine(root, "integration-overhead-verification-summary.json"), new
            { verified.Status, verified.ElapsedMs, verified_steps = verified.Best.Continuation.Length,
                verified.Best.Hp, verified.Best.HpLost, verified.Best.NetHpLoss, presentation_shortcuts_inactive = true });

        async Task<LocalSearchResult> Submit(LocalSearchRequest next)
        {
            next = next with { Id = Guid.NewGuid().ToString("N") };
            LocalWire.Write(Path.Combine(workerRoot, "request.json"), next);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < 120)
            {
                await Task.Delay(100);
                var file = Path.Combine(workerRoot, "result.json");
                if (!File.Exists(file)) continue;
                var result = LocalWire.Read<LocalSearchResult>(file);
                if (result.Id == next.Id && result.Status != "running") return result;
            }
            throw new TimeoutException("Owned overhead comparison did not finish");
        }
    }
}
