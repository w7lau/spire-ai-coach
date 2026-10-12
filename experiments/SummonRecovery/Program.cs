using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Commands;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

if (args.Length != 2) throw new ArgumentException("mod-directories.json output.json");
var directories = JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[0]))!;
AssemblyLoadContext.Default.Resolving += (_, name) => {
    var path = directories.Select(p => Path.Combine(p, name.Name + ".dll")).FirstOrDefault(File.Exists);
    return path == null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
};
var estimatorType = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalRecoveryEstimator", true)!;
var read = estimatorType.GetMethod("ReadContent", BindingFlags.Instance | BindingFlags.NonPublic)!;
var request = new LocalSearchRequest("metadata", "frozen", [], "native", 1, [], false);
var results = new List<object>();
void Check(Type type, bool expected, bool target = true)
{
    var effect = read.Invoke(Activator.CreateInstance(estimatorType, request), [type, target])!;
    bool actual = (bool)effect.GetType().GetProperty("ActiveRecovery")!.GetValue(effect)!;
    if (actual != expected) throw new InvalidOperationException($"Recipient classification: {type.Name}, target={target}, active={actual}");
    results.Add(new { source = type.FullName, target, activeRecovery = actual, callbackExecuted = false });
}
foreach (var type in new[] { typeof(DirectSummon), typeof(AsyncSummon), typeof(HelperSummon), typeof(GrowSummon) })
{ Check(type, false); Check(type, true, false); }
foreach (var type in new[] { typeof(PlayerHeal), typeof(UnknownHeal), typeof(MixedHeal), typeof(MixedBranches), typeof(MutableRecipient) }) Check(type, true);
var runePath = directories.Select(p => Path.Combine(p, "lib/0.111.0/HextechRunes.dll")).First(File.Exists);
var rune = Assembly.LoadFrom(runePath).GetType("HextechRunes.BodyguardUpgradeRune", true)!;
Check(rune, false); Check(rune, true, false);
var harmony = new Harmony("OwnedSummonRecoveryMetadata");
foreach (var method in new[] { typeof(Player).GetProperty("Osty")!.GetMethod!,
    typeof(CreatureCmd).GetMethods().Single(m => m.Name == "Heal" && m.GetParameters().Length == 3) })
{
    try
    {
        harmony.Patch(method, prefix: new(typeof(Patches).GetMethod(nameof(Patches.Prefix))!));
        Check(typeof(DirectSummon), true); Check(rune, true);
    }
    finally { harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); }
    Check(typeof(DirectSummon), false);
}
var assembly = typeof(LocalWorker).Assembly;
var output = new { completed = true, version = assembly.GetName().Version!.ToString(3),
    dll_sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location))),
    rune_mvid = rune.Module.ModuleVersionId, results, nativeCombatBenchmark = false };
File.WriteAllText(args[1], JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS {results.Count} recipient metadata checks; callbacks not executed");

public static class Patches { public static void Prefix() { } }
public abstract class DirectSummon : RelicModel { public Task Entry(Player player) => CreatureCmd.Heal(player.Osty!, 1, true); }
public abstract class AsyncSummon : RelicModel { public async Task Entry(Player player) { var target = player.Osty!; await Task.CompletedTask; await CreatureCmd.Heal(target, 1, true); } }
public abstract class HelperSummon : RelicModel { public Task Entry(Player player) => Heal(player.Osty!); private static Task Heal(Creature c) => CreatureCmd.Heal(c, 1, true); }
public abstract class GrowSummon : RelicModel { public Task Entry(Player player) => CreatureCmd.GainMaxHp(player.Osty!, 1); }
public abstract class PlayerHeal : RelicModel { public Task Entry(Player player) => CreatureCmd.Heal(player.Creature, 1, true); }
public abstract class UnknownHeal : RelicModel { public Task Entry(Creature target) => CreatureCmd.Heal(target, 1, true); }
public abstract class MixedHeal : RelicModel { public Task Entry(Player player, bool choose) => CreatureCmd.Heal(choose ? player.Osty! : player.Creature, 1, true); }
public abstract class MixedBranches : RelicModel { public Task Entry(Player player, bool choose) { if (choose) return CreatureCmd.Heal(player.Osty!, 1, true); return CreatureCmd.Heal(player.Creature, 1, true); } }
public abstract class MutableRecipient : RelicModel { private Creature? _target; public Task Entry(Player player, Creature other) { _target = player.Osty!; Change(ref _target, other); return CreatureCmd.Heal(_target!, 1, true); } private static void Change(ref Creature? target, Creature other) => target = other; }
