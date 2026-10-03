using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// A fixed computer-discovered route isolates execution cost. It is never used
// as the input to autonomous search or as evidence of finding a new solution.
internal static class LeanChecksumIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest captured,
        LocalInstallation installation, string seedPath)
    {
        var seed = LocalWire.Read<LocalSearchResult>(seedPath);
        if (seed.SnapshotId != captured.SnapshotId || seed.Best is not { Won: true } line)
            throw new InvalidOperationException("Fixed route must belong to this exact frozen combat");
        var records = new List<object>();
        LocalSearchResult? baseline = null;
        for (int i = 0; i < 2; i++)
        {
            var request = captured with
            {
                Id = Guid.NewGuid().ToString("N"), Workers = 1, MaxNodes = 1, StopOnZeroLoss = false,
                InitialPlan = line.Actions, VerifyCandidate = null, RecordedReplayProbe = null,
                LeanSearchChecksums = i == 1, CorrelatedRollouts = false, TimelineOrigin = 0, InitialTrace = null
            };
            var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, $"integration-lean-checksum-private-{i}.json"), result);
            var best = result.Best ?? throw new InvalidOperationException("Fixed route returned no result");
            var methods = result.Trace?.Methods ?? [];
            long skipped = methods.Where(m => m.Stage == "search" && m.Method == "ChecksumTracker.SearchSnapshotSuppressed").Sum(m => m.Skipped);
            long verifiedChecks = methods.Where(m => m.Stage == "verify" && m.Method == "ChecksumTracker.GenerateChecksum").Sum(m => m.Calls);
            if (result.Status != "done" || result.Timing?.Verifications != 1 || best.Continuation?.Length != best.Actions.Length ||
                result.Trace?.Spans.Any(s => s.Phase == "fallback") == true || i == 1 && (skipped == 0 || verifiedChecks == 0))
                throw new InvalidOperationException("Lean search must retain ordinary final verification and actually suppress redundant snapshots");
            if (baseline is { Best: { } prior } && (LocalSearchWork.Key("expand", best.Actions) != LocalSearchWork.Key("expand", prior.Actions) ||
                best.Hp != prior.Hp || best.HpLost != prior.HpLost || best.Rounds != prior.Rounds || best.Gold != prior.Gold ||
                best.MaxHp != prior.MaxHp || best.Won != prior.Won ||
                !best.Continuation!.SequenceEqual(prior.Continuation!)))
                throw new InvalidOperationException("Lean checksum execution changed a native action state, history or final settlement");
            baseline ??= result;
            records.Add(new { sample = i, lean = i == 1, result.Status, result.ElapsedMs, result.Timing,
                best.Hp, best.HpLost, best.Rounds, steps = best.Actions.Length,
                skipped_search_snapshots = skipped, final_verification_checksums = verifiedChecks,
                native_states_histories_and_settlement_match = true, autonomous_discovery = false });
            LocalWire.Write(Path.Combine(root, "integration-lean-checksum-summary.json"), records);
        }
    }
}
