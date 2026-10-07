using System.Reflection;
using System.Reflection.Emit;

namespace SpireAiCoach.Core;

// Small, conservative metadata interpreter. It never invokes the inspected code.
// Unsupported values become unknown; stack ambiguity keeps the entire method.
public static class LocalIlFacts
{
    public sealed record Value(int[]? Numbers = null, string? Tag = null)
    {
        public static readonly Value Unknown = new();
        public static Value Number(int number) => new([number]);
        public static Value Join(Value a, Value b)
        {
            if (a.Tag != b.Tag) return Unknown;
            if (a.Tag != null && a.Numbers == null && b.Numbers == null) return a;
            if (a.Numbers == null || b.Numbers == null) return Unknown;
            var values = a.Numbers.Concat(b.Numbers).Distinct().Order().ToArray();
            return values.Length > 32 ? Unknown : new(values, a.Tag);
        }
        public bool Same(Value other) => Tag == other.Tag &&
            (Numbers == null ? other.Numbers == null : other.Numbers != null && Numbers.SequenceEqual(other.Numbers));
    }
    public sealed record Result(bool[] Reachable, Dictionary<int, Value[]> Calls, Dictionary<int, Value> Writes, Value Return, bool Complete);
    private sealed record State(Value[] Stack, Dictionary<int, Value> Locals, Dictionary<FieldInfo, Value> FrameFields);

