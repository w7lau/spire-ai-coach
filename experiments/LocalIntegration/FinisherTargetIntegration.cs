using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Direct native actions only: no route search, benchmark or player-game changes.
internal static class FinisherTargetIntegration
{
    public static async Task Run(string root, SceneTree tree, SerializableRun fixture)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var records = new List<object>();
        var native = typeof(LocalWorker).Assembly;
        var ledgerType = native.GetType("SpireAiCoach.Mod.LocalCardGoalAccounting", true)!;
        var previewType = native.GetType("SpireAiCoach.Mod.LocalTacticalPreview", true)!;
        var finisherPriority = previewType.GetMethod("FinisherPriority", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var kind in new[] { "ordinary", "minion", "mod-fatal-denied", "mixed-final-hit", "no-eligible" })
        {
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
            NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control()); await Frame(); await Frame();
            var run = RunState.FromSerializable(fixture);
            await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
            var player = run.Players.Single();
            var old = player.Deck.Cards.ToArray(); player.Deck.Clear(silent: true);
            foreach (var card in old) run.RemoveCard(card);
            foreach (var id in new[] { "FEED", "FEED", "ANGER", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD" })
            {
                var card = run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player);
                if (id == "FEED") card.UpgradeInternal();
                await CardPileCmd.Add(card, player.Deck, skipVisuals: true);
            }
            foreach (var potion in player.Potions.ToArray()) potion.Discard();
            await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
            await PreloadManager.LoadActAssets(run.Acts[0]);
            RunManager.Instance.Launch(); NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await RunManager.Instance.GenerateMap();
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster,
                ModelDb.AllEncounters.Single(e => e.Id.Entry == "TWO_TAILED_RATS_NORMAL").ToMutable(), false);
            while (!LocalCapture.Stable()) await Frame();
            var enemies = player.Creature.CombatState!.Enemies.Where(e => e.IsAlive).ToArray();
            if (enemies.Length < 2) throw new InvalidOperationException("Expected multiple native fixture enemies");
            var target = enemies[0];
            bool denied = kind != "ordinary";
            if (kind == "mod-fatal-denied")
                await PowerCmd.Apply<FinisherFatalDeniedPower>(new ThrowingPlayerChoiceContext(), target, 1, target, null);
            else if (denied)
                await PowerCmd.Apply<MinionPower>(new ThrowingPlayerChoiceContext(), target, 1, target, null);
            if (denied)
                foreach (var extra in kind == "no-eligible" ? enemies.Skip(1) : enemies.Skip(1).SkipLast(1))
                    await PowerCmd.Apply<MinionPower>(new ThrowingPlayerChoiceContext(), extra, 1, extra, null);
            var goals = new LocalCardGoals("CARD.ANGER", "CARD.FEED", 5);
            using var ledger = (IDisposable)Activator.CreateInstance(ledgerType, player, goals)!;
            LocalCardGoalOutcome Counts() => (LocalCardGoalOutcome)ledgerType.GetMethod("Snapshot")!.Invoke(ledger, null)!;
            var stock = Counts().ConsumableGoals!.Finisher!;
            int eligibleEnemies = kind == "no-eligible" ? 0 : denied ? 1 : enemies.Length;
            if (stock.LivingEnemies != eligibleEnemies || stock.Target != Math.Min(2, eligibleEnemies))
                throw new InvalidOperationException(kind + ": ineligible enemy raised the consumable goal target");
            var legal = player.PlayerCombatState!.Hand.Cards.Where(c => c.CanPlay()).ToArray();
            var feed = legal.First(c => c.Id.Entry == "FEED");
            var anger = legal.Single(c => c.Id.Entry == "ANGER");
            // Read the real native previews; change only this owned fixture HP.
            target.SetCurrentHpInternal(1);
            object Preview() => previewType.GetMethod("Capture")!.Invoke(null, [player, legal, true])!;
            int Hint(CardModel card, object preview) => (int)previewType.GetMethod("CardGoalPriority")!
                .Invoke(preview, [card, target, goals])!;
            var lethal = Preview();
            int lethalFeed = Hint(feed, lethal), lethalOther = Hint(anger, lethal);
            int finite = (int)finisherPriority.Invoke(lethal, [feed, target])!;
            int reservation = eligibleEnemies > 0 ? -180 : 0;
            if (lethalFeed != (denied ? reservation : 120) || lethalOther != (denied ? 30 : -30) ||
                finite != (denied ? reservation : 120))
                throw new InvalidOperationException(kind + ": finishing/reservation hint ignored native Fatal eligibility");
            if (denied && (int)finisherPriority.Invoke(lethal, [anger, target])! != 0)
                throw new InvalidOperationException(kind + ": repeatable damage was reserved as a finite opportunity");
            target.SetCurrentHpInternal(16);
            var setup = Preview();
            int setupFeed = Hint(feed, setup), setupOther = Hint(anger, setup);
            if (setupFeed != (denied ? reservation : -180) || setupOther != (denied ? 30 : 80))
                throw new InvalidOperationException(kind + ": finite reservation or eligible-only setup hint changed");
            target.SetCurrentHpInternal(1);
            if (!feed.CanPlay() || !feed.IsValidTarget(target) || !anger.IsValidTarget(target))
                throw new InvalidOperationException("Ineligible finisher targets must remain legal damage targets");
            if (eligibleEnemies == 0)
            {
                // Native combat can settle at the next frame when every enemy is a
                // minion. Check this observed ordering boundary without enqueueing
                // an attack that may run only after victory has already started.
                var unchanged = Counts();
                if (unchanged.Kills != 0 || unchanged.ConsumableGoals!.Finisher!.Complete)
                    throw new InvalidOperationException("No eligible opportunity became a completed finisher goal");
                records.Add(new { kind, eligibleLivingEnemies = stock.LivingEnemies, target = stock.Target,
                    lethalFeed, lethalOther, finite, setupFeed, setupOther, nativePlayPerformed = false,
                    unchanged.Kills, consumable = unchanged.ConsumableGoals.Finisher });
                continue;
            }
            async Task PlayFeed(Creature enemy)
            {
                var card = player.PlayerCombatState!.Hand.Cards.First(c => c.Id.Entry == "FEED");
                RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, enemy));
                await Frame(); await RunManager.Instance.ActionQueueSet.BecameEmpty();
                while (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsOverOrEnding && !LocalCapture.Stable()) await Frame();
                await Frame(); await Frame();
                ledgerType.GetMethod("CompleteStep")!.Invoke(ledger, null);
            }
            int maxBefore = player.Creature.MaxHp;
            var opportunityBefore = (LocalGoalOpportunity)ledgerType.GetMethod("Opportunity")!.Invoke(ledger, null)!;
            await PlayFeed(target);
            var first = Counts();
            var opportunityAfter = (LocalGoalOpportunity?)ledgerType.GetMethod("Opportunity")!.Invoke(ledger, null);
            if (kind == "ordinary" && LocalGoalOpportunity.LossPenalty(opportunityBefore, opportunityAfter, goals) != 0)
                throw new InvalidOperationException("Successful native finishing consumption was penalized");
            int gain = player.Creature.MaxHp - maxBefore;
            if (!target.IsDead || first.Kills != (denied ? 0 : 1) || first.ConsumableGoals!.Finisher!.CompletedCopies != first.Kills ||
                first.ConsumableGoals.Finisher.Complete || (denied ? gain != 0 : gain <= 0))
                throw new InvalidOperationException(kind + ": goal credit disagrees with actual native Fatal reward: " +
                    $"dead={target.IsDead};kills={first.Kills};completed={first.ConsumableGoals!.Finisher!.CompletedCopies};" +
                    $"complete={first.ConsumableGoals.Finisher.Complete};gain={gain};battleEnded={CombatManager.Instance.IsOverOrEnding}");
            bool removedAfterDeath = kind == "mod-fatal-denied" &&
                target.Powers.All(p => p.ShouldOwnerDeathTriggerFatal());
            if (kind == "mod-fatal-denied" && !removedAfterDeath)
                throw new InvalidOperationException("Synthetic Mod power must be removed after death to cover cleanup timing");
            if (kind == "mixed-final-hit")
            {
                enemies[^1].SetCurrentHpInternal(1);
                await PlayFeed(enemies[^1]);
                if (!CombatManager.Instance.IsOverOrEnding || Counts() is not { Kills: 1, ConsumableGoals.Finisher.Complete: true })
                    throw new InvalidOperationException("Final non-minion kill did not close the one eligible finishing opportunity");
            }
            if (kind == "minion")
            {
                // Retaining the card is insufficient if another action spends
                // the final eligible enemy. Observe that real native terminal.
                var last = enemies[^1]; last.SetCurrentHpInternal(1);
                var beforeOtherKill = (LocalGoalOpportunity)ledgerType.GetMethod("Opportunity")!.Invoke(ledger, null)!;
                if (!anger.CanPlay() || !anger.IsValidTarget(last))
                    throw new InvalidOperationException("Synthetic non-finishing attack was not legal");
                RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(anger, last));
                await Frame(); await RunManager.Instance.ActionQueueSet.BecameEmpty();
                while (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsOverOrEnding && !LocalCapture.Stable()) await Frame();
                ledgerType.GetMethod("CompleteStep")!.Invoke(ledger, null);
                var afterOtherKill = (LocalGoalOpportunity)ledgerType.GetMethod("Opportunity")!.Invoke(ledger, null)!;
                if (!last.IsDead || Counts().Kills != 0 || afterOtherKill.Finisher?.Missing != 1 ||
                    LocalGoalOpportunity.LossPenalty(beforeOtherKill, afterOtherKill, goals) <= 0)
                    throw new InvalidOperationException("Native final enemy consumption lost no finishing opportunity");
                records.Add(new { kind = "native-last-eligible-enemy-consumed", beforeOtherKill, afterOtherKill });
            }
            var final = Counts();
            if (final.Steps.Sum(s => s.Kills) != final.Kills || !((bool)ledgerType.GetMethod("Matches")!.Invoke(ledger, [final])!))
                throw new InvalidOperationException("Step counts or independent recount contract changed");
            records.Add(new { kind, eligibleLivingEnemies = stock.LivingEnemies, target = stock.Target,
                lethalFeed, lethalOther, finite, setupFeed, setupOther, nativePlayPerformed = true, nativeKilled = target.IsDead,
                firstKills = first.Kills, firstMaxHpGain = gain, removedAfterDeath, final.Kills,
                final.Steps, consumable = final.ConsumableGoals!.Finisher });
        }
        LocalWire.Write(Path.Combine(root, "integration-finisher-targets-summary.json"), new {
            version = native.GetName().Version!.ToString(3), nativeModule = typeof(Creature).Assembly.ManifestModule.ModuleVersionId,
            modSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(native.Location))),
            scope = "Five owned synthetic native cases; no full search or arbitrary-Mod coverage claim", records });
    }
}

// A genuine external override, not MinionPower or a name-based minion marker.
// Native death cleanup removes it, so an after-death eligibility test is wrong.
public sealed class FinisherFatalDeniedPower : PowerModel
{
    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Single;
    public override bool ShouldPlayVfx => false;
    public override bool ShouldOwnerDeathTriggerFatal() => false;
}
