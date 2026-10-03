using Godot;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Paired single-attempt test in an owned host, never a new whole-search benchmark.
internal static class FinalVerificationIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation, string seedPath)
    {
        var seed = LocalWire.Read<LocalSearchResult>(seedPath).Best
            ?? throw new InvalidOperationException("No fixed native route");
        if (seed.Actions.Length == 0 || seed.Actions[0].BeforeHash != request.NativeHash || !seed.Won)
            throw new InvalidOperationException("The fixed route must belong to this frozen native root");
        var tree = (SceneTree)Engine.GetMainLoop();
        new CoachOverlay(tree).Mount();
        var layer = tree.Root.GetNode<CanvasLayer>("SpireAiCoach");
        var toggle = layer.FindChild("LocalSkipVerification", true, false) as Button;
        if (toggle == null || !toggle.ToggleMode || toggle.ButtonPressed)
            throw new InvalidOperationException("Final verification must remain the product default");
        toggle.ButtonPressed = true;
        if (!toggle.ButtonPressed) throw new InvalidOperationException("The opt-out button did not toggle");
        layer.QueueFree();
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        await Task.Run(() => pool.Prepare(installation, 1, CancellationToken.None));
        var summaries = new List<object>();
        LocalCandidate? skippedCandidate = null;
        foreach (bool skip in new[] { true, false })
        {
            var messages = new List<string>();
            var command = request with { Id = Guid.NewGuid().ToString("N"), Workers = 1, Partitions = 1,
                InitialPlan = seed.Actions, MaxNodes = 1, SearchOrder = LocalSearchOrder.MonteCarlo,
                VerifyCandidate = null, RecordedReplayProbe = null, DeferVerification = false,
                ShareSearchWork = false, StopOnZeroLoss = true, SkipFinalVerification = skip,
                TimelineOrigin = 0, InitialTrace = null };
            var result = await Task.Run(() => pool.Analyze(command, installation, messages.Add, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, $"integration-final-verification-{skip}-private.json"), result);
            var best = result.Best ?? throw new InvalidOperationException("Native route produced no result");
            if (result.Status != "done" || !best.Won || result.Evaluated != 1 || result.VerificationSkipped != skip ||
                result.Message.StartsWith("常规执行", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected native calculation outcome: " + result.Message);
            if (skip)
            {
                if (result.Timing is not { Verifications: 0, VerificationMs: 0 } || best.Continuation != null ||
                    result.Trace!.Spans.Any(s => s.Stage == "verify") || messages.Any(m => m.Contains("正在复核")))
                    throw new InvalidOperationException("Opt-out still ran final verification or retained execution points");
                skippedCandidate = best;
            }
            else if (result.Timing?.Verifications != 1 || best.Continuation?.Length != best.Actions.Length ||
                best.Hp != skippedCandidate!.Hp || best.HpLost != skippedCandidate.HpLost || best.Gold != skippedCandidate.Gold ||
                best.MaxHp != skippedCandidate.MaxHp || LocalTurnSearch.HistoryKey(best.Actions) != LocalTurnSearch.HistoryKey(skippedCandidate.Actions))
                throw new InvalidOperationException("The paired native result or verified action history diverged");
            summaries.Add(new { skip, result.Status, result.VerificationSkipped, result.Evaluated, result.ElapsedMs,
                result.SearchElapsedMs, result.Timing, best.Won, best.StartingHp, best.Hp, best.HpLost, best.NetHpLoss,
                best.Rounds, steps = best.Actions.Length, verified_steps = best.Continuation?.Length ?? 0,
                verification_spans = result.Trace!.Spans.Count(s => s.Stage == "verify"), result.Trace.Dropped });
        }
        LocalWire.Write(Path.Combine(root, "integration-final-verification-summary.json"), new
            { version = "0.7.13", ui_toggle_mounted = true, default_verification = true,
                same_frozen_root = true, supplied_initial_plan = true, same_observed_route = true, results = summaries });
    }
}
