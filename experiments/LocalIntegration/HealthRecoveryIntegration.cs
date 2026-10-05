using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Seeded mechanism check in an owned synthetic run; not a search benchmark.
internal static class HealthRecoveryIntegration
{
    public static async Task Run(string root, SceneTree tree, LocalWorkerPool pool, SerializableRun fixture)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var encounter = ModelDb.AllEncounters.Single(e => e.Id.Entry == "TOADPOLES_WEAK");
        var run = RunState.FromSerializable(fixture);
        await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
        var player = run.Players.Single();
        player.Creature.SetMaxHpInternal(250); player.Creature.SetCurrentHpInternal(100);
        if (!player.Relics.Any(r => r.Id.Entry == "BURNING_BLOOD"))
            throw new InvalidOperationException("This native fixture requires Burning Blood");
        foreach (var potion in player.Potions.ToArray()) potion.Discard();
        await PotionCmd.TryToProcure(ModelDb.AllPotions.Single(p => p.Id.Entry == "BLOOD_POTION").ToMutable(), player, 0);
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
        await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster, encounter.ToMutable(), false);
        var settle = Stopwatch.StartNew();
        while (!LocalCapture.Stable())
        {
            if (settle.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Native healing fixture did not settle");
            await Frame();
        }
        using var capture = new StateCapture(); var snapshot = capture.Capture(true)!;
        var nativeBefore = LocalCapture.Fingerprint();
        var combat = CombatManager.Instance.DebugOnlyGetState()!;
        var hand = player.PlayerCombatState!.Hand.Cards;
        var attacks = hand.Take(4).Select((card, i) => new LocalAction(i, card.Id.ToString(),
            combat.Enemies[i / 2 % combat.Enemies.Count].CombatId, card.Title, "enemy", nativeBefore, combat.RoundNumber,
            CombatCardIndex: NetCombatCard.FromModel(card).CombatCardIndex)).ToArray();
        var blood = player.GetPotionAtSlotIndex(0)!;
        uint? potionTarget = blood.IsValidTarget(null) ? null : player.Creature.CombatId;
        if (potionTarget != null && !blood.IsValidTarget(player.Creature))
            throw new InvalidOperationException("Native healing potion has no valid fixture target");
        var heal = new LocalAction(-1, blood.Id.ToString(), potionTarget, blood.Title.GetFormattedText(), "", nativeBefore,
            combat.RoundNumber, PotionSlot: 0);
        var frozen = LocalCapture.Capture(snapshot.Fingerprint(), false) with {
            Workers = 1, MaxRounds = 3, MaxDepth = 30, BudgetSeconds = 15,
            StopOnZeroLoss = false, StopOnFirstWin = false, SkipFinalVerification = false,
            ShareSearchWork = true, DebugEncounter = encounter.Id.Entry };
        var samples = new List<object>();
        foreach (var algorithm in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
        {
            var request = frozen with { Id = Guid.NewGuid().ToString("N"), SearchOrder = algorithm,
                IncludePotions = false, MaxNodes = 1, InitialPlan = attacks };
            var baseline = await Task.Run(() => pool.Analyze(request, LocalCapture.Installation(), _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, $"integration-healing-baseline-{algorithm}-private.json"), baseline);
            pool.DiscardSearch();
            request = request with { Id = Guid.NewGuid().ToString("N"), IncludePotions = true,
                MaxNodes = 8, InitialPlan = [heal, .. attacks] };
            var result = await Task.Run(() => pool.Analyze(request, LocalCapture.Installation(), _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, $"integration-healing-ranked-{algorithm}-private.json"), result);
            pool.DiscardSearch();
            var unknownRequest = request with { Id = Guid.NewGuid().ToString("N"), StopOnZeroLoss = true };
            var unknown = await Task.Run(() => pool.Analyze(unknownRequest, LocalCapture.Installation(), _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, $"integration-healing-unknown-{algorithm}-private.json"), unknown);
            foreach (var measured in new[] { baseline, result, unknown })
                if (measured.Status != "done" || measured.Best is not { Won: true, Dead: false, MaxHp: 250 } ||
                    measured.Timing?.Verifications != 1 || measured.Rejected != 0 || measured.Failure != null ||
                    measured.RecoveredFailures is { Length: > 0 } || !LocalSearchPolicy.HasExecutionPoints(measured))
                    throw new InvalidOperationException("Native healing route/verification failed: " + measured.Message);
            if (baseline.Best!.Hp != 106 || baseline.Best.HpChange != 6 ||
                result.Best!.Hp <= baseline.Best.Hp || result.Best.NetHpLoss != 0 ||
                result.Best.Actions.Count(a => a.PotionSlot.HasValue) != 1 ||
                !LocalSearchPolicy.Better(result.Best, baseline.Best) || result.StoppedEarly)
                throw new InvalidOperationException("Native final-HP ordering flattened potion/victory healing");
            if (unknown.Best!.Hp != result.Best.Hp || unknown.StoppedEarly || unknown.StoppedOnMinimum ||
                unknown.HealthBounds is not { UnknownRecoveryChecks: > 0 } ||
                unknown.MinimumLoss?.Certificate?.MaximumFinalHp != null || LocalSearchPolicy.HasMinimumProof(unknown))
                throw new InvalidOperationException("Unknown native continuation falsely certified a healed optimum");
            if (LocalCapture.Fingerprint() != nativeBefore || capture.Capture(true)!.Fingerprint() != snapshot.Fingerprint())
                throw new InvalidOperationException("Owned worker simulation changed the source combat");
            samples.Add(new { algorithm = algorithm.ToString(), baselineHp = baseline.Best.Hp,
                finalHp = result.Best.Hp, result.Best.HpChange, result.Best.HpLost,
                hpGained = result.Best.HealthChanges?.HpGained, result.Evaluated, result.Victories,
                measuredNoPotionWins = result.Trials?.Count(t => t.Complete && t.Won && t.PotionsUsed == 0),
                measuredPotionWins = result.Trials?.Count(t => t.Complete && t.Won && t.PotionsUsed == 1),
                result.ElapsedMs, finalVerifications = result.Timing!.Verifications,
                unknownRecoveryStopChecked = true, unknownRecoveryEvaluated = unknown.Evaluated,
                unknownRecoveryChecks = unknown.HealthBounds.UnknownRecoveryChecks,
                executionCheckpoints = true, sourceCombatUnchanged = true });
            pool.DiscardSearch();
        }
        LocalWire.Write(Path.Combine(root, "integration-healing-summary.json"), new {
            version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3), passed = true,
            scope = "Native potion and victory-healing ordering; two algorithms; seeded mechanism probe",
            nativeGame = true, samples });
    }
}
