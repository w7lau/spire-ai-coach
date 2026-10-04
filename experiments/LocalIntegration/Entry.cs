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
            await Frame(); await Frame();
            while (NAssetLoader.Instance.IsProcessing()) await Frame();
            SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_PRESENTATION_SMOKE") == "1")
            {
                await PresentationIntegration.Run(root, tree);
                File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_REPLAY") is { Length: > 0 } replayPath)
            {
                await ReplayIntegration.Run(root, pool, replayPath, System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_REPLAY_GAME")!);
                File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
                return;
            }
            var save = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root, "fixture.json")), JsonSerializationUtility.GetTypeInfo<SerializableRun>())!;
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_FOLLOWUP_TEST") == "1")
            {
                await FollowupIntegration.Run(root, tree, save);
                File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_CARD_GOALS_TEST") == "1")
            {
                await CardGoalIntegration.Run(root, tree, pool, save);
                File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SELECTION_MATRIX") == "1")
            {
                await SelectionMatrixIntegration.Run(root, tree, save);
                File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_SURVIVAL_TEST") == "1")
            {
                await SurvivalIntegration.Run(root, tree, save);
                File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_MECHANICS") == "1")
            {
                await MechanicsIntegration.Run(root, tree, pool, save);
                File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
                return;
            }
            var run = RunState.FromSerializable(save);
            await RunManager.Instance.SetUpSavedSingleplayer(run, save);
            await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
            await PreloadManager.LoadActAssets(run.Acts[0]);
            RunManager.Instance.Launch();
            NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await RunManager.Instance.GenerateMap();
            bool fallbackFixture = System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_FALLBACK") == "1";
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_CHOICES") == "1" || fallbackFixture ||
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_CHECKPOINT") == "1")
            {
                var owner = LocalContext.GetMe(run)!;
                foreach (var potion in owner.Potions.ToArray()) potion.Discard();
                if (!fallbackFixture) await PotionCmd.TryToProcure(ModelDb.AllPotions.Single(p => p.Id.Entry == "ATTACK_POTION").ToMutable(), owner, 0);
                var choiceFixtureCards = owner.Deck.Cards.ToArray();
                owner.Deck.Clear(silent: true);
                foreach (var card in choiceFixtureCards) run.RemoveCard(card);
                for (int i = 0; i < 5; i++)
                    await CardPileCmd.Add(run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == (fallbackFixture ? "ARMAMENTS" : "DEFEND_IRONCLAD")), owner), owner.Deck, skipVisuals: true);
            }
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_FEATURES") == "1")
            {
                var owner = LocalContext.GetMe(run)!;
                foreach (var potion in owner.Potions.ToArray()) potion.Discard();
                foreach (var (id, slot) in new[] { ("FIRE_POTION", 0), ("BLOCK_POTION", 1), ("FAIRY_IN_A_BOTTLE", 2) })
                    await PotionCmd.TryToProcure(ModelDb.AllPotions.Single(p => p.Id.Entry == id).ToMutable(), owner, slot);
            }
            await RunManager.Instance.EnterMapCoord(run.Map.GetAllMapPoints().First(p => p.PointType == MapPointType.Monster).coord);
            var player = LocalContext.GetMe(run)!;
            var capture = new StateCapture();
            while (capture.Capture(true)?.CanAdvise != true) await Frame();
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_CONCURRENCY_TEST") == "1")
            {
                await ConcurrencyIntegration.Run(root, pool, capture);
                File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_CHECKPOINT") == "1")
            {
                await CheckpointIntegration.Run(root, tree, pool, capture, player);
                File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
                return;
            }
            if (fallbackFixture)
            {
                var request = LocalCapture.Capture(capture.Capture(true)!.Fingerprint(), true) with
                    { Workers = 1, MaxNodes = 3, MaxRounds = 1, BudgetSeconds = 35 };
                var installation = LocalCapture.Installation();
                var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
                LocalWire.Write(Path.Combine(root, "integration-fallback.json"), result);
                // The old --fallback case used Armaments as an unsupported action. Native hand
                // selection now supports it, so this fixture must no longer exclude the card.
                if (result.Rejected != 0 || result.Status != "done" || result.Message.Contains("未纳入") ||
                    result.Best?.Continuation?.Length != result.Best?.Actions.Length || result.Best is not { Actions.Length: > 0 } ||
                    !result.Best.Actions.Any(a => a.Choices is { Length: > 0 }) || request.NativeHash != LocalCapture.Fingerprint())
                    throw new InvalidOperationException("Native hand selection was excluded or did not yield a verified result");
                File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_CHOICES") == "1")
            {
                await ChoiceIntegration.Run(root, tree, pool, capture, player);
                File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_EXECUTION") == "1")
            {
                await ExecutionIntegration.Run(root, tree, pool, capture, player);
                File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_OPTIMIZATION") == "1")
            {
                await OptimizationIntegration.Run(root, tree, pool, capture, player);
                File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_FEATURES") == "1")
            {
                await FeatureIntegration.Run(root, tree, pool, capture, player);
                File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_BENCHMARK") == "1")
            {
                var before = LocalCapture.Fingerprint();
                var benchmark = LocalCapture.Capture(capture.Capture(true)!.Fingerprint(), true);
                var installation = LocalCapture.Installation();
                // Warm each size separately, then use the same frozen state and 20s budget.
                foreach (int workers in new[] { 2, 4 })
                {
                    await Task.Run(() => pool.Analyze(benchmark with { Id = Guid.NewGuid().ToString("N"), Workers = workers, MaxNodes = 1 }, installation, _ => { }, CancellationToken.None));
                    var result = await Task.Run(() => pool.Analyze(benchmark with { Id = Guid.NewGuid().ToString("N"), Workers = workers, MaxNodes = 128, BudgetSeconds = 20 }, installation, _ => { }, CancellationToken.None));
                    records.Add(new { stage = "benchmark", workers, unchanged = before == LocalCapture.Fingerprint(), result });
                    LocalWire.Write(Path.Combine(root, "integration-benchmark.json"), records);
                    if (before != LocalCapture.Fingerprint() || result.Best?.Won != true) throw new InvalidOperationException("Benchmark changed host or did not complete combat");
                }
                File.WriteAllText(Path.Combine(root, "integration-success"), "passed");
                return;
            }
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
                    { MaxNodes = System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_INTEGRATION_QUICK") == "1" ? 2 : 6, BudgetSeconds = 45, Workers = 4 };
                var installation = LocalCapture.Installation();
                LocalWire.Write(Path.Combine(root, "integration-" + stage + "-request.json"), request);
                var result = await Task.Run(() => pool.Analyze(request, installation,
                    message => { lock (records) File.WriteAllText(Path.Combine(root, "integration-progress.txt"), message); }, CancellationToken.None));
                var unchanged = LocalCapture.Fingerprint() == before;
                records.Add(new { stage, unchanged, result });
                LocalWire.Write(Path.Combine(root, "integration-result.json"), records);
                if (!unchanged || result.Best == null) throw new InvalidOperationException("Integration did not produce a valid isolated result");
                if (result.Best.Won != true || result.Best.Rounds < 2 || !result.Best.Actions.Any(a => a.EndTurn))
                    throw new InvalidOperationException("Expected a native victory spanning multiple player rounds");
            }
            var limitedHash = LocalCapture.Fingerprint();
            var limitedRequest = LocalCapture.Capture(capture.Capture(true)!.Fingerprint(), false) with { MaxRounds = 1, MaxNodes = 1, Workers = 4 };
            var limitedInstall = LocalCapture.Installation();
            var limited = await Task.Run(() => pool.Analyze(limitedRequest, limitedInstall, _ => { }, CancellationToken.None));
            records.Add(new { stage = "round_limit", unchanged = limitedHash == LocalCapture.Fingerprint(), result = limited });
            if (limited.Best == null || limited.Best.Won || !limited.Best.StopReason.Contains("轮数上限") || limitedHash != LocalCapture.Fingerprint())
                throw new InvalidOperationException("Round limit falsely claimed victory or modified host");
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
            var lethalRequest = LocalCapture.Capture(capture.Capture(true)!.Fingerprint(), true) with { MaxNodes = 3, Workers = 4 };
            var lethalInstall = LocalCapture.Installation();
            var lethal = await Task.Run(() => pool.Analyze(lethalRequest, lethalInstall, _ => { }, CancellationToken.None));
            records.Add(new { stage = "victory", unchanged = lethalHash == LocalCapture.Fingerprint(), live_hp = lethalHp, result = lethal });
            LocalWire.Write(Path.Combine(root, "integration-result.json"), records);
            if (lethal.Best?.Won != true || lethal.Best.Hp != Math.Min(player.Creature.MaxHp, lethalHp + 6) || lethalHash != LocalCapture.Fingerprint())
                throw new InvalidOperationException("Victory did not settle native Burning Blood healing or changed the live state");
            var finalHash = LocalCapture.Fingerprint();
            var finalRequest = LocalCapture.Capture(capture.Capture(true)!.Fingerprint(), false) with { Workers = 4 };
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
