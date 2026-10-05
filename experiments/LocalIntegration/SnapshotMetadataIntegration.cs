using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Same resident worker, native route and search settings. This measures only
// metadata reuse; it does not select, combine or change a search algorithm.
internal static class SnapshotMetadataIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation, string seedPath)
    {
        var seed = LocalWire.Read<LocalSearchResult>(seedPath).Best
            ?? throw new InvalidDataException("A frozen native route is required");
        if (seed.Actions.Length == 0 || seed.Actions[0].BeforeHash != request.NativeHash || request.History == null)
            throw new InvalidDataException("Route/root/history required for exact native checkpoint comparisons");
        await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
        var worker = ((Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pool)!).GetValue(0)!;
        var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        if (!File.Exists(Path.Combine(workerRoot, ".coach-worker"))) throw new InvalidOperationException("Unowned worker");
        var command = request with { Workers = 1, Partitions = 1, Partition = 0, MaxNodes = 1,
            SearchOrder = LocalSearchOrder.MonteCarlo, ShareSearchWork = false, SearchWorkPipe = null,
            TurnWorkPipe = null, ProgressPipe = null, RecordedReplayProbe = null, VerifyCandidate = null,
            InitialPlan = seed.Actions, DataOnlyCombat = true, DataOnlyRun = true, NumericalExecution = true,
            DeferVerification = true, SkipFinalVerification = true, StopOnZeroLoss = false, StopOnFirstWin = false };
        LocalCandidate? baseline = null;
        var samples = new List<object>();
        int index = 0;
        // Warm both paths once. Alternating pairs reduce ordering/warm-cache bias.
        foreach (bool enabled in new[] { false, true, false, true, true, false, false, true, true, false })
        {
            var result = await Submit(command with { ReuseSnapshotMetadata = enabled });
            var best = result.Best ?? throw new InvalidOperationException(result.Message);
            if (result.Status != "searched" || result.Evaluated != 1 || result.Rejected != 0 ||
                best.Continuation?.Length != best.Actions.Length ||
                LocalSearchWork.Key("expand", best.Actions) != LocalSearchWork.Key("expand", seed.Actions))
                throw new InvalidOperationException("Fixed native route changed: " + result.Message);
            baseline ??= best;
            if (LocalSearchWork.Key("expand", best.Actions) != LocalSearchWork.Key("expand", baseline.Actions) ||
                !best.Continuation!.SequenceEqual(baseline.Continuation!) ||
                (best.Hp, best.HpLost, best.MaxHp, best.Gold, best.EnemyHp, best.Won, best.Dead, best.DamageSources) !=
                (baseline.Hp, baseline.HpLost, baseline.MaxHp, baseline.Gold, baseline.EnemyHp, baseline.Won, baseline.Dead, baseline.DamageSources))
                throw new InvalidOperationException("Metadata reuse changed native state/RNG/history/choices/settlement");
            var metadata = ReadMetadata();
            if (metadata.GetProperty("failures").GetArrayLength() != 0 || metadata.GetProperty("boundaries").GetArrayLength() != 2 ||
                enabled && (metadata.GetProperty("condition_hits").GetInt64() == 0 || metadata.GetProperty("keyword_reuses").GetInt64() == 0) ||
                !enabled && (metadata.GetProperty("condition_hits").GetInt64() != 0 || metadata.GetProperty("keyword_reuses").GetInt64() != 0))
                throw new InvalidOperationException("Snapshot metadata boundaries were not exercised in the requested mode");
            LocalWire.Write(Path.Combine(root, $"integration-snapshot-metadata-private-{index}.json"), result);
            var methods = result.Trace?.Methods ?? [];
            samples.Add(new { enabled, warm = index < 2, result.ElapsedMs, result.Timing,
                fingerprint_ms = methods.Where(m => m.Method == "LocalCapture.Fingerprint").Sum(m => m.TotalMs),
                native_snapshots = methods.Where(m => m.Method == "NetFullCombatState.FromRun").Sum(m => m.Calls),
                metadata, steps = best.Actions.Length });
            index++;
        }
        var verified = await Submit(command with { InitialPlan = null, VerifyCandidate = baseline,
            ReuseSnapshotMetadata = true, DeferVerification = false, SkipFinalVerification = false, FastVerification = false });
        var verificationMetadata = ReadMetadata();
        if (verified.Status != "done" || verified.Best?.Continuation?.Length != baseline!.Actions.Length ||
            !verified.Best.Continuation.SequenceEqual(baseline.Continuation!) ||
            verificationMetadata.GetProperty("condition_hits").GetInt64() != 0 ||
            verificationMetadata.GetProperty("keyword_reuses").GetInt64() != 0)
            throw new InvalidOperationException("Ordinary native verification changed checkpoints or reused metadata: " + verified.Message);
        LocalWire.Write(Path.Combine(root, "integration-snapshot-metadata-verification-private.json"), verified);
        LocalWire.Write(Path.Combine(root, "integration-snapshot-metadata-summary.json"), new {
            samples, native_state_rng_actions_choices_history_and_settlement_match = true,
            ordinary_verified_steps = verified.Best.Continuation.Length, ordinary_verification_metadata = verificationMetadata,
            algorithm_and_search_settings_changed = false, whole_search_throughput_measured = false });

        JsonElement ReadMetadata()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(workerRoot, "runtime.json")));
            return doc.RootElement.GetProperty("snapshot_metadata").Clone();
        }
        async Task<LocalSearchResult> Submit(LocalSearchRequest next)
        {
            next = next with { Id = Guid.NewGuid().ToString("N") };
            LocalWire.Write(Path.Combine(workerRoot, "request.json"), next);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < 120)
            {
                await Task.Delay(100);
                if (!File.Exists(Path.Combine(workerRoot, "result.json"))) continue;
                var result = LocalWire.Read<LocalSearchResult>(Path.Combine(workerRoot, "result.json"));
                if (result.Id != next.Id || result.Status == "running") continue;
                var idlePath = Path.Combine(workerRoot, "idle.json");
                if (!File.Exists(idlePath) || LocalWire.Read<LocalWorkerIdle>(idlePath).Id != next.Id) continue;
                if ((bool)worker.GetType().GetMethod("GameErrors")!.Invoke(worker, null)!)
                    throw new InvalidOperationException("Owned native worker reported a runtime error");
                return result;
            }
            throw new TimeoutException("Owned metadata comparison did not finish");
        }
    }
}
