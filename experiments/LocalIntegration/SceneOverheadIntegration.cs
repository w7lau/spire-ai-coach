using System.Diagnostics;
using System.Reflection;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// A bounded native mechanism check. The frozen route is a correctness/timing
// fixture, not an answer supplied to the product search or a quality benchmark.
internal static class SceneOverheadIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation, string seedPath)
    {
        var seed = LocalWire.Read<LocalSearchResult>(seedPath).Best
            ?? throw new InvalidDataException("A frozen native candidate is required");
        if (seed.Actions.Length == 0 || seed.Actions[0].BeforeHash != request.NativeHash)
            throw new InvalidDataException("Native route and root differ");
        await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
        var worker = ((Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pool)!).GetValue(0)!;
        var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        if (!File.Exists(Path.Combine(workerRoot, ".coach-worker"))) throw new InvalidOperationException("Not an owned worker");
        var command = request with { Workers = 1, Partitions = 1, Partition = 0, MaxNodes = 1,
            SearchOrder = LocalSearchOrder.MonteCarlo, ShareSearchWork = false, SearchWorkPipe = null,
            TurnWorkPipe = null, RecordedReplayProbe = null, VerifyCandidate = null, InitialPlan = seed.Actions,
            DataOnlyCombat = false, DataOnlyRun = false, DeferVerification = true, StopOnZeroLoss = false };
        var samples = new List<object>();
        LocalCandidate? baseline = null;
        int sample = 0;
        foreach (var trim in new[] { false, true, true, false })
        {
            var result = await Submit(command with { TrimWorkerOverhead = trim });
            AssertEquivalent(result);
            if (trim && (Skipped(result, "NCard.DescriptionPresentation") == 0 || Skipped(result, "NCard.UpdatePortrait") == 0 ||
                Skipped(result, "MegaLabel.AdjustFontSize") + Skipped(result, "MegaRichTextLabel.AdjustFontSize") == 0))
                throw new InvalidOperationException("Regular scene presentation shortcuts were not exercised");
            Save(result, "regular");
        }
        var numerical = await Submit(command with { DataOnlyCombat = true, DataOnlyRun = true, TrimWorkerOverhead = true });
        AssertEquivalent(numerical); Save(numerical, "numerical");

        // Keep externally observed native checksums even in a trimmed regular search.
        var observed = await Submit(command with { TrimWorkerOverhead = true, ProbeChecksumListener = true });
        AssertEquivalent(observed);
        if (Calls(observed, "ChecksumTracker.ExternalListenerProbe") == 0 ||
            Skipped(observed, "ChecksumTracker.SearchSnapshotSuppressed") != 0)
            throw new InvalidOperationException("Regular search suppressed an external native checksum callback");
        Save(observed, "external-checksum-listener");

        // The original scene-free incident fails on Void Form. Allow a small set
        // of native trials to exercise that presentation boundary with real Mods.
        var forms = await Submit(command with { DataOnlyCombat = true, DataOnlyRun = true, TrimWorkerOverhead = true,
            InitialPlan = null, MaxNodes = 16, EfficientTactics = false, OwnedWinningFocus = false, GuideWinningRoutes = false });
        if (forms.Status != "searched" || forms.Rejected != 0 || Skipped(forms, "NVoidFormVfx.Create") == 0)
            throw new InvalidOperationException("Void Form native route did not exercise the optional factory: " + forms.Message);
        Save(forms, "void-form-native-trials");

        // Explicit slow replay must keep original visuals/checksums. Its complete
        // per-step checkpoints still match the same route simulated above.
        var verified = await Submit(command with { InitialPlan = null, VerifyCandidate = numerical.Best,
            FastVerification = false, TrimWorkerOverhead = true, DeferVerification = false, SkipFinalVerification = false });
        if (verified.Status != "done" || verified.Best?.Continuation?.Length != seed.Actions.Length ||
            Skipped(verified, "NCard.DescriptionPresentation") != 0 || Skipped(verified, "MegaLabel.AdjustFontSize") != 0 ||
            Skipped(verified, "ChecksumTracker.SearchSnapshotSuppressed") != 0)
            throw new InvalidOperationException("Independent replay lost native checkpoints or its original execution path");
        Save(verified, "independent-native-verification");

        // A deliberately invalid native command fails before touching combat.
        // Invoke one pass (without its regular fallback) so the warm first lane
        // fails while a second owned lane is still preparing. No full search runs.
        LocalTrace failureTrace;
        var failureTimer = Stopwatch.StartNew();
        try
        {
            var pass = typeof(LocalWorkerPool).GetMethod("AnalyzePass", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task<LocalSearchResult>)pass.Invoke(pool, [command with { Id = Guid.NewGuid().ToString("N"),
                Workers = 2, MaxDepth = 0, AdaptiveWorkers = false, DataOnlyCombat = true, DataOnlyRun = true },
                installation, (Action<string>)(_ => { }), CancellationToken.None, null])!;
            await task;
            throw new InvalidOperationException("Invalid native command was accepted");
        }
        catch (CoachException ex) when (ex.Category == "local_data_unavailable")
        {
            failureTrace = ex.Data["local_trace"] as LocalTrace ?? throw new InvalidDataException("Missing failed-pass trace");
        }
        var stops = failureTrace.Spans.Where(s => s.Phase == "stop_failed_pass" && s.Worker == 1).ToArray();
        if (stops.Length == 0 || pool.Resources().Preparing != 0 || pool.Resources().Ready != 0 ||
            failureTrace.Spans.Any(s => s.Stage == "search" && s.Phase == "session" && s.Worker == 1))
            throw new InvalidOperationException("Rejected pass left a preparing peer or waited for its native search");
        LocalWire.Write(Path.Combine(root, "integration-scene-overhead-failure-private.json"), failureTrace);
        LocalWire.Write(Path.Combine(root, "integration-scene-overhead-summary.json"), new
        {
            samples, equivalent_native_history_and_settlement = true,
            independent_verified_steps = verified.Best!.Continuation!.Length,
            void_form_factory_skips = Skipped(forms, "NVoidFormVfx.Create"),
            failed_pass = new { elapsed_ms = failureTimer.ElapsedMilliseconds,
                peer_stop_ms = stops.Sum(s => s.DurationMs), peer_search_started = false,
                owned_preparations_drained = true },
            product_limits_unchanged = true, whole_search_speed_unmeasured = true
        });

        void AssertEquivalent(LocalSearchResult result)
        {
            var best = result.Best ?? throw new InvalidOperationException(result.Message);
            if (result.Status != "searched" || result.Evaluated != 1 || result.Rejected != 0 ||
                LocalSearchWork.Key("expand", best.Actions) != LocalSearchWork.Key("expand", seed.Actions))
                throw new InvalidOperationException("Native action history diverged: " + result.Message);
            baseline ??= best;
            if ((best.Hp, best.HpLost, best.MaxHp, best.Gold, best.EnemyHp, best.Won, best.Dead, best.DamageSources) !=
                (baseline.Hp, baseline.HpLost, baseline.MaxHp, baseline.Gold, baseline.EnemyHp, baseline.Won, baseline.Dead, baseline.DamageSources))
                throw new InvalidOperationException("Presentation shortcuts changed native settlement");
        }
        static long Skipped(LocalSearchResult r, string name) => r.Trace?.Methods?.Where(m => m.Method == name).Sum(m => m.Skipped) ?? 0;
        static long Calls(LocalSearchResult r, string name) => r.Trace?.Methods?.Where(m => m.Method == name).Sum(m => m.Calls) ?? 0;
        void Save(LocalSearchResult result, string mode)
        {
            LocalWire.Write(Path.Combine(root, $"integration-scene-overhead-private-{sample++}.json"), result);
            samples.Add(new { mode, result.ElapsedMs, result.Evaluated, result.Rejected,
                steps = result.Best?.Actions.Length, result.Best?.Hp, result.Best?.NetHpLoss,
                methods = result.Trace?.Methods?.Where(m => m.Method.StartsWith("NCard.", StringComparison.Ordinal) ||
                    m.Method.StartsWith("Mega", StringComparison.Ordinal) || m.Method.StartsWith("ChecksumTracker.", StringComparison.Ordinal) ||
                    m.Method == "NVoidFormVfx.Create" || m.Method == "NetFullCombatState.FromRun").ToArray() });
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
                if (File.Exists(idlePath) && LocalWire.Read<LocalWorkerIdle>(idlePath).Id == next.Id)
                {
                    if ((bool)worker.GetType().GetMethod("GameErrors")!.Invoke(worker, null)!)
                        throw new InvalidOperationException("Owned native worker reported a runtime error");
                    return result;
                }
                if (result.Status is "failed" or "unsupported" or "partial") throw new InvalidOperationException(result.Message);
            }
            throw new TimeoutException("Owned scene overhead check did not finish");
        }
    }
}
