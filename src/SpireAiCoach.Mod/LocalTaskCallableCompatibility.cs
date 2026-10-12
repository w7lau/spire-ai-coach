using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Owned-worker compatibility for audited deferred VFX callbacks. The delegate and
// its asynchronous body still run; only the unsupported Task return is removed.
internal static class LocalTaskCallableCompatibility
{
    internal const string Owner = "SpireAiCoach.owned-worker.task-callable";
    internal static readonly List<string> Patched = [];
    private static readonly MethodInfo Factory = typeof(Callable).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(Callable.From) && m.IsGenericMethodDefinition &&
            m.GetGenericArguments().Length == 1 && m.GetParameters() is [{ ParameterType: var p }] &&
            p.IsGenericType && p.GetGenericTypeDefinition() == typeof(Func<>)).MakeGenericMethod(typeof(Task));
    private static readonly MethodInfo Replacement = typeof(LocalTaskCallableCompatibility)
        .GetMethod(nameof(FromTask), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static void Install()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name == "HextechRunes")
            .Select(a => a.GetType("HextechRunes.HextechCombatVfx", false)).SingleOrDefault(t => t != null);
        if (type == null) return;
        (string Name, Type[] Arguments)[] sites = [
            ("BoomerangSweep", [typeof(Creature), typeof(IReadOnlyList<Creature>), typeof(Texture2D), typeof(bool)]),
            ("OmegaJudgment", [typeof(IReadOnlyList<Creature>)]),
            ("FlyingKickStrike", [typeof(Creature), typeof(Creature)]),
            ("CorpseBloomBurst", [typeof(Creature), typeof(IReadOnlyList<Creature>)]),
            ("QuantumPulse", [typeof(Creature), typeof(IReadOnlyList<Creature>)])
        ];
        foreach (var site in sites)
        {
            var method = type.GetMethod(site.Name, BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.DeclaredOnly, null, site.Arguments, null);
            if (method == null || method.ReturnType != typeof(void) || method.IsGenericMethod ||
                LocalMethodBody.Read(method) is not { } body ||
                body.Count(i => i.Code == OpCodes.Call && Equals(i.Operand, Factory)) != 1 ||
                body.Count(i => i.Operand is MethodInfo call && call.DeclaringType == typeof(Callable) &&
                    call.Name == nameof(Callable.CallDeferred) && call.ReturnType == typeof(void)) != 1)
                continue;
            var patcher = new Harmony(Owner + "." + site.Name);
            try
            {
                patcher.Patch(method, transpiler: new(AccessTools.Method(typeof(LocalTaskCallableCompatibility), nameof(Replace))));
                Patched.Add(site.Name);
            }
            catch
            {
                patcher.UnpatchAll(patcher.Id);
                throw;
            }
        }
    }

    private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToArray();
        if (code.Count(i => i.opcode == OpCodes.Call && Equals(i.operand, Factory)) != 1)
            throw new InvalidOperationException("Deferred Task callable boundary changed");
        foreach (var instruction in code)
            if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, Factory))
                instruction.operand = Replacement;
        return code;
    }

    private static Callable FromTask(Func<Task> callback) => Callable.From((Action)(() => Invoke(callback)));

    // An Action is a void Godot callback. Awaiting retains ordinary task error
    // propagation through the captured native context, including runtime logs.
    private static async void Invoke(Func<Task> callback) => await callback();
}
