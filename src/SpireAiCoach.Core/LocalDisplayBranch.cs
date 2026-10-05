using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace SpireAiCoach.Core;

// Metadata only. Extend a native optional display guard, never TestMode globally.
// A mixed block, escaping temporary, or unknown call keeps its original execution.
public static class LocalDisplayBranch
{
    public static int[] Find(MethodInfo method, MethodInfo enabled, Func<MethodBase, bool> displayCall, Func<Type, bool> displayValue)
    {
        var body = method.GetMethodBody();
        var code = LocalMethodBody.Read(method);
        return body == null || code == null ? [] : Find(code, method.DeclaringType!,
            body.LocalVariables.Select(l => l.LocalType).ToArray(), enabled, displayCall, displayValue);
    }

    public static int[] Find(IEnumerable<LocalInstruction> instructions, Type owner, Type[] locals, MethodInfo enabled,
        Func<MethodBase, bool> displayCall, Func<Type, bool> displayValue)
    {
        if (!enabled.IsStatic || enabled.ReturnType != typeof(bool) || enabled.GetParameters().Length != 0) return [];
        var code = instructions.Where(i => i.Code != OpCodes.Nop).ToArray();
        var found = new List<int>();
        bool async = typeof(IAsyncStateMachine).IsAssignableFrom(owner) && owner.IsDefined(typeof(CompilerGeneratedAttribute));
        for (int n = 0; n + 2 < code.Length; n++)
        {
            if (code[n].Code != OpCodes.Call || !Equals(code[n].Operand, enabled) ||
                code[n + 1].Code != OpCodes.Brfalse && code[n + 1].Code != OpCodes.Brfalse_S ||
                code[n + 1].Operand is not int target) continue;
            int end = Array.FindIndex(code, i => i.Offset == target);
            if (end <= n + 2) continue;
            bool valid = true, visual = false;
            var writes = new HashSet<FieldInfo>();
            var slots = new HashSet<int>();
            for (int k = n + 2; k < end && valid; k++)
            {
                var i = code[k];
                if (i.Code == OpCodes.Call || i.Code == OpCodes.Callvirt || i.Code == OpCodes.Newobj)
                {
                    valid = i.Operand is MethodBase call && (displayCall(call) || async && Infrastructure(call));
                    visual |= i.Operand is MethodBase operation && displayCall(operation);
                }
                else if (i.Code == OpCodes.Stfld)
                {
                    valid = i.Operand is FieldInfo field && async && field.DeclaringType == owner &&
                        (CompilerField(field) || displayValue(field.FieldType));
                    if (valid && !CompilerField((FieldInfo)i.Operand!)) writes.Add((FieldInfo)i.Operand!);
                }
                else if (i.Code == OpCodes.Ldflda)
                {
                    // An address can mutate a field through initobj/by-ref calls.
                    // Permit only this state machine's compiler/display storage.
                    valid = i.Operand is FieldInfo field && async && field.DeclaringType == owner &&
                        (CompilerField(field) || field.FieldType == typeof(AsyncTaskMethodBuilder) && field.Name == "<>t__builder" ||
                            displayValue(field.FieldType));
                    if (valid && displayValue(((FieldInfo)i.Operand!).FieldType)) writes.Add((FieldInfo)i.Operand!);
                }
                else if (i.Code == OpCodes.Stsfld || i.Code == OpCodes.Starg || i.Code == OpCodes.Starg_S ||
                    i.Code == OpCodes.Throw || i.Code == OpCodes.Rethrow || i.Code == OpCodes.Calli || i.Code == OpCodes.Ret ||
                    i.Code == OpCodes.Cpobj || i.Code == OpCodes.Cpblk || i.Code == OpCodes.Initblk ||
                    i.Code.Name?.StartsWith("stind", StringComparison.Ordinal) == true ||
                    i.Code.Name?.StartsWith("stelem", StringComparison.Ordinal) == true) valid = false;
                else if (Local(i, false) is { } slot) slots.Add(slot);
                else if (i.Code == OpCodes.Initobj)
                    valid = i.Operand is Type value && (displayValue(value) || value == typeof(TaskAwaiter) ||
                        Nullable.GetUnderlyingType(value) is { IsPrimitive: true });
                if (i.Code.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch)
                {
                    // The compiler leaves MoveNext only to suspend its display await.
                    bool suspension = async && (i.Code == OpCodes.Leave || i.Code == OpCodes.Leave_S) &&
                        i.Operand is int leave && code.Last().Offset == leave && code.Last().Code == OpCodes.Ret;
                    valid &= suspension || i.Operand is int jump && jump >= code[n + 2].Offset && jump <= target;
                }
            }
            if (!valid || !visual) continue;
            // Display values must not become inputs to later numerical effects.
            foreach (var i in code.Take(n + 2).Concat(code.Skip(end)))
            {
                if ((i.Code == OpCodes.Ldfld || i.Code == OpCodes.Ldflda) && i.Operand is FieldInfo field && writes.Contains(field))
                    valid = false;
                if (Local(i, true) is { } slot && slots.Contains(slot) &&
                    !(async && (slot == 0 && locals.ElementAtOrDefault(slot) == typeof(int) ||
                        locals.ElementAtOrDefault(slot) == typeof(TaskAwaiter)))) valid = false;
            }
            if (valid) found.Add(code[n].Offset);
        }
        return found.ToArray();
    }

    private static bool CompilerField(FieldInfo f) => f.Name == "<>1__state" && f.FieldType == typeof(int) ||
        f.Name.StartsWith("<>u__", StringComparison.Ordinal) && f.FieldType == typeof(TaskAwaiter);
    private static bool Infrastructure(MethodBase m) => m.DeclaringType == typeof(Task) && m.Name == nameof(Task.GetAwaiter) ||
        m.DeclaringType == typeof(TaskAwaiter) && m.Name is "get_IsCompleted" or "GetResult" ||
        m.DeclaringType == typeof(AsyncTaskMethodBuilder) && m.Name == "AwaitUnsafeOnCompleted";
    private static int? Local(LocalInstruction i, bool load)
    {
        var fixedCodes = load ? new[] { OpCodes.Ldloc_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2, OpCodes.Ldloc_3 }
            : new[] { OpCodes.Stloc_0, OpCodes.Stloc_1, OpCodes.Stloc_2, OpCodes.Stloc_3 };
        int fixedSlot = Array.IndexOf(fixedCodes, i.Code);
        if (fixedSlot >= 0) return fixedSlot;
        if (i.Code == (load ? OpCodes.Ldloc : OpCodes.Stloc) || i.Code == (load ? OpCodes.Ldloc_S : OpCodes.Stloc_S) ||
            load && (i.Code == OpCodes.Ldloca || i.Code == OpCodes.Ldloca_S))
            return i.Operand is byte small ? small : i.Operand is ushort big ? big : i.Operand is int slot ? slot : null;
        return null;
    }
}
