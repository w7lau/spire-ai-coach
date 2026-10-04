using System.Reflection;
using System.Security.Cryptography;
using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Two short synthetic native lines in the owned integration host. This verifies
// target previews, actual settlement and search feedback, not global optimality.
internal static class SurvivalIntegration
{
    public static async Task Run(string root, SceneTree tree, SerializableRun fixture)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        async Task Until(Func<bool> ready)
        {
            var elapsed = Stopwatch.StartNew();
            while (!ready())
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Synthetic native step did not settle");
                await Frame();
            }
        }
        async Task Settle()
        {
            await Frame();
            await RunManager.Instance.ActionQueueSet.BecameEmpty().WaitAsync(TimeSpan.FromSeconds(15));
            await Until(LocalCapture.Stable);
        }
        async Task EndTurn(Player player)
        {
            var round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
            RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(
                new EndPlayerTurnAction(player, player.PlayerCombatState!.TurnNumber));
            await Until(() => CombatManager.Instance.DebugOnlyGetState()!.RoundNumber > round && LocalCapture.Stable());
        }
        var type = typeof(ModEntry).Assembly.GetType("SpireAiCoach.Mod.LocalTacticalPreview")!;
        object Preview(Player player) => type.GetMethod("Capture")!.Invoke(null,
            [player, player.PlayerCombatState!.Hand.Cards.Where(c => c.CanPlay()).ToArray(), true])!;
        int Priority(object preview, CardModel card, Creature? target) => (int)type.GetMethod("Priority")!
            .Invoke(preview, [card, target, LocalRolloutStyle.Balanced])!;
        var results = new List<LocalCandidate>();
        var previews = new List<object>();
        foreach (var mode in new[] { "defense", "attack" })
        {
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp();
            NGame.Instance!.RootSceneContainer.SetCurrentScene(new Control()); await Frame(); await Frame();
            var run = RunState.FromSerializable(fixture);
            await RunManager.Instance.SetUpSavedSingleplayer(run, fixture);
            var player = run.Players.Single();
            player.Creature.SetMaxHpInternal(80); player.Creature.SetCurrentHpInternal(80);
            var old = player.Deck.Cards.ToArray(); player.Deck.Clear(silent: true);
            foreach (var card in old) run.RemoveCard(card);
            foreach (var id in new[] { "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD" })
                await CardPileCmd.Add(run.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), player), player.Deck, skipVisuals: true);
            await PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character));
            await PreloadManager.LoadActAssets(run.Acts[0]);
            RunManager.Instance.Launch(); NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await RunManager.Instance.GenerateMap();
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster,
                ModelDb.AllEncounters.Single(e => e.Id.Entry == "SLUMBERING_BEETLE_NORMAL").ToMutable(), false);
            await Until(LocalCapture.Stable);
            var enemies = CombatManager.Instance.DebugOnlyGetState()!.Enemies.ToArray();
            var enemy = enemies.First();
            int waiting = 0;
            while (enemy.Monster!.NextMove!.Intents.OfType<AttackIntent>().Count() == 0)
            {
                if (++waiting > 6) throw new InvalidOperationException("Synthetic enemy never reached an attacking move");
                await EndTurn(player);
            }
            var attacks = player.PlayerCombatState!.Hand.Cards.Where(c => c.Type == CardType.Attack).ToArray();
            attacks[0].UpgradeInternal();
            var uncappedPreview = Preview(player);
            double Damage(object captured, CardModel card) => (double)type.GetMethod("PreviewDamage",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(captured, [card, enemy])!;
            var upgradedDamage = Damage(uncappedPreview, attacks[0]);
            var ordinaryDamage = Damage(uncappedPreview, attacks[1]);
            if (attacks[0].Id != attacks[1].Id || upgradedDamage <= ordinaryDamage)
                throw new InvalidOperationException("Same-model native instances shared an upgraded damage preview");
            foreach (var targetEnemy in enemies)
                await PowerCmd.Apply<IntangiblePower>(new ThrowingPlayerChoiceContext(), targetEnemy, 1, targetEnemy, null);
            await Settle();
            int startingHp = player.Creature.CurrentHp, initialEnemyHp = enemies.Sum(e => e.CurrentHp);
            var hand = player.PlayerCombatState!.Hand.Cards;
            var strike = hand.First(c => c.Type == CardType.Attack);
            var defend = hand.First(c => c.Type == CardType.Skill);
            var preview = Preview(player);
            var damage = Damage(preview, strike);
            var hitPriority = Priority(preview, strike, enemy);
            var blockPriority = Priority(preview, defend, null);
            if (damage != 1 || blockPriority <= hitPriority)
                throw new InvalidOperationException("Targeted native cap/defense prior failed");
            previews.Add(new { mode, native_capped_damage = damage, hit_priority = hitPriority, defense_priority = blockPriority,
                upgraded_damage_before_cap = upgradedDamage, ordinary_damage_before_cap = ordinaryDamage,
                distinct_instances_and_fresh_native_state = true });
            var actions = new List<LocalAction>();
            while (hand.Any(c => c.CanPlay()))
            {
                var legal = hand.Where(c => c.CanPlay()).ToArray();
                var currentPreview = Preview(player);
                var card = mode == "attack" && legal.Any(c => c.Type == CardType.Attack) ?
                    legal.First(c => c.Type == CardType.Attack) :
                    legal.OrderByDescending(c => Priority(currentPreview, c, c.IsValidTarget(null) ? null : enemy)).First();
                Creature? target = card.IsValidTarget(null) ? null : enemy;
                actions.Add(new(hand.ToList().IndexOf(card), card.Id.ToString(), target?.CombatId, card.Title, target?.Name ?? "",
                    LocalCapture.Fingerprint(), CombatManager.Instance.DebugOnlyGetState()!.RoundNumber));
                RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, target));
                await Settle();
            }
            actions.Add(new(-1, "", null, "", "", LocalCapture.Fingerprint(),
                CombatManager.Instance.DebugOnlyGetState()!.RoundNumber, EndTurn: true));
            await EndTurn(player);
            var risk = (double)type.GetProperty("EndTurnHpLossHint")!.GetValue(Preview(player))!;
            results.Add(new(actions.ToArray(), player.Creature.CurrentHp, startingHp - player.Creature.CurrentHp,
                enemies.Sum(e => e.CurrentHp), player.Gold, player.Creature.MaxHp, false, false, false,
                StartingHp: startingHp, InitialEnemyHp: initialEnemyHp, EndTurnHpLossHint: risk));
        }
        var defended = results[0]; var attacked = results[1];
        if (defended.StartingHp != attacked.StartingHp || defended.InitialEnemyHp != attacked.InitialEnemyHp ||
            defended.Hp <= attacked.Hp || defended.EnemyHp <= attacked.EnemyHp ||
            !LocalSearchPolicy.Better(defended, attacked) || LocalSearchPolicy.Better(attacked, defended) ||
            LocalSearchTree.Reward(defended, defended.InitialEnemyHp!.Value) <= LocalSearchTree.Reward(attacked, attacked.InitialEnemyHp!.Value))
            throw new InvalidOperationException("Native defense/progress tradeoff did not reach both search feedback and incumbent ordering");
        LocalWire.Write(Path.Combine(root, "integration-survival-private.json"), results);
        LocalWire.Write(Path.Combine(root, "integration-survival-summary.json"), new
        {
            version = typeof(ModEntry).Assembly.GetName().Version!.ToString(),
            mod_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ModEntry).Assembly.Location))),
            fixture = "native single-turn intangible attacking enemies", previews,
            routes = results.Select((c, i) => new { mode = i == 0 ? "defense" : "attack", c.StartingHp, c.Hp, c.HpLost,
                c.InitialEnemyHp, c.EnemyHp, c.EndTurnHpLossHint, actions = c.Actions.Length,
                score = LocalSearchTree.Reward(c, c.InitialEnemyHp!.Value) }),
            legacy_enemy_hp_first_selects_attack = attacked.EnemyHp < defended.EnemyHp,
            new_incumbent_selects_defense = LocalSearchPolicy.Better(defended, attacked),
            new_feedback_selects_defense = LocalSearchTree.Reward(defended, defended.InitialEnemyHp!.Value) >
                LocalSearchTree.Reward(attacked, attacked.InitialEnemyHp!.Value),
            full_search = false, global_optimality_proven = false
        });
    }
}
