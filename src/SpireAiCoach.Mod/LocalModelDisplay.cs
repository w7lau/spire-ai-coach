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
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.TestSupport;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Optional display inside native model effects. Discover by IL, not card IDs.
internal static class LocalModelDisplay
{
    private static readonly List<string> Boundaries = [];
    private static readonly List<string> Failures = [];
    public static object Status() => new { boundaries = Boundaries.ToArray(), failures = Failures.ToArray() };
    public static void Install()
    {
        var enabled = AccessTools.PropertyGetter(typeof(TestMode), nameof(TestMode.IsOff));
        foreach (var type in typeof(CardModel).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(CardModel).IsAssignableFrom(t)))
        foreach (var effect in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            var state = effect.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
            var method = state == null ? effect : AccessTools.Method(state, "MoveNext");
            if (method == null || LocalDisplayBranch.Find(method, enabled, DisplayCall, DisplayValue).Length == 0) continue;
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
        var guards = LocalDisplayBranch.Find(code.Select((c, i) => new LocalInstruction(i, c.opcode, Operand(c.operand))),
            original.DeclaringType!, original.GetMethodBody()!.LocalVariables.Select(l => l.LocalType).ToArray(),
            enabled, DisplayCall, DisplayValue).ToHashSet();
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
        if (t == typeof(Player)) return m.Name == "get_Creature";
        if (t == typeof(LocalContext)) return m.Name == nameof(LocalContext.IsMe);
        if (t == typeof(ModelDb)) return m.Name == nameof(ModelDb.Potion) && m is MethodInfo { IsGenericMethod: true };
        if (t == typeof(PotionModel)) return m.Name == "get_Image";
        if (t == typeof(NCombatRoom)) return m.Name is "get_Instance" or "GetCreatureNode" or "get_CombatVfxContainer";
        if (t == typeof(NCreature)) return m.Name is "get_VfxSpawnPosition" or "GetBottomOfHitbox";
        if (t == typeof(NGame)) return m.Name is "get_Instance" or "get_CurrentRunNode";
        if (t == typeof(NRun)) return m.Name == "get_GlobalUi";
        if (t == typeof(Color) || t == typeof(Vector2)) return m.IsConstructor || m.Name.StartsWith("op_", StringComparison.Ordinal);
        if (t?.FullName == "MegaCrit.Sts2.Core.Helpers.GodotTreeExtensions") return m.Name == "AddChildSafely";
        if (t == typeof(Cmd)) return m.Name is nameof(Cmd.Wait) or nameof(Cmd.CustomScaledWait);
        // These passive visual factories own geometry only, never pile/choice callbacks.
        return m.Name == "Create" && t?.FullName is
            "MegaCrit.Sts2.Core.Nodes.Vfx.NItemThrowVfx" or "MegaCrit.Sts2.Core.Nodes.Vfx.NSplashVfx" or
            "MegaCrit.Sts2.Core.Nodes.Vfx.NLiquidOverlayVfx" or "MegaCrit.Sts2.Core.Nodes.Vfx.NGaseousImpactVfx" or
            "MegaCrit.Sts2.Core.Nodes.Vfx.NSmokyVignetteVfx" or "MegaCrit.Sts2.Core.Nodes.Vfx.Cards.NNightmareHandsVfx";
    }
}
