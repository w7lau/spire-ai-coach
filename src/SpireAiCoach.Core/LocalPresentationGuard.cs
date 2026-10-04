using System.Reflection;
using System.Reflection.Emit;

namespace SpireAiCoach.Core;

// Read the native method, never execute it. Only a leading, side-effect-free
// "if (presentationDisabled) return null" is an optional visual boundary.
public static class LocalPresentationGuard
{
    public static bool OptionalNullFactory(MethodInfo method, MethodInfo disabled)
    {
        if (!method.IsStatic || method.IsGenericMethod || method.ReturnType.IsValueType ||
            method.ReturnType == typeof(void) || method.GetMethodBody()?.ExceptionHandlingClauses.Count > 0)
            return false;
        var code = LocalMethodBody.Read(method)?.Where(i => i.Code != OpCodes.Nop).ToArray();
        return code is { Length: >= 5 } && code[0].Code == OpCodes.Call && Equals(code[0].Operand, disabled) &&
            code[1].Code is var jump && (jump == OpCodes.Brfalse || jump == OpCodes.Brfalse_S) &&
            code[1].Operand is int target && target == code[4].Offset &&
            code[2].Code == OpCodes.Ldnull && code[3].Code == OpCodes.Ret;
    }
}
