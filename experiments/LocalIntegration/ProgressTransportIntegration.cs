using System.Diagnostics;
using System.Reflection;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// A fixed native route and bounded compatibility probes, not a product budget
// change or an exhaustive search quality benchmark. Private inputs stay local.
internal static class ProgressTransportIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation, string seedPath)
    {
        var seed = LocalWire.Read<LocalSearchResult>(seedPath).Best
            ?? throw new InvalidDataException("A frozen native route is required");
        if (seed.Actions.Length == 0 || seed.Actions[0].BeforeHash != request.NativeHash)
            throw new InvalidDataException("Frozen route and native root differ");
        await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
        var worker = ((Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pool)!).GetValue(0)!;
        var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        if (!File.Exists(Path.Combine(workerRoot, ".coach-worker"))) throw new InvalidOperationException("Not an owned worker");
        var command = request with { Workers = 1, Partitions = 1, Partition = 0, MaxNodes = 1,
            SearchOrder = LocalSearchOrder.MonteCarlo, ShareSearchWork = false, SearchWorkPipe = null, TurnWorkPipe = null,
            RecordedReplayProbe = null, VerifyCandidate = null, InitialPlan = seed.Actions, DataOnlyCombat = true,
            DataOnlyRun = true, NumericalExecution = true, DeferVerification = true, StopOnZeroLoss = false,
            SkipFinalVerification = true, AsyncProgressOutput = true, TimelineOrigin = 0, InitialTrace = null };
        LocalCandidate? baseline = null;
        var samples = new List<object>(); int sample = 0;
        foreach (var (optimized, warm) in new[] { (false, true), (true, true), (true, false), (false, false) })
        {
            var result = await Submit(command with { MemoryProgress = optimized, ReuseFingerprintBuffer = optimized }, optimized);
            AssertEquivalent(result);
            var methods = result.Trace?.Methods ?? throw new InvalidDataException("Missing timings");
            if (optimized && (Skipped(result, "LocalCapture.ReusedPacketBuffer") == 0 || Calls(result, "LocalProgress.PipeWrite") == 0 ||
                Calls(result, "LocalProgress.Replace") != 0)) throw new InvalidOperationException("Optimized path was not exercised");
            if (!optimized && (Skipped(result, "LocalCapture.ReusedPacketBuffer") != 0 || Calls(result, "LocalProgress.Replace") == 0))
                throw new InvalidOperationException("Reference path was not exercised");
            Save(result, "sample-" + sample++);
            samples.Add(new { optimized, warm, result.ElapsedMs, steps = result.Best!.Actions.Length,
                fingerprint_ms = Ms(result, "LocalCapture.Fingerprint"), progress_write_ms = Ms(result, "LocalWorker.ProgressWrite"),
                progress_file_writes = Calls(result, "LocalProgress.Write"), progress_pipe_writes = Calls(result, "LocalProgress.PipeWrite"),
                reused_buffers = Skipped(result, "LocalCapture.ReusedPacketBuffer"),
                snapshot_methods = methods.Where(m => m.Method.StartsWith("Snapshot.", StringComparison.Ordinal)).ToArray() });
        }
        var verified = await Submit(command with { InitialPlan = null, VerifyCandidate = baseline,
            DeferVerification = false, SkipFinalVerification = false, FastVerification = false }, true);
        if (verified.Status != "done" || verified.Best?.Continuation?.Length != seed.Actions.Length ||
            verified.Best.Hp != baseline!.Hp || verified.Best.HpLost != baseline.HpLost ||
            !verified.Best.Continuation.SequenceEqual(baseline.Continuation!) || Skipped(verified, "LocalCapture.ReusedPacketBuffer") != 0)
            throw new InvalidOperationException("Full native reference verification diverged: " + verified.Message);
        Save(verified, "full-native-verification");
        var algorithms = new List<object>();
        foreach (var algorithm in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
        {
            int received = 0;
            var result = await Task.Run(() => pool.Analyze(command with { Id = Guid.NewGuid().ToString("N"),
                SearchOrder = algorithm, InitialPlan = algorithm == LocalSearchOrder.MonteCarlo ? seed.Actions : null,
                MemoryProgress = true, ReuseFingerprintBuffer = true }, installation, _ => { }, CancellationToken.None,
                _ => Interlocked.Increment(ref received)));
            Save(result, "parent-" + algorithm);
            if (result.Status != "done" || received == 0 || Calls(result, "LocalProgress.PipeWrite") == 0 ||
                Calls(result, "LocalProgress.Replace") != 0 || result.Trace!.Spans.Any(s => s.Phase == "fallback") ||
                !LocalSearchPolicy.HasExecutionPoints(result)) throw new InvalidOperationException("Native parent transport failed: " + result.Message);
            algorithms.Add(new { algorithm, result.Evaluated, result.ElapsedMs, received,
                result.Best!.Won, native_checkpoints = result.Best.Continuation!.Length });
        }
        var forms = await Submit(command with { InitialPlan = null, MaxNodes = 16 }, true);
        Save(forms, "form-compatibility");
        long formCalls = Skipped(forms, "NVoidFormVfx.Create");
        if (forms.Status != "searched" || forms.Failure != null || formCalls == 0)
            throw new InvalidOperationException("Optional native form compatibility was not exercised: " + forms.Message);
        LocalWire.Write(Path.Combine(root, "integration-progress-transport-summary.json"), new {
            samples, algorithms, exact_native_hashes_and_histories = true,
            full_native_verified_steps = verified.Best.Continuation.Length, full_native_verification_ms = verified.ElapsedMs,
            forms = new { forms.Evaluated, forms.Victories, forms.ElapsedMs, skipped_visual_factories = formCalls },
            product_limits_unchanged = true, whole_search_speed_unmeasured = true });

        void AssertEquivalent(LocalSearchResult result)
        {
            var best = result.Best ?? throw new InvalidOperationException(result.Message);
            if (result.Status != "searched" || result.Evaluated != 1 || result.Rejected != 0 ||
                LocalTurnSearch.HistoryKey(best.Actions) != LocalTurnSearch.HistoryKey(seed.Actions) || best.Continuation?.Length != best.Actions.Length)
                throw new InvalidOperationException("Fixed native route diverged: " + result.Message);
            baseline ??= best;
            if ((best.Hp, best.HpLost, best.Gold, best.MaxHp, best.EnemyHp, best.Won, best.Dead, best.DamageSources) !=
                (baseline.Hp, baseline.HpLost, baseline.Gold, baseline.MaxHp, baseline.EnemyHp, baseline.Won, baseline.Dead, baseline.DamageSources) ||
                !best.Actions.Select(a => a.BeforeHash).SequenceEqual(baseline.Actions.Select(a => a.BeforeHash)) ||
                !best.Continuation.SequenceEqual(baseline.Continuation!))
                throw new InvalidOperationException("Buffer or transport changed full native checkpoints or settlement");
        }
        void Save(LocalSearchResult result, string name) => LocalWire.Write(Path.Combine(root, "integration-progress-transport-" + name + "-private.json"), result);
        static long Calls(LocalSearchResult r, string name) => r.Trace?.Methods?.Where(m => m.Method == name).Sum(m => m.Calls) ?? 0;
        static long Skipped(LocalSearchResult r, string name) => r.Trace?.Methods?.Where(m => m.Method == name).Sum(m => m.Skipped) ?? 0;
        static double Ms(LocalSearchResult r, string name) => r.Trace?.Methods?.Where(m => m.Method == name).Sum(m => m.TotalMs) ?? 0;
        async Task<LocalSearchResult> Submit(LocalSearchRequest next, bool pipe)
        {
            next = next with { Id = Guid.NewGuid().ToString("N") };
            using var listener = pipe ? new LocalProgressTransport(next) : null;
            next = next with { ProgressPipe = listener?.PipeName };
            LocalWire.Write(Path.Combine(workerRoot, "request.json"), next);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < 120)
            {
                await Task.Delay(100);
                if (!File.Exists(Path.Combine(workerRoot, "result.json"))) continue;
                var result = LocalWire.Read<LocalSearchResult>(Path.Combine(workerRoot, "result.json"));
                if (result.Id != next.Id || result.Status == "running") continue;
                var idlePath = Path.Combine(workerRoot, "idle.json");
                if (File.Exists(idlePath) && LocalWire.Read<LocalWorkerIdle>(idlePath).Id == next.Id)
                {
                    if ((bool)worker.GetType().GetMethod("GameErrors")!.Invoke(worker, null)!)
                        throw new InvalidOperationException("Owned native process reported a runtime error");
                    if (pipe && listener!.Latest?.Id != next.Id) throw new InvalidOperationException("Native progress was not received");
                    return result;
                }
                if (result.Status is "failed" or "unsupported" or "partial") throw new InvalidOperationException(result.Message);
            }
            throw new TimeoutException("Owned native progress check did not finish");
        }
    }
}
