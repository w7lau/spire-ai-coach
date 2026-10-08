using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Metadata-only recognition of native automatic, death-triggered HP recovery.
// This is used for the estimated return goal, never for a pruning certificate.
internal sealed record LocalRevivalEstimate(decimal Fraction, decimal Minimum)
{
    public decimal MaximumHp(decimal maxHp) => decimal.Ceiling(Math.Max(Minimum, maxHp * Fraction));
    internal bool CanImprove(decimal goal, decimal maxHp, decimal terminalGain, bool dynamicMaxHp) =>
        dynamicMaxHp || MaximumHp(maxHp) + terminalGain > goal;

    internal static bool Automatic(Type type)
    {
        if (type.Assembly != typeof(AbstractModel).Assembly || !typeof(PotionModel).IsAssignableFrom(type)) return false;
        var getter = type.GetProperty("Usage")?.GetMethod;
        var code = getter == null ? null : LocalMethodBody.Read(getter);
        return getter != null && Unpatched(getter) && code is { Length: 2 } && code[1].Code == OpCodes.Ret &&
            LocalIlFacts.Integer(code[0]) is { } usage && Enum.GetName(getter.ReturnType, usage) == "Automatic";
    }

    internal static LocalRevivalEstimate? Read(Type type)
    {
        if (!Automatic(type)) return null;
        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        // Another callback or public entry point could supply ordinary recovery.
        if (methods.Any(m => !m.IsSpecialName && (m.IsVirtual || m.IsPublic) &&
            m.Name is not ("OnUse" or "ShouldDie" or "AfterPreventingDeath"))) return null;
        var use = methods.SingleOrDefault(m => m.Name == "OnUse");
        var death = methods.SingleOrDefault(m => m.Name == "AfterPreventingDeath");
        var shouldDie = methods.SingleOrDefault(m => m.Name == "ShouldDie");
        if (use == null || death == null || shouldDie == null || methods.Any(m => !Unpatched(m))) return null;
        var prevention = LocalMethodBody.Read(shouldDie);
        if (prevention is not { Length: 9 } || prevention[0].Code != OpCodes.Ldarg_1 ||
            prevention[1].Code != OpCodes.Ldarg_0 || !IsCall(prevention[2], typeof(PotionModel).FullName!, "get_Owner") ||
            !IsCall(prevention[3], "MegaCrit.Sts2.Core.Entities.Players.Player", "get_Creature") ||
            prevention[4].Code != OpCodes.Beq_S || LocalIlFacts.Integer(prevention[5]) != 1 ||
            prevention[6].Code != OpCodes.Ret || LocalIlFacts.Integer(prevention[7]) != 0 ||
            prevention[8].Code != OpCodes.Ret || prevention[4].Operand is not int sameOwner ||
            sameOwner != prevention[7].Offset) return null;
        var body = AsyncBody(use);
        var deathBody = AsyncBody(death);
        if (body == null || deathBody == null || !OnlyCalls(body, false) || !OnlyCalls(deathBody, true)) return null;
        if (deathBody.Count(i => IsCall(i, typeof(PotionModel).FullName!, "OnUseWrapper")) != 1 ||
            body.Count(i => IsCall(i, "MegaCrit.Sts2.Core.Commands.CreatureCmd", "Heal")) != 1) return null;
        // Recognize target.MaxHp * a constant decimal fraction, with a fixed
        // minimum. Other formulas, writes or helper calls keep the old goal.
        for (int n = 4; n + 12 < body.Length; n++)
        {
            if (!IsCall(body[n], "MegaCrit.Sts2.Core.Entities.Creatures.Creature", "get_MaxHp") ||
                !IsCall(body[n + 1], "System.Decimal", "op_Implicit")) continue;
            if (body[n - 4].Code != OpCodes.Ldarg_0 || body[n - 3].Code != OpCodes.Ldfld ||
                body[n - 2].Code != OpCodes.Ldarg_0 || body[n - 1].Code != OpCodes.Ldfld ||
                body[n - 3].Operand is not FieldInfo target || body[n - 1].Operand is not FieldInfo amountTarget ||
                target != amountTarget || target.FieldType.FullName != "MegaCrit.Sts2.Core.Entities.Creatures.Creature") continue;
            var parts = Enumerable.Range(n + 2, 5).Select(i => LocalIlFacts.Integer(body[i])).ToArray();
            if (parts.Any(p => p == null) || parts[3] is not (0 or 1) || parts[4] is < 0 or > 28 ||
                body[n + 7].Code != OpCodes.Newobj || body[n + 7].Operand is not ConstructorInfo ctor ||
                ctor.DeclaringType != typeof(decimal) ||
                !ctor.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(int), typeof(int), typeof(int), typeof(bool), typeof(byte) }) ||
                !IsCall(body[n + 8], "System.Decimal", "op_Multiply") ||
                body[n + 9].Code != OpCodes.Ldsfld || body[n + 9].Operand is not FieldInfo minimum ||
                minimum.DeclaringType != typeof(decimal) || minimum.Name is not ("One" or "Zero") ||
                !IsCall(body[n + 10], "System.Math", "Max") || LocalIlFacts.Integer(body[n + 11]) != 1 ||
                !IsCall(body[n + 12], "MegaCrit.Sts2.Core.Commands.CreatureCmd", "Heal")) continue;
            decimal fraction = new(parts[0]!.Value, parts[1]!.Value, parts[2]!.Value, parts[3] == 1, (byte)parts[4]!.Value);
            if (fraction is < 0 or > 1) return null;
            return new(fraction, minimum.Name == "One" ? 1 : 0);
        }
        return null;
    }

    private static LocalInstruction[]? AsyncBody(MethodInfo method)
    {
        var state = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        var move = state?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return move != null && Unpatched(move) ? LocalMethodBody.Read(move) : null;
    }

    private static bool OnlyCalls(LocalInstruction[] body, bool death)
    {
        foreach (var i in body)
        {
            if (i.Code == OpCodes.Stsfld || i.Code == OpCodes.Calli || i.Code == OpCodes.Ldftn || i.Code == OpCodes.Ldvirtftn) return false;
            if (i.Code == OpCodes.Stfld && i.Operand is FieldInfo written &&
                !written.DeclaringType!.IsDefined(typeof(CompilerGeneratedAttribute), false)) return false;
            if (i.Operand is not MethodBase method) continue;
            string? owner = method.DeclaringType?.FullName;
            bool infrastructure = owner == "System.Threading.Tasks.Task" && method.Name == "GetAwaiter" ||
                owner == "System.Runtime.CompilerServices.TaskAwaiter" && method.Name is "get_IsCompleted" or "GetResult" ||
                owner == "System.Runtime.CompilerServices.AsyncTaskMethodBuilder" &&
                method.Name is "AwaitUnsafeOnCompleted" or "SetException" or "SetResult";
            bool effect = death ? owner == typeof(PotionModel).FullName && method.Name == "OnUseWrapper" ||
                owner == "MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext" && method.IsConstructor :
                owner == typeof(PotionModel).FullName && method.Name == "AssertValidForTargetedPotion" ||
                owner == "MegaCrit.Sts2.Core.Entities.Creatures.Creature" && method.Name == "get_MaxHp" ||
                owner == "System.Decimal" && method.Name is "op_Implicit" or ".ctor" or "op_Multiply" ||
                owner == "System.Math" && method.Name == "Max" ||
                owner == "MegaCrit.Sts2.Core.Commands.CreatureCmd" && method.Name == "Heal";
            if (!infrastructure && !effect) return false;
        }
        return true;
    }

    private static bool IsCall(LocalInstruction instruction, string owner, string name) =>
        instruction.Code.FlowControl == FlowControl.Call && instruction.Operand is MethodBase method &&
        method.DeclaringType?.FullName == owner && method.Name == name;

    private static bool Unpatched(MethodBase method)
    {
        var info = Harmony.GetPatchInfo(method);
        return info == null || !info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
            .Any(p => !p.owner.StartsWith("SpireAiCoach", StringComparison.Ordinal));
    }
}
