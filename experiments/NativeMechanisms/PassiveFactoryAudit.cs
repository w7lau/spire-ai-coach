using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

internal static class PassiveFactoryAudit
{
    public static int Run(string path)
    {
        var passive = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalPassiveVfx", true)!;
        var eligible = passive.GetMethod("Eligible")!.CreateDelegate<Func<MethodInfo, bool>>();
        var leaf = passive.GetMethod("Leaf", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Func<MethodBase, Type, bool>>();
        var disabled = typeof(TestMode).GetProperty(nameof(TestMode.IsOn))!.GetMethod!;
        var rows = typeof(AbstractModel).Assembly.GetTypes().Where(t =>
                t.Namespace?.StartsWith("MegaCrit.Sts2.Core.Nodes.Vfx", StringComparison.Ordinal) == true && typeof(Node).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.Name == "Create" && typeof(Node).IsAssignableFrom(m.ReturnType))
            .Select(m => new { type = m.DeclaringType!.FullName, signature = m.ToString(),
                optional = LocalPresentationGuard.OptionalNullFactory(m, disabled), passive = eligible(m),
                fields = m.DeclaringType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance |
                    BindingFlags.DeclaredOnly).Select(f => f.FieldType.FullName + " " + f.Name).ToArray(),
                unknownCalls = m.DeclaringType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance |
                    BindingFlags.DeclaredOnly).Where(method => method.GetCustomAttribute<System.ComponentModel.EditorBrowsableAttribute>() == null &&
                        method.Name != "get_AssetPaths")
                    .Select(method => method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType?
                        .GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance) ?? method)
                    .SelectMany(method => LocalMethodBody.Read(method) ?? []).Select(i => i.Operand).OfType<MethodBase>()
                    .Where(call => call.DeclaringType != m.DeclaringType && !leaf(call, m.DeclaringType)).Distinct()
                    .Select(call => call.DeclaringType!.FullName + "." + call.Name).ToArray(),
                calls = LocalMethodBody.Read(m)?.Select(i => i.Operand).OfType<MethodBase>().Distinct()
                    .Select(c => c.DeclaringType!.FullName + "." + c.Name).ToArray() }).ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(new { nativeModule = typeof(AbstractModel).Assembly.ManifestModule.ModuleVersionId,
            metadataOnly = true, callbacksExecuted = false, rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { factories = rows.Length, passive = rows.Where(r => r.passive).Select(r => r.type),
            metadataOnly = true, callbacksExecuted = false }));
        return rows.Any(r => r.type == "MegaCrit.Sts2.Core.Nodes.Vfx.NGroundFireVfx" && r.passive) ? 0 : 1;
    }
}
