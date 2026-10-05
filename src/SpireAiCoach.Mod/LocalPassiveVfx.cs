using System.ComponentModel;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.TestSupport;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// A nullable TestMode guard alone is insufficient: card-flight nodes can own
// completion callbacks. Inspect the factory, stored values and lifecycle code
// without invoking any getter. Unknown rules/callbacks retain the original path.
internal static class LocalPassiveVfx
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    public static bool Eligible(MethodInfo factory)
    {
        var type = factory.DeclaringType;
        if (type == null || factory.ReturnType != type || !typeof(Node).IsAssignableFrom(type) ||
            type.Namespace != "MegaCrit.Sts2.Core.Nodes.Vfx" ||
            !LocalPresentationGuard.OptionalNullFactory(factory, AccessTools.PropertyGetter(typeof(TestMode), nameof(TestMode.IsOn))) ||
            factory.GetParameters().Any(p => !Value(p.ParameterType) && p.ParameterType != typeof(Creature))) return false;
        // No game objects, lazy enumerables, delegates, Callable, or completion
        // sources may be stored in an omitted node. Include private base fields.
        for (var current = type; current != null && current.Assembly == type.Assembly; current = current.BaseType)
            if (current.GetFields(Declared).Any(f => !Value(f.FieldType))) return false;
        var seen = new HashSet<MethodBase>();
        foreach (var method in type.GetMethods(Declared))
        {
            // Engine-generated dispatch/serialization invokes the audited user
            // methods, but is not itself an effect. Never use a name exclusion.
            if (method.GetCustomAttribute<EditorBrowsableAttribute>()?.State == EditorBrowsableState.Never) continue;
            if (method.Name == "get_AssetPaths") continue; // immutable asset enumeration, not a lifecycle entry
            if (!Inspect(method, type, seen)) return false;
        }
        foreach (var ctor in type.GetConstructors(Declared))
            if (!Inspect(ctor, type, seen)) return false;
        return true;
    }

    private static bool Inspect(MethodBase method, Type owner, HashSet<MethodBase> seen)
    {
        if (!seen.Add(method)) return true;
        if (Harmony.GetPatchInfo(method)?.Owners.Any(o => !o.StartsWith("SpireAiCoach.owned-worker.", StringComparison.Ordinal)) == true)
            return false;
        if (method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType is { } state)
        {
            var move = state.GetMethod("MoveNext", Declared);
            return move != null && Inspect(move, owner, seen);
        }
        var code = LocalMethodBody.Read(method);
        if (code == null) return false;
        foreach (var i in code)
        {
            if (i.Code == OpCodes.Calli || i.Code == OpCodes.Ldftn || i.Code == OpCodes.Ldvirtftn) return false;
            if (i.Operand is FieldInfo field && (i.Code == OpCodes.Stfld || i.Code == OpCodes.Stsfld) &&
                field.DeclaringType != owner && field.DeclaringType?.DeclaringType != owner) return false;
            if (i.Operand is not MethodBase call) continue;
            if (call.DeclaringType == owner) { if (!Inspect(call, owner, seen)) return false; }
            else if (!Leaf(call, owner)) return false;
        }
        return true;
    }

    public static bool FactoryBody(IEnumerable<LocalInstruction> body, Type owner) => body.All(i =>
        i.Code != OpCodes.Calli && i.Code != OpCodes.Ldftn && i.Code != OpCodes.Ldvirtftn &&
        (!(i.Operand is FieldInfo field && (i.Code == OpCodes.Stfld || i.Code == OpCodes.Stsfld)) || field.DeclaringType == owner) &&
        (i.Operand is not MethodBase call || Leaf(call, owner)));

    private static bool Value(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string) ||
        t == typeof(Vector2) || t == typeof(Color) || t == typeof(StringName) ||
        t.Assembly == typeof(Node).Assembly && (typeof(Node).IsAssignableFrom(t) || t == typeof(Tween) || typeof(Resource).IsAssignableFrom(t));

    private static bool Leaf(MethodBase m, Type owner)
    {
        var t = m.DeclaringType;
        if (t == typeof(TestMode)) return m.Name is "get_IsOn" or "get_IsOff";
        if (t == typeof(NCombatRoom)) return m.Name is "get_Instance" or "GetCreatureNode";
        if (t == typeof(NCreature)) return m.Name is "GetBottomOfHitbox" or "get_VfxSpawnPosition";
        if (t == typeof(PreloadManager)) return m.Name == "get_Cache";
        if (t == typeof(AssetCache)) return m.Name == "GetScene";
        if (t == typeof(SceneHelper)) return m.Name == "GetScenePath";
        if (t == typeof(TaskHelper)) return m.Name == "RunSafely";
        if (t?.FullName == "MegaCrit.Sts2.Core.Helpers.GodotTreeExtensions")
            return m.Name is "QueueFreeSafely" or "AwaitSignal";
        if (t?.FullName == "MegaCrit.Sts2.Core.Nodes.GodotExtensions.NodeUtil") return m.Name == "AwaitSignal";
        if (t?.FullName == "MegaCrit.Sts2.Core.Nodes.GodotExtensions.TweenHelper") return m.Name == "AwaitFinished";
        if (t == typeof(PackedScene)) return m.Name == "Instantiate" && m is MethodInfo { IsGenericMethod: true } generic &&
            generic.GetGenericArguments().SequenceEqual([owner]);
        if (t == typeof(Node)) return m.IsConstructor || m.Name is "GetNode" or "CreateTween" or "QueueFree";
        if (t == typeof(Node2D)) return m.IsConstructor || m.Name is "get_GlobalPosition" or "set_GlobalPosition" or
            "get_Position" or "set_Position" or "get_Scale" or "set_Scale";
        if (t == typeof(CanvasItem)) return m.Name is "get_Material" or "get_Modulate" or "set_Modulate";
        if (t == typeof(GpuParticles2D)) return m.Name is "get_Emitting" or "set_Emitting";
        if (t == typeof(ShaderMaterial)) return m.Name == "SetShaderParameter";
        if (t == typeof(Tween)) return m.Name is "SetParallel" or "Parallel" or "TweenProperty" or "Kill";
        if (t == typeof(PropertyTweener)) return m.Name is "SetEase" or "SetTrans" or "From";
        if (t == typeof(Colors)) return m.Name.StartsWith("get_", StringComparison.Ordinal);
        if (t == typeof(Color) || t == typeof(Vector2) || t == typeof(StringName) || t == typeof(NodePath) || t == typeof(Variant))
            return m.IsConstructor || m.Name.StartsWith("op_", StringComparison.Ordinal) ||
                t == typeof(Vector2) && m.Name is "get_Zero" or "get_One";
        if (t == typeof(ArgumentOutOfRangeException)) return m.IsConstructor;
        if (t == typeof(Task) || t == typeof(Task<bool>)) return m.Name == "GetAwaiter";
        if (t == typeof(TaskAwaiter) || t?.IsGenericType == true && t.GetGenericTypeDefinition() == typeof(TaskAwaiter<>))
            return m.Name is "get_IsCompleted" or "GetResult";
        if (t == typeof(AsyncTaskMethodBuilder)) return m.Name is "AwaitUnsafeOnCompleted" or "SetException" or "SetResult";
        return false;
    }
}
