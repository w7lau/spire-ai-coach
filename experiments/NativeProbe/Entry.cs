using System.Diagnostics;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using System.Security.Cryptography;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Settings;

namespace SpireNativeProbe;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string _root = "";
    private static string _stage = "init";
    private static string Stage
    {
        set
        {
            _stage = value;
            File.WriteAllText(Path.Combine(_root, "progress.json"), JsonSerializer.Serialize(new { stage = value }));
        }
    }
    public static void Initialize()
    {
        var root = System.Environment.GetEnvironmentVariable("SPIRE_NATIVE_PROBE_ROOT");
        if (root == null) return;
        _root = Path.GetFullPath(root);
        // Refuse execution in a normal installation even if someone sets the environment flag.
        var expected = Path.Combine(_root, "game", "SlayTheSpire2.exe");
        if (!string.Equals(Path.GetFullPath(OS.GetExecutablePath()), expected, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(_root, ".spire-native-probe-owner"))) return;
        Callable.From(Start).CallDeferred();
    }

    private static async void Start()
    {
        var timer = Stopwatch.StartNew();
        var tree = (SceneTree)Engine.GetMainLoop();
        try
        {
            Stage = "startup";
            while (NGame.Instance == null) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await NGame.Instance.GameStartupComplete;
            var request = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "request.json"))).RootElement;
            var inventory = new
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(a => new { name = a.GetName().Name, path = a.IsDynamic ? "dynamic" : a.Location }).ToArray(),
                relics = ModelDb.AllRelics.Where(r => r.GetType().Namespace?.StartsWith("HextechRunes") == true)
                    .Select(r => new { id = r.Id.ToString(), type = r.GetType().FullName }).ToArray(),
                encounters = ModelDb.AllEncounters.Select(e => new { id = e.Id.ToString(), type = e.GetType().Name }).ToArray()
            };
            File.WriteAllText(Path.Combine(_root, "inventory.json"), JsonSerializer.Serialize(inventory, Json));
            if (request.GetProperty("mode").GetString() == "inventory")
            {
                Finish(new { status = "inventory_ready", elapsed_ms = timer.ElapsedMilliseconds });
                return;
            }
            Engine.TimeScale = request.TryGetProperty("time_scale", out var scale) ? scale.GetDouble() : 1;
            if (Engine.TimeScale is < 1 or > 4) throw new InvalidDataException("Unsupported time scale");
            var instant = request.TryGetProperty("instant", out var instantValue) && instantValue.GetBoolean();
            var keepAssets = request.TryGetProperty("keep_assets", out var keepValue) && keepValue.GetBoolean();
            var skipTransitions = request.TryGetProperty("skip_transitions", out var skipValue) && skipValue.GetBoolean();
            SaveManager.Instance.PrefsSave.FastMode = instant ? FastModeType.Instant : FastModeType.Normal;
            if (request.GetProperty("mode").GetString() == "resident")
            {
                var branches = new List<object>();
                var known = new Dictionary<string, RouteResult>();
                var cleanupMs = 0L;
                // A-B-A-B-A: make one branch exercise death and different draws before replaying A.
                foreach (var label in new[] { "A", "B", "A", "B", "A" })
                {
                    var cycleTimer = Stopwatch.StartNew();
                    var restore = branches.Count > 0 || request.GetProperty("restore_fixture").GetBoolean();
                    if (branches.Count > 0)
                    {
                        Stage = "cleanup_before_" + branches.Count;
                        var cleanupTimer = Stopwatch.StartNew();
                        if (keepAssets)
                        {
                            // Reset run/combat state and release its scene, retaining preloaded assets.
                            RunManager.Instance.CleanUp();
                            NGame.Instance.RootSceneContainer.SetCurrentScene(new Control());
                        }
                        else await NGame.Instance.ReturnToMainMenu();
                        // QueueFree and pending continuations must get a chance to run.
                        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                        cleanupMs = cleanupTimer.ElapsedMilliseconds;
                    }
                    string[] actions = label == "A"
                        ? ["BLOODLETTING", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "END_TURN"]
                        : ["STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "END_TURN"];
                    var result = await RunRoute(request, restore, actions, tree);
                    var rootMatches = known.Count == 0 || known["A"].checkpoints[0].GetRawText() == result.checkpoints[0].GetRawText();
                    var traceMatches = !known.TryGetValue(label, out var prior)
                        || JsonSerializer.Serialize(prior.checkpoints, Json) == JsonSerializer.Serialize(result.checkpoints, Json);
                    var fixtureMatches = known.Count == 0 || known["A"].fixture_sha256 == result.fixture_sha256;
                    branches.Add(new { index = branches.Count, route = label, cleanup_ms = cleanupMs,
                        cycle_ms = cycleTimer.ElapsedMilliseconds, root_matches = rootMatches,
                        trace_matches = traceMatches, fixture_matches = fixtureMatches, result,
                        godot_objects = Performance.GetMonitor(Performance.Monitor.ObjectCount),
                        godot_nodes = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
                        godot_resources = Performance.GetMonitor(Performance.Monitor.ObjectResourceCount) });
                    File.WriteAllText(Path.Combine(_root, "branches.json"), JsonSerializer.Serialize(branches, Json));
                    if (!rootMatches || !traceMatches || !fixtureMatches)
                        throw new InvalidOperationException("Resident branch diverged; see branches.json");
                    known.TryAdd(label, result);
                }
                Stage = "final_cleanup";
                await NGame.Instance.ReturnToMainMenu();
                Finish(new { status = "native_resident_complete", time_scale = Engine.TimeScale, instant, keep_assets = keepAssets,
                    skip_transitions = skipTransitions,
                    elapsed_ms = timer.ElapsedMilliseconds, branches });
            }
            else
            {
                var actions = request.GetProperty("actions").EnumerateArray().Select(a => a.GetString()!).ToArray();
                Finish(await RunRoute(request, request.GetProperty("restore_fixture").GetBoolean(), actions, tree));
            }
        }
        catch (Exception ex) { Finish(new { status = "failed", stage = _stage, error = ex.ToString(), elapsed_ms = timer.ElapsedMilliseconds }); }
        finally { tree.Quit(); }
    }

    private sealed record RouteResult(string status, long elapsed_ms, long setup_ms, long action_ms,
        bool restored_fixture, string fixture_sha256, JsonElement[] checkpoints);

    private static async Task<RouteResult> RunRoute(JsonElement request, bool restore, string[] actions, SceneTree tree)
    {
        var timer = Stopwatch.StartNew();
        Stage = "create_run";
        var fixturePath = Path.Combine(_root, "fixture.json");
        // Synthetic fixture: enter combat directly, without the campaign's Neow/rune-selection UI.
        // This deliberately does not claim to restore a real player's current run.
        RunState run;
        if (restore)
        {
            var save = JsonSerializer.Deserialize(File.ReadAllText(fixturePath), JsonSerializationUtility.GetTypeInfo<SerializableRun>())
                ?? throw new InvalidDataException("Missing synthetic fixture");
            run = RunState.FromSerializable(save);
            await RunManager.Instance.SetUpSavedSingleplayer(run, save);
        }
        else
        {
            run = RunState.CreateForNewRun([Player.CreateForNewRun<Ironclad>(UnlockState.all, 1uL)],
                ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList(), [], GameMode.Standard, 0, "SPIRE-NATIVE-PROBE-1");
            RunManager.Instance.SetUpNewSingleplayer(run, false, null);
        }
        await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
        await PreloadManager.LoadActAssets(run.Acts[0]);
        if (!restore) await RunManager.Instance.FinalizeStartingRelics();
        RunManager.Instance.Launch();
        (NGame.Instance ?? throw new InvalidOperationException("Game host is missing")).RootSceneContainer.SetCurrentScene(NRun.Create(run));
        await RunManager.Instance.GenerateMap();
        var player = LocalContext.GetMe(run)!;
        Stage = "prepare_synthetic_loadout";
        if (!restore)
        {
            var old = player.Deck.Cards.ToArray();
            player.Deck.Clear(silent: true);
            foreach (var card in old) run.RemoveCard(card);
            foreach (var id in new[] { "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "BLOODLETTING" })
            {
                var canonical = ModelDb.AllCards.Single(c => c.Id.Entry == id);
                await CardPileCmd.Add(run.CreateCard(canonical, player), player.Deck, skipVisuals: true);
            }
            if (request.GetProperty("blood_armor").GetBoolean())
                await RelicCmd.Obtain(ModelDb.AllRelics.Single(r => r.GetType().FullName == "HextechRunes.BloodArmorRune").ToMutable(), player);
            File.WriteAllText(fixturePath, JsonSerializer.Serialize(RunManager.Instance.ToSave(null),
                JsonSerializationUtility.GetTypeInfo<SerializableRun>()));
        }
        if (player.Relics.Any(r => r.GetType().FullName == "HextechRunes.BloodArmorRune") != request.GetProperty("blood_armor").GetBoolean())
            throw new InvalidDataException("Fixture relic does not match request");
        Stage = "enter_combat";
        var encounterId = request.GetProperty("encounter").GetString();
        var encounter = ModelDb.AllEncounters.Single(e => e.Id.Entry == encounterId);
        var showTransition = !request.TryGetProperty("skip_transitions", out var skip) || !skip.GetBoolean();
        await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster, encounter.ToMutable(), showTransition);
        await WaitReady(tree);
        var checkpoints = new List<object>();
        object Capture(string label)
        {
            var state = CombatManager.Instance.DebugOnlyGetState()!;
            var pcs = player.PlayerCombatState!;
            var packet = new PacketWriter();
            NetFullCombatState.FromRun(run, null).Serialize(packet);
            return new
            {
                label, state.RoundNumber,
                // Native synchronization fingerprint, including native RNG and saved relic fields.
                // It is not a fingerprint of arbitrary Mod static/private state.
                native_state_sha256 = Convert.ToHexString(SHA256.HashData(packet.Buffer.AsSpan(0, (packet.BitPosition + 7) / 8))),
                player = new { hp = player.Creature.CurrentHp, block = player.Creature.Block, energy = pcs.Energy,
                    powers = player.Creature.Powers.Select(p => new { id = p.Id.ToString(), p.Amount }).ToArray(),
                    relics = player.Relics.Select(r => new { id = r.Id.ToString(), r.DisplayAmount, r.IsUsedUp, r.StackCount }).ToArray() },
                piles = new[] { pcs.Hand, pcs.DrawPile, pcs.DiscardPile, pcs.ExhaustPile, pcs.PlayPile }
                    .Select(p => new { type = p.Type.ToString(), cards = p.Cards.Select(c => c.Id.ToString()).ToArray() }).ToArray(),
                enemies = state.Enemies.Select(e => new { id = e.Monster!.Id.ToString(), hp = e.CurrentHp, block = e.Block,
                    powers = e.Powers.Select(p => new { id = p.Id.ToString(), p.Amount }).ToArray() }).ToArray()
            };
        }
        checkpoints.Add(Capture("root"));
        var setupMs = timer.ElapsedMilliseconds;
        var actionsTimer = Stopwatch.StartNew();
        foreach (var id in actions)
        {
            Stage = "play_" + id;
            if (id == "END_TURN")
            {
                var round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                PlayerCmd.EndTurn(player, canBackOut: false);
                var turnTimer = Stopwatch.StartNew();
                while (CombatManager.Instance.DebugOnlyGetState()!.RoundNumber <= round)
                {
                    if (turnTimer.Elapsed.TotalSeconds > 30) throw new TimeoutException("Enemy turn did not finish");
                    await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                }
                await WaitReady(tree);
                checkpoints.Add(Capture(id));
                continue;
            }
            var pcs = player.PlayerCombatState!;
            var card = pcs.Hand.Cards.First(c => c.Id.Entry == id);
            if (!card.CanPlay(out var reason, out _)) throw new InvalidOperationException("Illegal test action: " + reason);
            var target = card.IsValidTarget(null) ? null : CombatManager.Instance.DebugOnlyGetState()!.Enemies.First(e => e.IsAlive && card.IsValidTarget(e));
            RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, target));
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            await WaitReady(tree);
            if (pcs.Hand.Cards.Contains(card)) throw new InvalidOperationException("Action did not leave hand");
            checkpoints.Add(Capture(id!));
        }
        return new RouteResult("native_route_complete", timer.ElapsedMilliseconds, setupMs,
            actionsTimer.ElapsedMilliseconds, restore,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))),
            checkpoints.Select(c => JsonSerializer.SerializeToElement(c, Json)).ToArray());
    }

    private static async Task WaitReady(SceneTree tree)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var state = CombatManager.Instance.DebugOnlyGetState();
            var player = state == null ? null : LocalContext.GetMe(state);
            if (player?.PlayerCombatState?.Phase == PlayerTurnPhase.Play && !CombatManager.Instance.PlayerActionsDisabled
                && player.PlayerCombatState.PlayPile.IsEmpty) return;
            if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException("No stable player phase");
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }
    private static void Finish(object result) => File.WriteAllText(Path.Combine(_root, "result.json"), JsonSerializer.Serialize(result, Json));
}
