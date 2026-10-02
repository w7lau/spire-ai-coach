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

namespace SpireNativeProbe;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string _root = "";
    private static string _stage = "init";
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
            _stage = "startup";
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
            _stage = "create_run";
            var restore = request.GetProperty("restore_fixture").GetBoolean();
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
            NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await RunManager.Instance.GenerateMap();
            var player = LocalContext.GetMe(run)!;
            _stage = "prepare_synthetic_loadout";
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
            _stage = "enter_combat";
            var encounterId = request.GetProperty("encounter").GetString();
            var encounter = ModelDb.AllEncounters.Single(e => e.Id.Entry == encounterId);
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster, encounter.ToMutable());
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
            var actionsTimer = Stopwatch.StartNew();
            foreach (var item in request.GetProperty("actions").EnumerateArray())
            {
                var id = item.GetString();
                _stage = "play_" + id;
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
            Finish(new { status = "native_route_complete", elapsed_ms = timer.ElapsedMilliseconds,
                action_ms = actionsTimer.ElapsedMilliseconds, restored_fixture = restore,
                fixture_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))), checkpoints });
        }
        catch (Exception ex) { Finish(new { status = "failed", stage = _stage, error = ex.ToString(), elapsed_ms = timer.ElapsedMilliseconds }); }
        finally { tree.Quit(); }
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
