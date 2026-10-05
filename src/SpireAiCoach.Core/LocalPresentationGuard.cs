using System.Reflection;
using System.Reflection.Emit;

namespace SpireAiCoach.Core;

// Read the native method, never execute it. Only a leading, side-effect-free
// "if (presentationDisabled) return null" is an optional visual boundary.
public static class LocalPresentationGuard
{
    // Match one forward conditional whose body contains only the audited visual
    // calls and temporary argument/local moves. Never remove a model callback,
    // arbitrary calls, state writes, branching rules, or a local used afterward.
    public static bool OptionalGuardedCalls(MethodInfo method, MethodInfo enabled, params MethodInfo[] calls)
    {
        if (method.IsGenericMethod || method.ReturnType != typeof(void)) return false;
        var body = LocalMethodBody.Read(method);
        return body != null && OptionalGuardedCalls(body, enabled, calls);
    }

    public static bool OptionalGuardedCalls(IEnumerable<LocalInstruction> instructions, MethodInfo enabled, params MethodInfo[] calls)
    {
        if (calls.Length == 0 || !enabled.IsStatic || enabled.ReturnType != typeof(bool) || enabled.GetParameters().Length != 0)
            return false;
        var code = instructions.Where(i => i.Code != OpCodes.Nop).ToArray();
        var guards = code.Select((i, n) => (i, n)).Where(x => x.i.Code == OpCodes.Call && Equals(x.i.Operand, enabled)).ToArray();
        if (guards.Length != 1) return false;
        int start = guards[0].n;
        if (start + 2 >= code.Length || (code[start + 1].Code != OpCodes.Brfalse && code[start + 1].Code != OpCodes.Brfalse_S) ||
            code[start + 1].Operand is not int target) return false;
        int end = Array.FindIndex(code, i => i.Offset == target);
        if (end <= start + 2) return false;
        var found = new List<MethodInfo>();
        var stored = new HashSet<int>();
        for (int n = start + 2; n < end; n++)
        {
            var i = code[n];
            if ((i.Code == OpCodes.Call || i.Code == OpCodes.Callvirt) && i.Operand is MethodInfo call) found.Add(call);
            else if (Local(i, false) is {} slot) stored.Add(slot);
            else if (Local(i, true) == null && i.Code != OpCodes.Ldarg_0 && i.Code != OpCodes.Ldarg_1 &&
                i.Code != OpCodes.Ldarg_2 && i.Code != OpCodes.Ldarg_3 && i.Code != OpCodes.Ldarg && i.Code != OpCodes.Ldarg_S &&
                i.Code != OpCodes.Ldfld)
                return false;
        }
        if (!found.SequenceEqual(calls)) return false;
        for (int n = 0; n < code.Length; n++)
        {
            if (n >= start + 2 && n < end) continue;
            var i = code[n];
            if (Local(i, true) is {} slot && stored.Contains(slot)) return false;
            bool Inside(int offset) => offset >= code[start + 2].Offset && offset < target;
            if (i.Code.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch &&
                (i.Operand is int jump && Inside(jump) || i.Operand is int[] jumps && jumps.Any(Inside))) return false;
        }
        return true;
    }

    private static int? Local(LocalInstruction i, bool load)
    {
        var codes = load ? new[] { OpCodes.Ldloc_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2, OpCodes.Ldloc_3 }
            : new[] { OpCodes.Stloc_0, OpCodes.Stloc_1, OpCodes.Stloc_2, OpCodes.Stloc_3 };
        int fixedSlot = Array.IndexOf(codes, i.Code);
        if (fixedSlot >= 0) return fixedSlot;
        if (i.Code == (load ? OpCodes.Ldloc : OpCodes.Stloc) || i.Code == (load ? OpCodes.Ldloc_S : OpCodes.Stloc_S))
            return i.Operand is byte small ? small : i.Operand is ushort large ? large : i.Operand is int slot ? slot : null;
        return null;
    }

    public static bool OptionalNullFactory(MethodInfo method, MethodInfo disabled)
    {
        if (!method.IsStatic || method.IsGenericMethod || method.ReturnType.IsValueType ||
            method.ReturnType == typeof(void) || method.GetMethodBody()?.ExceptionHandlingClauses.Count > 0)
            return false;
        var body = LocalMethodBody.Read(method);
        return body != null && OptionalNullFactory(body, disabled);
    }

    public static bool OptionalNullFactory(IEnumerable<LocalInstruction> instructions, MethodInfo disabled)
    {
        if (!disabled.IsStatic || disabled.ReturnType != typeof(bool) || disabled.GetParameters().Length != 0) return false;
        var code = instructions.Where(i => i.Code != OpCodes.Nop).ToArray();
        return code is { Length: >= 5 } && code[0].Code == OpCodes.Call && Equals(code[0].Operand, disabled) &&
            code[1].Code is var jump && (jump == OpCodes.Brfalse || jump == OpCodes.Brfalse_S) &&
            code[1].Operand is int target && target == code[4].Offset &&
            code[2].Code == OpCodes.Ldnull && code[3].Code == OpCodes.Ret;
    }
}
