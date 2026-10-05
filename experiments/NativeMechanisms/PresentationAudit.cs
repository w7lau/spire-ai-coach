using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

// Candidate discovery only. A node reference does not prove that an effect can be skipped.
internal static class PresentationAudit
{
    public static int Run(string path)
    {
        var assembly = typeof(AbstractModel).Assembly;
        var models = assembly.GetTypes().Where(t => typeof(AbstractModel).IsAssignableFrom(t)).ToArray();
        var rows = new List<object>();
        var unreadable = new List<string>();
        var display = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalModelDisplay", true)!;
        var displayCall = display.GetMethod("DisplayCall", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Func<MethodBase, bool>>();
        var displayValue = display.GetMethod("DisplayValue", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Func<Type, bool>>();
        var enabled = typeof(AbstractModel).Assembly.GetType("MegaCrit.Sts2.Core.TestSupport.TestMode", true)!.GetProperty("IsOff")!.GetMethod!;
        int scanned = 0;
        foreach (var type in models)
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            scanned++;
            var state = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
            var body = state?.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ?? method;
            var code = LocalMethodBody.Read(body);
            if (code == null) { if (body.GetMethodBody() != null) unreadable.Add(type.FullName + "." + method.Name); continue; }
            var calls = code.Select(i => i.Operand).OfType<MethodBase>().Distinct().ToArray();
            var visual = calls.Where(c => c.DeclaringType?.Namespace?.StartsWith("MegaCrit.Sts2.Core.Nodes", StringComparison.Ordinal) == true ||
                c.DeclaringType?.Namespace == "Godot").ToArray();
            if (visual.Length == 0) continue;
            rows.Add(new { model = type.FullName, method = method.Name, async = state != null,
                guard = calls.Any(c => c.DeclaringType?.Name == "TestMode" && c.Name is "get_IsOn" or "get_IsOff"),
                eligibleGuards = LocalDisplayBranch.Find(body, enabled, displayCall, displayValue),
                visual = visual.Select(c => c.DeclaringType!.FullName + "." + c.Name).ToArray(),
                calls = calls.Select(c => c.DeclaringType!.FullName + "." + c.Name).ToArray() });
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new { nativeModule = assembly.ManifestModule.ModuleVersionId,
            modelTypes = models.Length, methodsScanned = scanned, candidateMethods = rows.Count,
            unreadable, rows, metadataOnly = true, callbacksExecuted = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { modelTypes = models.Length, methodsScanned = scanned,
            candidateMethods = rows.Count, unreadable = unreadable.Count, metadataOnly = true }));
        return 0;
    }
}
