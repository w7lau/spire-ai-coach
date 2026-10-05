using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.TestSupport;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Sandpit's position initialization/update already has native optional-display
// exits. Its removal callback also kills creatures: never skip that callback.
// Audit once at worker startup, including the actual Harmony input. Do not
// enable TestMode globally or replace the power's numerical/death rules.
internal static class LocalEnemyPresentation
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
        BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly MethodInfo Disabled = AccessTools.PropertyGetter(typeof(TestMode), nameof(TestMode.IsOn));
    private static readonly MethodInfo Projection = AccessTools.PropertyGetter(typeof(SandpitPower), "AllAffectedCreatures");
    private static readonly MethodInfo Initializer = AccessTools.Method(typeof(SandpitPower), nameof(SandpitPower.AfterApplied));
    private static readonly MethodInfo Update = AccessTools.Method(typeof(SandpitPower), "UpdateCreaturePositions");
    private static readonly MethodInfo Removal = AccessTools.Method(typeof(SandpitPower), nameof(SandpitPower.AfterRemoved));
    private static readonly FieldInfo[] Positions = [AccessTools.Field(typeof(SandpitPower), "_initialAmount"),
        AccessTools.Field(typeof(SandpitPower), "_initialTargetPosition")];
    private static bool _projectionAudited;

    public static void Install(Harmony harmony)
    {
        _projectionAudited = AuditProjection();
        if (!_projectionAudited || !AuditPositionFields())
            throw new InvalidOperationException("Native optional enemy geometry now depends on rules or saved state");
        foreach (var effect in new[] { Initializer, Update })
        {
            var method = Body(effect);
            if (!AuditPositionMethod(LocalMethodBody.Read(method) ?? [], method))
                throw new InvalidOperationException("Native optional enemy position method changed: " + effect.Name);
            harmony.Patch(method, transpiler: new(AccessTools.Method(typeof(LocalEnemyPresentation), nameof(PositionGuard))));
        }
    }

    private static MethodInfo Body(MethodInfo method) => method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
        .GetMethod("MoveNext", Declared) ?? method;

    private static bool AuditProjection()
    {
        if (Harmony.GetPatchInfo(Projection)?.Owners.Any() == true) return false;
        var code = LocalMethodBody.Read(Projection);
        return code != null && code.Count(i => i.Code == OpCodes.Newarr && Equals(i.Operand, typeof(Creature))) == 1 &&
            code.Count(i => i.Code == OpCodes.Newobj) == 1 && code.All(i => i.Operand is not FieldInfo &&
            i.Code != OpCodes.Stfld && i.Code != OpCodes.Stsfld && i.Code != OpCodes.Calli && i.Code != OpCodes.Ldftn &&
            i.Code != OpCodes.Ldvirtftn && i.Code != OpCodes.Starg && i.Code != OpCodes.Starg_S &&
            i.Code != OpCodes.Throw && i.Code != OpCodes.Rethrow && (i.Operand is not MethodBase call ||
                Read(call) || Collection(call) || call.IsConstructor && call.DeclaringType?.Name == "<>z__ReadOnlyArray`1" &&
                    call.DeclaringType.Assembly == typeof(SandpitPower).Assembly &&
                    call.DeclaringType.GetGenericArguments().SequenceEqual([typeof(Creature)])));
    }

    private static bool AuditPositionFields()
    {
        if (Positions.Any(f => f.IsPublic || f.IsStatic || f.GetCustomAttributesData().Count != 0)) return false;
        foreach (var method in typeof(SandpitPower).GetMethods(Declared).Select(Body))
        foreach (var i in LocalMethodBody.Read(method) ?? [])
        {
            if (i.Operand is not FieldInfo field || !Positions.Contains(field)) continue;
            if (method != Initializer && method != Body(Update)) return false;
            if (i.Code != OpCodes.Ldfld && (i.Code != OpCodes.Stfld || method != Initializer)) return false;
        }
        return true;
    }

    // Only stored identifiers/numbers and the native newly allocated creature
    // projection. No CanPlay, permission/preview hooks, RNG, model mutation,
    // arbitrary callbacks, or caller-provided lazy sequences.
    private static bool Read(MethodBase m) => m.DeclaringType == typeof(PowerModel) && m.Name is "get_Owner" or "get_Target" or "get_Amount" ||
        m.DeclaringType == typeof(Creature) && m.Name is "get_Player" or "get_Pets" or "get_IsDead" ||
        m.DeclaringType == typeof(Player) && m.Name is "get_Creature" or "get_IsOstyAlive" or "get_Osty" ||
        m.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Context.LocalContext" && m.Name == "IsMe";
    private static bool Collection(MethodBase m) =>
        m.DeclaringType == typeof(IEnumerable<Creature>) && m.Name == "GetEnumerator" ||
        m.DeclaringType == typeof(IEnumerator<Creature>) && m.Name == "get_Current" ||
        m.DeclaringType == typeof(IReadOnlyCollection<Creature>) && m.Name == "get_Count" ||
        m.DeclaringType == typeof(IEnumerator) && m.Name == "MoveNext" ||
        m.DeclaringType == typeof(IDisposable) && m.Name == "Dispose";
    private static bool Geometry(MethodBase m) =>
        m.DeclaringType == typeof(NCombatRoom) && m.Name is "get_Instance" or "GetCreatureNode" ||
        m.DeclaringType == typeof(NCreature) && m.Name is "get_Entity" or "GetOstyOffsetFromPlayer" ||
        m.DeclaringType == typeof(Control) && m.Name == "get_GlobalPosition" ||
        m.DeclaringType == typeof(Node) && m.Name == "CreateTween" ||
        m.DeclaringType == typeof(Tween) && m.Name is "SetParallel" or "SetEase" or "SetTrans" or "TweenProperty" ||
        m.DeclaringType == typeof(TweenHelper) && m.Name == "AwaitFinished" ||
        m.DeclaringType == typeof(Mathf) && m.Name is "Min" or "Max" ||
        m.DeclaringType == typeof(Math) && m.Name == "Abs" ||
        m.DeclaringType == typeof(NodePath) && m.Name == "op_Implicit" ||
        m.DeclaringType == typeof(Variant) && m.Name == "op_Implicit";
    private static bool Infrastructure(MethodBase m) =>
        m.DeclaringType == typeof(Task) && m.Name is "get_CompletedTask" or "GetAwaiter" ||
        m.DeclaringType == typeof(Task<bool>) && m.Name == "GetAwaiter" ||
        (m.DeclaringType == typeof(TaskAwaiter) || m.DeclaringType == typeof(TaskAwaiter<bool>)) &&
            m.Name is "get_IsCompleted" or "GetResult" ||
        m.DeclaringType == typeof(AsyncTaskMethodBuilder) && m.Name is "AwaitUnsafeOnCompleted" or "SetException" or "SetResult";

    internal static bool AuditPositionMethod(IEnumerable<LocalInstruction> body, MethodInfo method)
    {
        var code = body.ToArray();
        bool async = typeof(IAsyncStateMachine).IsAssignableFrom(method.DeclaringType);
        if (code.Count(i => Equals(i.Operand, Disabled)) != 1 || code.All(i => i.Operand is not MethodBase call || !Geometry(call))) return false;
        foreach (var i in code)
        {
            if (i.Code == OpCodes.Calli || i.Code == OpCodes.Ldftn || i.Code == OpCodes.Ldvirtftn ||
                i.Code == OpCodes.Throw || i.Code == OpCodes.Rethrow ||
                i.Code == OpCodes.Stsfld || i.Code == OpCodes.Starg || i.Code == OpCodes.Starg_S ||
                i.Code == OpCodes.Cpobj || i.Code == OpCodes.Cpblk || i.Code == OpCodes.Initblk ||
                i.Code.Name?.StartsWith("stind", StringComparison.Ordinal) == true ||
                i.Code.Name?.StartsWith("stelem", StringComparison.Ordinal) == true) return false;
            if (i.Code == OpCodes.Stfld && (i.Operand is not FieldInfo field ||
                !Positions.Contains(field) && (!async || field.DeclaringType != method.DeclaringType ||
                    field.Name != "<>1__state" && !field.Name.StartsWith("<>u__", StringComparison.Ordinal)))) return false;
            if (i.Code == OpCodes.Ldflda && (i.Operand is not FieldInfo address || !async || address.DeclaringType != method.DeclaringType ||
                address.Name != "<>t__builder" && !address.Name.StartsWith("<>u__", StringComparison.Ordinal))) return false;
            if (i.Operand is MethodBase call && !Equals(call, Disabled) && !Read(call) && !Geometry(call) && !Infrastructure(call) &&
                !Collection(call) && !(_projectionAudited && Equals(call, Projection))) return false;
        }
        return true;
    }

    public static bool ProjectionDisplayCall(MethodBase method, Type owner) => _projectionAudited &&
        owner == Body(Removal).DeclaringType && (Equals(method, Projection) || Collection(method));

    public static bool ProjectionBlock(IEnumerable<LocalInstruction> body, Type owner)
    {
        var code = body.ToArray();
        if (owner != Body(Removal).DeclaringType || !code.Any(i => i.Operand is MethodBase m && Collection(m))) return true;
        // The loop receiver must be the audited fresh projection, never an
        // arbitrary field/argument/lazy sequence introduced by another Mod.
        if (code.Any(i => i.Operand is FieldInfo f &&
            (typeof(IEnumerable<Creature>).IsAssignableFrom(f.FieldType) || typeof(IEnumerator<Creature>).IsAssignableFrom(f.FieldType)))) return false;
        var executable = code.Where(i => i.Code != OpCodes.Nop).ToArray();
        for (int n = 0; n < executable.Length; n++)
            if (executable[n].Operand is MethodBase m && m.DeclaringType == typeof(IEnumerable<Creature>) && m.Name == "GetEnumerator" &&
                (n == 0 || !Equals(executable[n - 1].Operand, Projection))) return false;
        return code.Count(i => Equals(i.Operand, Projection)) == 1;
    }

    private static IEnumerable<CodeInstruction> PositionGuard(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var code = instructions.ToArray();
        if (!AuditPositionMethod(code.Select((i, n) => new LocalInstruction(n, i.opcode, i.operand)), (MethodInfo)original))
            throw new InvalidOperationException("Modified enemy geometry now contains rules; retaining ordinary native execution");
        foreach (var instruction in code)
            yield return instruction.Calls(Disabled) ? new CodeInstruction(instruction) {
                operand = AccessTools.Method(typeof(LocalEnemyPresentation), nameof(DisabledHere)) } : instruction;
    }
    private static bool DisabledHere()
    {
        if (LocalWorkerDataMode.Active) LocalWorker.SkipMethod("Enemy.OptionalPositions");
        return LocalWorkerDataMode.Active || TestMode.IsOn;
    }
}
