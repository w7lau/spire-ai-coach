using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using SpireAiCoach.Core;
using V = SpireAiCoach.Core.LocalScalarProgram.Value;

namespace SpireAiCoach.Mod;

// Native scalar terminal callbacks, not a table of relics. Evaluate metadata
// over all surviving input HP values; never invoke callbacks/getters or RNG.
internal static class LocalTerminalRecovery
{
    private static readonly Assembly Native = typeof(AbstractModel).Assembly;
    internal static bool IsTerminal(MethodInfo method) => method.GetBaseDefinition().DeclaringType == typeof(AbstractModel) &&
        method.Name is "AfterCombatVictoryEarly" or "AfterCombatVictory" or "AfterCombatVictoryLate" or "AfterCombatEnd";

    internal static int[][]? Read(Type type, int maxHp, Func<string, decimal?> variableValue, Func<MethodBase, LocalInstruction[]?> body)
    {
        if (type.Assembly != Native || maxHp is <= 0 or > 4096) return null;
        var roots = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.DeclaringType != typeof(AbstractModel) && m.DeclaringType != typeof(RelicModel) && IsTerminal(m)).ToArray();
        if (roots.Length == 0 || roots.Length > 8) return null;
        var maps = new List<int[]>();
        var stable = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var methods = new Dictionary<MethodBase, LocalInstruction[]?>();
        bool Trusted(MethodBase m) => m.DeclaringType?.Assembly == Native ||
            m.DeclaringType?.Assembly == typeof(object).Assembly;
        bool Unpatched(MethodBase m) => Harmony.GetPatchInfo(m) is not { } p ||
            !p.Prefixes.Concat(p.Postfixes).Concat(p.Finalizers).Concat(p.Transpilers)
                .Any(x => !x.owner.StartsWith("SpireAiCoach.", StringComparison.Ordinal));
        // A source may update its amount without directly healing. Such a
        // mutable formula cannot use today's captured scalar in future states.
        var checkedMethods = new HashSet<MethodBase>();
        bool Stable(MethodBase method)
        {
            if (!checkedMethods.Add(method)) return true;
            if (checkedMethods.Count > 128 || !Unpatched(method)) return false;
            var state = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
            if (state != null && !Stable(state.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!)) return false;
            var code = body(method); if (code == null) return false;
            foreach (var called in code.Select(i => i.Operand).OfType<MethodBase>())
            {
                if (called.DeclaringType?.Namespace == "MegaCrit.Sts2.Core.Localization.DynamicVars" &&
                    !called.IsConstructor && !called.Name.StartsWith("get_", StringComparison.Ordinal)) return false;
                if (called.DeclaringType == type && !called.IsConstructor && !called.IsSpecialName && !Stable(called)) return false;
            }
            return true;
        }
        if (type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName).Any(m => !Stable(m))) return null;
        foreach (var root in roots)
        {
            var map = new int[maxHp + 1]; bool hasHeal = false;
            for (int input = 1; input <= maxHp; input++)
            {
                int hp = input, depth = 0, calls = 0;
                V? Evaluate(MethodBase method, V[] arguments)
                {
                    if (++depth > 12 || ++calls > 128 || !Trusted(method) || !Unpatched(method)) { depth--; return null; }
                    try
                    {
                        var state = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
                        var actual = state?.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ?? method;
                        if (!Unpatched(actual)) return null;
                        if (!methods.TryGetValue(actual, out var code)) methods[actual] = code = body(actual);
                        if (code == null) return null;
                        return LocalScalarProgram.Read(code, state == null ? arguments : [V.T("frame")], Field, Call);
                    }
                    finally { depth--; }
                }
                V? Field(FieldInfo field, V? receiver)
                {
                    if (field.DeclaringType == typeof(decimal)) return field.Name switch {
                        "Zero" => V.N(0), "One" => V.N(1), _ => null };
                    if (receiver?.Tag != "frame" || !field.DeclaringType!.IsDefined(typeof(CompilerGeneratedAttribute), false)) return null;
                    return field.Name switch { "<>1__state" => V.N(-1), "<>4__this" => V.T("source"),
                        "<>t__builder" => V.T("builder"), _ => null };
                }
                V? Call(MethodBase method, V[] args)
                {
                    if (!Trusted(method) || !Unpatched(method)) return null;
                    var ownerName = method.DeclaringType?.FullName; var name = method.Name;
                    var number = LocalScalarProgram.Arithmetic(method, args); if (number != null) return number;
                    if (ownerName == "System.Threading.Tasks.Task" && name is "GetAwaiter" or "get_CompletedTask") return V.T("awaiter");
                    if (ownerName == "System.Runtime.CompilerServices.TaskAwaiter") return name switch {
                        "get_IsCompleted" => V.N(1), "GetResult" => V.T("void"), _ => null };
                    if (ownerName == "System.Runtime.CompilerServices.AsyncTaskMethodBuilder" && name == "SetResult") return V.T("void");
                    if (ownerName == "MegaCrit.Sts2.Core.Models.RelicModel" && args.FirstOrDefault().Tag == "source")
                    {
                        if (name == "get_Owner") return V.T("player");
                        if (name == "get_DynamicVars") return V.T("variables");
                        if (name == "set_Status") return V.T("void");
                        if (name == "Flash") return V.T("void");
                    }
                    if (ownerName == "MegaCrit.Sts2.Core.Models.AbstractModel" && name == "Flash") return V.T("void");
                    if (ownerName == "MegaCrit.Sts2.Core.Entities.Players.Player" && name == "get_Creature" && args[0].Tag == "player") return V.T("creature");
                    if (ownerName == "MegaCrit.Sts2.Core.Entities.Creatures.Creature" && args[0].Tag == "creature")
                        return name switch { "get_CurrentHp" => V.N(hp), "get_MaxHp" => V.N(maxHp), "get_IsDead" => V.N(0), _ => null };
                    if (ownerName == "MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet" && args[0].Tag == "variables")
                    {
                        var key = name == "get_Item" ? args.ElementAtOrDefault(1).Tag : name.StartsWith("get_", StringComparison.Ordinal) ? "string:" + name[4..] : null;
                        return key?.StartsWith("string:", StringComparison.Ordinal) == true ? V.T("variable:" + key[7..]) : null;
                    }
                    if (ownerName == "MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar" && name == "get_BaseValue" &&
                        args[0].Tag is { } tag && tag.StartsWith("variable:", StringComparison.Ordinal))
                    {
                        var key = tag[9..];
                        if (!stable.TryGetValue(key, out var value))
                        {
                            // Existing native scalar storage, not invocation of an inspected helper.
                            if (variableValue(key) is not { } scalar) return null;
                            stable[key] = value = scalar;
                        }
                        return V.N(value);
                    }
                    if (ownerName == "MegaCrit.Sts2.Core.Commands.CreatureCmd" && name == "Heal" &&
                        args.Length == 3 && args[0].Tag == "creature" && args[1].Number is >= 0 && args[2].Number == 1)
                    {
                        hp = (int)Math.Min(maxHp, hp + decimal.Ceiling(args[1].Number!.Value));
                        hasHeal = true; return V.T("task");
                    }
                    if (method.DeclaringType == type && !method.IsVirtual && !method.IsConstructor) return Evaluate(method, args);
                    return null;
                }
                var values = new[] { V.T("source") }.Concat(root.GetParameters().Select(_ => V.T("unknown"))).ToArray();
                if (Evaluate(root, values) == null) return null;
                map[input] = hp;
            }
            if (hasHeal) maps.Add(LocalTerminalHealth.Envelope(map));
        }
        return maps.Count == 0 ? null : maps.ToArray();
    }
}
