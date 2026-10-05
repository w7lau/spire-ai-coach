using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Native catalogue plus bounded, seeded enemy-turn comparisons. This is a
// compatibility probe, not exhaustive route coverage or a strategy benchmark.
// All fixture changes occur inside the marker/executable-guarded private host.
internal static class EnemyCompatibilityIntegration
{
    public static async Task Run(string root, SceneTree tree, SerializableRun fixture)
    {
        var catalogue = ModelDb.AllEncounters.OrderBy(e => e.Id.Entry).ToArray();
        var monsters = catalogue.SelectMany(e => e.AllPossibleMonsters).Select(m => m.Id.Entry).Distinct().Order().ToArray();
        LocalWire.Write(Path.Combine(root, "integration-enemy-catalogue.json"), new {
            nativeModule = typeof(MonsterModel).Assembly.ManifestModule.ModuleVersionId,
            encounters = catalogue.Select(e => new { id = e.Id.Entry, monsters = e.AllPossibleMonsters.Select(m => m.Id.Entry).ToArray() }),
            monsters });
        var selected = System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_ENEMY_CASES");
        var filter = string.IsNullOrWhiteSpace(selected) ? null : selected.Split(',').ToHashSet();
        if (filter != null && filter.Except(catalogue.Select(e => e.Id.Entry)).Any())
            throw new ArgumentException("Enemy filter contains an ID absent from the native catalogue");
        int rounds = int.TryParse(System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_ENEMY_ROUNDS"), out var count) ? count : 8;
        bool regular = System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_ENEMY_FAST_ONLY") != "1";
        bool attack = System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_ENEMY_ATTACK") == "1";
        var samples = new List<object>();
        var failures = new List<object>();
        var pool = new LocalWorkerPool(Path.Combine(root, "integration-enemy-pool"));
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        void Save() => LocalWire.Write(Path.Combine(root, "integration-enemy-summary.json"), new {
            version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3),
            nativeModule = typeof(MonsterModel).Assembly.ManifestModule.ModuleVersionId,
            catalogueEncounters = catalogue.Length, catalogueMonsters = monsters.Length,
            seededEnemyTurns = attack ? 0 : rounds, attackSearch = attack, regularComparison = regular && !attack, samples, failures,
            scope = "Singleplayer synthetic native encounters; bounded seeded enemy turns; no full route coverage",
            passed = failures.Count == 0 });
        try
        {
            foreach (var encounter in catalogue.Where(e => filter == null || filter.Contains(e.Id.Entry)))
            {
                try
                {
                    if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
                    NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control()); await Frame(); await Frame();
                    var run = RunState.FromSerializable(fixture);
                    await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
                    var player = run.Players.Single();
                    player.Creature.SetMaxHpInternal(20000); player.Creature.SetCurrentHpInternal(20000);
                    foreach (var relic in player.Relics.ToArray()) player.RemoveRelicInternal(relic, silent: true);
                    foreach (var potion in player.Potions.ToArray()) potion.Discard();
                    var old = player.Deck.Cards.ToArray(); player.Deck.Clear(silent: true);
                    foreach (var c in old) run.RemoveCard(c);
                    for (int i = 0; i < 5; i++)
                    {
                        var card = run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == (attack ? "BLUDGEON" : "DEFEND_IRONCLAD")), player);
                        if (attack) card.UpgradeInternal();
                        await CardPileCmd.Add(card, player.Deck, skipVisuals: true);
                    }
                    await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
                    await PreloadManager.LoadActAssets(run.Acts[0]);
                    RunManager.Instance.Launch(); NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(run));
                    await RunManager.Instance.GenerateMap();
                    await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster, encounter.ToMutable(), false);
                    var settle = Stopwatch.StartNew();
                    while (!LocalCapture.Stable())
                    {
                        if (settle.Elapsed.TotalSeconds > 15) throw new TimeoutException("Fixture did not reach a player decision");
                        await Frame();
                    }
                    var capture = new StateCapture();
                    var snapshot = capture.Capture(true)!;
                    var sourceHash = LocalCapture.Fingerprint();
                    int firstRound = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                    var command = LocalCapture.Capture(snapshot.Fingerprint(), true) with {
                        Workers = 1, Partition = 0, Partitions = 1, MaxNodes = attack ? 2 : 1, MaxRounds = rounds, MaxDepth = 24,
                        BudgetSeconds = 30, IncludePotions = false, StopOnZeroLoss = false, StopOnFirstWin = false,
                        InitialPlan = attack ? null : Enumerable.Range(0, rounds).Select(i => new LocalAction(-1, "", null, "", "", i == 0 ? sourceHash : "",
                            firstRound + i, EndTurn: true)).ToArray(),
                        DebugEncounter = encounter.Id.Entry, ShareSearchWork = false, SearchWorkPipe = null, TurnWorkPipe = null,
                        MinimumLossPipe = null, ProgressPipe = null, VerifyCandidate = null, RecordedReplayProbe = null,
                        DeferVerification = true, SkipFinalVerification = true };
                    LocalCandidate? baseline = null;
                    var modes = regular && !attack ? new[] { "regular", "MonteCarlo", "TurnFrontier" } : new[] { "MonteCarlo", "TurnFrontier" };
                    foreach (var mode in modes)
                    {
                        try
                        {
                            var request = command with { Id = Guid.NewGuid().ToString("N"),
                                DataOnlyCombat = mode != "regular", DataOnlyRun = mode != "regular", NumericalExecution = mode != "regular",
                                SearchOrder = mode == "TurnFrontier" ? LocalSearchOrder.TurnFrontier : LocalSearchOrder.MonteCarlo };
                            var sample = await Submit(request);
                            LocalWire.Write(Path.Combine(root, $"integration-enemy-{encounter.Id.Entry}-{mode}-private.json"), sample);
                            if (sample.Status != "searched" || sample.Evaluated < 1 || sample.Rejected != 0 || sample.Failure != null ||
                                sample.RecoveredFailures is { Length: > 0 } || sample.Best is not { } best ||
                                best.Actions.Length == 0 || best.Continuation?.Length != best.Actions.Length)
                                throw new InvalidOperationException("Native simulation failed: " + sample.Message);
                            if (mode != "regular" && sample.Trace?.Spans?.Any(s => s.Phase == "scene" && s.Detail == "原生战斗初始化") != true)
                                throw new InvalidOperationException("Requested numerical mode was unavailable; ordinary fallback is not a numerical pass");
                            long skippedPositions = sample.Trace?.Methods?.Where(m => m.Method == "Enemy.OptionalPositions").Sum(m => m.Skipped) ?? 0;
                            if (encounter.Id.Entry == "THE_INSATIABLE_BOSS" && mode != "regular" && skippedPositions == 0)
                                throw new InvalidOperationException("Native Sandpit position boundary was not exercised");
                            if (mode == "regular" || !regular && baseline == null) baseline = best;
                            if (!attack && (JsonSerializer.Serialize(best.Actions) != JsonSerializer.Serialize(baseline!.Actions) ||
                                JsonSerializer.Serialize(best.Continuation) != JsonSerializer.Serialize(baseline.Continuation) ||
                                (best.Hp, best.HpLost, best.MaxHp, best.Gold, best.EnemyHp, best.Won, best.Dead) !=
                                (baseline.Hp, baseline.HpLost, baseline.MaxHp, baseline.Gold, baseline.EnemyHp, baseline.Won, baseline.Dead)))
                                throw new InvalidOperationException("Native state/RNG/history or settlement diverged from baseline");
                            if (attack)
                            {
                                var verified = await Submit(request with { Id = Guid.NewGuid().ToString("N"), VerifyCandidate = best,
                                    DataOnlyCombat = false, DataOnlyRun = false, NumericalExecution = false,
                                    DeferVerification = false, SkipFinalVerification = false, FastVerification = false });
                                if (verified.Status != "done" || verified.Best?.Continuation?.Length != best.Actions.Length ||
                                    JsonSerializer.Serialize(verified.Best.Continuation) != JsonSerializer.Serialize(best.Continuation))
                                    throw new InvalidOperationException("Independent native scene replay/checkpoints failed");
                            }
                            if (LocalCapture.Fingerprint() != sourceHash || capture.Capture(true)!.Fingerprint() != snapshot.Fingerprint())
                                throw new InvalidOperationException("Owned worker changed the source fixture");
                            samples.Add(new { encounter = encounter.Id.Entry, mode, sample.Evaluated, steps = best.Actions.Length,
                                best.Rounds, best.Won, best.Dead, best.Hp, best.EnemyHp, sample.ElapsedMs,
                                observed = _observed, independentSceneReplay = attack,
                                skippedPositions,
                                nativeStateRngHistoryMatch = true, sourceUnchanged = true });
                        }
                        catch (Exception ex)
                        {
                            failures.Add(new { encounter = encounter.Id.Entry, mode, type = ex.GetType().Name, message = ex.Message });
                            pool.Dispose(); pool = new LocalWorkerPool(Path.Combine(root, "integration-enemy-pool"));
                        }
                        Save();
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(new { encounter = encounter.Id.Entry, mode = "fixture", type = ex.GetType().Name, message = ex.Message });
                    Save();
                }
            }
        }
        finally { pool.Dispose(); Save(); }
        if (failures.Count > 0) throw new InvalidOperationException($"Enemy compatibility failures: {failures.Count}; see private summary");

        async Task<LocalSearchResult> Submit(LocalSearchRequest request)
        {
            await Task.Run(() => pool.Prepare(LocalCapture.Installation() with { MinimalWorkerBootstrap = true }, 1, CancellationToken.None));
            var worker = ((Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pool)!).GetValue(0)!;
            var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
            if (!File.Exists(Path.Combine(workerRoot, ".coach-worker"))) throw new InvalidOperationException("Missing worker ownership marker");
            LocalWire.Write(Path.Combine(workerRoot, "request.json"), request);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < 45)
            {
                await Task.Delay(50);
                if (!File.Exists(Path.Combine(workerRoot, "result.json"))) continue;
                var sample = LocalWire.Read<LocalSearchResult>(Path.Combine(workerRoot, "result.json"));
                if (sample.Id != request.Id || sample.Status == "running") continue;
                LocalWire.Write(Path.Combine(root, $"integration-enemy-{request.DebugEncounter}-{request.SearchOrder}-last-private.json"), sample);
                if (sample.Status != "searched" && sample.Status != "done") throw new InvalidOperationException(sample.Message + "; " + sample.Failure?.Message);
                var idle = Path.Combine(workerRoot, "idle.json");
                if (!File.Exists(idle) || LocalWire.Read<LocalWorkerIdle>(idle).Id != request.Id) continue;
                if ((bool)worker.GetType().GetMethod("GameErrors")!.Invoke(worker, null)!)
                    throw new InvalidOperationException("Native worker reported a runtime error; see owned game log");
                var guardAudit = Path.Combine(workerRoot, "enemy-guard-audit.json");
                if (!File.Exists(guardAudit) || !JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(guardAudit)).GetProperty("passed").GetBoolean())
                    throw new InvalidOperationException("Native optional geometry guard audit was not completed");
                var observations = Path.Combine(workerRoot, "enemy-probe.jsonl");
                _observed = File.Exists(observations) ? File.ReadLines(observations).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray() : [];
                return sample;
            }
            throw new TimeoutException("Owned enemy simulation did not finish");
        }
    }
    private static JsonElement[] _observed = [];
}
