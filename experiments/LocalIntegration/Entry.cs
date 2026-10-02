using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    public static void Initialize()
    {
        var root = System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_INTEGRATION");
        if (root == null || System.Environment.GetEnvironmentVariable("SPIRE_COACH_WORKER") != null) return;
        if (!File.Exists(Path.Combine(root, ".spire-native-probe-owner")) ||
            !string.Equals(OS.GetExecutablePath().Replace('/', '\\'), Path.Combine(root, "game", "SlayTheSpire2.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Integration requires owned private game");
        Callable.From(() => Run(root)).CallDeferred();
    }

    private static async void Run(string root)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var records = new List<object>();
        using var pool = new LocalWorkerPool(Path.Combine(root, "integration-pool"));
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        try
        {
            while (NGame.Instance == null) await Frame();
            await NGame.Instance.GameStartupComplete;
            SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
            var save = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root, "fixture.json")), JsonSerializationUtility.GetTypeInfo<SerializableRun>())!;
            var run = RunState.FromSerializable(save);
            await RunManager.Instance.SetUpSavedSingleplayer(run, save);
            await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
            await PreloadManager.LoadActAssets(run.Acts[0]);
            RunManager.Instance.Launch();
            NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await RunManager.Instance.GenerateMap();
            await RunManager.Instance.EnterMapCoord(run.Map.GetAllMapPoints().First(p => p.PointType == MapPointType.Monster).coord);
            var player = LocalContext.GetMe(run)!;
            var capture = new StateCapture();
            while (capture.Capture(true)?.CanAdvise != true) await Frame();
            foreach (var stage in new[] { "root", "after_play", "after_turn" })
            {
                if (stage == "after_play")
                {
                    var card = player.PlayerCombatState!.Hand.Cards.Single(c => c.Id.Entry == "BLOODLETTING");
                    RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, null));
                    await Frame(); await RunManager.Instance.ActionQueueSet.BecameEmpty();
                    while (capture.Capture(true)?.CanAdvise != true) await Frame();
                }
                if (stage == "after_turn")
                {
                    var round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                    RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new EndPlayerTurnAction(player, player.PlayerCombatState!.TurnNumber));
                    while (CombatManager.Instance.DebugOnlyGetState()!.RoundNumber <= round || capture.Capture(true)?.CanAdvise != true) await Frame();
                }
                var before = LocalCapture.Fingerprint();
                var request = LocalCapture.Capture(capture.Capture(true)!.Fingerprint(), false) with
                    { MaxNodes = System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_INTEGRATION_QUICK") == "1" ? 2 : 6, BudgetSeconds = 45 };
                var installation = LocalCapture.Installation();
                LocalWire.Write(Path.Combine(root, "integration-" + stage + "-request.json"), request);
                var result = await Task.Run(() => pool.Analyze(request, installation,
                    message => { lock (records) File.WriteAllText(Path.Combine(root, "integration-progress.txt"), message); }, CancellationToken.None));
                var unchanged = LocalCapture.Fingerprint() == before;
                records.Add(new { stage, unchanged, result });
                LocalWire.Write(Path.Combine(root, "integration-result.json"), records);
                if (!unchanged || result.Best == null) throw new InvalidOperationException("Integration did not produce a valid isolated result");
            }
            // A separate synthetic lethal route verifies that victory healing has fully settled.
            RunManager.Instance.CleanUp();
            NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control());
            await Frame(); await Frame();
            save = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root, "fixture.json")), JsonSerializationUtility.GetTypeInfo<SerializableRun>())!;
            run = RunState.FromSerializable(save);
            await RunManager.Instance.SetUpSavedSingleplayer(run, save);
            RunManager.Instance.Launch();
            NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await RunManager.Instance.GenerateMap();
            player = LocalContext.GetMe(run)!;
            var oldCards = player.Deck.Cards.ToArray();
            player.Deck.Clear(silent: true);
            foreach (var card in oldCards) run.RemoveCard(card);
            foreach (var id in new[] { "WHIRLWIND", "BLOODLETTING", "BLOODLETTING", "BLOODLETTING", "BLOODLETTING" })
                await CardPileCmd.Add(run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player), player.Deck, skipVisuals: true);
            await RunManager.Instance.EnterMapCoord(run.Map.GetAllMapPoints().First(p => p.PointType == MapPointType.Monster).coord);
            while (capture.Capture(true)?.CanAdvise != true) await Frame();
            foreach (var card in player.PlayerCombatState!.Hand.Cards.Where(c => c.Id.Entry == "BLOODLETTING").ToArray())
            {
                RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, null));
                await Frame(); await RunManager.Instance.ActionQueueSet.BecameEmpty();
                while (capture.Capture(true)?.CanAdvise != true) await Frame();
            }
            var lethalHash = LocalCapture.Fingerprint();
            var lethalHp = player.Creature.CurrentHp;
            var lethalRequest = LocalCapture.Capture(capture.Capture(true)!.Fingerprint(), true) with { MaxNodes = 3 };
            var lethalInstall = LocalCapture.Installation();
            var lethal = await Task.Run(() => pool.Analyze(lethalRequest, lethalInstall, _ => { }, CancellationToken.None));
            records.Add(new { stage = "victory", unchanged = lethalHash == LocalCapture.Fingerprint(), live_hp = lethalHp, result = lethal });
            LocalWire.Write(Path.Combine(root, "integration-result.json"), records);
            if (lethal.Best?.Won != true || lethal.Best.Hp != Math.Min(player.Creature.MaxHp, lethalHp + 6) || lethalHash != LocalCapture.Fingerprint())
                throw new InvalidOperationException("Victory did not settle native Burning Blood healing or changed the live state");
            var finalHash = LocalCapture.Fingerprint();
            var finalRequest = LocalCapture.Capture(capture.Capture(true)!.Fingerprint(), false);
            var finalInstall = LocalCapture.Installation();
            using (var cancel = new CancellationTokenSource(500))
            {
                try
                {
                    await Task.Run(() => pool.Analyze(finalRequest, finalInstall, _ => { }, cancel.Token));
                    throw new InvalidOperationException("Cancellation did not propagate");
                }
                catch (OperationCanceledException) { records.Add(new { stage = "cancel", unchanged = finalHash == LocalCapture.Fingerprint() }); }
            }
            try
            {
                await Task.Run(() => pool.Analyze(finalRequest with { Id = Guid.NewGuid().ToString("N"), NativeHash = "wrong-root" }, finalInstall, _ => { }, CancellationToken.None));
                throw new InvalidOperationException("Wrong root hash was accepted");
            }
            catch (CoachException ex) { records.Add(new { stage = "wrong_root", category = ex.Category, unchanged = finalHash == LocalCapture.Fingerprint() }); }
            if (finalHash != LocalCapture.Fingerprint()) throw new InvalidOperationException("Live state changed during failure tests");
            LocalWire.Write(Path.Combine(root, "integration-result.json"), records);
            capture.Dispose();
            File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(root, "integration-error.txt"), ex.ToString()); }
        finally { tree.Quit(); }
    }
}
