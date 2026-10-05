using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.TestSupport;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Optional display inside all native model effects, including deferred static
// helpers. Discover by IL; a model name never authorizes skipping its rules.
internal static class LocalModelDisplay
{
    private static readonly List<string> Boundaries = [];
    private static readonly List<string> Failures = [];
    public static object Status() => new { boundaries = Boundaries.ToArray(), failures = Failures.ToArray() };
    public static void Install()
    {
        var enabled = AccessTools.PropertyGetter(typeof(TestMode), nameof(TestMode.IsOff));
        foreach (var type in typeof(AbstractModel).Assembly.GetTypes().Where(t => typeof(AbstractModel).IsAssignableFrom(t)))
        foreach (var effect in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            var state = effect.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
            var method = state == null ? effect : AccessTools.Method(state, "MoveNext");
            if (method == null || LocalDisplayBranch.Find(method, enabled,
                call => DisplayCall(call) || LocalEnemyPresentation.ProjectionDisplayCall(call, method.DeclaringType!), DisplayValue).Length == 0) continue;
            var harmony = new Harmony("SpireAiCoach.owned-worker.model-display." + type.FullName + "." + effect.MetadataToken);
            try
            {
                harmony.Patch(method, transpiler: new(AccessTools.Method(typeof(LocalModelDisplay), nameof(DisplayGuard))));
                Boundaries.Add(type.Name + "." + effect.Name);
            }
            catch (Exception ex)
            { harmony.UnpatchAll(harmony.Id); Failures.Add(type.Name + "." + effect.Name + ": " + ex.Message); }
        }
    }
    private static IEnumerable<CodeInstruction> DisplayGuard(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var code = instructions.ToArray();
        int Offset(System.Reflection.Emit.Label label) => Array.FindIndex(code, c => c.labels.Contains(label));
        object? Operand(object? value) => value switch {
            System.Reflection.Emit.Label label => Offset(label),
            System.Reflection.Emit.Label[] labels => labels.Select(Offset).ToArray(),
            LocalBuilder local => local.LocalIndex, _ => value };
        var enabled = AccessTools.PropertyGetter(typeof(TestMode), nameof(TestMode.IsOff));
        // Recheck actual Harmony input: a Mod may have inserted a rule into this block.
        var body = code.Select((c, i) => new LocalInstruction(i, c.opcode, Operand(c.operand))).ToArray();
        var guards = LocalDisplayBranch.Find(body,
            original.DeclaringType!, original.GetMethodBody()!.LocalVariables.Select(l => l.LocalType).ToArray(),
            enabled, call => DisplayCall(call) || LocalEnemyPresentation.ProjectionDisplayCall(call, original.DeclaringType!), DisplayValue)
            .Where(start => body.ElementAtOrDefault(start + 1)?.Operand is int end &&
                LocalEnemyPresentation.ProjectionBlock(body.Skip(start + 2).Take(end - start - 2), original.DeclaringType!)).ToHashSet();
        if (guards.Count == 0) throw new InvalidOperationException("Optional display block now contains rules or escaping values");
        for (int i = 0; i < code.Length; i++)
        {
            if (!guards.Contains(i)) { yield return code[i]; continue; }
            var name = new CodeInstruction(OpCodes.Ldstr, (original.DeclaringType!.DeclaringType ?? original.DeclaringType).Name + ".OptionalDisplay");
            name.labels.AddRange(code[i].labels); name.blocks.AddRange(code[i].blocks);
            yield return name;
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LocalModelDisplay), nameof(Enabled)));
        }
    }
    private static bool Enabled(string name)
    {
        if (LocalWorkerDataMode.Active) LocalWorker.SkipMethod(name);
        return !LocalWorkerDataMode.Active && TestMode.IsOff;
    }
    private static bool DisplayValue(Type t) => t == typeof(Vector2) || t == typeof(Color) ||
        typeof(Node).IsAssignableFrom(t) || Nullable.GetUnderlyingType(t) is { } inner && DisplayValue(inner);

    private static bool DisplayCall(MethodBase m)
    {
        var t = m.DeclaringType;
        if (t == typeof(CardModel)) return m.Name == "get_Owner";
        if (t == typeof(PowerModel) || t == typeof(RelicModel)) return m.Name == "get_Owner";
        if (t == typeof(MonsterModel)) return m.Name == "get_Creature";
        if (t == typeof(Player)) return m.Name == "get_Creature";
        if (t == typeof(LocalContext)) return m.Name == nameof(LocalContext.IsMe);
        if (t == typeof(ModelDb)) return m.Name == nameof(ModelDb.Potion) && m is MethodInfo { IsGenericMethod: true };
        if (t == typeof(PotionModel)) return m.Name is "get_Image" or "get_Owner";
        if (t == typeof(NCombatRoom)) return m.Name is "get_Instance" or "GetCreatureNode" or "get_CombatVfxContainer";
        if (t == typeof(NCreature)) return m.Name is "get_VfxSpawnPosition" or "GetBottomOfHitbox" or "get_Visuals" or "GetSpecialNode";
        if (t == typeof(NCreatureVisuals)) return m.Name == "GetCurrentBody";
        if (t == typeof(NGame)) return m.Name is "get_Instance" or "get_CurrentRunNode";
        if (t == typeof(NRun)) return m.Name == "get_GlobalUi";
        if (t == typeof(Color) || t == typeof(Vector2)) return m.IsConstructor || m.Name.StartsWith("op_", StringComparison.Ordinal);
        // Only scene geometry and passive audio leaves. No model mutation,
        // RNG, signal callbacks, selection completion, or engine timing calls.
        if (t == typeof(Control) || t == typeof(Node2D)) return m.Name is
            "get_GlobalPosition" or "set_GlobalPosition" or "get_Position" or "set_Position" or "get_Scale" or "set_Scale";
        if (t == typeof(CanvasItem)) return m.Name is "get_Visible" or "set_Visible" or "SetVisible";
        if (t == typeof(Node)) return m.Name == "CreateTween" || m.Name == "GetNode" && m is MethodInfo { IsGenericMethod: true };
        if (t == typeof(Tween)) return m.Name is "Parallel" or "SetParallel" or "TweenProperty" or "SetEase" or "SetTrans";
        if (t == typeof(PropertyTweener)) return m.Name is "SetEase" or "SetTrans" or "From";
        if (t == typeof(Variant) || t == typeof(NodePath)) return m.Name == "op_Implicit";
        if (t?.IsGenericType == true && t.GetGenericTypeDefinition() == typeof(Nullable<>) && DisplayValue(t.GetGenericArguments()[0]))
            return m.Name is "get_HasValue" or "get_Value" or "GetValueOrDefault";
        if (t == typeof(NRunMusicController)) return m.Name is "get_Instance" or "TriggerEliteSecondPhase" or "UpdateMusicParameter";
        // A native model may expose a saved visual position through a trivial
        // getter. Do not trust a getter's name or accept a computed rule callback.
        if (m is MethodInfo getter && !getter.IsStatic && getter.GetParameters().Length == 0 &&
            getter.Name.StartsWith("get_", StringComparison.Ordinal) && DisplayValue(getter.ReturnType) &&
            t != null && typeof(AbstractModel).IsAssignableFrom(t))
        {
            var body = LocalMethodBody.Read(getter)?.Where(i => i.Code != OpCodes.Nop).ToArray();
            return body is { Length: 3 } && body[0].Code == OpCodes.Ldarg_0 && body[1].Code == OpCodes.Ldfld && body[2].Code == OpCodes.Ret;
        }
        if (t?.FullName == "MegaCrit.Sts2.Core.Helpers.GodotTreeExtensions") return m.Name == "AddChildSafely";
        if (t == typeof(Cmd)) return m.Name is nameof(Cmd.Wait) or nameof(Cmd.CustomScaledWait);
        // These passive visual factories own geometry only, never pile/choice callbacks.
        return m.Name == "Create" && t?.FullName is
            "MegaCrit.Sts2.Core.Nodes.Vfx.NItemThrowVfx" or "MegaCrit.Sts2.Core.Nodes.Vfx.NSplashVfx" or
            "MegaCrit.Sts2.Core.Nodes.Vfx.NLiquidOverlayVfx" or "MegaCrit.Sts2.Core.Nodes.Vfx.NGaseousImpactVfx" or
            "MegaCrit.Sts2.Core.Nodes.Vfx.NSmokyVignetteVfx" or "MegaCrit.Sts2.Core.Nodes.Vfx.Cards.NNightmareHandsVfx";
    }
}
