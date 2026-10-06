using System.Diagnostics;
using System.Text.Json;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

internal static class ProofReplyIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest frozen, LocalInstallation installation)
    {
        if (frozen.InitialPlan is { Length: > 0 } || frozen.VerifyCandidate != null || frozen.RecordedReplayProbe != null)
            throw new InvalidOperationException("Reply comparison requires an unseeded frozen root");
        installation = installation with { MinimalWorkerBootstrap = true };
        await pool.Prepare(installation, 1, CancellationToken.None);
        var cases = new[] { (LocalSearchOrder.MonteCarlo, false), (LocalSearchOrder.MonteCarlo, true),
            (LocalSearchOrder.TurnFrontier, false), (LocalSearchOrder.TurnFrontier, true) };
        bool reverse = System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_PROOF_REPLY_REVERSE") == "1";
        int fixedNodes = int.TryParse(System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_PROOF_REPLY_NODES"), out var count) ? count : frozen.MaxNodes;
        if (reverse) cases = cases.Reverse().ToArray();
        var samples = new List<object>();
        foreach (var (order, compact) in cases)
        {
            var request = frozen with { Id = Guid.NewGuid().ToString("N"), Workers = 1, Partitions = 1, Partition = 0,
                SearchOrder = order, MaxNodes = fixedNodes, InitialPlan = null, VerifyCandidate = null,
                DataOnlyCombat = true, DataOnlyRun = true, NumericalExecution = true,
                ContinueOptimization = false, DeferVerification = false, SkipFinalVerification = false,
                CompactMinimumLossReplies = compact, TurnWorkPipe = null, SearchWorkPipe = null, MinimumLossPipe = null };
            var timer = Stopwatch.StartNew();
            var result = await pool.Analyze(request, installation, _ => { }, CancellationToken.None);
            string variant = compact ? "compact" : "baseline";
            LocalWire.Write(Path.Combine(root, $"integration-proof-reply-{order}-{variant}-private.json"), result);
            if (result.Best is not { Won: true, Dead: false } best || result.Status != "done" || result.Rejected != 0 ||
                result.Failure != null || result.RecoveredFailures is { Length: > 0 } || result.Timing?.Verifications != 1)
                throw new InvalidOperationException("Reply comparison failed native root/search/independent replay checks");
            var methods = result.Trace?.Methods?.Where(m => m.Method is "LocalMinimumLoss.ObserveTrial" or "LocalMinimumLoss.TakePrefix").ToArray() ?? [];
            samples.Add(new { algorithm = order.ToString(), compact, request.MaxNodes, request.MaxRounds, request.BudgetSeconds,
                result.Evaluated, result.Victories, best.Hp, best.NetHpLoss,
                potions = best.Actions.Count(a => a.PotionSlot.HasValue), result.HealthBounds,
                replyMs = methods.Sum(m => m.TotalMs), elapsedMs = timer.ElapsedMilliseconds,
                nativeRootMatched = true, independentOrdinaryReplay = true });
            LocalWire.Write(Path.Combine(root, "integration-proof-reply-summary.json"), new {
                version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3), reverse, samples, passed = true,
                scope = "One owned native worker; same frozen unseeded input and budgets; only reply payload changes" });
        }
    }
}
