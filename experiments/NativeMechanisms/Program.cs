using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

if (args is ["--presentation-audit", var presentationPath]) return PresentationAudit.Run(presentationPath);

// Read metadata/IL only. Do not instantiate models or execute hooks outside Godot.
var assembly = typeof(CardModel).Assembly;
var models = assembly.GetTypes().Where(t => !t.IsAbstract && new[] {
    typeof(CardModel), typeof(RelicModel), typeof(PowerModel), typeof(PotionModel)
}.Any(b => b.IsAssignableFrom(t))).ToArray();
var calls = new List<object>();
var otherChoices = new List<object>();
foreach (var type in models)
foreach (var owner in Nested(type).Where(t => !typeof(IAsyncStateMachine).IsAssignableFrom(t)))
foreach (var method in owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
    BindingFlags.Static | BindingFlags.DeclaredOnly))
{
    var state = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
    var body = state?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) ?? method;
    foreach (var called in (LocalMethodBody.Read(body) ?? []).Select(i => i.Operand).OfType<MethodInfo>().Distinct())
        if (called.DeclaringType == typeof(CardSelectCmd))
            calls.Add(new { Model = type.FullName, Method = method.Name, Selection = called.ToString() });
        else if (called.DeclaringType?.Name.EndsWith("SelectCmd", StringComparison.Ordinal) == true ||
            called.DeclaringType?.Name == "PlayerChoiceSynchronizer")
            otherChoices.Add(new { Model = type.FullName, Method = method.Name, Owner = called.DeclaringType.FullName,
                Selection = called.ToString() });
}
var report = new {
    NativeModule = assembly.ManifestModule.ModuleVersionId,
    Counts = new { Cards = models.Count(t => typeof(CardModel).IsAssignableFrom(t)),
        Relics = models.Count(t => typeof(RelicModel).IsAssignableFrom(t)),
        Powers = models.Count(t => typeof(PowerModel).IsAssignableFrom(t)),
        Potions = models.Count(t => typeof(PotionModel).IsAssignableFrom(t)) },
    SelectionEntryPoints = typeof(CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.Static |
        BindingFlags.DeclaredOnly).Where(m => m.Name.StartsWith("From", StringComparison.Ordinal))
        .Select(m => m.ToString()).Order(StringComparer.Ordinal).ToArray(),
    SelectionScreens = assembly.GetTypes().Where(t => t.Namespace == "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection" &&
        t.Name.StartsWith("N", StringComparison.Ordinal) && !t.IsNested).Select(t => t.Name).Order(StringComparer.Ordinal).ToArray(),
    Models = models.Select(t => t.FullName).Order(StringComparer.Ordinal).ToArray(),
    SelectionCalls = calls,
    OtherChoiceReferences = otherChoices,
    CoverageFailures = LocalSelectionCoverage.Audit(),
    MetadataOnly = true
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (args is [var path]) File.WriteAllText(path, json);
else if (args.Length != 0) throw new ArgumentException("Optional report path expected");
Console.WriteLine(JsonSerializer.Serialize(new { report.Counts, ChoiceCalls = calls.Count,
    Entries = report.SelectionEntryPoints.Length, OtherChoices = otherChoices.Count, report.NativeModule, report.CoverageFailures }));
if (report.CoverageFailures.Length > 0) return 1;
return 0;

IEnumerable<Type> Nested(Type type)
{
    yield return type;
    foreach (var child in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        foreach (var descendant in Nested(child)) yield return descendant;
}
