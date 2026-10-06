using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Normal native fleeing and killing in an owned artificial encounter. No rule
// patches, forced Escape/Kill commands, frozen-state mutations or RNG edits.
internal static class EscapeVictoryIntegration
{
    public static async Task Run(string root, SceneTree tree, LocalWorkerPool pool, SerializableRun fixture)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var encounters = ModelDb.AllEncounters.Where(e => e.AllPossibleMonsters.Any() &&
            e.AllPossibleMonsters.All(m => m.GetType().Name == "ThievingHopper")).ToArray();
        if (encounters.Length == 0) throw new InvalidOperationException("Native all-hopper encounter absent");
        var pureEncounter = encounters.OrderBy(e => e.Id.Entry).First();
        var mixedEncounters = ModelDb.AllEncounters.Where(e =>
            e.AllPossibleMonsters.Any(m => m.GetType().Name == "FatGremlin") &&
            e.AllPossibleMonsters.Any(m => m.GetType().Name == "GremlinMerc")).ToArray();
        LocalWire.Write(Path.Combine(root, "integration-escape-catalogue-private.json"), ModelDb.AllEncounters
            .Where(e => e.AllPossibleMonsters.Any(m => m.GetType().Name is "ThievingHopper" or "FatGremlin"))
            .Select(e => new { id = e.Id.Entry, monsters = e.AllPossibleMonsters.Select(m => m.GetType().Name).ToArray() }));
        if (mixedEncounters.Length == 0) throw new InvalidOperationException("Native mixed-gremlin encounter absent; see private catalogue");
        bool ended = false, nativeWin = false;
        void Ended(CombatRoom _) => ended = true;
        void Won(CombatRoom _) => nativeWin = true;
        CombatManager.Instance.CombatEnded += Ended;
        CombatManager.Instance.CombatWon += Won;
        var samples = new List<object>();
        object? layoutProof = null;
        var expectedLayout = System.Environment.GetEnvironmentVariable("SPIRE_ESCAPE_EXPECTED_LAYOUT");
        string CodeHash(string directory) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(
            Directory.EnumerateFiles(Path.Combine(directory, "game", "mods"), "SpireAiCoach.dll", SearchOption.AllDirectories).Single())));
        var priorCodes = new Dictionary<string, string>();
        var layoutDirectory = Path.Combine(root, ".spire-ai-coach-workers");
        if (Directory.Exists(layoutDirectory))
            foreach (var group in Directory.EnumerateDirectories(layoutDirectory))
                foreach (var prior in Directory.EnumerateDirectories(group, "worker-*"))
                    if (File.Exists(Path.Combine(prior, ".coach-worker")) && Directory.Exists(Path.Combine(prior, "game", "mods")) &&
                        Directory.EnumerateFiles(Path.Combine(prior, "game", "mods"), "SpireAiCoach.dll", SearchOption.AllDirectories).Count() == 1)
                        priorCodes[prior] = CodeHash(prior);
        string? previousCode = null;
        if (!string.IsNullOrEmpty(expectedLayout))
        {
            expectedLayout = Path.GetFullPath(expectedLayout);
            if (!expectedLayout.StartsWith(Path.GetFullPath(Path.Combine(root, ".spire-ai-coach-workers")) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(expectedLayout, ".coach-worker")))
                throw new InvalidOperationException("Expected layout must be an owned worker of this test host");
            previousCode = CodeHash(expectedLayout);
        }
        try
        {
            foreach (string kind in new[] { "all-escape", "mixed", "defeated" })
            {
                var encounter = kind == "mixed" ? mixedEncounters.OrderBy(e => e.Id.Entry).First() : pureEncounter;
                if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
                NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control()); await Frame(); await Frame();
                ended = nativeWin = false;
                var run = RunState.FromSerializable(fixture);
                await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
                typeof(RunManager).GetProperty(nameof(RunManager.ShouldSave))!.SetValue(RunManager.Instance, false);
                var player = run.Players.Single();
                player.Creature.SetMaxHpInternal(2000); player.Creature.SetCurrentHpInternal(2000);
                foreach (var relic in player.Relics.ToArray()) player.RemoveRelicInternal(relic, silent: true);
                foreach (string relic in new[] { "AKABEKO", "CHEMICAL_X", "VAJRA" })
                    await RelicCmd.Obtain(ModelDb.AllRelics.Single(r => r.Id.Entry == relic).ToMutable(), player);
                foreach (var potion in player.Potions.ToArray()) potion.Discard();
                var old = player.Deck.Cards.ToArray(); player.Deck.Clear(silent: true);
                foreach (var card in old) run.RemoveCard(card);
                for (int i = 0; i < 5; i++)
                {
                    var card = run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == (i == 0 ? "WHIRLWIND" : "CLASH")), player);
                    card.UpgradeInternal(); await CardPileCmd.Add(card, player.Deck, skipVisuals: true);
                }
                await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
                await PreloadManager.LoadActAssets(run.Acts[0]);
                RunManager.Instance.Launch(); NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(run));
                await RunManager.Instance.GenerateMap();
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster, encounter.ToMutable(), false);
                while (!LocalCapture.Stable()) await Frame();
                var state = CombatManager.Instance.DebugOnlyGetState()!;
                var originalEnemies = state.Enemies.ToArray();
                var target = kind == "mixed" ? originalEnemies.First(e => e.Monster!.GetType().Name == "GremlinMerc") : originalEnemies[0];
                var capture = new StateCapture(); var snapshot = capture.Capture(true)!;
                var request = LocalCapture.Capture(snapshot.Fingerprint(), true) with
                {
                    Workers = 1, Partitions = 1, MaxNodes = 1, MaxRounds = 12, BudgetSeconds = 20,
                    StopOnFirstWin = true, StopOnZeroLoss = true, ShareSearchWork = false,
                    DebugEncounter = encounter.Id.Entry
                };
                LocalWire.Write(Path.Combine(root, $"integration-escape-{kind}-root-private.json"), request);
                if (layoutProof == null)
                {
                    var before = LocalCapture.Fingerprint();
                    await Task.Run(() => pool.Prepare(LocalCapture.Installation(), 8, CancellationToken.None));
                    var workers = (Array)typeof(LocalWorkerPool).GetField("_workers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(pool)!;
                    string RootAt(int i) => (string)workers.GetValue(i)!.GetType().GetProperty("Root")!.GetValue(workers.GetValue(i))!;
                    var currentCode = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(LocalWorkerPool).Assembly.Location)));
                    bool reused = !string.IsNullOrEmpty(expectedLayout) ? RootAt(0) == expectedLayout : priorCodes.TryGetValue(RootAt(0), out previousCode);
                    if (before != LocalCapture.Fingerprint() || pool.Resources().Ready != 8 ||
                        Enumerable.Range(0, 8).Any(i => CodeHash(RootAt(i)) != currentCode) ||
                        !string.IsNullOrEmpty(expectedLayout) && (!reused || previousCode == currentCode))
                        throw new InvalidOperationException("Eight native lanes did not preserve the expected old layout and prepare the new code");
                    layoutProof = new { ready = 8, previousLayoutReused = reused, previousCodeSha256 = previousCode,
                        currentCodeSha256 = currentCode, allEightCodeCopiesMatched = true,
                        sourceFingerprintUnchanged = true, cacheRemoved = false, codeGenerationRestarted = true };
                }
                var plan = new List<LocalAction>();
                while (!CombatManager.Instance.IsOverOrEnding)
                {
                    if (plan.Count > 32 || state.RoundNumber > 12) throw new InvalidOperationException("Natural native flee fixture exceeded its limit");
                    var hand = player.PlayerCombatState!.Hand.Cards;
                    // SurprisePower generates the followers after the leader's
                    // actual death. Let the fat gremlin flee before killing the
                    // remaining attacking follower through ordinary card play.
                    var attackTarget = !target.IsDead ? target : state.Enemies.FirstOrDefault(e =>
                        e.IsAlive && e.Monster!.GetType().Name == "SneakyGremlin");
                    var card = kind == "defeated" ? hand.FirstOrDefault(c => c.Id.Entry == "WHIRLWIND" && c.CanPlay()) :
                        kind == "mixed" && attackTarget != null && (!target.IsDead || state.EscapedCreatures.Count > 0) ?
                            hand.FirstOrDefault(c => c.Id.Entry == "CLASH" && c.CanPlay() && c.IsValidTarget(attackTarget)) : null;
                    LocalAction action;
                    if (card != null)
                    {
                        action = new(hand.ToList().IndexOf(card), card.Id.ToString(), card.IsValidTarget(null) ? null : attackTarget!.CombatId,
                            card.Title, "enemy", LocalCapture.Fingerprint(), state.RoundNumber,
                            CombatCardIndex: NetCombatCard.FromModel(card).CombatCardIndex);
                        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, card.IsValidTarget(null) ? null : attackTarget));
                    }
                    else
                    {
                        action = new(-1, "", null, "", "", LocalCapture.Fingerprint(), state.RoundNumber, EndTurn: true);
                        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new EndPlayerTurnAction(player, player.PlayerCombatState.TurnNumber));
                    }
                    plan.Add(action); await Frame();
                    await RunManager.Instance.ActionQueueSet.BecameEmpty().WaitAsync(TimeSpan.FromSeconds(15));
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    while (!ended && (CombatManager.Instance.IsOverOrEnding || !LocalCapture.Stable() ||
                        action.EndTurn && state.RoundNumber == action.Round))
                    {
                        if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("Native host settlement did not complete");
                        await Frame();
                    }
                    await Frame(); await Frame();
                }
                while (!ended) await Frame();
                var escaped = state.EscapedCreatures.Where(c => c.Side == CombatSide.Enemy)
                    .Select(c => new LocalEscapedEnemy(c.CombatId, c.ModelId.ToString(), c.CurrentHp)).OrderBy(c => c.CombatId).ToArray();
                if (!nativeWin || player.Creature.IsDead || kind == "defeated" && escaped.Length != 0 ||
                    kind == "all-escape" && escaped.Length != originalEnemies.Length ||
                    kind == "mixed" && (!target.IsDead || escaped.Length == 0 ||
                        state.Enemies.Any(e => e.IsAlive)))
                    throw new InvalidOperationException("Native escape fixture did not exercise the expected outcome: " + kind + ", escaped=" + escaped.Length);
                var expected = new LocalCombatOutcome(true, nativeWin, escaped);
                int hp = player.Creature.CurrentHp, gold = player.Gold;
                foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
                {
                    string? points = null;
                    foreach (bool skip in new[] { false, true })
                    {
                        var command = request with { Id = Guid.NewGuid().ToString("N"), SearchOrder = order,
                            InitialPlan = plan.ToArray(), SkipFinalVerification = skip };
                        var result = await Task.Run(() => pool.Analyze(command, LocalCapture.Installation(), _ => { }, CancellationToken.None));
                        LocalWire.Write(Path.Combine(root, $"integration-escape-{kind}-{order}-{skip}-private.json"), result);
                        bool win = kind == "defeated";
                        if (result.Status != "done" || result.Best is not { } best || best.Won != win ||
                            result.Victories != (win ? 1 : 0) || result.StoppedOnFirstWin != win ||
                            result.StoppedOnManualVictory || result.StoppedOnHealthTarget || result.StoppedOnCardGoals ||
                            result.StoppedOnMinimum || !expected.Matches(best.CombatOutcome) ||
                            !LocalSearchPolicy.HasExecutionPoints(result) || best.Hp != hp || best.Gold != gold ||
                            result.Timing?.Verifications != (skip ? 0 : 1) || result.VerificationSkipped != skip ||
                            result.RecoveredFailures is { Length: > 0 } ||
                            player.Creature.CurrentHp != hp || player.Gold != gold)
                            throw new InvalidOperationException("Native escape candidate/replay failed: " + kind + "/" + order + "/" + skip + ": " +
                                JsonSerializer.Serialize(new { result.Status, result.Message, result.Victories, result.StoppedOnFirstWin, result.Best?.CombatOutcome }));
                        var actualPoints = JsonSerializer.Serialize(best.Continuation);
                        if (points != null && points != actualPoints) throw new InvalidOperationException("Skipped and independent replay state/RNG/history checkpoints differ");
                        points = actualPoints;
                        if (!win && (result.Evidence?.TerminalEscapes != 1 || result.Evidence.TerminalWins != 0 ||
                            !LocalSearchPolicy.FormatAdvice(result).Contains("敌人逃跑")))
                            throw new InvalidOperationException("Native escape advice or terminal statistics claimed victory");
                        samples.Add(new { kind, encounter = encounter.Id.Entry, algorithm = order.ToString(), skippedFinalReplay = skip, nativeVictory = nativeWin,
                            escapedEnemies = escaped.Length, best.Won, result.Victories, result.StoppedOnFirstWin,
                            checkpoints = best.Actions.Length, verifications = result.Timing.Verifications, hostUnchanged = true });
                    }
                }
                LocalWire.Write(Path.Combine(root, "integration-escape-summary.json"), new {
                    version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3), passed = samples.Count == 12, encounter = encounter.Id.Entry, layoutProof, samples,
                    scope = "Seeded natural native escape/mixed kill/complete kill; both algorithms with final replay on/off. No unseeded search performance or arbitrary Mod claim." });
            }
        }
        finally { CombatManager.Instance.CombatEnded -= Ended; CombatManager.Instance.CombatWon -= Won; }
    }
}
