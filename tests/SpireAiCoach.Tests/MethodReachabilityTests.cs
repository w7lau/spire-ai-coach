using System.Reflection.Emit;
using SpireAiCoach.Core;

internal static class MethodReachabilityTests
{
    public static void Register(Action<string, Action> test)
    {
        LocalInstruction[] guarded = [new(0, OpCodes.Brtrue_S, 3), new(1, OpCodes.Call, null),
            new(2, OpCodes.Ret, null), new(3, OpCodes.Ret, null)];
        void Check(bool condition) { if (!condition) throw new Exception("Reachable effect paths changed"); }
        test("known branch excludes only its unreachable effect", () =>
            Check(LocalMethodBody.Reachable(guarded, _ => true).SequenceEqual([true, false, false, true])));
        test("unknown branch retains both effect paths", () =>
            Check(LocalMethodBody.Reachable(guarded, _ => null).All(value => value)));
        test("exception handler effects remain independent roots", () =>
            Check(LocalMethodBody.Reachable(guarded, _ => true, [1]).All(value => value)));
        test("switch and loop preserve alternate effect entry", () =>
        {
            LocalInstruction[] code = [new(0, OpCodes.Switch, new[] { 3, 4 }), new(1, OpCodes.Call, null),
                new(2, OpCodes.Ret, null), new(3, OpCodes.Br_S, 0), new(4, OpCodes.Br_S, 1)];
            Check(LocalMethodBody.Reachable(code, _ => null).All(value => value));
        });
        test("unresolved branch target retains every effect", () =>
            Check(LocalMethodBody.Reachable([new(0, OpCodes.Br_S, 99), new(1, OpCodes.Call, null),
                new(2, OpCodes.Ret, null)], _ => null).All(value => value)));
    }
}
