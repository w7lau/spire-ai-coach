using System.Reflection.Emit;
using SpireAiCoach.Core;

internal static class IlFactsTests
{
    private sealed class Frame { public int Value; }
    private static void OpaqueFrame(Frame frame) { frame.Value = 6; }
    private static void OpaqueReference(ref int value) { value = 6; }
    public static void Register(Action<string, Action> test)
    {
        void Check(bool value) { if (!value) throw new Exception("IL facts lost an effect path"); }
        test("finite event domain excludes an impossible comparison", () =>
        {
            LocalInstruction[] code = [new(0, OpCodes.Ldarg_0, null), new(1, OpCodes.Ldc_I4_6, null),
                new(2, OpCodes.Beq_S, 5), new(3, OpCodes.Ldc_I4_0, null), new(4, OpCodes.Ret, null),
                new(5, OpCodes.Ldc_I4_1, null), new(6, OpCodes.Ret, null)];
            var result = LocalIlFacts.Read(code, _ => new([0, 1, 2, 3, 4, 5]));
            Check(result.Complete && !result.Reachable[5] && result.Return.Numbers!.SequenceEqual([0]));
        });
        test("unknown event domain keeps a conditional effect", () =>
        {
            LocalInstruction[] code = [new(0, OpCodes.Ldarg_0, null), new(1, OpCodes.Brfalse_S, 4),
                new(2, OpCodes.Ldc_I4_1, null), new(3, OpCodes.Ret, null), new(4, OpCodes.Ldc_I4_0, null), new(5, OpCodes.Ret, null)];
            var result = LocalIlFacts.Read(code);
            Check(result.Complete && result.Reachable.All(v => v) && result.Return.Numbers!.SequenceEqual([0, 1]));
        });
        test("double boolean comparison preserves a dead event branch", () =>
        {
            LocalInstruction[] code = [new(0, OpCodes.Ldarg_0, null), new(1, OpCodes.Ldc_I4_6, null),
                new(2, OpCodes.Ceq, null), new(3, OpCodes.Ldc_I4_0, null), new(4, OpCodes.Ceq, null), new(5, OpCodes.Brfalse_S, 8),
                new(6, OpCodes.Ldc_I4_0, null), new(7, OpCodes.Ret, null), new(8, OpCodes.Ldc_I4_1, null), new(9, OpCodes.Ret, null)];
            var result = LocalIlFacts.Read(code, _ => new([0, 1, 2, 3, 4, 5]));
            Check(result.Complete && !result.Reachable[8]);
        });
        test("unknown branch joins alternate enum destinations", () =>
        {
            LocalInstruction[] code = [new(0, OpCodes.Ldarg_0, null), new(1, OpCodes.Brtrue_S, 4), new(2, OpCodes.Ldc_I4_4, null),
                new(3, OpCodes.Br_S, 5), new(4, OpCodes.Ldc_I4_6, null), new(5, OpCodes.Ret, null)];
            var result = LocalIlFacts.Read(code);
            Check(result.Complete && result.Return.Numbers!.SequenceEqual([4, 6]));
        });
        test("tagged tuple join keeps the permanent destination", () =>
            Check(LocalIlFacts.Value.Join(new([4], "pile-result"), new([6], "pile-result")).Numbers!.SequenceEqual([4, 6])));
        test("tagged tuple cannot become an integer branch condition", () =>
        {
            LocalInstruction[] code = [new(0, OpCodes.Ldarg_0, null), new(1, OpCodes.Brfalse_S, 4),
                new(2, OpCodes.Ldc_I4_1, null), new(3, OpCodes.Ret, null), new(4, OpCodes.Ldc_I4_0, null), new(5, OpCodes.Ret, null)];
            var result = LocalIlFacts.Read(code, _ => new([0], "pile-result"));
            Check(result.Reachable.All(v => v));
        });
        test("handler entry retains a separately reachable effect", () =>
        {
            LocalInstruction[] code = [new(0, OpCodes.Ret, null), new(1, OpCodes.Pop, null), new(2, OpCodes.Ldc_I4_6, null), new(3, OpCodes.Ret, null)];
            var result = LocalIlFacts.Read(code, handlers: [(1, 1)]);
            Check(result.Complete && result.Reachable[2]);
        });
        test("stack ambiguity falls back to all effect paths", () =>
        {
            LocalInstruction[] code = [new(0, OpCodes.Ldarg_0, null), new(1, OpCodes.Brtrue_S, 4), new(2, OpCodes.Ldc_I4_1, null),
                new(3, OpCodes.Br_S, 4), new(4, OpCodes.Ret, null)];
            var result = LocalIlFacts.Read(code);
            Check(!result.Complete && result.Reachable.All(v => v));
        });
        test("unresolved target cannot prove an event impossible", () =>
        {
            var result = LocalIlFacts.Read([new(0, OpCodes.Br_S, 99), new(1, OpCodes.Ret, null)]);
            Check(!result.Complete && result.Reachable.All(v => v));
        });
        test("unknown joins absorb a claimed integer range", () =>
            Check(LocalIlFacts.Value.Join(new([1, 2]), LocalIlFacts.Value.Unknown).Numbers == null));
        test("known async state excludes unrelated resume entries", () =>
        {
            LocalInstruction[] code = [new(0, OpCodes.Ldc_I4_M1, null), new(1, OpCodes.Switch, new[] { 4 }),
                new(2, OpCodes.Ldc_I4_1, null), new(3, OpCodes.Ret, null), new(4, OpCodes.Ldc_I4_6, null), new(5, OpCodes.Ret, null)];
            var result = LocalIlFacts.Read(code);
            Check(result.Complete && !result.Reachable[4] && result.Return.Numbers!.SequenceEqual([1]));
        });
        test("frame store preserves a selected value until its load", () =>
        {
            var member = typeof(Frame).GetField(nameof(Frame.Value))!;
            LocalInstruction[] code = [new(0, OpCodes.Ldarg_0, null), new(1, OpCodes.Ldc_I4_1, null), new(2, OpCodes.Stfld, member),
                new(3, OpCodes.Ldarg_0, null), new(4, OpCodes.Ldfld, member), new(5, OpCodes.Ret, null)];
            var result = LocalIlFacts.Read(code, _ => new(Tag: "frame"));
            Check(result.Complete && result.Return.Numbers!.SequenceEqual([1]));
        });
        test("opaque frame call invalidates stored value", () =>
        {
            var member = typeof(Frame).GetField(nameof(Frame.Value))!;
            var opaque = typeof(IlFactsTests).GetMethod(nameof(OpaqueFrame), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            LocalInstruction[] code = [new(0, OpCodes.Ldarg_0, null), new(1, OpCodes.Ldc_I4_1, null), new(2, OpCodes.Stfld, member),
                new(3, OpCodes.Ldarg_0, null), new(4, OpCodes.Call, opaque), new(5, OpCodes.Ldarg_0, null), new(6, OpCodes.Ldfld, member), new(7, OpCodes.Ret, null)];
            var result = LocalIlFacts.Read(code, _ => new(Tag: "frame"));
            Check(result.Complete && result.Return.Numbers == null);
        });
        test("opaque ref call invalidates a claimed local destination", () =>
        {
            var opaque = typeof(IlFactsTests).GetMethod(nameof(OpaqueReference), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            LocalInstruction[] code = [new(0, OpCodes.Ldc_I4_1, null), new(1, OpCodes.Stloc_0, null), new(2, OpCodes.Ldloca_S, 0),
                new(3, OpCodes.Call, opaque), new(4, OpCodes.Ldloc_0, null), new(5, OpCodes.Ret, null)];
            var result = LocalIlFacts.Read(code);
            Check(result.Complete && result.Return.Numbers == null);
        });
        test("argument mutation cannot retain an event domain", () =>
        {
            var result = LocalIlFacts.Read([new(0, OpCodes.Ldc_I4_6, null), new(1, OpCodes.Starg_S, 0),
                new(2, OpCodes.Ldarg_0, null), new(3, OpCodes.Ret, null)], _ => new([0, 1, 2, 3, 4, 5]));
            Check(!result.Complete && result.Reachable.All(v => v));
        });
        test("indirect local mutation loses its old destination", () =>
        {
            var result = LocalIlFacts.Read([new(0, OpCodes.Ldc_I4_1, null), new(1, OpCodes.Stloc_0, null), new(2, OpCodes.Ldloca_S, 0),
                new(3, OpCodes.Ldc_I4_6, null), new(4, OpCodes.Stind_I4, null), new(5, OpCodes.Ldloc_0, null), new(6, OpCodes.Ret, null)]);
            Check(result.Complete && result.Return.Numbers == null);
        });
    }
}
