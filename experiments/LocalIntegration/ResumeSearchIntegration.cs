using System.Diagnostics;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Fixed short native continuation probe. The low trial count creates a pause;
// it is not a search-quality or speed benchmark and does not alter product limits.
internal static class ResumeSearchIntegration
{
    public static async Task Run(string root, SceneTree tree, LocalWorkerPool pool, SerializableRun fixture)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control()); await Frame(); await Frame();
        var run = RunState.FromSerializable(fixture);
        await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
        var player = run.Players.Single(); player.Creature.SetCurrentHpInternal(1);
        var old = player.Deck.Cards.ToArray(); player.Deck.Clear(silent: true);
        foreach (var c in old) run.RemoveCard(c);
        for (int i = 0; i < 5; i++)
            await CardPileCmd.Add(run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == "STRIKE_IRONCLAD"), player),
                player.Deck, skipVisuals: true);
        await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
        await PreloadManager.LoadActAssets(run.Acts[0]);
        RunManager.Instance.Launch(); NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(run));
        await RunManager.Instance.GenerateMap();
        await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster,
            ModelDb.AllEncounters.Single(e => e.Id.Entry == "SLUMBERING_BEETLE_NORMAL").ToMutable(), false);
        var settle = Stopwatch.StartNew();
        while (!LocalCapture.Stable()) {
            if (settle.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Native fixture did not settle");
            await Frame();
        }
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var overlay = new CoachOverlay(tree); overlay.Mount();
        T Field<T>(string name) => (T)typeof(CoachOverlay).GetField(name, fields)!.GetValue(overlay)!;
        void Set(string name, object value) => typeof(CoachOverlay).GetField(name, fields)!.SetValue(overlay, value);
        void Refresh() => typeof(CoachOverlay).GetMethod("RefreshSnapshot", fields)!.Invoke(overlay, null);
        Field<LocalWorkerPool>("_localPool").Dispose(); Set("_localPool", pool);
        Field<SpinBox>("_localWorkers").Value = 1;
        Field<SpinBox>("_localMaxAttempts").Value = 4;
        Field<SpinBox>("_localMaxRounds").Value = 6;
        Field<SpinBox>("_localSearchSeconds").Value = 25;
        Field<SpinBox>("_localTargetVictoryRounds").Value = 0;
        foreach (var name in new[] { "_localPotions", "_localStopOnZeroLoss", "_localStopOnFirstWin", "_localSkipVerification" })
            Field<CheckBox>(name).ButtonPressed = false;
        Refresh();
        var capture = Field<StateCapture>("_capture");
        bool reveal = Field<CoachSettings>("_settings").RevealDrawOrder;
        var snapshot = capture.Capture(reveal)!;
        var captured = LocalCapture.Capture(snapshot.Fingerprint(), false) with {
            Workers = 1, MaxNodes = 4, MaxDepth = 40, MaxRounds = 6, BudgetSeconds = 25,
            ShareSearchWork = true, MemorySearchWork = true, SkipFinalVerification = false,
            StopOnZeroLoss = false, StopOnFirstWin = false, DebugEncounter = "SLUMBERING_BEETLE_NORMAL" };
        var initialNative = LocalCapture.Fingerprint();
        var sourceSnapshot = capture.Capture(reveal)!;
        LocalWire.Write(Path.Combine(root, "integration-resume-search-source-before-private.json"), new { initialNative, sourceSnapshot });
        var samples = new List<object>();
        foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
        {
            var request = captured with { Id = Guid.NewGuid().ToString("N"), SearchOrder = order };
            var first = await Task.Run(() => pool.Analyze(request, LocalCapture.Installation(), _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, $"integration-resume-search-{order}-first-private.json"), first);
            if (first.SearchProgress is not { Batch: 1, Pending: > 0, CanContinue: true, Resumed: false } ||
                first.Victories != 0 || first.Best is not { Dead: true } || LocalSearchPolicy.HasExecutionPoints(first) ||
                !pool.CanResume(request))
                throw new InvalidOperationException("Native losing first batch did not retain its pending frontier: " + first.Message);
            Set("_lastSearchRequest", request); Set("_lastLocalOrder", order); Refresh();
            if (Field<Button>("_continueOptimize").Disabled || !Field<Button>("_execute").Disabled)
                throw new InvalidOperationException("Native UI tied search continuation to an executable plan");
            Field<CheckBox>("_localPotions").ButtonPressed = true; Refresh();
            if (!Field<Button>("_continueOptimize").Disabled)
                throw new InvalidOperationException("Native UI allowed resuming a changed search goal");
            Field<CheckBox>("_localPotions").ButtonPressed = false; Refresh();
            // The actual button captures again. Reusing the old request alone
            // would not exercise native replay serialization and history matching.
            var recaptured = LocalCapture.Capture(snapshot.Fingerprint(), true);
            var next = request with { Id = recaptured.Id, Replay = recaptured.Replay, NativeHash = recaptured.NativeHash,
                History = recaptured.History, ModelHash = recaptured.ModelHash, LoadedMods = recaptured.LoadedMods,
                EventEntry = recaptured.EventEntry, TargetLabels = recaptured.TargetLabels,
                ContinueOptimization = true, MaxNodes = 3, BudgetSeconds = 30 };
            if (!pool.CanResume(next) || pool.CanResume(next with { IncludePotions = true }) ||
                pool.CanResume(next with { NativeHash = "different-native-root" }))
                throw new InvalidOperationException("Resume matching weakened its native root or goal boundary");
            var second = await Task.Run(() => pool.Analyze(next, LocalCapture.Installation(), _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, $"integration-resume-search-{order}-second-private.json"), second);
            if (second.SearchProgress is not { Batch: 2, Resumed: true } p ||
                p.TotalEvaluated != first.Evaluated + second.Evaluated || second.Victories != 0 ||
                second.Evaluated == 0 || !pool.CanResume(next) ||
                (first.RecoveredFailures?.Length ?? 0) + (second.RecoveredFailures?.Length ?? 0) != 0 ||
                first.Failure != null || second.Failure != null ||
                first.Trace!.Spans.Concat(second.Trace!.Spans).Any(s => s.Phase is "fallback" or "stop_failed_pass"))
                throw new InvalidOperationException("Native second batch did not resume cleanly");
            var sourceAfter = capture.Capture(reveal)!;
            var nativeAfter = LocalCapture.Fingerprint();
            LocalWire.Write(Path.Combine(root, $"integration-resume-search-source-after-{order}-private.json"), new { nativeAfter, sourceAfter });
            if (initialNative != nativeAfter || sourceSnapshot.Fingerprint() != sourceAfter.Fingerprint())
                throw new InvalidOperationException("Isolated search changed the source combat");
            samples.Add(new { order = order.ToString(), first.Evaluated, firstPending = first.SearchProgress.Pending,
                secondEvaluated = second.Evaluated, resumed = p.Resumed, p.Batch, p.TotalEvaluated, p.Pending,
                firstElapsedMs = first.ElapsedMs, secondElapsedMs = second.ElapsedMs,
                freshNativeCaptureMatched = true, deadCandidateCanResume = true, deadCandidateCanExecute = false, nativeContinueButtonEnabled = true,
                nativeExecuteButtonDisabled = true, nativeChangedGoalDisabled = true, failures = 0, sourceCombatUnchanged = true,
                firstWork = first.Work, secondWork = second.Work, firstTurns = first.TurnSearch, secondTurns = second.TurnSearch });
            pool.DiscardSearch();
            if (pool.CanResume(next)) throw new InvalidOperationException("Discarded frontier revived");
        }
        LocalWire.Write(Path.Combine(root, "integration-resume-search-summary.json"), new {
            version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3), passed = true,
            scope = "Two batches of native search for both parent queues; losing first result with pending work; one worker",
            productLimitsUnchanged = true, completedPrefixDeduplicationSeparatelyTested = true, samples });
    }
}