    public static Result Read(LocalInstruction[] code, Func<int, Value>? argument = null,
        Func<FieldInfo, Value, Value>? field = null, Func<MethodBase, Value[], Value>? call = null,
        IEnumerable<(int Offset, int Stack)>? handlers = null)
    {
        var reachable = new bool[code.Length];
        var calls = new Dictionary<int, Value[]>();
        var writes = new Dictionary<int, Value>();
        Value? returned = null;
        var positions = code.Select((i, n) => (i.Offset, n)).ToDictionary(p => p.Offset, p => p.n);
        var states = new Dictionary<int, State>();
        var pending = new Queue<int>();
        bool complete = true;
        void Offer(int index, State state)
        {
            if (index >= code.Length) return;
            if (!states.TryGetValue(index, out var old)) { states[index] = state; pending.Enqueue(index); return; }
            if (old.Stack.Length != state.Stack.Length) { complete = false; return; }
            var stack = old.Stack.Zip(state.Stack, Value.Join).ToArray();
            var locals = old.Locals.Keys.Union(state.Locals.Keys).ToDictionary(k => k,
                k => Value.Join(old.Locals.GetValueOrDefault(k, Value.Unknown), state.Locals.GetValueOrDefault(k, Value.Unknown)));
            var fields = old.FrameFields.Keys.Union(state.FrameFields.Keys).ToDictionary(k => k,
                k => Value.Join(old.FrameFields.GetValueOrDefault(k, Value.Unknown), state.FrameFields.GetValueOrDefault(k, Value.Unknown)));
            if (stack.Where((v, n) => !v.Same(old.Stack[n])).Any() ||
                locals.Any(p => !p.Value.Same(old.Locals.GetValueOrDefault(p.Key, Value.Unknown))) ||
                fields.Any(p => !p.Value.Same(old.FrameFields.GetValueOrDefault(p.Key, Value.Unknown))))
            { states[index] = new(stack, locals, fields); pending.Enqueue(index); }
        }
        void Jump(int offset, State state)
        { if (positions.TryGetValue(offset, out int index)) Offer(index, state); else complete = false; }
        Offer(0, new([], [], []));
        foreach (var handler in handlers ?? []) Jump(handler.Offset, new(Enumerable.Repeat(Value.Unknown, handler.Stack).ToArray(), [], []));
        int steps = 0;
        while (pending.TryDequeue(out int index) && complete)
        {
            if (++steps > Math.Max(4096, code.Length * 64)) { complete = false; break; }
            reachable[index] = true;
            var instruction = code[index]; var op = instruction.Code;
            var stack = states[index].Stack.ToList(); var locals = new Dictionary<int, Value>(states[index].Locals);
            var fields = new Dictionary<FieldInfo, Value>(states[index].FrameFields);
            Value Pop() { if (stack.Count == 0) { complete = false; return Value.Unknown; } var v = stack[^1]; stack.RemoveAt(stack.Count - 1); return v; }
            State Snapshot() => new(stack.ToArray(), locals, fields);
            bool? Compare(Value a, Value b, Func<int, int, bool> compare)
            {
                if (a.Tag != null || b.Tag != null || a.Numbers == null || b.Numbers == null) return null;
                var values = a.Numbers.SelectMany(x => b.Numbers.Select(y => compare(x, y))).Distinct().ToArray();
                return values.Length == 1 ? values[0] : null;
            }
            if (Integer(instruction) is { } number) stack.Add(Value.Number(number));
            else if (op == OpCodes.Ldnull) stack.Add(Value.Number(0));
            else if (Slot(op, instruction.Operand, OpCodes.Ldarg_0, OpCodes.Ldarg_3, OpCodes.Ldarg, OpCodes.Ldarg_S) is { } arg)
                stack.Add(argument?.Invoke(arg) ?? Value.Unknown);
            else if (op == OpCodes.Ldarga || op == OpCodes.Ldarga_S)
                stack.Add(argument?.Invoke(Convert.ToInt32(instruction.Operand)) ?? Value.Unknown);
            else if (op == OpCodes.Starg || op == OpCodes.Starg_S)
            { complete = false; break; } // The supplied argument domain no longer applies.
            else if (Slot(op, instruction.Operand, OpCodes.Ldloc_0, OpCodes.Ldloc_3, OpCodes.Ldloc, OpCodes.Ldloc_S) is { } local)
                stack.Add(locals.GetValueOrDefault(local, Value.Unknown));
            else if (op == OpCodes.Ldloca || op == OpCodes.Ldloca_S)
                stack.Add(locals.GetValueOrDefault(Convert.ToInt32(instruction.Operand), Value.Unknown));
            else if (Slot(op, instruction.Operand, OpCodes.Stloc_0, OpCodes.Stloc_3, OpCodes.Stloc, OpCodes.Stloc_S) is { } store)
                locals[store] = Pop();
            else if (op == OpCodes.Dup) { var v = Pop(); stack.Add(v); stack.Add(v); }
            else if (op == OpCodes.Pop) Pop();
            else if (op == OpCodes.Ldobj) stack.Add(Pop());
            else if ((op == OpCodes.Ldfld || op == OpCodes.Ldflda) && instruction.Operand is FieldInfo member)
            {
                var receiver = Pop();
                stack.Add(receiver.Tag == "frame" && fields.TryGetValue(member, out var stored) ? stored :
                    field?.Invoke(member, receiver) ?? Value.Unknown);
            }
            else if (op == OpCodes.Stfld || op == OpCodes.Stsfld || op.Name?.StartsWith("stind", StringComparison.Ordinal) == true || op == OpCodes.Stobj)
            {
                var value = Pop();
                writes[index] = writes.TryGetValue(index, out var old) ? Value.Join(old, value) : value;
                if (op != OpCodes.Stsfld)
                {
                    var receiver = Pop();
                    if (op == OpCodes.Stfld && receiver.Tag == "frame" && instruction.Operand is FieldInfo stored)
                        fields[stored] = value;
                    else if (receiver.Tag == null) fields.Clear();
                }
                if (op != OpCodes.Stfld && op != OpCodes.Stsfld) { locals.Clear(); fields.Clear(); }
            }
            else if (instruction.Operand is MethodBase method && (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj))
            {
                int count = method.GetParameters().Length + (method.IsStatic || op == OpCodes.Newobj ? 0 : 1);
                var args = new Value[count]; for (int n = count - 1; n >= 0; n--) args[n] = Pop();
                calls[index] = calls.TryGetValue(index, out var old) ? old.Zip(args, Value.Join).ToArray() : args;
                // Passing the frame to opaque code loses its stored-value facts.
                if (args.Any(v => v.Tag == "frame")) fields.Clear();
                if (method.GetParameters().Any(p => p.ParameterType.IsByRef))
                { locals.Clear(); fields.Clear(); }
                if (op == OpCodes.Newobj || method is MethodInfo info && info.ReturnType != typeof(void))
                    stack.Add(call?.Invoke(method, args) ?? Value.Unknown);
            }
            else if (op == OpCodes.Ceq || op == OpCodes.Cgt || op == OpCodes.Clt)
            {
                var b = Pop(); var a = Pop();
                bool? answer = Compare(a, b, (x, y) => op == OpCodes.Ceq ? x == y : op == OpCodes.Cgt ? x > y : x < y);
                stack.Add(answer.HasValue ? Value.Number(answer.Value ? 1 : 0) : new([0, 1]));
            }
            else if (op.FlowControl == FlowControl.Cond_Branch)
            {
                bool? taken = null;
                if (op == OpCodes.Switch)
                {
                    var value = Pop(); var targets = instruction.Operand as int[] ?? [];
                    if (value.Tag == null && value.Numbers is { Length: > 0 } numbers)
                    {
                        foreach (int n in numbers.Where(n => n >= 0 && n < targets.Length).Distinct()) Jump(targets[n], Snapshot());
                        if (numbers.All(n => n >= 0 && n < targets.Length)) continue;
                    }
                    else foreach (int target in targets) Jump(target, Snapshot());
                }
                else if (op == OpCodes.Brtrue || op == OpCodes.Brtrue_S || op == OpCodes.Brfalse || op == OpCodes.Brfalse_S)
                {
                    var value = Pop(); bool zero = op == OpCodes.Brfalse || op == OpCodes.Brfalse_S;
                    taken = Compare(value, Value.Number(0), (x, _) => zero ? x == 0 : x != 0);
                }
                else
                {
                    var b = Pop(); var a = Pop();
                    taken = Compare(a, b, (x, y) => op.Name?.Split('.')[0] switch {
                        "beq" => x == y, "bne" => x != y, "bgt" => x > y, "bge" => x >= y,
                        "blt" => x < y, "ble" => x <= y, _ => false });
                    if (op.Name?.Contains(".un", StringComparison.Ordinal) == true) taken = null;
                }
                if (taken != false && instruction.Operand is int branch) Jump(branch, Snapshot());
                if (taken == true) continue;
            }
            else if (op.FlowControl == FlowControl.Branch && instruction.Operand is int jump)
            {
                if (op == OpCodes.Leave || op == OpCodes.Leave_S) stack.Clear();
                Jump(jump, Snapshot()); continue;
            }
            else if (op.FlowControl is FlowControl.Return or FlowControl.Throw)
            {
                if (op == OpCodes.Ret && stack.Count == 1) returned = returned == null ? stack[0] : Value.Join(returned, stack[0]);
                continue;
            }
            else
            {
                int pops = PopCount(op.StackBehaviourPop); int pushes = PushCount(op.StackBehaviourPush);
                if (pops < 0 || pushes < 0) { complete = false; break; }
                for (int n = 0; n < pops; n++) Pop();
                for (int n = 0; n < pushes; n++) stack.Add(Value.Unknown);
            }
            Offer(index + 1, Snapshot());
        }
        return new(complete ? reachable : Enumerable.Repeat(true, code.Length).ToArray(), calls, writes,
            complete ? returned ?? Value.Unknown : Value.Unknown, complete);
    }
    public static int? Integer(LocalInstruction instruction) => instruction.Code.Value switch {
        var v when v == OpCodes.Ldc_I4_M1.Value => -1,
        var v when v >= OpCodes.Ldc_I4_0.Value && v <= OpCodes.Ldc_I4_8.Value => v - OpCodes.Ldc_I4_0.Value,
        var v when v == OpCodes.Ldc_I4.Value && instruction.Operand is int value => value,
        var v when v == OpCodes.Ldc_I4_S.Value && instruction.Operand is sbyte value => value, _ => null };
    private static int? Slot(OpCode op, object? operand, OpCode first, OpCode last, OpCode full, OpCode shortOp) =>
        op.Value >= first.Value && op.Value <= last.Value ? op.Value - first.Value :
        op == full || op == shortOp ? Convert.ToInt32(operand) : null;
    private static int PopCount(StackBehaviour behavior) => behavior switch {
        StackBehaviour.Pop0 => 0, StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or
        StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_pop1 or StackBehaviour.Popref_popi_popi or
        StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3, _ => -1 };
    private static int PushCount(StackBehaviour behavior) => behavior switch {
        StackBehaviour.Push0 => 0, StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or
        StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        StackBehaviour.Push1_push1 => 2, _ => -1 };
}
