using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

public static class MechanicsIntegration
{
    public static async Task Run(string root, SceneTree tree, LocalWorkerPool pool, SerializableRun fixture)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var records = new List<object>();
        var cases = System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_MECHANICS_CASES")?.Split(',') ??
            ["ordering", "exhaust", "upgrade", "multi", "retrieve", "longfight"];
        foreach (var kind in cases)
        {
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
            NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control()); await Frame(); await Frame();
            var run = RunState.FromSerializable(fixture);
            await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
            var player = run.Players.Single();
            if (kind == "longfight")
            {
                // Synthetic high HP lets the deliberately delayed line reach the horizon check.
                player.Creature.SetMaxHpInternal(1000); player.Creature.SetCurrentHpInternal(1000);
            }
            var old = player.Deck.Cards.ToArray(); player.Deck.Clear(silent: true);
            foreach (var card in old) run.RemoveCard(card);
            var ids = kind switch
            {
                "ordering" => new[] { "STRIKE_IRONCLAD", "ARMAMENTS", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD" },
                "exhaust" => new[] { "TRUE_GRIT", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD" },
                "upgrade" => new[] { "ARMAMENTS", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD" },
                "multi" => new[] { "PREPARED", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD" },
                "longfight" => new[] { "BLUDGEON", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD" },
                _ => new[] { "HEADBUTT", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD" }
            };
            foreach (var id in ids)
            {
                var card = run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player);
                if (kind == "ordering" && id == "ARMAMENTS" || kind == "exhaust" && id == "TRUE_GRIT" || kind == "multi" && id == "PREPARED" || kind == "longfight") card.UpgradeInternal();
                await CardPileCmd.Add(card, player.Deck, skipVisuals: true);
            }
            await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
            await PreloadManager.LoadActAssets(run.Acts[0]);
            RunManager.Instance.Launch(); NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await RunManager.Instance.GenerateMap();
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster,
                ModelDb.AllEncounters.Single(e => e.Id.Entry == "SLUMBERING_BEETLE_NORMAL").ToMutable(), false);
            var capture = new StateCapture(); while (!LocalCapture.Stable()) await Frame();
            var snapshot = capture.Capture(true)!;
            var hand = player.PlayerCombatState!.Hand.Cards;
            LocalAction Action(CardModel c) => new(hand.ToList().IndexOf(c), c.Id.ToString(), c.IsValidTarget(null) ? null : player.Creature.CombatState!.Enemies[0].CombatId,
                c.Title, "enemy", LocalCapture.Fingerprint(), CombatManager.Instance.DebugOnlyGetState()!.RoundNumber,
                CombatCardIndex: NetCombatCard.FromModel(c).CombatCardIndex);
            var target = hand.Single(c => c.Id.Entry == (kind == "ordering" || kind == "upgrade" ? "ARMAMENTS" : kind == "exhaust" ? "TRUE_GRIT" : kind == "multi" ? "PREPARED" : kind == "longfight" ? "BLUDGEON" : "HEADBUTT"));
            var initial = kind == "ordering" ? hand.Where(c => c.Id.Entry == "STRIKE_IRONCLAD").Select(Action).Append(Action(target)).ToArray() :
                kind == "retrieve" ? hand.Where(c => c.Id.Entry == "DEFEND_IRONCLAD").Take(2).Select(Action).Append(Action(target)).ToArray() : [Action(target)];
            if (kind == "longfight")
            {
                var firstRound = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                // Explicitly hold for eleven turns to exercise the horizon independently of early kills.
                initial = Enumerable.Range(0, 11).Select(i => new LocalAction(-1, "", null, "", "", "", firstRound + i, EndTurn: true))
                    .Append(Action(target) with { Round = firstRound + 11 }).ToArray();
            }
            var request = LocalCapture.Capture(snapshot.Fingerprint(), true) with
                { Workers = 1, MaxNodes = kind == "ordering" ? 8 : 1, MaxRounds = kind == "longfight" ? 64 : 1,
                    BudgetSeconds = kind == "longfight" ? 40 : 25, InitialPlan = initial,
                    DebugEncounter = "SLUMBERING_BEETLE_NORMAL" };
            var result = await Task.Run(() => pool.Analyze(request, LocalCapture.Installation(), _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, "integration-mechanics-" + kind + "-private.json"), result);
            if (LocalCapture.Fingerprint() != request.NativeHash || result.Rejected != 0 || result.Best?.Continuation?.Length != result.Best?.Actions.Length)
                throw new InvalidOperationException(kind + ": native search/verification failed: " + result.Message +
                    "; unchanged=" + (LocalCapture.Fingerprint() == request.NativeHash));
            var best = result.Best!;
            if (kind == "ordering")
            {
                var played = best.Actions.Where(a => !a.EndTurn).ToArray();
                if (played.Length < 3 || played[0].ModelId != "CARD.ARMAMENTS") throw new InvalidOperationException("Native order refinement did not move Armaments before strikes");
            }
            else if (kind == "longfight")
            {
                if (best.Rounds <= 10 || !best.Won) throw new InvalidOperationException("Need verified victory beyond ten rounds");
            }
            else
            {
                if (!best.Actions.Any(a => a.Choices is { Length: > 0 })) throw new InvalidOperationException(kind + ": no native choice tested");
                if (kind == "multi" && !best.Actions.SelectMany(a => a.Choices ?? []).Any(c => c.Indices?.Length == 2))
                    throw new InvalidOperationException("No multiple selection tested");
                var messages = new List<string>();
                await new LocalPlanExecutor(tree).Execute(new(snapshot.CombatId, request.LoadedMods, result),
                    () => capture.Capture(false)?.CombatId, true, messages.Add, CancellationToken.None);
                var after = LocalCapture.Capture(capture.Capture(true)!.Fingerprint(), true) with
                    { Workers = 1, MaxNodes = 1, MaxRounds = 1, BudgetSeconds = 20, DebugEncounter = "SLUMBERING_BEETLE_NORMAL" };
                var replay = await Task.Run(() => pool.Analyze(after, LocalCapture.Installation(), _ => { }, CancellationToken.None));
                if (replay.Best == null || replay.Rejected != 0 || replay.Best.Continuation?.Length != replay.Best.Actions.Length ||
                    after.NativeHash != LocalCapture.Fingerprint()) throw new InvalidOperationException(kind + ": completed choice continuation failed");
            }
            records.Add(new { kind, result.Status, result.Evaluated, result.Rejected, result.ElapsedMs, result.Timing,
                best.EnemyHp, best.HpLost, best.Rounds, best.Won, actions = best.Actions.Select(a => new { a.ModelId, a.CombatCardIndex,
                    choices = (a.Choices ?? []).Select(c => new { c.Kind, c.Indices }) }), verified = true });
            LocalWire.Write(Path.Combine(root, "integration-mechanics.json"), records);
        }
    }
}
