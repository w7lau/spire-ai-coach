using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

internal static class VerificationIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation, string seedFile)
    {
        var seed = LocalWire.Read<LocalSearchResult>(seedFile).Best ?? throw new InvalidOperationException("Missing fixed candidate");
        if (seed.Actions.Length == 0 || seed.Actions[0].BeforeHash != request.NativeHash)
            throw new InvalidOperationException("Verification comparison must use the same frozen native root");
        await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
        var worker = ((Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pool)!).GetValue(0)!;
        var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        if (!File.Exists(Path.Combine(workerRoot, ".coach-worker"))) throw new InvalidOperationException("Not an owned worker");
        var command = request with { Partition = 0, Partitions = 1, Workers = 1, MaxNodes = 1,
            VerifyCandidate = seed, InitialPlan = null, RecordedReplayProbe = null, ShareSearchWork = false,
            DeferVerification = false, NumericalExecution = false, TrimWorkerOverhead = true };
        LocalCandidate? reference = null;
        var records = new List<object>();
        // First sample includes initial scene resources. Warm both paths, then reverse
        // the paired order. This measures verification, not search quality or startup.
        foreach (var (fast, warm) in new[] { (false, true), (true, true),
            (false, false), (true, false), (true, false), (false, false) })
        {
            var result = await Submit(command with { FastVerification = fast });
            var best = result.Best;
            if (result.Status != "done" || best?.Continuation?.Length != seed.Actions.Length ||
                JsonSerializer.Serialize(best.Actions) != JsonSerializer.Serialize(seed.Actions) ||
                best.Hp != seed.Hp || best.HpLost != seed.HpLost || best.StartingHp != seed.StartingHp ||
                best.Gold != seed.Gold || best.MaxHp != seed.MaxHp || best.EnemyHp != seed.EnemyHp ||
                best.Won != seed.Won || best.Dead != seed.Dead)
                throw new InvalidOperationException("Native verification diverged: " + result.Message);
            reference ??= best;
            if (JsonSerializer.Serialize(best.Continuation) != JsonSerializer.Serialize(reference.Continuation))
                throw new InvalidOperationException("Per-step continuation state or action/choice history changed");
            using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(workerRoot, "runtime.json")));
            var verify = runtime.RootElement.GetProperty("verification").Clone();
            if (runtime.RootElement.GetProperty("mode").GetString() != "regular-scene" ||
                runtime.RootElement.GetProperty("numerical").GetProperty("DirectActions").GetInt64() != 0 ||
                verify.GetProperty("used_fast").GetBoolean() != fast || !verify.GetProperty("available").GetBoolean() ||
                verify.GetProperty("active").GetBoolean() || verify.GetProperty("fallback").ValueKind != JsonValueKind.Null ||
                !runtime.RootElement.GetProperty("preload_enabled").GetBoolean())
                throw new InvalidOperationException("Verification did not keep the real scene/executor or reset its scope: " + runtime.RootElement);
            var methods = result.Trace!.Methods!;
            if (!methods.Any(m => m.Method == "MegaLabel.AdjustFontSize" && m.Calls > 0 && (fast ? m.Skipped == m.Calls : m.Skipped == 0)) ||
                !methods.Any(m => m.Method == "ChecksumTracker.GenerateChecksum" && m.Calls > 0 && m.Skipped == 0))
                throw new InvalidOperationException("Font boundary not exercised or checksums skipped");
            if (warm)
                LocalWire.Write(Path.Combine(root, $"integration-verification-warm-{(fast ? "new" : "old")}.json"), result);
            else
            {
                LocalWire.Write(Path.Combine(root, $"integration-verification-private-{records.Count}.json"), result);
                records.Add(new { fast, result.Status, result.ElapsedMs, steps = best.Actions.Length, best.Rounds,
                    best.Hp, best.HpLost, best.NetHpLoss, methods, verify,
                    phases = result.Trace.Spans.GroupBy(s => new { s.Depth, s.Phase })
                        .Select(g => new { g.Key.Depth, g.Key.Phase, calls = g.Count(), ms = g.Sum(s => s.DurationMs) }).ToArray(),
                    full_scene_native_executor_and_identical_continuation = true });
                LocalWire.Write(Path.Combine(root, "integration-verification-summary.json"), records);
            }
        }
        // A wrong final settlement must remain unusable after the ordinary fallback.
        var invalid = await Submit(command with { FastVerification = true, VerifyCandidate = seed with { Hp = seed.Hp - 1 } });
        if (invalid.Status != "failed" || invalid.Best != null || !invalid.Trace!.Spans.Any(s => s.Phase == "verify_fallback"))
            throw new InvalidOperationException("A settlement mismatch escaped final verification");
        LocalWire.Write(Path.Combine(root, "integration-verification-rejection-private.json"), invalid);

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
            throw new TimeoutException("Owned verification comparison did not finish");
        }
    }
}
