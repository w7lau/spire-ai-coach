using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

// Metadata only. No game callback or recovery command is executed.
var estimator = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalRecoveryEstimator", true)!;
var read = estimator.GetMethod("ReadContent", BindingFlags.Instance | BindingFlags.NonPublic)!;
var request = new LocalSearchRequest("rest-metadata", "root", [], "native", 1, [], false);
var rows = new List<object>();
foreach (var (type, target, active, growth, heals, excluded) in new[] {
    (typeof(StoneHumidifier), true, false, false, 0, true),
    (typeof(StoneHumidifier), false, true, true, 0, false),
    (typeof(BurningBlood), true, false, false, 1, false),
    (typeof(ExternalRest), true, true, true, 0, false),
    (typeof(CombatHelper), true, true, true, 0, false),
    (typeof(CombatHeal), true, true, false, 0, false) })
{
    var instance = Activator.CreateInstance(estimator, request)!;
    estimator.GetField("_sourceTypes", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, new[] { type });
    var effect = read.Invoke(instance, [type, target])!;
    object? Get(string name) => effect.GetType().GetProperty(name)!.GetValue(effect);
    var actualActive = (bool)Get("ActiveRecovery")!;
    var actualGrowth = (bool)Get("DynamicMaxHp")!;
    var actualHeals = (int)Get("VictoryHeals")!;
    var actualExcluded = ((string[]?)Get("ExcludedCallbacks"))?.Any(s => s.EndsWith(".AfterRestSiteHeal", StringComparison.Ordinal)) == true;
    if (actualActive != active || actualGrowth != growth || actualHeals != heals || actualExcluded != excluded)
        throw new InvalidOperationException($"Wrong phase classification: {type.Name}, target={target}");
    rows.Add(new { source = type.Name, target, active = actualActive, dynamicMaxHp = actualGrowth,
        victoryHeals = actualHeals, restExcluded = actualExcluded });
}
var assembly = typeof(LocalWorker).Assembly;
File.WriteAllText(args[0], JsonSerializer.Serialize(new { completed = true,
    dll_sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location))),
    rows, callbacksExecuted = false, nativeSearchExecuted = false }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS {rows.Count} recovery phase metadata checks");

public abstract class ExternalRest : RelicModel
{
    public override Task AfterRestSiteHeal(Player player, bool isMend) => CreatureCmd.GainMaxHp(player.Creature, 1);
}
public abstract class CombatHelper : ExternalRest
{
    public Task Entry(Player player) => AfterRestSiteHeal(player, false);
}
public abstract class CombatHeal : RelicModel
{
    public Task Entry(Player player) => CreatureCmd.Heal(player.Creature, 1, true);
}
