using System.Diagnostics;
using System.Reflection;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Synthetic owned-host acceptance, not a search-quality or wall-time benchmark.
internal static class ConcurrencyIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, StateCapture capture)
    {
        var frozen = LocalCapture.Fingerprint();
        var captured = LocalCapture.Capture(capture.Capture(true)!.Fingerprint(), false);
        var installation = LocalCapture.Installation();
        var timer = Stopwatch.StartNew();
        await Task.Run(() => pool.Prepare(installation, 8, CancellationToken.None));
        var workers = (Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
        int Alive() => workers.Cast<object>().Count(w => w.GetType().GetProperty("Process")!.GetValue(w) is Process p && !p.HasExited);
        if (Alive() != 1) throw new InvalidOperationException("Prewarming started more than one instance");
        var records = new List<object> { new { stage = "prewarm", configured = 8, alive = Alive(), elapsedMs = timer.ElapsedMilliseconds } };
        foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
        {
            var command = LocalCalculation.Configure(captured, order, 2, false, false) with
            {
                Id = Guid.NewGuid().ToString("N"), MaxNodes = 2, MaxRounds = 2,
                TimelineOrigin = 0, InitialTrace = null
            };
            var result = await Task.Run(() => pool.Analyze(command, installation, _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, "integration-concurrency-" + order + "-private.json"), result);
            if (result.Status != "done" || result.Best is not { Actions.Length: > 0 } best ||
                best.Continuation?.Length != best.Actions.Length || result.Timing?.Verifications != 1 ||
                LocalCapture.Fingerprint() != frozen || result.Workers is < 1 or > 2 || result.WorkerLimit > 2)
                throw new InvalidOperationException("Adaptive native result was not fully verified: " + result.Message);
            var admissions = result.Trace!.Spans.Where(s => s.Phase == "admit_worker").ToArray();
            if (admissions.Length != result.Workers || admissions[0].Worker != 0 ||
                admissions.Select(s => s.Worker).Distinct().Count() != admissions.Length)
                throw new InvalidOperationException("Worker admission was not reported accurately");
            var firstDecision = result.Trace.Spans.Where(s => s.Worker == 0 && s.Phase == "decision").Min(s => s.StartMs);
            if (admissions.Skip(1).Any(s => s.StartMs < firstDecision))
                throw new InvalidOperationException("Extra workers started before real root branches existed");
            records.Add(new
            {
                stage = order.ToString(), result.Status, result.Workers, result.WorkerLimit, result.RootBranches,
                result.Evaluated, result.Rejected, result.ElapsedMs, result.Timing, result.Work,
                best.Won, best.Hp, best.NetHpLoss, verifiedSteps = best.Continuation.Length,
                admissions = admissions.Select(s => new { s.Worker, s.StartMs }), sourceUnchanged = true
            });
        }
        LocalWire.Write(Path.Combine(root, "integration-concurrency-summary.json"), records);
    }
}
