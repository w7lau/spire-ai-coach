using System.Reflection;
using System.Reflection.Emit;

namespace SpireAiCoach.Core;

// A bounded interpreter for scalar metadata. No inspected method is invoked.
// Unknown branches, indirect memory, loops and unsupported calls fail closed.
public static class LocalScalarProgram
{
    public readonly record struct Value(decimal? Number = null, string? Tag = null)
    {
        public static Value N(decimal n) => new(n);
        public static Value T(string tag) => new(null, tag);
    }
    public static Value? Read(LocalInstruction[] code, Value[] arguments,
        Func<FieldInfo, Value?, Value?> field, Func<MethodBase, Value[], Value?> call)
    {
        var stack = new List<Value>();
        var locals = new Dictionary<int, Value>();
        var fields = new Dictionary<FieldInfo, Value>();
        var positions = code.Select((x, i) => (x.Offset, i)).ToDictionary(x => x.Offset, x => x.i);
        var visited = new HashSet<int>();
        Value Pop() { var v = stack[^1]; stack.RemoveAt(stack.Count - 1); return v; }
        int Slot(LocalInstruction i, OpCode first, OpCode last) => i.Code.Value >= first.Value && i.Code.Value <= last.Value
            ? i.Code.Value - first.Value : Convert.ToInt32(i.Operand);
        try
        {
            for (int at = 0; at < code.Length; at++)
            {
                if (!visited.Add(at) || visited.Count > 2048) return null;
                var i = code[at]; var op = i.Code; string name = op.Name ?? "";
                if (LocalIlFacts.Integer(i) is { } integer) stack.Add(Value.N(integer));
                else if (op == OpCodes.Ldstr) stack.Add(Value.T("string:" + i.Operand));
                else if (op == OpCodes.Ldnull) stack.Add(Value.T("null"));
                else if (name.StartsWith("ldarg", StringComparison.Ordinal)) stack.Add(arguments[Slot(i, OpCodes.Ldarg_0, OpCodes.Ldarg_3)]);
                else if (name.StartsWith("ldloc", StringComparison.Ordinal)) stack.Add(locals[Slot(i, OpCodes.Ldloc_0, OpCodes.Ldloc_3)]);
                else if (name.StartsWith("stloc", StringComparison.Ordinal)) locals[Slot(i, OpCodes.Stloc_0, OpCodes.Stloc_3)] = Pop();
                else if (op == OpCodes.Dup) stack.Add(stack[^1]);
                else if (op == OpCodes.Pop) Pop();
                else if (op == OpCodes.Nop) { }
                else if (op == OpCodes.Ldfld || op == OpCodes.Ldflda || op == OpCodes.Ldsfld)
                {
                    var member = (FieldInfo)i.Operand!;
                    Value? receiver = op == OpCodes.Ldsfld ? null : Pop();
                    var value = receiver?.Tag == "frame" && fields.TryGetValue(member, out var saved) ? saved : field(member, receiver);
                    if (value == null) return null;
                    stack.Add(value.Value);
                }
                else if (op == OpCodes.Stfld)
                {
                    var value = Pop(); var receiver = Pop();
                    if (receiver.Tag != "frame") return null;
                    fields[(FieldInfo)i.Operand!] = value;
                }
                else if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
                {
                    var m = (MethodBase)i.Operand!;
                    int count = m.GetParameters().Length + (m.IsStatic || op == OpCodes.Newobj ? 0 : 1);
                    var args = new Value[count];
                    for (int j = count - 1; j >= 0; j--) args[j] = Pop();
                    var value = call(m, args);
                    if (value == null) return null;
                    if (op == OpCodes.Newobj || m is MethodInfo info && info.ReturnType != typeof(void)) stack.Add(value.Value);
                }
                else if (op == OpCodes.Add || op == OpCodes.Sub || op == OpCodes.Mul || op == OpCodes.Div ||
                    op == OpCodes.Ceq || op == OpCodes.Cgt || op == OpCodes.Clt)
                {
                    var b = Pop(); var a = Pop();
                    if (a.Number is not { } x || b.Number is not { } y) return null;
                    stack.Add(Value.N(op == OpCodes.Add ? x + y : op == OpCodes.Sub ? x - y :
                        op == OpCodes.Mul ? x * y : op == OpCodes.Div ? decimal.Truncate(x / y) :
                        (op == OpCodes.Ceq ? x == y : op == OpCodes.Cgt ? x > y : x < y) ? 1 : 0));
                }
                else if (op == OpCodes.Conv_I4) stack.Add(Value.N(checked((int)Pop().Number!.Value)));
                else if (op == OpCodes.Ret) return stack.Count == 0 ? Value.T("void") : stack.Count == 1 ? Pop() : null;
                else if (op.FlowControl == FlowControl.Branch || op.FlowControl == FlowControl.Cond_Branch)
                {
                    bool jump;
                    if (op == OpCodes.Switch)
                    {
                        int index = checked((int)Pop().Number!.Value); var targets = (int[])i.Operand!;
                        if (index >= 0 && index < targets.Length) at = positions[targets[index]] - 1;
                        continue;
                    }
                    if (op.FlowControl == FlowControl.Branch)
                    { jump = true; if (op == OpCodes.Leave || op == OpCodes.Leave_S) stack.Clear(); }
                    else if (op == OpCodes.Brtrue || op == OpCodes.Brtrue_S || op == OpCodes.Brfalse || op == OpCodes.Brfalse_S)
                    {
                        var value = Pop(); if (value.Number is not { } n) return null;
                        jump = name.StartsWith("brtrue", StringComparison.Ordinal) ? n != 0 : n == 0;
                    }
                    else
                    {
                        var b = Pop(); var a = Pop();
                        if (name.Contains(".un", StringComparison.Ordinal) || a.Number is not { } x || b.Number is not { } y) return null;
                        jump = name.Split('.')[0] switch { "beq" => x == y, "bne" => x != y, "bgt" => x > y,
                            "bge" => x >= y, "blt" => x < y, "ble" => x <= y, _ => throw new InvalidOperationException() };
                    }
                    if (jump) at = positions[(int)i.Operand!] - 1;
                }
                else return null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or
            IndexOutOfRangeException or OverflowException or DivideByZeroException) { }
        return null;
    }

    public static Value? Arithmetic(MethodBase method, Value[] args)
    {
        if (args.Any(a => a.Number == null)) return null;
        var n = args.Select(a => a.Number!.Value).ToArray();
        try
        {
            if (method.DeclaringType == typeof(decimal))
                return method.Name switch {
                    ".ctor" when n.Length == 1 => Value.N(n[0]),
                    ".ctor" when n.Length == 5 => Value.N(new decimal(checked((int)n[0]), checked((int)n[1]), checked((int)n[2]), n[3] != 0, checked((byte)n[4]))),
                    "op_Implicit" => Value.N(n[0]),
                    "op_Explicit" when method is MethodInfo { ReturnType: var t } && t == typeof(int) => Value.N(checked((int)n[0])),
                    "op_Multiply" => Value.N(n[0] * n[1]), "op_Division" => Value.N(n[0] / n[1]),
                    "op_Addition" => Value.N(n[0] + n[1]), "op_Subtraction" => Value.N(n[0] - n[1]),
                    "op_LessThanOrEqual" => Value.N(n[0] <= n[1] ? 1 : 0), "op_GreaterThan" => Value.N(n[0] > n[1] ? 1 : 0),
                    _ => null };
            if (method.DeclaringType == typeof(Math)) return method.Name switch {
                "Min" => Value.N(Math.Min(n[0], n[1])), "Max" => Value.N(Math.Max(n[0], n[1])),
                "Ceiling" => Value.N(decimal.Ceiling(n[0])), "Floor" => Value.N(decimal.Floor(n[0])), _ => null };
        }
        catch (Exception ex) when (ex is OverflowException or DivideByZeroException or ArgumentException) { }
        return null;
    }
}
