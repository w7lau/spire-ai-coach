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

// Actual UI signals and owned native workers. A short synthetic combat proves
// manual return mechanics, not search quality, speed or arbitrary Mod support.
internal static class ManualVictoryReturnIntegration
{
    public static async Task Run(string root, SceneTree tree, LocalWorkerPool pool, SerializableRun fixture)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var run = RunState.FromSerializable(fixture);
        await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
        var player = run.Players.Single();
        player.Creature.SetMaxHpInternal(250); player.Creature.SetCurrentHpInternal(100);
        var old = player.Deck.Cards.ToArray(); player.Deck.Clear(silent: true);
        foreach (var card in old) run.RemoveCard(card);
        for (int i = 0; i < 5; i++)
        {
            var card = run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == "CLASH"), player);
            card.UpgradeInternal();
            await CardPileCmd.Add(card, player.Deck, skipVisuals: true);
        }
        await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
        await PreloadManager.LoadActAssets(run.Acts[0]);
        RunManager.Instance.Launch(); NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
        await RunManager.Instance.GenerateMap();
        // The real UI capture has no DebugEncounter override. Enter an actual
        // map node so its native replay can restore the recorded combat entry.
        await RunManager.Instance.EnterMapCoord(run.Map.GetAllMapPoints().First(p => p.PointType == MapPointType.Monster).coord);
        async Task Until(Func<bool> ready, string reason, int seconds = 60)
        {
            var timer = Stopwatch.StartNew();
            while (!ready())
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(reason);
                await Frame();
            }
        }
        await Until(LocalCapture.Stable, "Synthetic combat did not settle", 15);
        using (var settlingCapture = new StateCapture())
        {
            // Normal map entry still has initial scene notifications after its
            // play queue empties. Require a settled, unchanged native/UI root.
            var settled = typeof(LocalCapture).GetMethod("ExecutionSettled", BindingFlags.Static | BindingFlags.NonPublic)!;
            string? previous = null; var stable = Stopwatch.StartNew();
            await Until(() => {
                var snapshot = settlingCapture.Capture(false);
                var signature = LocalCapture.Stable() && (bool)settled.Invoke(null, null)! && snapshot?.CanAdvise == true
                    ? LocalCapture.Fingerprint() + snapshot.Fingerprint() : null;
                if (signature == null || signature != previous) { previous = signature; stable.Restart(); }
                return signature != null && stable.ElapsedMilliseconds >= 1000;
            }, "Native map-entry root kept changing", 15);
        }
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var overlay = new CoachOverlay(tree); overlay.Mount();
        T Field<T>(string name) => (T)typeof(CoachOverlay).GetField(name, fields)!.GetValue(overlay)!;
        void Set(string name, object value) => typeof(CoachOverlay).GetField(name, fields)!.SetValue(overlay, value);
        void Refresh() => typeof(CoachOverlay).GetMethod("RefreshSnapshot", fields)!.Invoke(overlay, null);
        Field<LocalWorkerPool>("_localPool").Dispose(); Set("_localPool", pool);
        Field<SpinBox>("_localWorkers").Value = 1;
        Field<SpinBox>("_localMaxAttempts").Value = 512;
        Field<SpinBox>("_localMaxRounds").Value = 6;
        Field<SpinBox>("_localSearchSeconds").Value = 20;
        Field<SpinBox>("_localTargetVictoryRounds").Value = 0;
        Field<CheckBox>("_localPotions").ButtonPressed = false;
        Field<CheckBox>("_localStopOnFirstWin").ButtonPressed = false;
        Refresh();
        var button = Field<Button>("_useVictory");
        if (button.Name != "LocalUseWinningRoute" || !button.Disabled)
            throw new InvalidOperationException("Manual return was enabled before any victory");
        var nativeBefore = LocalCapture.Fingerprint();
        var sourceSnapshot = Field<StateCapture>("_capture").Capture(Field<CoachSettings>("_settings").RevealDrawOrder)!;
        var samples = new List<object>();
        foreach (var algorithm in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
        foreach (bool skip in new[] { false, true })
        {
            Field<CheckBox>("_localStopOnZeroLoss").ButtonPressed = skip;
            Field<CheckBox>("_localSkipVerification").ButtonPressed = skip;
            var picker = Field<OptionButton>("_localPlayCard");
            picker.Select(skip ? 1 : 0);
            picker.EmitSignal(OptionButton.SignalName.ItemSelected, (long)picker.Selected);
            Refresh();
            var search = Field<Button>(algorithm == LocalSearchOrder.MonteCarlo ? "_localAnalyze" : "_turnAnalyze");
            if (search.Disabled || !button.Disabled) throw new InvalidOperationException("Native UI cannot begin this search");
            search.EmitSignal(Button.SignalName.Pressed);
            await Until(() => {
                if (button.Disabled && Field<CancellationTokenSource>("_request") == null)
                {
                    LocalWire.Write(Path.Combine(root, "manual-ended-source-private.json"), new {
                        before = sourceSnapshot, after = Field<StateCapture>("_capture").Capture(Field<CoachSettings>("_settings").RevealDrawOrder),
                        nativeBefore, nativeAfter = LocalCapture.Fingerprint(), status = Field<Label>("_status").Text });
                    throw new InvalidOperationException("Search ended before manual return: " + Field<Label>("_status").Text + "; " + Field<RichTextLabel>("_advice").Text);
                }
                return !button.Disabled;
            }, "Native victory did not enable the return button");
            if (Field<CancellationTokenSource>("_request") == null || Field<bool>("_executing"))
                throw new InvalidOperationException("Search ended or execution began before the user's stop choice");
            var control = Field<LocalVictoryReturn>("_victoryReturn");
            if (!control.CanRequest) throw new InvalidOperationException("Button and frozen manual choice diverged");
            var stop = Stopwatch.StartNew();
            button.EmitSignal(Button.SignalName.Pressed);
            if (!control.Requested || !button.Disabled || Field<bool>("_executing"))
                throw new InvalidOperationException("Actual UI click failed to request a search-only stop");
            await Until(() => Field<CancellationTokenSource>("_request") == null, "Manual return did not finish");
            Refresh();
            if (Field<Button>("_execute").Disabled || !button.Disabled || Field<bool>("_executing"))
                throw new InvalidOperationException("Manual route was discarded or automatically executed: " + Field<Label>("_status").Text);
            var captured = Field<LocalSearchRequest>("_lastSearchRequest");
            if (captured.ReuseSnapshotMetadata)
                throw new InvalidOperationException("Manual UI search enabled the opt-in snapshot experiment");
            var result = Field<LocalContinuation>("_continuation").Advance(sourceSnapshot.CombatId,
                captured.LoadedMods, nativeBefore, captured.History!)!;
            if (result.Status != "done" || !result.StoppedOnManualVictory || !result.StoppedEarly ||
                result.StoppedOnFirstWin || result.StoppedOnCardGoals || result.StoppedOnMinimum ||
                result.Best is not { Won: true, Dead: false } || !LocalSearchPolicy.HasExecutionPoints(result) ||
                result.Timing?.Verifications != (skip ? 0 : 1) || result.MinimumLoss != null ||
                result.Failure != null || result.RecoveredFailures is { Length: > 0 } ||
                result.Evidence is not { ManualStopped: true, GoalStopped: false } ||
                !Field<RichTextLabel>("_advice").Text.Contains("手动停止"))
                throw new InvalidOperationException("Native manual result lost its win, checkpoints, verification setting or stop reason");
            if (LocalCapture.Fingerprint() != nativeBefore ||
                Field<StateCapture>("_capture").Capture(Field<CoachSettings>("_settings").RevealDrawOrder)!.Fingerprint() != sourceSnapshot.Fingerprint())
                throw new InvalidOperationException("Manual adoption changed the source game");
            LocalWire.Write(Path.Combine(root, $"manual-{algorithm}-{skip}-private.json"), result);
            samples.Add(new { algorithm = algorithm.ToString(), skipFinalVerification = skip, autoStopEnabled = skip,
                cardGoalsEnabled = captured.CardGoals?.Enabled == true, snapshotMetadataEnabled = captured.ReuseSnapshotMetadata,
                result.Evaluated, result.Victories,
                finalHp = result.Best.Hp, result.Best.HpChange, finalVerifications = result.Timing.Verifications,
                stopToResultMs = stop.ElapsedMilliseconds, usableExecutionPoints = true, executeButtonEnabled = true,
                actualButtonSignal = true, automaticExecution = false, sourceCombatUnchanged = true });
            pool.DiscardSearch();
            // Discard the old plan before the next calculation so this probe
            // never seeds a search with a previously measured answer.
            Set("_continuation", null!); Refresh();
        }
        var layout = new List<object>();
        var panel = Field<PanelContainer>("_panel");
        foreach (var size in new[] { new Vector2I(1280, 720), new Vector2I(1920, 1080) })
        {
            tree.Root.ContentScaleSize = size;
            tree.Root.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
            typeof(CoachOverlay).GetMethod("Resize", fields)!.Invoke(overlay, null);
            for (int i = 0; i < 5; i++) await Frame();
            var rect = panel.GetGlobalRect(); var actionRect = button.GetGlobalRect();
            if (!panel.IsVisibleInTree() || actionRect.Position.X < rect.Position.X || actionRect.End.X > rect.End.X ||
                actionRect.Position.Y < rect.Position.Y || actionRect.End.Y > rect.End.Y || rect.End.Y > tree.Root.GetVisibleRect().End.Y + 1)
                throw new InvalidOperationException("Manual return button overflowed the fixed panel at " + size);
            await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            tree.Root.GetTexture().GetImage().SavePng(Path.Combine(root, $"manual-return-{size.X}x{size.Y}.png"));
            layout.Add(new { width = size.X, height = size.Y, fixedButtonVisible = true, overflow = false });
        }
        LocalWire.Write(Path.Combine(root, "integration-manual-summary.json"), new {
            version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3), passed = true, nativeGame = true,
            scope = "Actual manual-return UI button; two algorithms; final verification on/off; owned synthetic native combat", samples, layout });
    }
}
