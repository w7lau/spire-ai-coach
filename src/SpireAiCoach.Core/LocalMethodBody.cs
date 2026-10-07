using System.Reflection;
using System.Reflection.Emit;

namespace SpireAiCoach.Core;

// Metadata only: reading a model's code must never execute a getter or a hook.
public sealed record LocalInstruction(int Offset, OpCode Code, object? Operand);
public static class LocalMethodBody
{
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(c => c.Value);

    public static LocalInstruction[]? Read(MethodBase method)
    {
        try
        {
            var bytes = method.GetMethodBody()?.GetILAsByteArray();
            if (bytes == null) return null;
            var result = new List<LocalInstruction>();
            int at = 0;
            while (at < bytes.Length)
            {
                int offset = at;
                short code = bytes[at++];
                if (code == 0xfe) code = (short)(0xfe00 | bytes[at++]);
                var op = Codes[code];
                object? operand = null;
                int ReadInt() { int value = BitConverter.ToInt32(bytes, at); at += 4; return value; }
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineI: operand = (sbyte)bytes[at++]; break;
                    case OperandType.InlineI: operand = ReadInt(); break;
                    case OperandType.InlineI8: operand = BitConverter.ToInt64(bytes, at); at += 8; break;
                    case OperandType.ShortInlineR: operand = BitConverter.ToSingle(bytes, at); at += 4; break;
                    case OperandType.InlineR: operand = BitConverter.ToDouble(bytes, at); at += 8; break;
                    case OperandType.ShortInlineVar: operand = bytes[at++]; break;
                    case OperandType.InlineVar: operand = BitConverter.ToUInt16(bytes, at); at += 2; break;
                    case OperandType.ShortInlineBrTarget:
                        int shortJump = (sbyte)bytes[at++]; operand = at + shortJump; break;
                    case OperandType.InlineBrTarget:
                        int jump = ReadInt(); operand = at + jump; break;
                    case OperandType.InlineSwitch:
                        int count = ReadInt(); var jumps = new int[count];
                        for (int i = 0; i < count; i++) jumps[i] = ReadInt();
                        operand = jumps.Select(j => at + j).ToArray(); break;
                    case OperandType.InlineString: operand = method.Module.ResolveString(ReadInt()); break;
                    case OperandType.InlineMethod:
                    case OperandType.InlineField:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                        operand = method.Module.ResolveMember(ReadInt(), method.DeclaringType?.GetGenericArguments(),
                            method.IsGenericMethod ? method.GetGenericArguments() : null); break;
                    default: return null; // calli/signatures are not a proof of bounded recovery.
                }
                result.Add(new(offset, op, operand));
            }
            return result.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or
            IndexOutOfRangeException or KeyNotFoundException or BadImageFormatException)
        { return null; }
    }

    public static bool HasBackwardJump(IEnumerable<LocalInstruction> code) => code.Any(i =>
        (i.Code.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch) &&
        (i.Operand is int target && target <= i.Offset || i.Operand is int[] targets && targets.Any(t => t <= i.Offset)));

    // Unknown conditions keep both paths. Exception handlers are independent
    // roots, so excluding a guarded block cannot hide a handler's effects.
    public static bool[] Reachable(LocalInstruction[] code, Func<int, bool?> branchTaken,
        IEnumerable<int>? handlerOffsets = null)
    {
        var reachable = new bool[code.Length];
        var positions = code.Select((instruction, index) => (instruction.Offset, index)).ToDictionary(p => p.Offset, p => p.index);
        var pending = new Queue<int>();
        if (code.Length > 0) pending.Enqueue(0);
        void Target(int offset)
        {
            if (positions.TryGetValue(offset, out var position)) pending.Enqueue(position);
            else for (int i = 0; i < code.Length; i++) pending.Enqueue(i);
        }
        foreach (var offset in handlerOffsets ?? []) Target(offset);
        while (pending.TryDequeue(out var index))
        {
            if (index >= code.Length || reachable[index]) continue;
            reachable[index] = true;
            var instruction = code[index];
            if (instruction.Code.FlowControl is FlowControl.Return or FlowControl.Throw) continue;
            if (instruction.Code.FlowControl == FlowControl.Branch && instruction.Operand is int jump)
            { Target(jump); continue; }
            if (instruction.Code.FlowControl == FlowControl.Cond_Branch)
            {
                bool? taken = branchTaken(index);
                if (taken != false)
                {
                    if (instruction.Operand is int branch) Target(branch);
                    else if (instruction.Operand is int[] branches) foreach (int offset in branches) Target(offset);
                }
                if (taken == true) continue;
            }
            pending.Enqueue(index + 1);
        }
        return reachable;
    }
}
