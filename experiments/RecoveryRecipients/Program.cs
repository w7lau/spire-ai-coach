using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Enchantments;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

// Metadata only. Never execute any fixture callback or game effect.
var estimator = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalRecoveryEstimator", true)!;
var read = AccessTools.Method(estimator, "ReadContent");
var deck = AccessTools.Method(estimator, "CallCanChangeDeck");
var request = new LocalSearchRequest("recipient-metadata", "root", [], "native", 1, [], false);
var rows = new List<object>();
void Check(Type type, bool active, bool target = true, bool combatOnly = false)
{
    var instance = Activator.CreateInstance(estimator, request)!;
    AccessTools.Field(estimator, "_sourceTypes").SetValue(instance, new[] { type });
    AccessTools.Field(estimator, "_combatPileEventsOnly").SetValue(instance, combatOnly);
    var effect = read.Invoke(instance, [type, target])!;
    var actual = (bool)effect.GetType().GetProperty("ActiveRecovery")!.GetValue(effect)!;
    if (actual != active) throw new InvalidOperationException($"{type.Name}: target={target}, combatOnly={combatOnly}, expected={active}, actual={actual}");
    rows.Add(new { source = type.Name, target, combatOnly, active = actual });
}
void DeckCheck(MethodInfo method, bool expected)
{
    bool actual = (bool)deck.Invoke(null, [method, null])!;
    if (actual != expected) throw new InvalidOperationException($"Deck mutation: {method.Name}, expected={expected}, actual={actual}");
    rows.Add(new { method = method.DeclaringType!.Name + "." + method.Name, changesDeck = actual });
}
foreach (var t in new[] { typeof(FurCoat), typeof(EnemyList), typeof(EnemyGuard), typeof(AsyncEnemyGuard), typeof(SummonSet) })
{ Check(t, false); Check(t, true, target: false); }
foreach (var t in new[] { typeof(PlayerSet), typeof(UnknownSet), typeof(MixedSet), typeof(OwnerHealFromEnemy), typeof(UnguardedTail), typeof(MutableRecipient) })
    Check(t, true);
Check(typeof(BookOfFiveRings), false, combatOnly: true);
Check(typeof(BookOfFiveRings), true, combatOnly: false);
Check(typeof(SoulsPower), false);
var removeKeyword = typeof(CardCmd).GetMethod("RemoveKeyword")!;
DeckCheck(removeKeyword, false);
foreach (var m in typeof(CardPileCmd).GetMethods().Where(m => m.Name is "RemoveFromDeck" or "AddCurseToDeck" or "AddCursesToDeck")) DeckCheck(m, true);
foreach (var m in typeof(CardCmd).GetMethods().Where(m => m.Name.StartsWith("Transform"))) DeckCheck(m, true);
var patcher = new Harmony("OwnedRecoveryRecipientMetadata");
foreach (var (method, fixture) in new[] {
    (typeof(Creature).GetProperty("Side")!.GetMethod!, typeof(EnemyGuard)),
    (typeof(CombatState).GetProperty("HittableEnemies")!.GetMethod!, typeof(EnemyList)),
    (typeof(CreatureCmd).GetMethod("SetCurrentHp")!, typeof(EnemyGuard)),
    (typeof(EnemyGuard).GetMethod("Entry")!, typeof(EnemyGuard)) })
{
    try { patcher.Patch(method, prefix: new(typeof(Patches).GetMethod("Prefix")!)); Check(fixture, true); }
    finally { patcher.Unpatch(method, HarmonyPatchType.All, patcher.Id); }
    Check(fixture, false);
}
try { patcher.Patch(removeKeyword, prefix: new(typeof(Patches).GetMethod("Prefix")!)); DeckCheck(removeKeyword, true); }
finally { patcher.Unpatch(removeKeyword, HarmonyPatchType.All, patcher.Id); }
DeckCheck(removeKeyword, false);
var assembly = typeof(LocalWorker).Assembly;
File.WriteAllText(args[0], JsonSerializer.Serialize(new { completed = true,
    dll_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
    rows, callbacksExecuted = false, nativeSearchExecuted = false }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS {rows.Count} recipient/deck metadata checks");

public static class Patches { public static void Prefix() { } }
public abstract class EnemyList : RelicModel { public async Task Entry(ICombatState state) { foreach (var c in state.HittableEnemies) await CreatureCmd.SetCurrentHp(c, 1); } }
public abstract class EnemyGuard : RelicModel { public Task Entry(Creature c) => c.Side == CombatSide.Enemy ? CreatureCmd.SetCurrentHp(c, 1) : Task.CompletedTask; }
public abstract class AsyncEnemyGuard : RelicModel { public async Task Entry(Creature c) { if (c.Side != CombatSide.Enemy) return; await Task.CompletedTask; await CreatureCmd.SetCurrentHp(c, 1); } }
public abstract class SummonSet : RelicModel { public Task Entry(Player p) => CreatureCmd.SetCurrentHp(p.Osty!, 1); }
public abstract class PlayerSet : RelicModel { public Task Entry(Player p) => CreatureCmd.SetCurrentHp(p.Creature, 1); }
public abstract class UnknownSet : RelicModel { public Task Entry(Creature c) => CreatureCmd.SetCurrentHp(c, 1); }
public abstract class MixedSet : RelicModel { public Task Entry(Player p, Creature c, bool choose) => CreatureCmd.SetCurrentHp(choose ? p.Creature : c, 1); }
public abstract class OwnerHealFromEnemy : RelicModel { public Task Entry(Creature c, Player p) => c.Side == CombatSide.Enemy ? CreatureCmd.Heal(p.Creature, 1, true) : Task.CompletedTask; }
public abstract class UnguardedTail : RelicModel { public async Task Entry(Creature c) { if (c.Side == CombatSide.Enemy) await CreatureCmd.SetCurrentHp(c, 1); await CreatureCmd.Heal(c, 1, true); } }
public abstract class MutableRecipient : RelicModel { public Task Entry(Creature c, Player p) { if (c.Side != CombatSide.Enemy) return Task.CompletedTask; Change(ref c, p.Creature); return CreatureCmd.Heal(c, 1, true); } private static void Change(ref Creature c, Creature other) => c = other; }
