using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

// Inspect the installed game's actual asynchronous method bodies, without
// launching a battle, executing model getters, changing saves or benchmarking.
var estimatorType = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalRecoveryEstimator", true)!;
var request = new LocalSearchRequest("metadata-audit", "root", [], "native", 1, [], false);
var estimator = Activator.CreateInstance(estimatorType, request)!;
var describe = estimatorType.GetMethod("Describe", BindingFlags.NonPublic | BindingFlags.Instance)!;
var output = new List<object>();
foreach (var (id, expectedHeals, expectedUnknown) in new (string, int, bool)[]
{
    ("Cards.StrikeIronclad", 0, false), ("Cards.DefendIronclad", 0, false),
    ("Cards.Armaments", 0, false), ("Cards.TrueGrit", 0, false),
    ("Cards.Barricade", 0, false), ("Cards.BodySlam", 0, false),
    ("Cards.Discovery", 0, true), ("Cards.Feed", 0, true),
    ("Relics.BurningBlood", 1, false), ("Relics.BlackBlood", 1, false)
})
{
    var type = typeof(AbstractModel).Assembly.GetType("MegaCrit.Sts2.Core.Models." + id, true)!;
    var proof = describe.Invoke(estimator, [type])!;
    var proofType = proof.GetType();
    var unknown = proofType.GetProperty("Unknown")!.GetValue(proof) as string;
    var heals = (int)proofType.GetProperty("VictoryHeals")!.GetValue(proof)!;
    var references = (Type[])proofType.GetProperty("References")!.GetValue(proof)!;
    if ((unknown != null) != expectedUnknown || heals != expectedHeals)
        throw new InvalidOperationException("Unexpected recovery certificate for " + id + ": " + unknown);
    output.Add(new { Model = id, Unknown = unknown, VictoryHealCalls = heals, References = references.Select(t => t.Name).ToArray() });
}
// Find real native healing powers by their code rather than assuming names
// carried over from the first game.
foreach (var type in typeof(AbstractModel).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(PowerModel).IsAssignableFrom(t)))
{
    bool writesHp = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Any(method =>
        {
            var state = method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?.StateMachineType;
            var body = state?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? method;
            return LocalMethodBody.Read(body)?.Any(i => i.Operand is MethodInfo called &&
                called.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Commands.CreatureCmd" &&
                called.Name is "Heal" or "GainMaxHp") == true;
        });
    if (!writesHp) continue;
    var proof = describe.Invoke(estimator, [type])!;
    var unknown = proof.GetType().GetProperty("Unknown")!.GetValue(proof) as string;
    if (unknown == null) throw new InvalidOperationException("Repeated healing power became bounded: " + type.Name);
    output.Add(new { Model = type.FullName, Unknown = unknown });
}
var external = Activator.CreateInstance(estimatorType, request with { LoadedMods = ["unknown-mod:1"] })!;
var guarded = (LocalRecoveryAllowance)estimatorType.GetMethod("Estimate")!.Invoke(external, [null])!;
if (guarded.MaximumFurtherHpGain != null || !guarded.Reason.Contains("Mod", StringComparison.Ordinal))
    throw new InvalidOperationException("External global effects were treated as no healing");
var report = new { ModVersion = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3),
    ModSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        File.ReadAllBytes(typeof(LocalWorker).Assembly.Location))),
    NativeModule = typeof(AbstractModel).Assembly.ManifestModule.ModuleVersionId,
    MetadataChecks = output.Count + 1, ExternalGlobalEffectsRemainUnknown = true, Models = output,
    NativeBattleExecuted = false, SpeedBenchmarkExecuted = false };
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
if (args is [var destination]) File.WriteAllText(destination, json);
else if (args.Length != 0) throw new ArgumentException("Pass an optional report path");
Console.WriteLine(json);
