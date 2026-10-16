using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Relics;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

// Metadata only. Scalar values are declared fixtures; no game effects run.
var assembly = typeof(LocalWorker).Assembly;
if (args is ["--dump"])
{
    foreach (var m in typeof(BurningBlood).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
    {
        var state = m.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?.StateMachineType;
        var method = state?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? m;
        Console.WriteLine(m.Name);
        foreach (var i in LocalMethodBody.Read(method) ?? []) Console.WriteLine($"{i.Offset}: {i.Code.Name} {i.Operand}");
    }
    return;
}
var analysis = assembly.GetType("SpireAiCoach.Mod.LocalTerminalRecovery", true)!;
var read = analysis.GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic)!;
var rows = new List<object>();
int[][]? Read(Type type, int max, decimal threshold = 50, decimal heal = 12) =>
    (int[][]?)read.Invoke(null, [type, max, new Func<string, decimal?>(key => key switch {
        "HpThreshold" => threshold, "Heal" => heal, _ => null }), new Func<MethodBase, LocalInstruction[]?>(LocalMethodBody.Read)]);
void Check(Type type, int max, int start, int expected, decimal threshold = 50, decimal heal = 12)
{
    var maps = Read(type, max, threshold, heal);
    var actual = maps == null ? null : LocalTerminalHealth.Compose(start, maps);
    if (actual != expected) throw new Exception($"{type.Name} {start}/{max}: expected {expected}, got {actual}");
    rows.Add(new { source = type.Name, max, start, expected, actual, threshold, heal });
}
Check(typeof(MeatOnTheBone), 66, 52, 52);
Check(typeof(MeatOnTheBone), 66, 30, 42);
Check(typeof(MeatOnTheBone), 66, 33, 45);
Check(typeof(MeatOnTheBone), 67, 34, 45);
Check(typeof(MeatOnTheBone), 80, 20, 25, 25, 5);
Check(typeof(BurningBlood), 66, 30, 36, heal: 6);
if (Read(typeof(MeatOnTheBone), 100000) != null) throw new Exception("Unbounded HP enumeration");
var helper = AccessTools.Method(typeof(MeatOnTheBone), "WillHealOnCombatFinished");
var harmony = new Harmony("ConditionalRecovery.fixture");
harmony.Patch(helper, prefix: new HarmonyMethod(typeof(Patch), nameof(Patch.Prefix)));
if (Read(typeof(MeatOnTheBone), 66) != null) throw new Exception("Patched predicate was accepted");
harmony.UnpatchAll(harmony.Id);
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, metadata_only = true,
    product_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))), rows }));
static class Patch { public static void Prefix() { } }
